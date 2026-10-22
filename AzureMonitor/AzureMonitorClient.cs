using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PPObjectSearch.Auth;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;

namespace PPObjectSearch.AzureMonitor;

/// <summary>Why an Azure call failed, as far as it can be told - each says something different to do.</summary>
public enum AzureFailure
{
    /// <summary>No token: the sign-in client is not allowed this API in the tenant.</summary>
    ClientNotAllowed,

    /// <summary>No token, for any other reason - cancelled sign-in, conditional access, network.</summary>
    SignIn,

    /// <summary>Signed in, but the account has no Azure role on what was asked for.</summary>
    Forbidden,

    /// <summary>The subscription, resource or app id is not there (or not visible).</summary>
    NotFound,

    /// <summary>Anything else the API said.</summary>
    Other
}

public sealed class AzureMonitorException : Exception
{
    public AzureMonitorException(string message, AzureFailure failure, HttpStatusCode? statusCode = null, string? errorCode = null)
        : base(message)
    {
        Failure = failure;
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public AzureFailure Failure { get; }
    public HttpStatusCode? StatusCode { get; }

    /// <summary>The AADSTS code of a refused sign-in, or the API's own error code.</summary>
    public string? ErrorCode { get; }
}

/// <summary>An Azure subscription the signed-in account can see.</summary>
public sealed record AzureSubscription(string Id, string Name, string? State);

/// <summary>An Application Insights resource, as Azure Resource Manager lists it.</summary>
public sealed record AppInsightsComponent(
    string ResourceId,
    string Name,
    string SubscriptionId,
    string? SubscriptionName,
    string? ResourceGroup,
    string? Location,
    string? AppId,
    string? WorkspaceResourceId);

/// <summary>One column of a query result.</summary>
public sealed record TelemetryColumn(string Name, string Type);

/// <summary>
/// A query's primary result: its columns and rows, each row a cell per column. Cells keep their
/// JSON form; <see cref="TelemetryRow"/> reads them by column name.
/// </summary>
public sealed class TelemetryTable : IDisposable
{
    private readonly JsonDocument? _document;
    private readonly Dictionary<string, int> _ordinals;

    internal TelemetryTable(JsonDocument? document, IReadOnlyList<TelemetryColumn> columns, IReadOnlyList<JsonElement> rows, string? partialError)
    {
        _document = document;
        Columns = columns;
        Rows = rows.Select(r => new TelemetryRow(this, r)).ToList();
        PartialError = partialError;
        _ordinals = columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);
    }

    public static TelemetryTable Empty { get; } = new(null, [], [], null);

    public IReadOnlyList<TelemetryColumn> Columns { get; }
    public IReadOnlyList<TelemetryRow> Rows { get; }

    /// <summary>
    /// The query ran but not all of it: a partial result, which the API reports beside the rows
    /// (a timeout, a limit). Null when the result is whole.
    /// </summary>
    public string? PartialError { get; }

    internal int Ordinal(string column) => _ordinals.TryGetValue(column, out var i) ? i : -1;

    public void Dispose() => _document?.Dispose();
}

/// <summary>One row of a <see cref="TelemetryTable"/>, read by column name. A missing column reads as nothing.</summary>
public sealed class TelemetryRow
{
    private readonly TelemetryTable _table;
    private readonly JsonElement _cells;

    internal TelemetryRow(TelemetryTable table, JsonElement cells)
    {
        _table = table;
        _cells = cells;
    }

    private JsonElement? Cell(string column)
    {
        var i = _table.Ordinal(column);
        if (i < 0 || _cells.ValueKind != JsonValueKind.Array || i >= _cells.GetArrayLength()) return null;

        var cell = _cells[i];
        return cell.ValueKind == JsonValueKind.Null ? null : cell;
    }

    public string? String(string column) => Cell(column) is { } cell
        ? cell.ValueKind == JsonValueKind.String ? cell.GetString() : cell.GetRawText()
        : null;

    public DateTimeOffset? Date(string column) =>
        String(column) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
            ? when
            : null;

    public double? Number(string column) => Cell(column) is { } cell
        ? cell.ValueKind switch
        {
            JsonValueKind.Number => cell.GetDouble(),
            JsonValueKind.String when double.TryParse(cell.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
            _ => null
        }
        : null;

    public bool? Bool(string column) => Cell(column) is { } cell
        ? cell.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(cell.GetString(), out var b) => b,
            _ => null
        }
        : null;

    /// <summary>
    /// A <c>dynamic</c> column - customDimensions - as name/value pairs. The API sends a dynamic
    /// cell as JSON text; an object sent as is reads the same.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties(string column)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Cell(column) is not { } cell) return result;

        try
        {
            using var doc = cell.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(cell.GetString() ?? "{}")
                : JsonDocument.Parse(cell.GetRawText());

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Not an object after all; nothing to read.
        }

        return result;
    }
}

/// <summary>
/// The Azure side of an environment's telemetry: Azure Resource Manager to find its Application
/// Insights resource, and the Application Insights query API to read it. Read-only.
///
/// Signs in as the tab's account, in the environment's tenant, so it sees what that account sees
/// in Azure - Reader (or Monitoring Reader) on the resource is all a query needs. Whether the
/// default public client may have tokens for these APIs is the tenant's call; where it may not,
/// the error says so and points at the <c>ClientId</c> setting.
/// </summary>
public sealed partial class AzureMonitorClient : IDisposable
{
    private const string SubscriptionsApiVersion = "api-version=2022-12-01";
    private const string ComponentsApiVersion = "api-version=2020-02-02";

    /// <summary>Pages of subscriptions or resources; an account that sees more is not one to list by eye.</summary>
    private const int MaxPages = 50;

    private readonly EnvironmentAuthContext _auth;
    private readonly HttpClient _http;

    public AzureMonitorClient(EnvironmentAuthContext auth, Cloud cloud) : this(auth, cloud, handler: null)
    {
    }

    /// <summary>For tests: every request goes through <paramref name="handler"/> rather than the network.</summary>
    internal AzureMonitorClient(EnvironmentAuthContext auth, Cloud cloud, HttpMessageHandler? handler)
    {
        _auth = auth;
        Azure = cloud.Azure;
        // The shared pipeline retries throttled and transient failures; tests pass their own.
        _http = handler is null ? new HttpClient(RetryHandler.Shared, disposeHandler: false) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public AzureEndpoints Azure { get; }

    // ---------------------------------------------------------------- sign-in

    /// <summary>A token for one of the Azure APIs, or an <see cref="AzureMonitorException"/> saying why not.</summary>
    public async Task<string> GetTokenAsync(string resource, CancellationToken ct = default)
    {
        try
        {
            return await _auth.GetTokenAsync(resource, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw SignInFailure(resource, ex);
        }
    }

    // AADSTS codes that mean the sign-in client itself is the problem, not the account:
    // 65001 consent not given, 65002 first-party pre-authorization missing, 650057 the API is not
    // among the client's permissions, 700016 the client is not in the tenant at all.
    private static readonly string[] ClientNotAllowedCodes = ["AADSTS65001", "AADSTS65002", "AADSTS650057", "AADSTS700016"];

    [GeneratedRegex(@"AADSTS\d+")]
    private static partial Regex AadstsCode();

    internal static AzureMonitorException SignInFailure(string resource, Exception ex)
    {
        var code = AadstsCode().Match(ex.Message) is { Success: true } m ? m.Value : null;
        var firstLine = ex.Message.Split('\n', 2)[0].Trim();

        if (code is not null && ClientNotAllowedCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            return new AzureMonitorException(
                $"The sign-in client is not allowed tokens for {resource} in this tenant ({code}). " +
                "Register your own public client with this API's delegated permission and set ClientId in settings.",
                AzureFailure.ClientNotAllowed, errorCode: code);
        }

        return new AzureMonitorException($"Could not sign in to {resource} - {firstLine}", AzureFailure.SignIn, errorCode: code);
    }

    // ---------------------------------------------------------------- Resource Manager

    /// <summary>The subscriptions the account can see, by name.</summary>
    public async Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(CancellationToken ct = default)
    {
        var subscriptions = new List<AzureSubscription>();

        await ReadArmPagesAsync($"{Azure.ResourceManager}/subscriptions?{SubscriptionsApiVersion}", item =>
        {
            var id = JsonHelper.GetString(item, "subscriptionId");
            if (string.IsNullOrWhiteSpace(id)) return;

            subscriptions.Add(new AzureSubscription(id, JsonHelper.GetString(item, "displayName") ?? id, JsonHelper.GetString(item, "state")));
        }, ct).ConfigureAwait(false);

        return subscriptions.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The Application Insights resources in one subscription.</summary>
    public async Task<IReadOnlyList<AppInsightsComponent>> ListComponentsAsync(AzureSubscription subscription, CancellationToken ct = default)
    {
        var components = new List<AppInsightsComponent>();
        var url = $"{Azure.ResourceManager}/subscriptions/{Uri.EscapeDataString(subscription.Id)}" +
                  $"/providers/Microsoft.Insights/components?{ComponentsApiVersion}";

        await ReadArmPagesAsync(url, item =>
        {
            var id = JsonHelper.GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) return;

            var properties = item.TryGetProperty("properties", out var p) ? p : default;
            components.Add(new AppInsightsComponent(
                id,
                JsonHelper.GetString(item, "name") ?? id,
                subscription.Id,
                subscription.Name,
                ResourceGroupOf(id),
                JsonHelper.GetString(item, "location"),
                properties.ValueKind == JsonValueKind.Object ? JsonHelper.GetString(properties, "AppId") : null,
                properties.ValueKind == JsonValueKind.Object ? JsonHelper.GetString(properties, "WorkspaceResourceId") : null));
        }, ct).ConfigureAwait(false);

        return components;
    }

    /// <summary>
    /// Every Application Insights resource the account can see, subscription by subscription. A
    /// subscription that refuses is noted and passed over, so one locked-down subscription does not
    /// hide the rest.
    /// </summary>
    public async Task<(IReadOnlyList<AppInsightsComponent> Components, IReadOnlyList<string> Notes)> ListAllComponentsAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var subscriptions = await ListSubscriptionsAsync(ct).ConfigureAwait(false);
        var components = new List<AppInsightsComponent>();
        var notes = new List<string>();

        foreach (var subscription in subscriptions)
        {
            progress?.Report($"Reading {subscription.Name}...");
            try
            {
                components.AddRange(await ListComponentsAsync(subscription, ct).ConfigureAwait(false));
            }
            catch (AzureMonitorException ex) when (ex.Failure is AzureFailure.Forbidden or AzureFailure.NotFound or AzureFailure.Other)
            {
                notes.Add($"{subscription.Name} could not be read: {ex.Message}");
            }
        }

        if (subscriptions.Count == 0) notes.Add("This account sees no Azure subscriptions in the environment's tenant.");

        return (components.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(), notes);
    }

    /// <summary>The resource group named in a resource id, e.g. ".../resourceGroups/rg-telemetry/providers/...".</summary>
    internal static string? ResourceGroupOf(string resourceId)
    {
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals("resourceGroups", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : null;
    }

    private async Task ReadArmPagesAsync(string url, Action<JsonElement> onItem, CancellationToken ct)
    {
        var token = await GetTokenAsync(Azure.ResourceManager, ct).ConfigureAwait(false);

        for (var page = 0; page < MaxPages && !string.IsNullOrEmpty(url); page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var doc = await SendAsync(request, token, "Azure Resource Manager", ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) onItem(item);
            }

            // ARM pages with nextLink, not @odata.nextLink.
            url = JsonHelper.GetString(doc.RootElement, "nextLink") ?? string.Empty;
        }
    }

    // ---------------------------------------------------------------- query

    /// <summary>
    /// Runs a KQL query against an Application Insights resource, by its app id, over the given
    /// period. The period goes to the API as its timespan, so the query itself need not filter
    /// on time.
    /// </summary>
    public async Task<TelemetryTable> QueryAsync(string appId, string query, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(Azure.AppInsightsApi, ct).ConfigureAwait(false);

        var body = JsonSerializer.Serialize(new { query, timespan = Timespan(from, to) });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Azure.AppInsightsApi}/v1/apps/{Uri.EscapeDataString(appId)}/query")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        var doc = await SendAsync(request, token, "Application Insights", ct).ConfigureAwait(false);
        try
        {
            return ReadTable(doc);
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    /// <summary>An ISO 8601 interval, as the query API takes it: "2026-10-01T00:00:00Z/2026-10-08T00:00:00Z".</summary>
    internal static string Timespan(DateTimeOffset from, DateTimeOffset to) =>
        $"{from.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}/{to.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";

    /// <summary>The first table of a query response, which holds the result; the document goes with it.</summary>
    internal static TelemetryTable ReadTable(JsonDocument doc)
    {
        var root = doc.RootElement;
        var partial = root.TryGetProperty("error", out var error) ? ErrorMessage(error) : null;

        if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array || tables.GetArrayLength() == 0)
        {
            doc.Dispose();
            return partial is null ? TelemetryTable.Empty : new TelemetryTable(null, [], [], partial);
        }

        var table = tables[0];
        var columns = table.TryGetProperty("columns", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray()
                .Select(col => new TelemetryColumn(JsonHelper.GetString(col, "name") ?? string.Empty, JsonHelper.GetString(col, "type") ?? "string"))
                .ToList()
            : [];
        var rows = table.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().ToList()
            : [];

        return new TelemetryTable(doc, columns, rows, partial);
    }

    // ---------------------------------------------------------------- transport

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, string token, string service, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) throw HttpFailure(service, response.StatusCode, body);

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    internal static AzureMonitorException HttpFailure(string service, HttpStatusCode status, string body)
    {
        var (code, message) = ReadError(body);

        var failure = status switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => AzureFailure.Forbidden,
            HttpStatusCode.NotFound => AzureFailure.NotFound,
            _ => AzureFailure.Other
        };

        var hint = failure switch
        {
            AzureFailure.Forbidden => " - the signed-in account needs Reader (or Monitoring Reader) on the resource or its subscription.",
            AzureFailure.NotFound => " - it may have been deleted or moved, or this account cannot see it.",
            _ => string.Empty
        };

        return new AzureMonitorException($"{service}: {(int)status} {message}{hint}", failure, status, code);
    }

    private static (string? Code, string Message) ReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, "(no details)");

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                return (JsonHelper.GetString(error, "code"), ErrorMessage(error));
            }
        }
        catch (JsonException)
        {
            // Not JSON.
        }

        return (null, body.Length > 300 ? body[..300] + "..." : body);
    }

    /// <summary>An API error's message, with the innermost detail where the outer one is generic.</summary>
    private static string ErrorMessage(JsonElement error)
    {
        var message = JsonHelper.GetString(error, "message") ?? JsonHelper.GetString(error, "code") ?? "(no details)";

        var inner = error;
        var nested = false;
        while (inner.ValueKind == JsonValueKind.Object && inner.TryGetProperty("innererror", out var next))
        {
            inner = next;
            nested = true;
        }

        var detail = nested ? JsonHelper.GetString(inner, "message") : null;
        return detail is null || detail == message ? message : $"{message} {detail}";
    }

    public void Dispose() => _http.Dispose();
}
