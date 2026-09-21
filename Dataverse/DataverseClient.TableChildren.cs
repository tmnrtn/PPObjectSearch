using System.Text;
using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>The table behind a Table component, as its child-component queries need it.</summary>
public sealed record TableIdentity(Guid MetadataId, string LogicalName, int? ObjectTypeCode, string? DisplayName);

/// <summary>
/// Reads what a table owns - columns, relationships, keys, forms, views, charts and dashboards -
/// the same grouping the maker portal's table page shows.
///
/// Each kind is listed with only the handful of properties its row needs. The full record waits
/// until a child is selected: a form or view carries its entire XML definition, and a column's
/// real properties only exist on its concrete metadata type, which the list does not know until
/// it has read the row.
/// </summary>
public sealed partial class DataverseClient
{
    /// <summary>
    /// Resolves a Table component to its table. A table component's object id is the table's
    /// metadata id, so that is tried first; the logical name is the fallback for an environment
    /// where the two have come apart.
    /// </summary>
    public async Task<TableIdentity?> GetTableIdentityAsync(
        Guid metadataId,
        string? logicalNameHint,
        CancellationToken ct = default)
    {
        const string select = "?$select=MetadataId,LogicalName,SchemaName,ObjectTypeCode,DisplayName";

        var keys = new List<string>();
        if (metadataId != Guid.Empty) keys.Add($"EntityDefinitions({metadataId})");
        if (!string.IsNullOrWhiteSpace(logicalNameHint))
        {
            keys.Add($"EntityDefinitions(LogicalName='{Uri.EscapeDataString(logicalNameHint!.ToLowerInvariant())}')");
        }

        for (var i = 0; i < keys.Count; i++)
        {
            try
            {
                using var doc = await GetJsonAsync(EnvironmentUrl + ApiPath + keys[i] + select, ct)
                    .ConfigureAwait(false);

                var row = doc.RootElement;
                var logicalName = JsonHelper.GetString(row, "LogicalName");
                if (string.IsNullOrWhiteSpace(logicalName)) continue;

                Guid.TryParse(JsonHelper.GetString(row, "MetadataId"), out var id);

                return new TableIdentity(
                    id == Guid.Empty ? metadataId : id,
                    logicalName!,
                    JsonHelper.GetInt(row, "ObjectTypeCode"),
                    ReadLabel(row, "DisplayName"));
            }
            catch (OperationCanceledException) { throw; }
            catch (DataverseException) when (i + 1 < keys.Count)
            {
                // Try the next way of naming the table before giving up on it.
            }
        }

        return null;
    }

    /// <summary>Every column on the table, system ones included - hiding them would be a lie.</summary>
    public async Task<IReadOnlyList<TableChild>> GetTableColumnsAsync(
        TableIdentity table,
        CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath + $"EntityDefinitions({table.MetadataId})/Attributes" +
                  "?$select=MetadataId,LogicalName,SchemaName,DisplayName,AttributeType," +
                  "RequiredLevel,IsManaged,IsPrimaryId,IsPrimaryName";

        return await ReadListAsync(url, ct, row =>
        {
            var logicalName = JsonHelper.GetString(row, "LogicalName");
            if (string.IsNullOrWhiteSpace(logicalName)) return null;

            Guid.TryParse(JsonHelper.GetString(row, "MetadataId"), out var id);

            var detail = JsonHelper.GetString(row, "AttributeType");

            detail = ReadNested(row, "RequiredLevel", "Value") switch
            {
                "ApplicationRequired" => Join(detail, "required"),
                "SystemRequired" => Join(detail, "system required"),
                "Recommended" => Join(detail, "recommended"),
                _ => detail
            };

            if (JsonHelper.GetBool(row, "IsPrimaryId") ?? false) detail = Join(detail, "primary id");
            if (JsonHelper.GetBool(row, "IsPrimaryName") ?? false) detail = Join(detail, "primary name");

            return new TableChild
            {
                Kind = TableChildKind.Column,
                Name = logicalName!,
                DisplayName = ReadLabel(row, "DisplayName"),
                Detail = detail,
                IsManaged = JsonHelper.GetBool(row, "IsManaged") ?? false,
                Id = id,
                // Only the concrete metadata type carries a column's real properties - maximum
                // length, precision, the choice's own options - so the list's @odata.type picks
                // the cast. GetChildPropertiesAsync drops back to the base type if it is refused.
                PropertiesQuery = BuildColumnQuery(table.MetadataId, id, JsonHelper.GetString(row, "@odata.type"))
            };
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Relationships in all three shapes. A table sits on the parent end of its one-to-many
    /// relationships and the child end of its many-to-one ones, so both are listed from this
    /// table's point of view rather than the metadata's.
    /// </summary>
    public async Task<IReadOnlyList<TableChild>> GetTableRelationshipsAsync(
        TableIdentity table,
        CancellationToken ct = default)
    {
        const string oneToManySelect =
            "?$select=MetadataId,SchemaName,ReferencedEntity,ReferencingEntity,IsManaged";

        const string manyToManySelect =
            "?$select=MetadataId,SchemaName,Entity1LogicalName,Entity2LogicalName,IsManaged";

        var results = new List<TableChild>();

        var root = EnvironmentUrl + ApiPath + $"EntityDefinitions({table.MetadataId})/";

        results.AddRange(await ReadListAsync(root + "OneToManyRelationships" + oneToManySelect, ct,
            row => ReadRelationship(row, "1:N", JsonHelper.GetString(row, "ReferencingEntity"),
                "OneToManyRelationshipMetadata")).ConfigureAwait(false));

        results.AddRange(await ReadListAsync(root + "ManyToOneRelationships" + oneToManySelect, ct,
            row => ReadRelationship(row, "N:1", JsonHelper.GetString(row, "ReferencedEntity"),
                "OneToManyRelationshipMetadata")).ConfigureAwait(false));

        results.AddRange(await ReadListAsync(root + "ManyToManyRelationships" + manyToManySelect, ct,
            row =>
            {
                // Which of the two ends is "the other table" depends on which end this table is.
                var first = JsonHelper.GetString(row, "Entity1LogicalName");
                var second = JsonHelper.GetString(row, "Entity2LogicalName");
                var other = string.Equals(first, table.LogicalName, StringComparison.OrdinalIgnoreCase)
                    ? second
                    : first;

                return ReadRelationship(row, "N:N", other, "ManyToManyRelationshipMetadata");
            }).ConfigureAwait(false));

        return results;
    }

    /// <summary>Alternate keys.</summary>
    public async Task<IReadOnlyList<TableChild>> GetTableKeysAsync(
        TableIdentity table,
        CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath + $"EntityDefinitions({table.MetadataId})/Keys" +
                  "?$select=MetadataId,LogicalName,SchemaName,DisplayName,KeyAttributes,EntityKeyIndexStatus,IsManaged";

        return await ReadListAsync(url, ct, row =>
        {
            var logicalName = JsonHelper.GetString(row, "LogicalName") ?? JsonHelper.GetString(row, "SchemaName");
            if (string.IsNullOrWhiteSpace(logicalName)) return null;

            Guid.TryParse(JsonHelper.GetString(row, "MetadataId"), out var id);

            var columns = row.TryGetProperty("KeyAttributes", out var attributes) &&
                          attributes.ValueKind == JsonValueKind.Array
                ? string.Join(", ", attributes.EnumerateArray().Select(a => a.GetString()).Where(a => a is not null))
                : null;

            return new TableChild
            {
                Kind = TableChildKind.Key,
                Name = logicalName!,
                DisplayName = ReadLabel(row, "DisplayName"),
                Detail = string.IsNullOrWhiteSpace(columns) ? JsonHelper.GetString(row, "EntityKeyIndexStatus") : columns,
                IsManaged = JsonHelper.GetBool(row, "IsManaged") ?? false,
                Id = id,
                PropertiesQuery = $"EntityDefinitions({table.MetadataId})/Keys({id})"
            };
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Forms and table dashboards, which are the same systemform rows - type 0 is a dashboard,
    /// everything else is a form - so one query answers both.
    /// </summary>
    public async Task<IReadOnlyList<TableChild>> GetTableFormsAsync(
        TableIdentity table,
        CancellationToken ct = default)
    {
        const string select = "systemforms?$select=formid,name,type,ismanaged&$filter=";

        return await ReadRecordListAsync(select, "objecttypecode", table, ct, row =>
        {
            if (!Guid.TryParse(JsonHelper.GetString(row, "formid"), out var id)) return null;

            var type = JsonHelper.GetInt(row, "type");

            return new TableChild
            {
                Kind = type == 0 ? TableChildKind.Dashboard : TableChildKind.Form,
                Name = JsonHelper.GetString(row, "name") ?? id.ToString(),
                Detail = JsonHelper.GetString(row, "type@OData.Community.Display.V1.FormattedValue"),
                IsManaged = JsonHelper.GetBool(row, "ismanaged") ?? false,
                Id = id,
                PropertiesQuery = $"systemforms({id})"
            };
        }).ConfigureAwait(false);
    }

    /// <summary>System views. Personal views belong to their owner, not to the table's solution.</summary>
    public async Task<IReadOnlyList<TableChild>> GetTableViewsAsync(
        TableIdentity table,
        CancellationToken ct = default)
    {
        const string select = "savedqueries?$select=savedqueryid,name,querytype,isdefault,ismanaged&$filter=";

        return await ReadRecordListAsync(select, "returnedtypecode", table, ct, row =>
        {
            if (!Guid.TryParse(JsonHelper.GetString(row, "savedqueryid"), out var id)) return null;

            var queryType = JsonHelper.GetString(row, "querytype@OData.Community.Display.V1.FormattedValue");
            var isDefault = JsonHelper.GetBool(row, "isdefault") ?? false;

            return new TableChild
            {
                Kind = TableChildKind.View,
                Name = JsonHelper.GetString(row, "name") ?? id.ToString(),
                Detail = isDefault ? Join(queryType, "default") : queryType,
                IsManaged = JsonHelper.GetBool(row, "ismanaged") ?? false,
                Id = id,
                PropertiesQuery = $"savedqueries({id})"
            };
        }).ConfigureAwait(false);
    }

    /// <summary>Charts owned by the table.</summary>
    public async Task<IReadOnlyList<TableChild>> GetTableChartsAsync(
        TableIdentity table,
        CancellationToken ct = default)
    {
        const string select = "savedqueryvisualizations?$select=savedqueryvisualizationid,name,isdefault," +
                              "ismanaged&$filter=";

        return await ReadRecordListAsync(select, "primaryentitytypecode", table, ct, row =>
        {
            if (!Guid.TryParse(JsonHelper.GetString(row, "savedqueryvisualizationid"), out var id)) return null;

            return new TableChild
            {
                Kind = TableChildKind.Chart,
                Name = JsonHelper.GetString(row, "name") ?? id.ToString(),
                Detail = (JsonHelper.GetBool(row, "isdefault") ?? false) ? "default" : null,
                IsManaged = JsonHelper.GetBool(row, "ismanaged") ?? false,
                Id = id,
                PropertiesQuery = $"savedqueryvisualizations({id})"
            };
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Every property of one child, read from its own record. A column's query names a concrete
    /// metadata type it may turn out not to have, so the cast and its expansion are dropped in
    /// turn rather than failing - the base type still answers most of the question.
    /// </summary>
    public async Task<IReadOnlyList<ComponentProperty>> GetChildPropertiesAsync(
        TableChild child,
        CancellationToken ct = default)
    {
        var queries = Fallbacks(child.PropertiesQuery);

        for (var i = 0; i < queries.Count; i++)
        {
            try
            {
                // Metadata endpoints ignore the annotation preference; record endpoints use it to
                // return option labels and lookup names alongside the raw values.
                using var doc = await GetJsonAsync(EnvironmentUrl + ApiPath + queries[i], ct, true)
                    .ConfigureAwait(false);

                return RecordProperties.Flatten(doc.RootElement);
            }
            catch (OperationCanceledException) { throw; }
            catch (DataverseException) when (i + 1 < queries.Count)
            {
                // Fall back to the less specific query.
            }
        }

        return Array.Empty<ComponentProperty>();
    }

    /// <summary>The same query with its expansion, then its type cast, taken off.</summary>
    private static IReadOnlyList<string> Fallbacks(string query)
    {
        var queries = new List<string> { query };

        var options = query.IndexOf('?');
        if (options > 0) queries.Add(query[..options]);

        var cast = queries[^1].IndexOf("/Microsoft.Dynamics.CRM.", StringComparison.Ordinal);
        if (cast > 0) queries.Add(queries[^1][..cast]);

        return queries;
    }

    private static string BuildColumnQuery(Guid tableId, Guid columnId, string? odataType)
    {
        var query = $"EntityDefinitions({tableId})/Attributes({columnId})";

        // "#Microsoft.Dynamics.CRM.StringAttributeMetadata" -> a cast segment.
        var type = odataType?.TrimStart('#');
        if (string.IsNullOrWhiteSpace(type) || !type.StartsWith("Microsoft.Dynamics.CRM.", StringComparison.Ordinal))
        {
            return query;
        }

        query += "/" + type;

        // A choice column's options are the point of looking at it, and they only arrive expanded.
        return type.Contains("Picklist", StringComparison.Ordinal) ||
               type.Contains("State", StringComparison.Ordinal) ||
               type.Contains("Status", StringComparison.Ordinal) ||
               type.Contains("Boolean", StringComparison.Ordinal)
            ? query + "?$expand=OptionSet"
            : query;
    }

    private static TableChild? ReadRelationship(JsonElement row, string shape, string? otherEntity, string metadataType)
    {
        var schemaName = JsonHelper.GetString(row, "SchemaName");
        if (string.IsNullOrWhiteSpace(schemaName)) return null;

        Guid.TryParse(JsonHelper.GetString(row, "MetadataId"), out var id);

        return new TableChild
        {
            Kind = TableChildKind.Relationship,
            Name = schemaName!,
            Detail = string.IsNullOrWhiteSpace(otherEntity) ? shape : $"{shape}  {otherEntity}",
            IsManaged = JsonHelper.GetBool(row, "IsManaged") ?? false,
            Id = id,
            PropertiesQuery = $"RelationshipDefinitions({id})/Microsoft.Dynamics.CRM.{metadataType}"
        };
    }

    /// <summary>
    /// Reads a table-owned record type. The entity name columns these tables filter on are
    /// strings in the Web API, but the underlying columns are object type codes, so a rejected
    /// string filter is retried numerically rather than losing the whole section.
    /// </summary>
    private async Task<IReadOnlyList<TableChild>> ReadRecordListAsync(
        string selectClause,
        string typeColumn,
        TableIdentity table,
        CancellationToken ct,
        Func<JsonElement, TableChild?> read)
    {
        var url = EnvironmentUrl + ApiPath + selectClause + $"{typeColumn} eq '{table.LogicalName}'";

        try
        {
            return await ReadListAsync(url, ct, read, includeFormattedValues: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (DataverseException) when (table.ObjectTypeCode is not null)
        {
            var byCode = EnvironmentUrl + ApiPath + selectClause + $"{typeColumn} eq {table.ObjectTypeCode}";
            return await ReadListAsync(byCode, ct, read, includeFormattedValues: true).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<TableChild>> ReadListAsync(
        string url,
        CancellationToken ct,
        Func<JsonElement, TableChild?> read,
        bool includeFormattedValues = false)
    {
        var results = new List<TableChild>();

        while (url.Length > 0)
        {
            using var doc = await GetJsonAsync(url, ct, includeFormattedValues).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    var child = read(row);
                    if (child is null) continue;

                    child.BuildFilterIndex();
                    results.Add(child);
                }
            }

            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        return results;
    }

    /// <summary>The user-facing text of a metadata Label, whichever shape it arrives in.</summary>
    private static string? ReadLabel(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var label) || label.ValueKind != JsonValueKind.Object) return null;

        if (label.TryGetProperty("UserLocalizedLabel", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var text = JsonHelper.GetString(user, "Label");
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        if (label.TryGetProperty("LocalizedLabels", out var localized) && localized.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in localized.EnumerateArray())
            {
                var text = JsonHelper.GetString(entry, "Label");
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        return null;
    }

    private static string? ReadNested(JsonElement row, string property, string child) =>
        row.TryGetProperty(property, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? JsonHelper.GetString(nested, child)
            : null;

    private static string? Join(string? first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : $"{first}, {second}";
}

/// <summary>
/// Turns one Dataverse record into readable property rows. Nested objects become dotted paths and
/// arrays indexed ones, so a choice's options and a key's column list stay visible instead of
/// collapsing into "[object]".
/// </summary>
internal static class RecordProperties
{
    private const string FormattedSuffix = "@OData.Community.Display.V1.FormattedValue";
    private const int MaxDepth = 8;

    public static IReadOnlyList<ComponentProperty> Flatten(JsonElement element)
    {
        var rows = new List<ComponentProperty>();
        Flatten(element, null, rows, 0);
        return rows;
    }

    private static void Flatten(JsonElement element, string? path, List<ComponentProperty> rows, int depth)
    {
        if (depth > MaxDepth) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // @odata.context and friends describe the response, not the component; the
                    // formatted-value annotations are folded into their own property below.
                    if (property.Name.Contains('@', StringComparison.Ordinal)) continue;

                    var name = Combine(path, CleanName(property.Name));

                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Flatten(property.Value, name, rows, depth + 1);
                        continue;
                    }

                    var formatted = element.TryGetProperty(property.Name + FormattedSuffix, out var annotation)
                        ? annotation.GetString()
                        : null;

                    Add(rows, name, Merge(formatted, Scalar(property.Value)));
                }

                break;

            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();

                // A list of plain values reads better on one line than as ten indexed rows.
                if (items.Count > 0 && items.All(i => i.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
                {
                    Add(rows, path, string.Join(", ", items.Select(Scalar).Where(v => v is not null)));
                    break;
                }

                for (var i = 0; i < items.Count; i++)
                {
                    Flatten(items[i], $"{path}[{i}]", rows, depth + 1);
                }

                break;

            default:
                Add(rows, path, Scalar(element));
                break;
        }
    }

    private static void Add(List<ComponentProperty> rows, string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) return;

        rows.Add(new ComponentProperty { Name = name!, Value = value! });
    }

    private static string Combine(string? path, string name) =>
        string.IsNullOrEmpty(path) ? name : path + "." + name;

    /// <summary>Lookups arrive as _ownerid_value; the underscores are plumbing, not a name.</summary>
    private static string CleanName(string name) =>
        name.Length > 7 && name.StartsWith('_') && name.EndsWith("_value", StringComparison.Ordinal)
            ? name[1..^6]
            : name;

    /// <summary>
    /// The label Dataverse formats an option or lookup into, keeping the raw value alongside it -
    /// the code behind a status and the id behind a lookup are both worth seeing.
    /// </summary>
    private static string? Merge(string? formatted, string? raw)
    {
        if (string.IsNullOrWhiteSpace(formatted)) return raw;
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(formatted, raw, StringComparison.Ordinal)) return formatted;

        return new StringBuilder(formatted).Append("  (").Append(raw).Append(')').ToString();
    }

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => null
    };
}
