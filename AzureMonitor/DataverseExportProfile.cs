using System.Text;
using PPObjectSearch.Models;

namespace PPObjectSearch.AzureMonitor;

/// <summary>A plug-in's executions over a period, from its dependency rows.</summary>
public sealed record PluginTelemetrySummary(long Executions, long Failures, double? AverageMs, double? P95Ms, DateTimeOffset? Last)
{
    public static PluginTelemetrySummary None { get; } = new(0, 0, null, null, null);
}

/// <summary>
/// The Power Platform admin center's Dataverse data export, as written to Application Insights:
/// plug-in executions as <c>dependencies</c>, Web API calls as <c>requests</c>, and what threw as
/// <c>exceptions</c>, tied together by operation id.
///
/// The column names are App Insights' own and fixed. Which <c>customDimensions</c> hold a
/// plug-in's message and table is less certain, so several names are tried, and where the
/// export names the environment is not assumed at all - see <see cref="TelemetryProfiles.EnvironmentFilter"/>.
/// </summary>
public sealed class DataverseExportProfile : ITelemetryProfile
{
    public static DataverseExportProfile Instance { get; } = new();

    private DataverseExportProfile()
    {
    }

    public TelemetryKind Kind => TelemetryKind.DataverseExport;

    /// <summary>Most rows read per table in one period; past it the result says it is cut short.</summary>
    public const int MaxRows = Dataverse.DataverseClient.MaxFailureRows;

    // Plug-in executions are dependencies of type "Plugin"; "contains" also takes in any variant.
    private const string PluginRows = "dependencies\n| where type contains 'plugin'\n";

    private static readonly string[] MessageKeys = ["MessageName", "Message", "messagename", "PluginMessage", "SdkMessage"];
    private static readonly string[] EntityKeys = ["PrimaryEntityName", "PrimaryEntity", "EntityName", "Entity", "EntityLogicalName"];

    // ---------------------------------------------------------------- queries

    public string CheckQuery(TelemetryScope scope) =>
        "union requests, dependencies, exceptions, traces, pageViews\n" +
        $"| summarize Total = count(), Matching = countif({TelemetryProfiles.MatchExpression(scope)})";

    /// <summary>Failed plug-in executions, newest first - only the named plug-in types when given.</summary>
    public static string FailedPluginsQuery(TelemetryScope scope, IReadOnlyCollection<string>? pluginTypeNames) =>
        PluginRows +
        "| where success == false\n" +
        TelemetryProfiles.EnvironmentFilter(scope) +
        PluginNameFilter(pluginTypeNames) +
        "| project timestamp, id, operation_Id, name, resultCode, duration, customDimensions\n" +
        $"| order by timestamp desc\n| take {MaxRows}";

    public static string ExceptionsQuery(TelemetryScope scope) =>
        "exceptions\n" +
        TelemetryProfiles.EnvironmentFilter(scope) +
        "| project timestamp, operation_Id, operation_ParentId, operation_Name, type, outerMessage, innermostMessage, problemId, customDimensions\n" +
        $"| order by timestamp desc\n| take {MaxRows}";

    public static string FailedRequestsQuery(TelemetryScope scope) =>
        "requests\n" +
        "| where success == false\n" +
        TelemetryProfiles.EnvironmentFilter(scope) +
        "| project timestamp, id, operation_Id, name, resultCode, url, duration, customDimensions\n" +
        $"| order by timestamp desc\n| take {MaxRows}";

    /// <summary>How often the plug-in ran, how often it failed, and how long it took.</summary>
    public static string PluginSummaryQuery(TelemetryScope scope, IReadOnlyCollection<string> pluginTypeNames) =>
        PluginRows +
        TelemetryProfiles.EnvironmentFilter(scope) +
        PluginNameFilter(pluginTypeNames) +
        "| summarize Executions = count(), Failures = countif(success == false), AverageMs = avg(duration), " +
        "P95Ms = percentile(duration, 95), Last = max(timestamp)";

    /// <summary>
    /// A plug-in type's rows, matched on the type name at the start: the export may add the
    /// message or assembly after it.
    /// </summary>
    private static string PluginNameFilter(IReadOnlyCollection<string>? names) =>
        names is null
            ? string.Empty
            : names.Count == 0
                ? "| where false\n"
                : "| where " + string.Join(" or ", names.Select(n => $"name startswith {Kql.Literal(n)}")) + "\n";

    // ---------------------------------------------------------------- failures

    public async Task ReadFailuresAsync(AzureMonitorClient client, string appId, TelemetryScope scope,
        DateTimeOffset from, DateTimeOffset to, FailureQuery query, FailureData data, CancellationToken ct)
    {
        // A solution's failures are its plug-ins'; requests and stray exceptions belong to no component.
        var scoped = query.PluginTypeNames is not null || query.PluginStepIds is not null || query.WorkflowIds is not null;
        IReadOnlyCollection<string>? names = scoped ? query.PluginTypeNames ?? new HashSet<string>() : null;

        using var plugins = await client.QueryAsync(appId, FailedPluginsQuery(scope, names), from, to, ct).ConfigureAwait(false);
        using var exceptions = await client.QueryAsync(appId, ExceptionsQuery(scope), from, to, ct).ConfigureAwait(false);
        using var requests = scoped
            ? TelemetryTable.Empty
            : await client.QueryAsync(appId, FailedRequestsQuery(scope), from, to, ct).ConfigureAwait(false);

        foreach (var (table, what) in new[] { (plugins, "failed plug-ins"), (exceptions, "exceptions"), (requests, "failed requests") })
        {
            if (table.Rows.Count >= MaxRows) data.Notes.Add($"App Insights {what} stopped at {MaxRows:N0} rows; narrow the period to see them all.");
            if (table.PartialError is { } partial) data.Notes.Add($"App Insights {what} came back incomplete: {partial}");
        }

        data.TelemetryRowsRead += plugins.Rows.Count + exceptions.Rows.Count + requests.Rows.Count;
        data.Events.AddRange(ToFailures(plugins, exceptions, requests, includeUnmatched: !scoped));
    }

    /// <summary>
    /// The rows of the three queries as failures: each failed plug-in with the exception it threw,
    /// each failed request with the exception in its operation, and - for the whole environment -
    /// any exception neither claimed. An exception is told once, with whichever claims it first.
    /// </summary>
    internal static IReadOnlyList<FailureEvent> ToFailures(TelemetryTable plugins, TelemetryTable exceptions, TelemetryTable requests, bool includeUnmatched)
    {
        var events = new List<FailureEvent>();
        var claimed = new HashSet<TelemetryRow>();

        var byParent = exceptions.Rows
            .Where(r => !string.IsNullOrEmpty(r.String("operation_ParentId")))
            .GroupBy(r => r.String("operation_ParentId")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byOperation = exceptions.Rows
            .Where(r => !string.IsNullOrEmpty(r.String("operation_Id")))
            .ToLookup(r => r.String("operation_Id")!, StringComparer.OrdinalIgnoreCase);

        TelemetryRow? Claim(string? id, string? operation)
        {
            var row = id is not null && byParent.TryGetValue(id, out var own) && !claimed.Contains(own)
                ? own
                : operation is null ? null : byOperation[operation].FirstOrDefault(r => !claimed.Contains(r));
            if (row is not null) claimed.Add(row);
            return row;
        }

        var failedOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in plugins.Rows)
        {
            var operation = plugin.String("operation_Id");
            var exception = Claim(plugin.String("id"), operation);
            var typeName = PluginTypeName(plugin.String("name"));
            if (operation is not null) failedOperations.Add(operation);

            events.Add(new FailureEvent
            {
                Source = FailureSource.AppInsights,
                IsPlugin = true,
                ComponentKey = typeName,
                ComponentName = typeName,
                When = plugin.Date("timestamp") ?? DateTimeOffset.MinValue,
                ErrorCode = exception?.String("type") ?? plugin.String("resultCode"),
                ErrorMessage = Message(exception),
                Context = PluginContext(plugin.Properties("customDimensions")) ?? PluginContext(exception?.Properties("customDimensions")),
                OperationId = operation,
                RunName = plugin.String("id"),
                Details = exception is null ? null : Describe(exception)
            });
        }

        foreach (var request in requests.Rows)
        {
            var operation = request.String("operation_Id");

            // The plug-in that failed it is already listed, with its exception.
            if (operation is not null && failedOperations.Contains(operation)) continue;

            var exception = Claim(null, operation);
            var name = request.String("name") ?? "Request";

            events.Add(new FailureEvent
            {
                Source = FailureSource.AppInsights,
                ComponentKey = "request:" + name,
                ComponentName = name,
                When = request.Date("timestamp") ?? DateTimeOffset.MinValue,
                ErrorCode = request.String("resultCode"),
                ErrorMessage = Message(exception) ?? $"{name} returned {request.String("resultCode")}",
                Context = "Request " + request.String("resultCode"),
                Regarding = request.String("url"),
                OperationId = operation,
                RunName = request.String("id"),
                Details = exception is null ? null : Describe(exception)
            });
        }

        if (includeUnmatched)
        {
            foreach (var exception in exceptions.Rows.Where(r => !claimed.Contains(r)))
            {
                var name = exception.String("operation_Name") is { Length: > 0 } op ? op : exception.String("type") ?? "Exception";

                events.Add(new FailureEvent
                {
                    Source = FailureSource.AppInsights,
                    ComponentKey = "exception:" + name,
                    ComponentName = name,
                    When = exception.Date("timestamp") ?? DateTimeOffset.MinValue,
                    ErrorCode = exception.String("type"),
                    ErrorMessage = Message(exception),
                    Context = "Exception",
                    OperationId = exception.String("operation_Id"),
                    RunName = exception.String("problemId"),
                    Details = Describe(exception)
                });
            }
        }

        return events;
    }

    /// <summary>
    /// The plug-in type a dependency names: its class name, before any assembly (after a comma) or
    /// step description (after a colon) the export adds.
    /// </summary>
    internal static string PluginTypeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "(unknown plug-in)";

        var end = name.IndexOfAny([',', ':']);
        return (end > 0 ? name[..end] : name).Trim();
    }

    /// <summary>"Update of account", as the trace log puts it - from whichever dimensions the export used.</summary>
    internal static string? PluginContext(IReadOnlyDictionary<string, string>? dimensions)
    {
        if (dimensions is null) return null;

        var message = First(dimensions, MessageKeys);
        var entity = First(dimensions, EntityKeys);

        return (message, entity) switch
        {
            (null, null) => null,
            (_, null or "none") => message,
            (null, _) => entity,
            _ => $"{message} of {entity}"
        };
    }

    private static string? First(IReadOnlyDictionary<string, string> dimensions, IEnumerable<string> keys) =>
        keys.Select(k => dimensions.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>The outer message, with the innermost after it where that says something more.</summary>
    private static string? Message(TelemetryRow? exception)
    {
        if (exception is null) return null;

        var outer = exception.String("outerMessage");
        var inner = exception.String("innermostMessage");

        if (string.IsNullOrWhiteSpace(outer)) return inner;
        return string.IsNullOrWhiteSpace(inner) || outer.Contains(inner, StringComparison.Ordinal) ? outer : $"{outer}\n{inner}";
    }

    /// <summary>An exception in full, for the details: type, messages, problem id and every dimension.</summary>
    internal static string Describe(TelemetryRow exception)
    {
        var text = new StringBuilder();
        text.AppendLine(exception.String("type") ?? "Exception");
        if (Message(exception) is { } message) text.AppendLine(message);
        if (exception.String("problemId") is { } problem) text.AppendLine().Append("Problem id: ").AppendLine(problem);
        if (exception.String("operation_Id") is { } operation) text.Append("Operation id: ").AppendLine(operation);

        var dimensions = exception.Properties("customDimensions");
        if (dimensions.Count > 0)
        {
            text.AppendLine();
            foreach (var (key, value) in dimensions.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
            {
                text.Append(key).Append(": ").AppendLine(value);
            }
        }

        return text.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- one plug-in

    /// <summary>Executions, failures and timings of the named plug-in types over the period.</summary>
    public static async Task<PluginTelemetrySummary> ReadPluginSummaryAsync(AzureMonitorClient client, string appId, TelemetryScope scope,
        IReadOnlyCollection<string> pluginTypeNames, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        using var table = await client.QueryAsync(appId, PluginSummaryQuery(scope, pluginTypeNames), from, to, ct).ConfigureAwait(false);
        if (table.Rows.FirstOrDefault() is not { } row) return PluginTelemetrySummary.None;

        return new PluginTelemetrySummary(
            (long)(row.Number("Executions") ?? 0),
            (long)(row.Number("Failures") ?? 0),
            row.Number("AverageMs"),
            row.Number("P95Ms"),
            row.Date("Last"));
    }
}
