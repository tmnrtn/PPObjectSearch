using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    /// <summary>
    /// A table's columns and the relationships it starts, for an entity relationship diagram: its
    /// lookups (many-to-one, so a one-to-many is read from its many end) and its many-to-many
    /// relationships. Null when the table cannot be found by metadata id or logical name.
    /// </summary>
    public async Task<ErdTable?> GetErdTableAsync(Guid metadataId, string? logicalNameHint, CancellationToken ct = default)
    {
        const string select = "?$select=MetadataId,LogicalName,DisplayName,IsCustomEntity,IsIntersect";

        var keys = new List<string>();
        if (metadataId != Guid.Empty) keys.Add($"EntityDefinitions({metadataId})");
        if (!string.IsNullOrWhiteSpace(logicalNameHint))
        {
            keys.Add($"EntityDefinitions(LogicalName='{Uri.EscapeDataString(logicalNameHint.ToLowerInvariant())}')");
        }

        for (var i = 0; i < keys.Count; i++)
        {
            string? logicalName;
            string? displayName;
            bool isCustom, isIntersect;

            try
            {
                using var doc = await GetJsonAsync(EnvironmentUrl + ApiPath + keys[i] + select, ct).ConfigureAwait(false);
                var row = doc.RootElement;
                logicalName = JsonHelper.GetString(row, "LogicalName");
                displayName = ReadLabel(row, "DisplayName");
                isCustom = JsonHelper.GetBool(row, "IsCustomEntity") ?? false;
                isIntersect = JsonHelper.GetBool(row, "IsIntersect") ?? false;
            }
            catch (DataverseException) when (i + 1 < keys.Count)
            {
                // Try the next way of naming the table before giving up on it.
                continue;
            }

            if (string.IsNullOrWhiteSpace(logicalName)) continue;

            // The key that answered names the table for the rest.
            var root = EnvironmentUrl + ApiPath + keys[i] + "/";

            var columns = new List<ErdColumn>();
            await ForEachRowAsync(root + "Attributes?$select=LogicalName,DisplayName,AttributeType,AttributeTypeName," +
                                  "RequiredLevel,IsPrimaryId,IsPrimaryName,IsCustomAttribute,AttributeOf", null, a =>
            {
                // A column with AttributeOf is another's shadow - a lookup's name, a choice's label.
                if (JsonHelper.GetString(a, "AttributeOf") is { Length: > 0 }) return;
                if (JsonHelper.GetString(a, "LogicalName") is not { Length: > 0 } name) return;

                columns.Add(new ErdColumn(
                    name,
                    ReadLabel(a, "DisplayName"),
                    ColumnType(a),
                    JsonHelper.GetBool(a, "IsPrimaryId") ?? false,
                    JsonHelper.GetBool(a, "IsPrimaryName") ?? false,
                    ReadNested(a, "RequiredLevel", "Value") is "ApplicationRequired" or "SystemRequired",
                    JsonHelper.GetBool(a, "IsCustomAttribute") ?? false));
            }, ct).ConfigureAwait(false);

            var lookups = new List<ErdLookup>();
            await ForEachRowAsync(root + "ManyToOneRelationships?$select=SchemaName,ReferencedEntity,ReferencingAttribute", null, r =>
            {
                if (JsonHelper.GetString(r, "ReferencingAttribute") is { Length: > 0 } column &&
                    JsonHelper.GetString(r, "ReferencedEntity") is { Length: > 0 } target)
                {
                    lookups.Add(new ErdLookup(JsonHelper.GetString(r, "SchemaName") ?? column, column, target));
                }
            }, ct).ConfigureAwait(false);

            var manyToMany = new List<ErdManyToMany>();
            await ForEachRowAsync(root + "ManyToManyRelationships?$select=SchemaName,Entity1LogicalName,Entity2LogicalName,IntersectEntityName", null, r =>
            {
                if (JsonHelper.GetString(r, "Entity1LogicalName") is { Length: > 0 } first &&
                    JsonHelper.GetString(r, "Entity2LogicalName") is { Length: > 0 } second)
                {
                    manyToMany.Add(new ErdManyToMany(
                        JsonHelper.GetString(r, "SchemaName") ?? $"{first}_{second}", first, second,
                        JsonHelper.GetString(r, "IntersectEntityName")));
                }
            }, ct).ConfigureAwait(false);

            return new ErdTable(logicalName, displayName, columns, lookups, manyToMany, isCustom, isIntersect);
        }

        return null;
    }

    /// <summary>Every table's display name by logical name - to name the tables a diagram reaches outside its solutions.</summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetTableDisplayNamesAsync(CancellationToken ct = default) =>
        (await TableNamesAsync(ct).ConfigureAwait(false))
            .GroupBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);

    /// <summary>"String", "Lookup", "MultiSelectPicklist" - the type name without its "Type", else the older attribute type.</summary>
    private static string ColumnType(JsonElement attribute)
    {
        var name = ReadNested(attribute, "AttributeTypeName", "Value");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.EndsWith("Type", StringComparison.Ordinal) && name.Length > 4 ? name[..^4] : name;
        }

        return JsonHelper.GetString(attribute, "AttributeType") ?? "Unknown";
    }
}
