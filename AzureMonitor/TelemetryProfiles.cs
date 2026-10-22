using PPObjectSearch.Models;

namespace PPObjectSearch.AzureMonitor;

/// <summary>
/// Which environment's telemetry to read. One App Insights resource can receive several
/// environments' export, so queries are narrowed to rows that name this one - by organization id,
/// environment id or host - unless <see cref="OnlyThisEnvironment"/> is off.
/// </summary>
public sealed record TelemetryScope(Guid? OrganizationId, string? EnvironmentId, string? Host, bool OnlyThisEnvironment = true)
{
    /// <summary>The values a row is matched on, most specific first.</summary>
    public IReadOnlyList<string> Terms =>
        new[] { OrganizationId is { } org && org != Guid.Empty ? org.ToString() : null, EnvironmentId, Host }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>What the connection check found: rows in the resource, and how many were this environment's.</summary>
public sealed record TelemetryCheck(long Total, long Matching)
{
    public string Describe(bool onlyThisEnvironment) => (Total, Matching) switch
    {
        (0, _) => "Nothing has arrived in this resource in the last day. Is the export switched on and pointed here?",
        (_, 0) when onlyThisEnvironment =>
            $"{Total:N0} rows arrived in the last day, but none mention this environment. Is this environment's export " +
            "pointed here? If the resource only receives this environment, untick 'Only this environment's telemetry'.",
        _ when !onlyThisEnvironment => $"Receiving telemetry: {Total:N0} rows in the last day, all read as this environment's.",
        _ => $"Receiving telemetry from this environment: {Matching:N0} of {Total:N0} rows in the last day."
    };
}

/// <summary>
/// How one kind of telemetry is read: its tables, dimensions and the KQL that turns them into what
/// the app shows. Adding a kind - Power Automate export, canvas apps - means adding a profile.
/// </summary>
public interface ITelemetryProfile
{
    TelemetryKind Kind { get; }

    /// <summary>A query over the last day: <c>Total</c> rows, and <c>Matching</c> rows that name the environment.</summary>
    string CheckQuery(TelemetryScope scope);

    /// <summary>
    /// The period's failures, added to <paramref name="data"/>. Anything that cannot be read throws;
    /// the caller turns it into a note.
    /// </summary>
    Task ReadFailuresAsync(AzureMonitorClient client, string appId, TelemetryScope scope,
        DateTimeOffset from, DateTimeOffset to, FailureQuery query, FailureData data, CancellationToken ct);
}

public static class TelemetryProfiles
{
    /// <summary>The profile for a kind, or null for one not read yet.</summary>
    public static ITelemetryProfile? For(TelemetryKind kind) => kind switch
    {
        TelemetryKind.DataverseExport => DataverseExportProfile.Instance,
        // Power Automate export, canvas apps and custom telemetry are planned; each gets a profile.
        _ => null
    };

    /// <summary>
    /// A <c>where</c> clause that keeps rows naming the environment, or nothing when the scope does
    /// not narrow. The columns are the ones every App Insights table has; the values are matched
    /// as text anywhere in them, because which dimension the export puts them in is not fixed.
    /// </summary>
    public static string EnvironmentFilter(TelemetryScope scope) =>
        !scope.OnlyThisEnvironment || scope.Terms.Count == 0
            ? string.Empty
            : $"| where {MatchExpression(scope)}\n";

    /// <summary>True for a row that names the environment, for a filter or a <c>countif</c>.</summary>
    public static string MatchExpression(TelemetryScope scope)
    {
        if (scope.Terms.Count == 0) return "true";

        return string.Join(" or ", scope.Terms.SelectMany(term => new[]
        {
            $"tostring(customDimensions) contains {Kql.Literal(term)}",
            $"cloud_RoleInstance contains {Kql.Literal(term)}",
            $"cloud_RoleName contains {Kql.Literal(term)}"
        }));
    }

    /// <summary>Runs the profile's check over the last day.</summary>
    public static async Task<TelemetryCheck> CheckAsync(AzureMonitorClient client, ITelemetryProfile profile, string appId,
        TelemetryScope scope, DateTimeOffset now, CancellationToken ct = default)
    {
        using var table = await client.QueryAsync(appId, profile.CheckQuery(scope), now.AddDays(-1), now, ct).ConfigureAwait(false);
        var row = table.Rows.FirstOrDefault();
        return new TelemetryCheck((long)(row?.Number("Total") ?? 0), (long)(row?.Number("Matching") ?? 0));
    }
}
