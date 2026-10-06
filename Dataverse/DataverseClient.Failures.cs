using System.Globalization;
using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    /// <summary>Most rows read per source in one period; past it the result says it is cut short.</summary>
    public const int MaxFailureRows = 5000;

    /// <summary>
    /// The failures of cloud flows, classic workflows and plug-ins between two times. Each source is
    /// read on its own; one that cannot be read is noted in <see cref="FailureData.Notes"/>.
    /// </summary>
    public async Task<FailureData> GetFailuresAsync(
        DateTimeOffset from, DateTimeOffset to, FailureQuery query,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var data = new FailureData();

        async Task Source(string what, Func<Task> read)
        {
            progress?.Report($"Reading {what}...");
            try
            {
                await read().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                data.Notes.Add($"{what} could not be read: {ex.Message}");
            }
        }

        if (query.CloudFlows) await Source("cloud flow runs", () => FlowFailuresAsync(from, to, query, data, ct)).ConfigureAwait(false);
        if (query.ClassicWorkflows) await Source("system jobs", () => ClassicFailuresAsync(from, to, query, data, ct)).ConfigureAwait(false);
        if (query.Plugins) await Source("the plug-in trace log", () => PluginFailuresAsync(from, to, query, data, ct)).ConfigureAwait(false);

        return data;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>Rows of one query, page by page, up to <see cref="MaxFailureRows"/>. True when cut short.</summary>
    private async Task<bool> ReadCappedAsync(string url, Action<JsonElement> row, CancellationToken ct)
    {
        var read = 0;

        while (url.Length > 0)
        {
            using var doc = await GetJsonAsync(url, ct, Annotations.Formatted).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (read++ >= MaxFailureRows) return true;
                    row(item);
                }
            }

            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        return false;
    }

    private async Task FlowFailuresAsync(DateTimeOffset from, DateTimeOffset to, FailureQuery query, FailureData data, CancellationToken ct)
    {
        // Every run in the period is read, not only failures, so each flow has a failure rate. A
        // short scope is asked for by id; a long one is filtered here.
        var filter = $"starttime ge {Iso(from)} and starttime le {Iso(to)}";
        if (query.WorkflowIds is { Count: > 0 and <= 40 } few)
        {
            filter += " and (" + string.Join(" or ", few.Select(id => $"_workflow_value eq {id}")) + ")";
        }

        var failed = new List<(Guid Workflow, JsonElement Row)>();
        var runs = new Dictionary<Guid, int>();

        var truncated = await ReadCappedAsync(
            EnvironmentUrl + ApiPath + "flowruns?$select=name,status,starttime,errorcode,errormessage,workflowid,_workflow_value&$filter=" +
            Uri.EscapeDataString(filter),
            row =>
            {
                data.FlowRunsRead++;
                if (!Guid.TryParse(JsonHelper.GetString(row, "_workflow_value"), out var workflow)) return;
                if (query.WorkflowIds is { } scope && !scope.Contains(workflow)) return;

                runs[workflow] = runs.GetValueOrDefault(workflow) + 1;
                if (FlowOutcome(JsonHelper.GetString(row, "status") ?? string.Empty) == RunOutcome.Failed) failed.Add((workflow, row.Clone()));
            }, ct).ConfigureAwait(false);

        if (truncated) data.Notes.Add($"Cloud flows: only the first {MaxFailureRows:N0} runs in the period were read.");

        var names = await GetWorkflowNamesAsync(runs.Keys, ct).ConfigureAwait(false);
        foreach (var (workflow, count) in runs) data.RunCounts[workflow.ToString()] = count;

        foreach (var (workflow, row) in failed)
        {
            data.Events.Add(new FailureEvent
            {
                Source = FailureSource.CloudFlow,
                ComponentKey = workflow.ToString(),
                ComponentName = names.TryGetValue(workflow, out var name) ? name : $"Flow {workflow.ToString()[..8]}",
                When = JsonHelper.GetDate(row, "starttime") ?? from,
                ErrorCode = JsonHelper.GetString(row, "errorcode"),
                ErrorMessage = JsonHelper.GetString(row, "errormessage"),
                RunName = JsonHelper.GetString(row, "name"),
                WorkflowId = workflow
            });
        }
    }

    private async Task ClassicFailuresAsync(DateTimeOffset from, DateTimeOffset to, FailureQuery query, FailureData data, CancellationToken ct)
    {
        // Operation type 10 is a workflow; status 31 is Failed.
        var filter = $"operationtype eq 10 and statuscode eq 31 and completedon ge {Iso(from)} and completedon le {Iso(to)}";
        var jobs = new List<JsonElement>();

        var truncated = await ReadCappedAsync(
            EnvironmentUrl + ApiPath +
            "asyncoperations?$select=name,completedon,message,friendlymessage,errorcode,_workflowactivationid_value,_regardingobjectid_value" +
            "&$filter=" + Uri.EscapeDataString(filter) + "&$orderby=completedon desc",
            row => jobs.Add(row.Clone()), ct).ConfigureAwait(false);

        if (truncated) data.Notes.Add($"Classic workflows: only the newest {MaxFailureRows:N0} failed jobs were read.");
        data.SystemJobsRead = jobs.Count;

        // A job names the activation that ran; the definition it belongs to is its parent.
        var activations = jobs.Select(j => ParseGuid(JsonHelper.GetString(j, "_workflowactivationid_value")))
            .OfType<Guid>().Distinct().ToList();
        var parents = new Dictionary<Guid, (Guid Id, string? Name)>();

        foreach (var chunk in activations.Chunk(50))
        {
            using var doc = await GetJsonAsync(
                EnvironmentUrl + ApiPath + "workflows?$select=workflowid,name,_parentworkflowid_value&$filter=" +
                InFilter("workflowid", chunk.Select(id => id.ToString())), ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("value", out var value)) continue;

            foreach (var row in value.EnumerateArray())
            {
                if (!Guid.TryParse(JsonHelper.GetString(row, "workflowid"), out var id)) continue;
                parents[id] = (ParseGuid(JsonHelper.GetString(row, "_parentworkflowid_value")) ?? id, JsonHelper.GetString(row, "name"));
            }
        }

        foreach (var job in jobs)
        {
            var activation = ParseGuid(JsonHelper.GetString(job, "_workflowactivationid_value"));
            var (workflow, name) = activation is { } a && parents.TryGetValue(a, out var parent) ? parent : (activation ?? Guid.Empty, null);
            if (query.WorkflowIds is { } scope && !scope.Contains(workflow)) continue;

            var friendly = JsonHelper.GetString(job, "friendlymessage");
            data.Events.Add(new FailureEvent
            {
                Source = FailureSource.ClassicWorkflow,
                ComponentKey = workflow.ToString(),
                ComponentName = name ?? JsonHelper.GetString(job, "name") ?? "System job",
                When = JsonHelper.GetDate(job, "completedon") ?? from,
                ErrorCode = JsonHelper.GetInt(job, "errorcode")?.ToString(CultureInfo.InvariantCulture) ?? JsonHelper.GetString(job, "errorcode"),
                ErrorMessage = string.IsNullOrWhiteSpace(friendly) ? JsonHelper.GetString(job, "message") : friendly,
                RunName = JsonHelper.GetString(job, "name"),
                Regarding = Label(job, "_regardingobjectid_value"),
                WorkflowId = workflow == Guid.Empty ? null : workflow
            });
        }
    }

    private async Task PluginFailuresAsync(DateTimeOffset from, DateTimeOffset to, FailureQuery query, FailureData data, CancellationToken ct)
    {
        if (await GetPluginTraceSettingAsync(ct).ConfigureAwait(false) == PluginTraceSetting.Off)
        {
            data.Notes.Add("Plug-in tracing is off in this environment, so only failures traced before it was turned off appear.");
        }

        var filter = $"createdon ge {Iso(from)} and createdon le {Iso(to)} and exceptiondetails ne null";
        var scoped = query.PluginTypeNames is not null || query.PluginStepIds is not null;

        var truncated = await ReadCappedAsync(
            EnvironmentUrl + ApiPath +
            "plugintracelogs?$select=plugintracelogid,createdon,typename,messagename,primaryentity,exceptiondetails,pluginstepid" +
            "&$filter=" + Uri.EscapeDataString(filter) + "&$orderby=createdon desc",
            row =>
            {
                data.TraceLogsRead++;
                var exception = JsonHelper.GetString(row, "exceptiondetails");
                if (string.IsNullOrWhiteSpace(exception)) return;

                var typeName = JsonHelper.GetString(row, "typename") ?? "(unknown plug-in)";
                var step = ParseGuid(JsonHelper.GetString(row, "pluginstepid"));

                if (scoped &&
                    !(query.PluginTypeNames?.Contains(typeName) ?? false) &&
                    !(step is { } s && (query.PluginStepIds?.Contains(s) ?? false)))
                {
                    return;
                }

                var message = JsonHelper.GetString(row, "messagename");
                var table = JsonHelper.GetString(row, "primaryentity");

                data.Events.Add(new FailureEvent
                {
                    Source = FailureSource.Plugin,
                    ComponentKey = typeName,
                    ComponentName = typeName,
                    When = JsonHelper.GetDate(row, "createdon") ?? from,
                    ErrorMessage = ExceptionMessage(exception!),
                    RunName = JsonHelper.GetString(row, "plugintracelogid"),
                    Context = string.IsNullOrWhiteSpace(table) || table == "none" ? message : $"{message} of {table}",
                    StepId = step
                });
            }, ct).ConfigureAwait(false);

        if (truncated) data.Notes.Add($"Plug-ins: only the newest {MaxFailureRows:N0} traced exceptions were read.");
    }

    /// <summary>
    /// The readable part of a trace's exception details - the line after "Message:" where the trace
    /// has one, otherwise its first non-empty line.
    /// </summary>
    internal static string ExceptionMessage(string details)
    {
        var lines = details.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var message = lines.FirstOrDefault(l => l.StartsWith("Message:", StringComparison.OrdinalIgnoreCase));
        if (message is not null && message.Length > "Message:".Length) return message["Message:".Length..].Trim();

        return lines.FirstOrDefault(l => !l.StartsWith("Unhandled exception", StringComparison.OrdinalIgnoreCase))
               ?? lines.FirstOrDefault()
               ?? details.Trim();
    }
}
