using System.Text.Json;

namespace PPObjectSearch.Dataverse;

/// <summary>A plug-in step as documentation describes it.</summary>
public sealed record PluginStepInfo(
    Guid Id, string Name, string? Message, string? Table, string? Stage, string? Mode, int? Rank, string? FilteringAttributes);

public sealed partial class DataverseClient
{
    /// <summary>Plug-in steps by id: the message, table, stage, mode, rank and filtering attributes of each.</summary>
    public async Task<IReadOnlyList<PluginStepInfo>> GetPluginStepsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var steps = new List<PluginStepInfo>();

        foreach (var chunk in ids.Distinct().Chunk(50))
        {
            var rows = await ReadRowsAsync(
                EnvironmentUrl + ApiPath +
                "sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid,name,stage,mode,rank,filteringattributes,_sdkmessageid_value" +
                "&$expand=sdkmessagefilterid($select=primaryobjecttypecode)&$filter=" +
                InFilter("sdkmessageprocessingstepid", chunk.Select(id => id.ToString())),
                row =>
                {
                    if (!Guid.TryParse(JsonHelper.GetString(row, "sdkmessageprocessingstepid"), out var id)) return null;
                    var filter = row.TryGetProperty("sdkmessagefilterid", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;

                    return new PluginStepInfo(
                        id,
                        JsonHelper.GetString(row, "name") ?? id.ToString(),
                        Label(row, "_sdkmessageid_value"),
                        JsonHelper.GetString(filter, "primaryobjecttypecode"),
                        Label(row, "stage") ?? JsonHelper.GetInt(row, "stage")?.ToString(),
                        Label(row, "mode") ?? JsonHelper.GetInt(row, "mode")?.ToString(),
                        JsonHelper.GetInt(row, "rank"),
                        JsonHelper.GetString(row, "filteringattributes"));
                }, ct).ConfigureAwait(false);

            steps.AddRange(rows);
        }

        return steps;
    }
}
