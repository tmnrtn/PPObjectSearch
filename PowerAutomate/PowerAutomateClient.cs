using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.PowerAutomate;

public sealed class PowerAutomateException : Exception
{
    public PowerAutomateException(string message, HttpStatusCode? statusCode = null) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// Reads one flow run step by step from the Power Automate API - the detail Dataverse's flowrun
/// table does not hold. Read-only.
///
/// Signs in as the tab's account, in the environment's tenant; the default public client is
/// granted the Power Automate API without consent. Per-step detail needs the user to own or
/// co-own the flow; an environment admin is served through the admin scope instead.
/// </summary>
public sealed class PowerAutomateClient : IDisposable
{
    // Power Automate lives at a different host in each sovereign cloud.
    private string Resource => _auth.Cloud.FlowResource;
    private string BaseUrl => _auth.Cloud.FlowApi + "/providers/Microsoft.ProcessSimple/";
    private const string ApiVersion = "api-version=2016-11-01";

    // The names the API's run, action and connection objects share.
    private const string PropertiesKey = "properties";
    private const string StatusKey = "status";
    private const string ErrorKey = "error";
    private const string MessageKey = "message";

    /// <summary>Pages of 100 actions; a flow larger than this is not one to read whole.</summary>
    private const int MaxPages = 50;

    private readonly EnvironmentAuthContext _auth;
    private readonly HttpClient _http;

    public PowerAutomateClient(EnvironmentAuthContext auth) : this(auth, handler: null)
    {
    }

    /// <summary>For tests: every request goes through <paramref name="handler"/> rather than the network.</summary>
    internal PowerAutomateClient(EnvironmentAuthContext auth, HttpMessageHandler? handler)
    {
        _auth = auth;
        // The shared pipeline retries throttled and transient failures; tests pass their own.
        _http = handler is null ? new HttpClient(Core.RetryHandler.Shared, disposeHandler: false) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    /// <summary>
    /// The run and every step's result in it. Tries the maker's route, then the admin route if
    /// that is refused; says plainly why when neither works.
    /// </summary>
    public async Task<FlowRunDetail> GetRunAsync(string environmentId, string flowId, string runName, CancellationToken ct = default)
    {
        var path = $"environments/{Uri.EscapeDataString(environmentId)}/flows/{Uri.EscapeDataString(flowId)}/runs/{Uri.EscapeDataString(runName)}";

        var (runUrl, run, viaAdmin) = await GetRunDocumentAsync(path, ct).ConfigureAwait(false);

        using (run)
        {
            var properties = run.RootElement.TryGetProperty(PropertiesKey, out var p) ? p : default;
            var status = JsonHelper.GetString(properties, StatusKey) ?? "Unknown";
            var error = Child(properties, ErrorKey);

            FlowActionResult? trigger = Child(properties, "trigger") is { ValueKind: not JsonValueKind.Undefined } t
                ? ReadResult(JsonHelper.GetString(t, "name") ?? "trigger", t)
                : null;

            var actions = await GetActionResultsAsync(runUrl, ct).ConfigureAwait(false);

            return new FlowRunDetail(
                runName,
                status,
                Outcome(status),
                JsonHelper.GetDate(properties, "startTime"),
                JsonHelper.GetDate(properties, "endTime"),
                trigger,
                actions,
                JsonHelper.GetString(error, "code"),
                JsonHelper.GetString(error, MessageKey),
                runUrl,
                viaAdmin);
        }
    }

    /// <summary>Every step's result in a run, by step name.</summary>
    private async Task<Dictionary<string, FlowActionResult>> GetActionResultsAsync(string runUrl, CancellationToken ct)
    {
        var actions = new Dictionary<string, FlowActionResult>(StringComparer.Ordinal);
        var pages = 0;

        string? url = $"{runUrl}/actions?{ApiVersion}";
        while (url is not null)
        {
            if (pages >= MaxPages)
            {
                throw new PowerAutomateException($"The run has more than {MaxPages * 100:N0} step results, which is more than this tool will read.");
            }

            using var page = await SendAsync(url, ct).ConfigureAwait(false);

            foreach (var action in JsonHelper.Rows(page.RootElement))
            {
                if (JsonHelper.GetString(action, "name") is not { } name) continue;
                actions[name] = ReadResult(name, action.TryGetProperty(PropertiesKey, out var ap) ? ap : action);
            }

            url = Links.SameHostNext(JsonHelper.GetString(page.RootElement, "nextLink"), url, m => new PowerAutomateException(m));
            pages++;
        }

        return actions;
    }

    /// <summary>
    /// A flow's latest runs, live: including those still running or waiting, and those finished
    /// too recently for Dataverse's copy. Maker's route first, then the admin route.
    /// </summary>
    public async Task<IReadOnlyList<ProcessRun>> GetRunsAsync(string environmentId, string flowId, int top = 50, CancellationToken ct = default)
    {
        var path = $"environments/{Uri.EscapeDataString(environmentId)}/flows/{Uri.EscapeDataString(flowId)}/runs";

        JsonDocument doc;
        try
        {
            doc = await SendAsync($"{BaseUrl}{path}?{ApiVersion}&$top={top}", ct).ConfigureAwait(false);
        }
        catch (PowerAutomateException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            try
            {
                doc = await SendAsync($"{BaseUrl}scopes/admin/{path}?{ApiVersion}&$top={top}", ct).ConfigureAwait(false);
            }
            catch (PowerAutomateException adminEx) when (adminEx.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw new PowerAutomateException(
                    "Power Automate would not list this flow's runs - that needs you to own or co-own the flow, " +
                    "or to be an admin of the environment.", adminEx.StatusCode);
            }
        }

        using (doc)
        {
            return JsonHelper.Rows(doc.RootElement)
                .Select(run => ReadLiveRun(run, flowId))
                .OfType<ProcessRun>()
                .ToList();
        }
    }

    private static ProcessRun? ReadLiveRun(JsonElement run, string flowId)
    {
        if (JsonHelper.GetString(run, "name") is not { } name) return null;

        var properties = run.TryGetProperty(PropertiesKey, out var p) ? p : default;
        var status = JsonHelper.GetString(properties, StatusKey) ?? "Unknown";
        var error = Child(properties, ErrorKey);
        var trigger = Child(properties, "trigger");
        var start = JsonHelper.GetDate(properties, "startTime");
        var end = JsonHelper.GetDate(properties, "endTime");

        return new ProcessRun
        {
            Name = name,
            Status = status,
            Outcome = DataverseClient.FlowOutcome(status),
            StartTime = start,
            EndTime = end,
            DurationMs = start is { } s && end is { } en && en >= s ? (long)(en - s).TotalMilliseconds : null,
            TriggerType = JsonHelper.GetString(trigger, "name"),
            ErrorCode = JsonHelper.GetString(error, "code"),
            ErrorMessage = JsonHelper.GetString(error, MessageKey),
            FlowId = flowId,
            IsLiveOnly = true
        };
    }

    /// <summary>
    /// Each iteration of a step inside a loop. Asked of the step itself: a loop's own entry lists
    /// none, its steps list one per pass.
    /// </summary>
    public async Task<FlowRepetitions> GetRepetitionsAsync(FlowRunDetail run, string actionName, CancellationToken ct = default)
    {
        var repetitions = new FlowRepetitions();
        var pages = 0;

        string? url = $"{run.RunUrl}/actions/{Uri.EscapeDataString(actionName)}/repetitions?{ApiVersion}";
        while (url is not null)
        {
            // Unlike the run's own steps this is not an error: the iterations read so far are
            // still worth showing, as long as they are not presented as all of them.
            if (pages >= MaxPages)
            {
                repetitions.IsTruncated = true;
                break;
            }

            using var page = await SendAsync(url, ct).ConfigureAwait(false);

            foreach (var repetition in JsonHelper.Rows(page.RootElement))
            {
                var properties = repetition.TryGetProperty(PropertiesKey, out var p) ? p : repetition;
                var result = ReadResult(actionName, properties);

                repetitions.Add(new FlowRepetition(
                    RepetitionLabel(properties, repetitions.Count),
                    result.Status, result.Outcome, result.StartTime, result.EndTime,
                    result.ErrorCode, result.ErrorMessage, result.InputsLink, result.OutputsLink));
            }

            url = Links.SameHostNext(JsonHelper.GetString(page.RootElement, "nextLink"), url, m => new PowerAutomateException(m));
            pages++;
        }

        return repetitions;
    }

    /// <summary>
    /// A step's inputs or outputs. The link is pre-signed and short-lived, so it is fetched
    /// without the user's token - which it neither needs nor should be sent to.
    /// </summary>
    public async Task<string> GetContentAsync(string link, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, link);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new PowerAutomateException(
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    ? "The link to this content has expired or been withdrawn. Show the run again to get a fresh one."
                    : $"Could not read the content: HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }

        // A step's outputs can be a whole file or a huge array. Shown in a code pane they would
        // freeze the window, or run it out of memory, long before anyone could read them.
        if (response.Content.Headers.ContentLength is > MaxContentBytes)
        {
            throw TooLarge(response.Content.Headers.ContentLength.Value);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            if (buffer.Length > MaxContentBytes) throw TooLarge(null);
        }

        var body = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);

        // Indenting a large document is slow to do and to draw; past this it is shown as it came.
        return body.Length > MaxPrettifyChars ? body : TextDiff.Prettify(body);
    }

    /// <summary>Inputs or outputs larger than this are not loaded into the window.</summary>
    internal const int MaxContentBytes = 5 * 1024 * 1024;

    private const int MaxPrettifyChars = 1024 * 1024;

    private static PowerAutomateException TooLarge(long? bytes) => new(
        $"This content is {(bytes is { } b ? $"{b / 1024.0 / 1024.0:0.#} MB" : "more than " + MaxContentBytes / 1024 / 1024 + " MB")}" +
        " - too large to show here. Open the run in Power Automate to see it.");

    // ---------------------------------------------------------------- reading

    private async Task<(string RunUrl, JsonDocument Run, bool ViaAdmin)> GetRunDocumentAsync(string path, CancellationToken ct)
    {
        var makerUrl = BaseUrl + path;

        try
        {
            return (makerUrl, await SendAsync($"{makerUrl}?{ApiVersion}", ct).ConfigureAwait(false), false);
        }
        catch (PowerAutomateException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Not an owner: an environment admin can still read it through the admin scope.
            var adminUrl = BaseUrl + "scopes/admin/" + path;

            try
            {
                return (adminUrl, await SendAsync($"{adminUrl}?{ApiVersion}", ct).ConfigureAwait(false), true);
            }
            catch (PowerAutomateException adminEx) when (adminEx.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw new PowerAutomateException(
                    "Power Automate would not show this run's steps. They need you to own or co-own the flow, " +
                    "or to be an admin of the environment.", adminEx.StatusCode);
            }
        }
        catch (PowerAutomateException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new PowerAutomateException(
                "Power Automate has no record of this run. Run details are kept for about 28 days, so older runs " +
                "cannot be shown step by step.", ex.StatusCode);
        }
    }

    /// <summary>
    /// The connections in an environment that the signed-in user can see - their own and those
    /// shared with them - with each one's status. A connection reference bound to anything else
    /// will not be in the list.
    /// </summary>
    public async Task<IReadOnlyList<ConnectionInfo>> GetConnectionsAsync(string environmentId, CancellationToken ct = default)
    {
        var connections = new List<ConnectionInfo>();
        var url = $"{BaseUrl}environments/{Uri.EscapeDataString(environmentId)}/connections?{ApiVersion}";

        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var doc = await SendAsync(url, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                connections.AddRange(value.EnumerateArray().Select(ReadConnection).OfType<ConnectionInfo>());
            }

            url = Links.SameHostNext(JsonHelper.GetString(doc.RootElement, "nextLink"), url,
                message => new PowerAutomateException(message));
        }

        return connections;
    }

    private static ConnectionInfo? ReadConnection(JsonElement row)
    {
        if (JsonHelper.GetString(row, "name") is not { Length: > 0 } name) return null;

        var properties = row.TryGetProperty(PropertiesKey, out var p) ? p : default;

        // A connection reports its status as the first of a list.
        var statuses = Child(properties, "statuses");
        var status = statuses.ValueKind == JsonValueKind.Array && statuses.GetArrayLength() > 0 ? statuses[0] : default;

        var error = Child(status, ErrorKey);
        var createdBy = Child(properties, "createdBy");

        return new ConnectionInfo
        {
            Name = name,
            DisplayName = JsonHelper.GetString(properties, "displayName"),
            ConnectorId = JsonHelper.GetString(properties, "apiId"),
            Status = JsonHelper.GetString(status, StatusKey),
            StatusMessage = JsonHelper.GetString(error, MessageKey),
            Owner = JsonHelper.GetString(createdBy, "userPrincipalName")
                    ?? JsonHelper.GetString(createdBy, "email")
                    ?? JsonHelper.GetString(createdBy, "displayName")
        };
    }

    private async Task<JsonDocument> SendAsync(string url, CancellationToken ct)
    {
        string token;
        try
        {
            token = await _auth.GetTokenAsync(Resource, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new PowerAutomateException("Could not sign in to Power Automate - " + ex.Message);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new PowerAutomateException(
                $"Power Automate: {(int)response.StatusCode} {ErrorMessage(body)}", response.StatusCode);
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static FlowActionResult ReadResult(string name, JsonElement properties)
    {
        var status = JsonHelper.GetString(properties, StatusKey) ?? "Unknown";
        var error = Child(properties, ErrorKey);
        var code = JsonHelper.GetString(properties, "code");

        return new FlowActionResult(
            name,
            status,
            Outcome(status),
            JsonHelper.GetDate(properties, "startTime"),
            JsonHelper.GetDate(properties, "endTime"),
            JsonHelper.GetString(error, "code") ?? (Outcome(status) == FlowStepOutcome.Failed ? code : null),
            JsonHelper.GetString(error, MessageKey),
            Link(properties, "inputsLink"),
            Link(properties, "outputsLink"));
    }

    /// <summary>
    /// A property of an object - or, when there is none, an undefined element, which every
    /// JsonHelper read treats as having nothing in it.
    /// </summary>
    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    private static string? Link(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var link)
            ? JsonHelper.GetString(link, "uri")
            : null;

    /// <summary>"#3" - the iteration's position in its loop, counted from one as the portal does.</summary>
    private static string RepetitionLabel(JsonElement properties, int position)
    {
        if (properties.ValueKind == JsonValueKind.Object &&
            properties.TryGetProperty("repetitionIndexes", out var indexes) &&
            indexes.ValueKind == JsonValueKind.Array &&
            indexes.GetArrayLength() > 0)
        {
            return string.Join(" › ", indexes.EnumerateArray().Select(i =>
                JsonHelper.GetInt(i, "itemIndex") is { } index ? $"#{index + 1}" : "#?"));
        }

        return $"#{position + 1}";
    }

    internal static FlowStepOutcome Outcome(string status) => status.ToLowerInvariant() switch
    {
        "succeeded" => FlowStepOutcome.Succeeded,
        "failed" or "faulted" => FlowStepOutcome.Failed,
        "skipped" or "ignored" => FlowStepOutcome.Skipped,
        "timedout" => FlowStepOutcome.TimedOut,
        "cancelled" or "aborted" => FlowStepOutcome.Cancelled,
        "running" => FlowStepOutcome.Running,
        "waiting" or "suspended" or "paused" => FlowStepOutcome.Waiting,
        _ => FlowStepOutcome.Other
    };

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty(ErrorKey, out var error) &&
                error.TryGetProperty(MessageKey, out var message))
            {
                return message.GetString() ?? body;
            }
        }
        catch
        {
            // not JSON
        }

        return body.Length > 300 ? body[..300] + "..." : body;
    }

    public void Dispose() => _http.Dispose();
}
