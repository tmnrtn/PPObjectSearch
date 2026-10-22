using PPObjectSearch.Models;

namespace PPObjectSearch.AzureMonitor;

/// <summary>
/// Which environment's telemetry to read. One App Insights resource can receive several
/// environments' export, so queries are narrowed to this one's rows unless
/// <see cref="OnlyThisEnvironment"/> is off. How a row is told to be this environment's is the
/// profile's business: the Dataverse export names the environment only in a request's URL.
/// </summary>
public sealed record TelemetryScope(Guid? OrganizationId, string? EnvironmentId, string? Host, bool OnlyThisEnvironment = true)
{
    /// <summary>Whether rows are narrowed at all: asked to, and with a host to narrow by.</summary>
    public bool Narrows => OnlyThisEnvironment && !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// What the connection check found over the last day: rows in the resource, the requests among
/// them, and how many of those were made to this environment.
/// </summary>
public sealed record TelemetryCheck(long Total, long Requests, long Matching)
{
    /// <summary>Whether this environment's telemetry is arriving, as far as the setting reads it.</summary>
    public bool Found(bool onlyThisEnvironment) => onlyThisEnvironment ? Matching > 0 : Total > 0;

    public string Describe(bool onlyThisEnvironment) => this switch
    {
        { Total: 0 } => "Nothing has arrived in this resource in the last day. Is the export switched on and pointed here?",
        _ when !onlyThisEnvironment => $"Receiving telemetry: {Total:N0} rows in the last day, all read as this environment's.",
        { Requests: 0 } =>
            $"{Total:N0} rows arrived in the last day but no requests, and only a request says which environment it was " +
            "made to. If the resource only receives this environment, untick 'Only this environment's telemetry'.",
        { Matching: 0 } =>
            $"None of the {Requests:N0} requests in the last day were made to this environment. Is this environment's " +
            "export pointed here?",
        _ when Matching == Requests =>
            $"Receiving telemetry from this environment: all {Requests:N0} requests in the last day were made to it. " +
            "If no other environment's export comes here, untick to include plug-ins that ran outside a request, such as asynchronous ones.",
        _ => $"Receiving telemetry from this environment: {Matching:N0} of {Requests:N0} requests in the last day were made to it."
    };
}

/// <summary>
/// How one kind of telemetry is read: its tables, dimensions and the KQL that turns them into what
/// the app shows. Adding a kind - Power Automate export, canvas apps - means adding a profile.
/// </summary>
public interface ITelemetryProfile
{
    TelemetryKind Kind { get; }

    /// <summary>A query over the last day: <c>Total</c> rows, <c>Requests</c> among them, and <c>Matching</c> requests to the environment.</summary>
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

    /// <summary>Runs the profile's check over the last day.</summary>
    public static async Task<TelemetryCheck> CheckAsync(AzureMonitorClient client, ITelemetryProfile profile, string appId,
        TelemetryScope scope, DateTimeOffset now, CancellationToken ct = default)
    {
        using var table = await client.QueryAsync(appId, profile.CheckQuery(scope), now.AddDays(-1), now, ct).ConfigureAwait(false);
        var row = table.Rows.FirstOrDefault();
        return new TelemetryCheck((long)(row?.Number("Total") ?? 0), (long)(row?.Number("Requests") ?? 0), (long)(row?.Number("Matching") ?? 0));
    }
}
