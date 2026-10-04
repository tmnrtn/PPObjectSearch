using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    /// <summary>Most a timeline reads; past it the window says the list is cut short.</summary>
    public const int MaxRecentChanges = 5000;

    /// <summary>
    /// Components modified since a point in time, newest first - read from the default solution,
    /// which holds every component in the environment. The summary row carries the modified date
    /// but not who made the change.
    /// </summary>
    public async Task<ComponentList> GetRecentlyChangedAsync(
        Guid defaultSolutionId, DateTimeOffset since, CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath + "msdyn_solutioncomponentsummaries?$filter=" +
                  Uri.EscapeDataString($"(msdyn_solutionid eq {defaultSolutionId}) and msdyn_modifiedon ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}") +
                  "&$orderby=msdyn_modifiedon desc";

        var items = new List<SolutionComponentItem>();

        while (url.Length > 0 && items.Count < MaxRecentChanges)
        {
            using var doc = await GetJsonAsync(url, ct, Annotations.Formatted).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray()) items.Add(ReadComponent(row));
            }

            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        // The filter is applied again here in case the virtual table ignored it.
        var recent = new ComponentList { IsTruncated = url.Length > 0 };
        recent.AddRange(items.Where(i => i.ModifiedOn is null || i.ModifiedOn >= since));
        return recent;
    }

    private static readonly Dictionary<int, (string Set, string Id)> ModifiedBySources = new()
    {
        [29] = ("workflows", "workflowid"),
        [61] = ("webresourceset", "webresourceid"),
        [60] = ("systemforms", "formid"),
        [26] = ("savedqueries", "savedqueryid"),
        [92] = ("sdkmessageprocessingsteps", "sdkmessageprocessingstepid"),
        [380] = ("environmentvariabledefinitions", "environmentvariabledefinitionid"),
        [381] = ("environmentvariablevalues", "environmentvariablevalueid")
    };

    /// <summary>
    /// Who last modified each component, for the types whose table records it. Best effort per
    /// type: a type that cannot be read is simply left without names.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetModifiedByAsync(
        IEnumerable<SolutionComponentItem> items, CancellationToken ct = default)
    {
        var names = new Dictionary<Guid, string>();

        foreach (var group in items.Where(i => i.ObjectId != Guid.Empty && ModifiedBySources.ContainsKey(i.ComponentType))
                                   .GroupBy(i => i.ComponentType))
        {
            var (set, idColumn) = ModifiedBySources[group.Key];

            try
            {
                foreach (var chunk in group.Select(i => i.ObjectId).Distinct().Chunk(50))
                {
                    var rows = await ReadRowsAsync(
                        EnvironmentUrl + ApiPath + $"{set}?$select={idColumn},_modifiedby_value&$filter=" +
                        InFilter(idColumn, chunk.Select(id => id.ToString())),
                        row => Guid.TryParse(JsonHelper.GetString(row, idColumn), out var id) && Label(row, "_modifiedby_value") is { } by
                            ? Tuple.Create(id, by)
                            : null,
                        ct).ConfigureAwait(false);

                    foreach (var (id, by) in rows) names[id] = by;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Services.Log.Warn($"Modified-by could not be read for component type {group.Key}", ex);
            }
        }

        return names;
    }
}
