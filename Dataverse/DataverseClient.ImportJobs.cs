using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>An import job: one solution import, its progress, and (separately) its log.</summary>
public sealed record ImportJobInfo(
    Guid Id, string? SolutionName, double? Progress, DateTimeOffset? StartedOn, DateTimeOffset? CompletedOn, DateTimeOffset? CreatedOn)
{
    public bool IsFinished => CompletedOn is not null || Progress >= 100;
}

public sealed partial class DataverseClient
{
    /// <summary>A solution's import jobs, newest first. The log itself is read separately - it can be large.</summary>
    public async Task<IReadOnlyList<ImportJobInfo>> GetImportJobsAsync(string solutionName, CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  "importjobs?$select=importjobid,solutionname,progress,startedon,completedon,createdon" +
                  $"&$filter=solutionname eq '{Escape(solutionName)}'&$orderby=createdon desc&$top=50";

        return await ReadRowsAsync(url, row =>
            Guid.TryParse(JsonHelper.GetString(row, "importjobid"), out var id)
                ? new ImportJobInfo(id, JsonHelper.GetString(row, "solutionname"),
                    row.TryGetProperty("progress", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetDouble() : null,
                    JsonHelper.GetDate(row, "startedon"), JsonHelper.GetDate(row, "completedon"), JsonHelper.GetDate(row, "createdon"))
                : null, ct).ConfigureAwait(false);
    }

    /// <summary>The job's progress now - for following an import that is still running.</summary>
    public async Task<ImportJobInfo?> GetImportJobAsync(Guid id, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            EnvironmentUrl + ApiPath + $"importjobs({id})?$select=importjobid,solutionname,progress,startedon,completedon,createdon",
            ct, maxPageSize: false).ConfigureAwait(false);
        var row = doc.RootElement;

        return new ImportJobInfo(id, JsonHelper.GetString(row, "solutionname"),
            row.TryGetProperty("progress", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetDouble() : null,
            JsonHelper.GetDate(row, "startedon"), JsonHelper.GetDate(row, "completedon"), JsonHelper.GetDate(row, "createdon"));
    }

    /// <summary>The import log: importjob.data, the XML the maker portal offers as a download.</summary>
    public async Task<string?> GetImportJobDataAsync(Guid id, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(EnvironmentUrl + ApiPath + $"importjobs({id})?$select=data", ct, maxPageSize: false)
            .ConfigureAwait(false);
        return JsonHelper.GetString(doc.RootElement, "data");
    }

    /// <summary>
    /// The import job behind a solution history row: same solution, started closest to the
    /// operation. Null when none is within an hour of it.
    /// </summary>
    public static ImportJobInfo? MatchImportJob(IEnumerable<ImportJobInfo> jobs, SolutionHistoryEntry entry)
    {
        if (entry.StartTime is not { } started) return jobs.FirstOrDefault();

        return jobs
            .Select(j => (Job: j, Gap: ((j.StartedOn ?? j.CreatedOn) is { } at ? (at - started).Duration() : TimeSpan.MaxValue)))
            .Where(x => x.Gap <= TimeSpan.FromHours(1))
            .OrderBy(x => x.Gap)
            .Select(x => x.Job)
            .FirstOrDefault();
    }
}
