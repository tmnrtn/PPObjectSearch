using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>
/// What an object has actually been doing: a cloud flow's runs, a classic workflow's system jobs,
/// and the plug-in trace log for an assembly, a plug-in type or a registration step. All of it is
/// ordinary Dataverse data, so it needs nothing beyond the connection the tab already has.
/// </summary>
public sealed partial class DataverseClient
{
    /// <summary>Enough to see a pattern; the maker portal is the place for anything older.</summary>
    public const int MaxRunHistory = 100;

    private const string SelectFlowRun =
        "name,status,starttime,endtime,duration,triggertype,errorcode,errormessage,workflowid";

    /// <summary>
    /// A cloud flow's recent runs, newest first. Dataverse keeps these in the flowrun table - an
    /// elastic table, for 28 days by default - and only once flow run history in Dataverse is on.
    /// </summary>
    /// <summary>
    /// A client for the Power Automate API, signed in as this environment's account - for a cloud
    /// flow run step by step, which the flowrun table does not record.
    /// </summary>
    public PowerAutomate.PowerAutomateClient CreatePowerAutomateClient() => new(_auth);

    public async Task<IReadOnlyList<ProcessRun>> GetCloudFlowRunsAsync(Guid workflowId, CancellationToken ct = default)
    {
        var baseUrl = EnvironmentUrl + ApiPath +
                      $"flowruns?$select={SelectFlowRun}&$filter=_workflow_value eq {workflowId}&$top={MaxRunHistory}";

        JsonDocument doc;
        try
        {
            doc = await GetJsonAsync(baseUrl + "&$orderby=starttime desc", ct).ConfigureAwait(false);
        }
        catch (DataverseException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Elastic tables are pickier about sorting than standard ones; sort here instead.
            doc = await GetJsonAsync(baseUrl, ct).ConfigureAwait(false);
        }

        using (doc)
        {
            var runs = new List<ProcessRun>();
            if (!doc.RootElement.TryGetProperty("value", out var value)) return runs;

            foreach (var row in value.EnumerateArray())
            {
                var status = JsonHelper.GetString(row, "status") ?? "Unknown";

                runs.Add(new ProcessRun
                {
                    Name = JsonHelper.GetString(row, "name") ?? string.Empty,
                    Status = status,
                    Outcome = FlowOutcome(status),
                    StartTime = JsonHelper.GetDate(row, "starttime"),
                    EndTime = JsonHelper.GetDate(row, "endtime"),
                    DurationMs = GetLong(row, "duration"),
                    TriggerType = JsonHelper.GetString(row, "triggertype"),
                    ErrorCode = JsonHelper.GetString(row, "errorcode"),
                    ErrorMessage = JsonHelper.GetString(row, "errormessage"),
                    FlowId = JsonHelper.GetString(row, "workflowid")
                });
            }

            return runs.OrderByDescending(r => r.StartTime).ToList();
        }
    }

    /// <summary>
    /// A classic background workflow's recent system jobs, newest first. The jobs point at the
    /// workflow's activation records rather than the definition in the solution, so those are
    /// found first.
    /// </summary>
    public async Task<IReadOnlyList<ProcessRun>> GetClassicWorkflowRunsAsync(Guid workflowId, CancellationToken ct = default)
    {
        var ids = new List<Guid> { workflowId };

        using (var activations = await GetJsonAsync(
                   EnvironmentUrl + ApiPath +
                   $"workflows?$select=workflowid&$filter=_parentworkflowid_value eq {workflowId}", ct).ConfigureAwait(false))
        {
            if (activations.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    if (Guid.TryParse(JsonHelper.GetString(row, "workflowid"), out var id)) ids.Add(id);
                }
            }
        }

        var filter = string.Join(" or ", ids.Distinct().Take(40).Select(id => $"_workflowactivationid_value eq {id}"));
        var url = EnvironmentUrl + ApiPath +
                  "asyncoperations?$select=name,statuscode,startedon,completedon,createdon,message,friendlymessage,errorcode," +
                  "_regardingobjectid_value" +
                  $"&$filter={filter}&$orderby=createdon desc&$top={MaxRunHistory}";

        using var doc = await GetJsonAsync(url, ct, Annotations.Formatted).ConfigureAwait(false);

        var runs = new List<ProcessRun>();
        if (!doc.RootElement.TryGetProperty("value", out var jobs)) return runs;

        foreach (var row in jobs.EnumerateArray())
        {
            var code = JsonHelper.GetInt(row, "statuscode");
            var started = JsonHelper.GetDate(row, "startedon") ?? JsonHelper.GetDate(row, "createdon");
            var completed = JsonHelper.GetDate(row, "completedon");
            var friendly = JsonHelper.GetString(row, "friendlymessage");
            var message = JsonHelper.GetString(row, "message");

            runs.Add(new ProcessRun
            {
                Name = JsonHelper.GetString(row, "name") ?? string.Empty,
                Status = JsonHelper.GetString(row, "statuscode@" + Annotations.Formatted) ?? code?.ToString() ?? "Unknown",
                Outcome = code switch
                {
                    30 => RunOutcome.Succeeded,
                    31 => RunOutcome.Failed,
                    32 => RunOutcome.Cancelled,
                    0 or 10 or 20 or 21 or 22 => RunOutcome.Running,
                    _ => RunOutcome.Other
                },
                StartTime = started,
                EndTime = completed,
                DurationMs = started is { } s && completed is { } c ? (long)(c - s).TotalMilliseconds : null,
                TriggerType = "System job",
                ErrorCode = JsonHelper.GetString(row, "errorcode"),
                ErrorMessage = string.IsNullOrWhiteSpace(friendly) ? message : friendly,
                Regarding = JsonHelper.GetString(row, "_regardingobjectid_value@" + Annotations.Formatted)
            });
        }

        return runs;
    }

    /// <summary>Whether plug-ins write to the trace log at all: off, exceptions only, or everything.</summary>
    public async Task<PluginTraceSetting?> GetPluginTraceSettingAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            EnvironmentUrl + ApiPath + "organizations?$select=plugintracelogsetting", ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("value", out var value)) return null;

        foreach (var row in value.EnumerateArray())
        {
            if (JsonHelper.GetInt(row, "plugintracelogsetting") is { } setting) return (PluginTraceSetting)setting;
        }

        return null;
    }

    /// <summary>
    /// Trace log entries for a plug-in assembly, plug-in type or registration step, newest first.
    /// A step is matched on its own id; a type on its class name; an assembly on the class names
    /// of every type it holds. Null for anything that is not one of those.
    /// </summary>
    public async Task<IReadOnlyList<PluginTraceEntry>?> GetPluginTraceLogAsync(
        Guid objectId, int componentType, CancellationToken ct = default)
    {
        string? filter;

        switch (componentType)
        {
            case 92: // SDK message processing step
                filter = $"pluginstepid eq {objectId}";
                break;

            case 90: // plug-in type
            {
                using var type = await GetJsonAsync(
                    EnvironmentUrl + ApiPath + $"plugintypes({objectId})?$select=typename", ct).ConfigureAwait(false);
                var name = JsonHelper.GetString(type.RootElement, "typename");
                filter = string.IsNullOrWhiteSpace(name) ? null : $"typename eq '{Escape(name)}'";
                break;
            }

            case 91: // plug-in assembly
            {
                using var types = await GetJsonAsync(
                    EnvironmentUrl + ApiPath +
                    $"plugintypes?$select=typename&$filter=_pluginassemblyid_value eq {objectId}", ct).ConfigureAwait(false);

                var names = types.RootElement.TryGetProperty("value", out var value)
                    ? value.EnumerateArray()
                        .Select(row => JsonHelper.GetString(row, "typename"))
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(40)
                        .ToList()
                    : new List<string?>();

                if (names.Count == 0) return Array.Empty<PluginTraceEntry>();

                filter = string.Join(" or ", names.Select(n => $"typename eq '{Escape(n!)}'"));
                break;
            }

            default:
                return null;
        }

        if (filter is null) return Array.Empty<PluginTraceEntry>();

        var url = EnvironmentUrl + ApiPath +
                  "plugintracelogs?$select=plugintracelogid,createdon,typename,messagename,primaryentity,mode,depth," +
                  "performanceexecutionduration,messageblock,exceptiondetails,correlationid" +
                  $"&$filter={filter}&$orderby=createdon desc&$top={MaxRunHistory}";

        using var doc = await GetJsonAsync(url, ct, Annotations.Formatted).ConfigureAwait(false);

        var entries = new List<PluginTraceEntry>();
        if (!doc.RootElement.TryGetProperty("value", out var rows)) return entries;

        foreach (var row in rows.EnumerateArray())
        {
            if (!Guid.TryParse(JsonHelper.GetString(row, "plugintracelogid"), out var id)) continue;

            entries.Add(new PluginTraceEntry
            {
                Id = id,
                CreatedOn = JsonHelper.GetDate(row, "createdon"),
                TypeName = JsonHelper.GetString(row, "typename") ?? string.Empty,
                MessageName = JsonHelper.GetString(row, "messagename"),
                PrimaryEntity = JsonHelper.GetString(row, "primaryentity"),
                Mode = JsonHelper.GetString(row, "mode@" + Annotations.Formatted),
                Depth = JsonHelper.GetInt(row, "depth"),
                DurationMs = GetLong(row, "performanceexecutionduration"),
                MessageBlock = JsonHelper.GetString(row, "messageblock"),
                ExceptionDetails = JsonHelper.GetString(row, "exceptiondetails"),
                CorrelationId = Guid.TryParse(JsonHelper.GetString(row, "correlationid"), out var correlation)
                    ? correlation
                    : null
            });
        }

        return entries;
    }

    private static RunOutcome FlowOutcome(string status) => status.Trim().ToLowerInvariant() switch
    {
        "succeeded" => RunOutcome.Succeeded,
        "failed" or "timedout" => RunOutcome.Failed,
        "running" or "waiting" => RunOutcome.Running,
        "cancelled" or "canceled" => RunOutcome.Cancelled,
        _ => RunOutcome.Other
    };

    private static long? GetLong(JsonElement row, string name) =>
        long.TryParse(JsonHelper.GetString(row, name), out var value) ? value : null;

    /// <summary>OData string literals double their single quotes.</summary>
    private static string Escape(string value) => Uri.EscapeDataString(value.Replace("'", "''"));
}
