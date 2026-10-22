using System.Text;

namespace PPObjectSearch.AzureMonitor;

/// <summary>One thing the access check tried: what, whether it worked, and what came back.</summary>
public sealed record AzureProbeResult(string Check, bool Ok, string Detail)
{
    public string Line => $"{(Ok ? "OK" : "FAILED")} - {Check}: {Detail}";
}

/// <summary>
/// "Can this tab reach the environment's telemetry?" Asks for a token for each Azure API the
/// App Insights features use and makes one cheap call with it, so a refusal is told apart from a
/// missing role before anything relies on either. Read-only, and safe to run at any time.
/// </summary>
public static class AzureAccessProbe
{
    public const string ResourceManagerCheck = "Azure Resource Manager";
    public const string AppInsightsCheck = "Application Insights query API";
    public const string LogAnalyticsCheck = "Log Analytics query API";

    /// <summary>
    /// Runs every check. <paramref name="appId"/> is the chosen resource's app id; without one the
    /// first resource found with an app id is queried instead, so a first run tests a query too.
    /// </summary>
    public static async Task<IReadOnlyList<AzureProbeResult>> RunAsync(
        AzureMonitorClient client, string? appId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<AzureProbeResult>();
        string? queryTarget = appId;
        string? queryTargetName = appId is null ? null : "the chosen resource";

        progress?.Report("Checking Azure Resource Manager...");
        try
        {
            var subscriptions = await client.ListSubscriptionsAsync(ct).ConfigureAwait(false);
            results.Add(new(ResourceManagerCheck, true,
                $"{subscriptions.Count:N0} subscription{(subscriptions.Count == 1 ? string.Empty : "s")} visible"));

            if (subscriptions.Count > 0)
            {
                progress?.Report("Listing Application Insights resources...");
                var (components, notes) = await client.ListAllComponentsAsync(progress, ct).ConfigureAwait(false);
                var detail = $"{components.Count:N0} resource{(components.Count == 1 ? string.Empty : "s")} found";
                if (notes.Count > 0) detail += $"; {string.Join("; ", notes)}";
                results.Add(new("Application Insights resources", true, detail));

                if (queryTarget is null && components.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.AppId)) is { } first)
                {
                    queryTarget = first.AppId;
                    queryTargetName = first.Name;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AzureMonitorException ex)
        {
            results.Add(new(ResourceManagerCheck, false, ex.Message));
        }

        progress?.Report("Checking the Application Insights query API...");
        results.Add(await TokenCheckAsync(client, AppInsightsCheck, client.Azure.AppInsightsApi, ct).ConfigureAwait(false));

        if (results[^1].Ok && queryTarget is not null)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                using var table = await client.QueryAsync(queryTarget, "print ok = 1", now.AddHours(-1), now, ct).ConfigureAwait(false);
                results.Add(new("Query", true, $"ran against {queryTargetName}"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AzureMonitorException ex)
            {
                results.Add(new("Query", false, $"against {queryTargetName}: {ex.Message}"));
            }
        }

        progress?.Report("Checking the Log Analytics query API...");
        results.Add(await TokenCheckAsync(client, LogAnalyticsCheck, client.Azure.LogAnalyticsApi, ct).ConfigureAwait(false));

        return results;
    }

    private static async Task<AzureProbeResult> TokenCheckAsync(AzureMonitorClient client, string check, string resource, CancellationToken ct)
    {
        try
        {
            await client.GetTokenAsync(resource, ct).ConfigureAwait(false);
            return new(check, true, "token issued");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AzureMonitorException ex)
        {
            return new(check, false, ex.Message);
        }
    }

    /// <summary>The results as plain text, for the clipboard and the log.</summary>
    public static string ToText(string environment, string signInClient, IEnumerable<AzureProbeResult> results)
    {
        var text = new StringBuilder()
            .AppendLine($"Azure access check for {environment}")
            .AppendLine($"Sign-in client: {signInClient}");

        foreach (var result in results) text.AppendLine(result.Line);
        return text.ToString();
    }
}
