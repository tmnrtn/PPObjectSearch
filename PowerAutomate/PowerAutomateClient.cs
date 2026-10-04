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
    private const string Resource = "https://service.flow.microsoft.com/";
    private const string BaseUrl = "https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/";
    private const string ApiVersion = "api-version=2016-11-01";

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
            var properties = run.RootElement.TryGetProperty("properties", out var p) ? p : default;
            var status = JsonHelper.GetString(properties, "status") ?? "Unknown";
            var error = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("error", out var e) ? e : default;

            FlowActionResult? trigger = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("trigger", out var t)
                ? ReadResult(JsonHelper.GetString(t, "name") ?? "trigger", t)
                : null;

            var actions = new Dictionary<string, FlowActionResult>(StringComparer.Ordinal);
            var pages = 0;

            for (string? url = $"{runUrl}/actions?{ApiVersion}"; url is not null; pages++)
            {
                if (pages >= MaxPages)
                {
                    throw new PowerAutomateException($"The run has more than {MaxPages * 100:N0} step results, which is more than this tool will read.");
                }

                using var page = await SendAsync(url, ct).ConfigureAwait(false);

                if (page.RootElement.TryGetProperty("value", out var value))
                {
                    foreach (var action in value.EnumerateArray())
                    {
                        if (JsonHelper.GetString(action, "name") is not { } name) continue;
                        actions[name] = ReadResult(name, action.TryGetProperty("properties", out var ap) ? ap : action);
                    }
                }

                url = JsonHelper.GetString(page.RootElement, "nextLink");
            }

            return new FlowRunDetail(
                runName,
                status,
                Outcome(status),
                JsonHelper.GetDate(properties, "startTime"),
                JsonHelper.GetDate(properties, "endTime"),
                trigger,
                actions,
                JsonHelper.GetString(error, "code"),
                JsonHelper.GetString(error, "message"),
                runUrl,
                viaAdmin);
        }
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
            var runs = new List<ProcessRun>();
            if (!doc.RootElement.TryGetProperty("value", out var value)) return runs;

            foreach (var run in value.EnumerateArray())
            {
                if (JsonHelper.GetString(run, "name") is not { } name) continue;

                var properties = run.TryGetProperty("properties", out var p) ? p : default;
                var status = JsonHelper.GetString(properties, "status") ?? "Unknown";
                var error = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("error", out var e) ? e : default;
                var trigger = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("trigger", out var t) ? t : default;
                var start = JsonHelper.GetDate(properties, "startTime");
                var end = JsonHelper.GetDate(properties, "endTime");

                runs.Add(new ProcessRun
                {
                    Name = name,
                    Status = status,
                    Outcome = DataverseClient.FlowOutcome(status),
                    StartTime = start,
                    EndTime = end,
                    DurationMs = start is { } s && end is { } en && en >= s ? (long)(en - s).TotalMilliseconds : null,
                    TriggerType = JsonHelper.GetString(trigger, "name"),
                    ErrorCode = JsonHelper.GetString(error, "code"),
                    ErrorMessage = JsonHelper.GetString(error, "message"),
                    FlowId = flowId,
                    IsLiveOnly = true
                });
            }

            return runs;
        }
    }

    /// <summary>
    /// Each iteration of a step inside a loop. Asked of the step itself: a loop's own entry lists
    /// none, its steps list one per pass.
    /// </summary>
    public async Task<FlowRepetitions> GetRepetitionsAsync(FlowRunDetail run, string actionName, CancellationToken ct = default)
    {
        var repetitions = new FlowRepetitions();
        var pages = 0;

        for (string? url = $"{run.RunUrl}/actions/{Uri.EscapeDataString(actionName)}/repetitions?{ApiVersion}"; url is not null; pages++)
        {
            // Unlike the run's own steps this is not an error: the iterations read so far are
            // still worth showing, as long as they are not presented as all of them.
            if (pages >= MaxPages)
            {
                repetitions.IsTruncated = true;
                break;
            }

            using var page = await SendAsync(url, ct).ConfigureAwait(false);

            if (page.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var repetition in value.EnumerateArray())
                {
                    var properties = repetition.TryGetProperty("properties", out var p) ? p : repetition;
                    var result = ReadResult(actionName, properties);

                    repetitions.Add(new FlowRepetition(
                        RepetitionLabel(properties, repetitions.Count),
                        result.Status, result.Outcome, result.StartTime, result.EndTime,
                        result.ErrorCode, result.ErrorMessage, result.InputsLink, result.OutputsLink));
                }
            }

            url = JsonHelper.GetString(page.RootElement, "nextLink");
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
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new PowerAutomateException(
                response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    ? "The link to this content has expired or been withdrawn. Show the run again to get a fresh one."
                    : $"Could not read the content: HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }

        return TextDiff.Prettify(body);
    }

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
        var status = JsonHelper.GetString(properties, "status") ?? "Unknown";
        var error = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("error", out var e) ? e : default;
        var code = JsonHelper.GetString(properties, "code");

        return new FlowActionResult(
            name,
            status,
            Outcome(status),
            JsonHelper.GetDate(properties, "startTime"),
            JsonHelper.GetDate(properties, "endTime"),
            JsonHelper.GetString(error, "code") ?? (Outcome(status) == FlowStepOutcome.Failed ? code : null),
            JsonHelper.GetString(error, "message"),
            Link(properties, "inputsLink"),
            Link(properties, "outputsLink"));
    }

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
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
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
