using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.AzureMonitor;

/// <summary>
/// The Failures window's App Insights source: the environment's chosen resource, read through its
/// profile, beside what Dataverse holds. A resource that cannot be read becomes a note, as a
/// Dataverse source does, rather than an error that loses the rest of the read.
/// </summary>
public static class AppInsightsFailures
{
    public static async Task ReadAsync(AzureMonitorClient client, TelemetryBinding binding, TelemetryScope scope,
        DateTimeOffset from, DateTimeOffset to, FailureQuery query, FailureData data,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (binding.KnownKind is not { } kind || TelemetryProfiles.For(kind) is not { } profile)
        {
            data.Notes.Add($"App Insights: {binding.Name} holds telemetry this version cannot read ({binding.Kind}).");
            return;
        }

        progress?.Report($"Reading App Insights ({binding.Name})...");
        try
        {
            await profile.ReadFailuresAsync(client, binding.AppId, scope with { OnlyThisEnvironment = binding.OnlyThisEnvironment },
                from, to, query, data, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn("App Insights failures could not be read", ex);
            data.Notes.Add($"App Insights ({binding.Name}) could not be read: {ex.Message}{(ex as AzureMonitorException)?.Advice}");
            return;
        }

        var overlaps = FailureOverview.MarkTraceLogOverlaps(data);
        if (overlaps > 0)
        {
            data.Notes.Add($"{overlaps:N0} App Insights plug-in failure{(overlaps == 1 ? " is" : "s are")} also in the trace log; " +
                           "each is counted once while both sources are included.");
        }
    }
}
