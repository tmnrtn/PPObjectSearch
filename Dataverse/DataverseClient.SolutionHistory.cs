using System.Net;
using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    /// <summary>More than any one question about "what happened recently" needs.</summary>
    public const int MaxSolutionHistory = 500;

    /// <summary>
    /// The environment's solution operations, newest first, from msdyn_solutionhistory. It is a
    /// virtual table, so sorting is attempted on the server and done here if it is refused.
    /// </summary>
    public async Task<IReadOnlyList<SolutionHistoryEntry>> GetSolutionHistoryAsync(CancellationToken ct = default)
    {
        const string select =
            "msdyn_solutionhistoryid,msdyn_name,msdyn_solutionversion,msdyn_operation,msdyn_suboperation,msdyn_ismanaged," +
            "msdyn_ispatch,msdyn_isoverwritecustomizations,msdyn_publishername,msdyn_packagename,msdyn_packageversion," +
            "msdyn_starttime,msdyn_endtime,msdyn_totaltime,msdyn_status,msdyn_result,msdyn_errorcode,msdyn_exceptionmessage," +
            "msdyn_exceptionstack,msdyn_retrycount,msdyn_activityid,msdyn_correlationid";

        var baseUrl = EnvironmentUrl + ApiPath + $"msdyn_solutionhistories?$select={select}&$top={MaxSolutionHistory}";

        JsonDocument doc;
        try
        {
            doc = await GetJsonAsync(baseUrl + "&$orderby=msdyn_starttime desc", ct, Annotations.Formatted).ConfigureAwait(false);
        }
        catch (DataverseException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            doc = await GetJsonAsync(baseUrl, ct, Annotations.Formatted).ConfigureAwait(false);
        }

        using (doc)
        {
            var entries = new List<SolutionHistoryEntry>();
            if (!doc.RootElement.TryGetProperty("value", out var rows)) return entries;

            foreach (var row in rows.EnumerateArray())
            {
                if (!Guid.TryParse(JsonHelper.GetString(row, "msdyn_solutionhistoryid"), out var id)) continue;

                string? Label(string name) => JsonHelper.GetString(row, name + "@" + Annotations.Formatted);

                entries.Add(new SolutionHistoryEntry
                {
                    Id = id,
                    SolutionName = JsonHelper.GetString(row, "msdyn_name") ?? "(unnamed)",
                    Version = JsonHelper.GetString(row, "msdyn_solutionversion"),
                    OperationCode = JsonHelper.GetInt(row, "msdyn_operation"),
                    Operation = Label("msdyn_operation") ?? JsonHelper.GetString(row, "msdyn_operation") ?? "Unknown",
                    SubOperation = Label("msdyn_suboperation"),
                    IsManaged = JsonHelper.GetBool(row, "msdyn_ismanaged"),
                    IsPatch = JsonHelper.GetBool(row, "msdyn_ispatch"),
                    OverwriteCustomizations = JsonHelper.GetBool(row, "msdyn_isoverwritecustomizations"),
                    PublisherName = JsonHelper.GetString(row, "msdyn_publishername"),
                    PackageName = JsonHelper.GetString(row, "msdyn_packagename"),
                    PackageVersion = JsonHelper.GetString(row, "msdyn_packageversion"),
                    StartTime = JsonHelper.GetDate(row, "msdyn_starttime"),
                    EndTime = JsonHelper.GetDate(row, "msdyn_endtime"),
                    TotalSeconds = JsonHelper.GetInt(row, "msdyn_totaltime"),
                    StatusCode = JsonHelper.GetInt(row, "msdyn_status"),
                    Succeeded = JsonHelper.GetBool(row, "msdyn_result"),
                    ErrorCode = JsonHelper.GetString(row, "msdyn_errorcode"),
                    ExceptionMessage = JsonHelper.GetString(row, "msdyn_exceptionmessage"),
                    ExceptionStack = JsonHelper.GetString(row, "msdyn_exceptionstack"),
                    RetryCount = JsonHelper.GetInt(row, "msdyn_retrycount"),
                    ActivityId = JsonHelper.GetString(row, "msdyn_activityid"),
                    CorrelationId = JsonHelper.GetString(row, "msdyn_correlationid")
                });
            }

            return entries.OrderByDescending(e => e.StartTime).ToList();
        }
    }
}
