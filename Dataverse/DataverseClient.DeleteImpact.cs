using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>What deleting a row of one table does to the rows of another that point at it.</summary>
public sealed record DeleteBehaviour(string ReferencingEntity, string ReferencingAttribute, string Behaviour)
{
    /// <summary>The child rows are deleted too.</summary>
    public bool Cascades => Behaviour == "Cascade";

    /// <summary>The delete is refused while any child row points at the parent.</summary>
    public bool Restricts => Behaviour == "Restrict";

    /// <summary>The child rows stay, with the reference cleared.</summary>
    public bool RemovesLink => Behaviour == "RemoveLink";
}

public sealed partial class DataverseClient
{
    /// <summary>
    /// The one-to-many relationships whose delete behaviour reaches past the row being deleted:
    /// cascading, restricting or unlinking. The rest (no cascade) change nothing elsewhere.
    /// </summary>
    public async Task<IReadOnlyList<DeleteBehaviour>> GetDeleteBehavioursAsync(
        string logicalName, CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  $"EntityDefinitions(LogicalName='{Uri.EscapeDataString(logicalName.ToLowerInvariant())}')" +
                  "/OneToManyRelationships?$select=ReferencingEntity,ReferencingAttribute,CascadeConfiguration";

        var results = new List<DeleteBehaviour>();

        while (url.Length > 0)
        {
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    var entity = JsonHelper.GetString(row, "ReferencingEntity");
                    var attribute = JsonHelper.GetString(row, "ReferencingAttribute");
                    var delete = ReadNested(row, "CascadeConfiguration", "Delete");

                    if (string.IsNullOrWhiteSpace(entity) || string.IsNullOrWhiteSpace(attribute)) continue;
                    if (delete is not ("Cascade" or "Restrict" or "RemoveLink")) continue;

                    results.Add(new DeleteBehaviour(entity, attribute, delete));
                }
            }

            // GetJsonAsync refuses a nextLink on any other host.
            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        return results;
    }

    /// <summary>
    /// How many rows of a table point at any of the given rows through one lookup. Dataverse stops
    /// counting at 5,000, so a result of 5,000 means "at least".
    /// </summary>
    public async Task<int> CountReferencingAsync(
        EntitySummary referencing, string attribute, IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;

        if (string.IsNullOrWhiteSpace(referencing.EntitySetName))
        {
            throw new DataverseException($"{referencing.LogicalName} has no entity set name, so its rows cannot be counted.");
        }

        var total = 0;

        // Small enough that the filter stays within a GET URL rather than going through $batch.
        foreach (var chunk in ids.Chunk(20))
        {
            var filter = string.Join(" or ", chunk.Select(id => $"_{attribute}_value eq {id}"));
            var url = EnvironmentUrl + ApiPath +
                      $"{referencing.EntitySetName}?$select={referencing.PrimaryIdAttribute}&$filter={Uri.EscapeDataString(filter)}" +
                      "&$count=true&$top=1";

            using var doc = await GetJsonAsync(url, ct, maxPageSize: false).ConfigureAwait(false);
            total += JsonHelper.GetInt(doc.RootElement, "@odata.count") ?? 0;
        }

        return total;
    }
}
