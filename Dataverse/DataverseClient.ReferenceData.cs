using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>
/// Reading table rows rather than solution components. Reference data - currencies, categories,
/// configuration tables - lives in ordinary tables, so comparing it between environments means
/// listing tables, reading their column and key metadata, and then retrieving the rows themselves.
/// </summary>
public sealed partial class DataverseClient
{
    /// <summary>
    /// Default ceiling on rows read from one table, so a transactional table picked by mistake
    /// stops early instead of paging for ten minutes.
    /// </summary>
    public const int DefaultMaxRecordsPerEntity = 5000;

    /// <summary>
    /// Every table a person could sensibly compare. Private tables are internal plumbing, and a
    /// table with no entity set name has no row endpoint at all.
    /// </summary>
    public async Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath + "EntityDefinitions" +
                  "?$select=LogicalName,DisplayName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute," +
                  "IsPrivate,IsManaged,IsActivity";

        var results = new List<EntitySummary>();

        await ForEachRowAsync(url, null, row =>
        {
            if (ReadEntitySummary(row) is { } entity) results.Add(entity);
        }, ct).ConfigureAwait(false);

        results.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
        return results;
    }

    private static EntitySummary? ReadEntitySummary(JsonElement row)
    {
        var logicalName = JsonHelper.GetString(row, "LogicalName");
        if (string.IsNullOrWhiteSpace(logicalName)) return null;
        if (JsonHelper.GetBool(row, "IsPrivate") ?? false) return null;

        var entitySetName = JsonHelper.GetString(row, "EntitySetName");
        if (string.IsNullOrWhiteSpace(entitySetName)) return null;

        return new EntitySummary(
            logicalName,
            ReadLabel(row, "DisplayName"),
            entitySetName,
            JsonHelper.GetString(row, "PrimaryIdAttribute") ?? logicalName + "id",
            JsonHelper.GetString(row, "PrimaryNameAttribute"),
            JsonHelper.GetBool(row, "IsManaged") ?? false,
            JsonHelper.GetBool(row, "IsActivity") ?? false);
    }

    /// <summary>
    /// Column types that cannot take part in a value comparison: binaries and party lists have no
    /// scalar value, managed properties and calendar rules are not data, and the shadow entity-name
    /// column merely repeats its lookup's target.
    /// </summary>
    private static readonly HashSet<string> UncomparableColumnTypes = new(StringComparer.Ordinal)
    {
        "EntityNameType", "PartyListType", "ImageType", "FileType",
        "ManagedPropertyType", "CalendarRulesType", "VirtualType"
    };

    /// <summary>
    /// The columns of a table that a row comparison can read. Columns derived from another one -
    /// a money column's base-currency twin, a lookup's shadow name - are dropped: they restate a
    /// value that is already being compared, so keeping them would double-count every difference.
    /// </summary>
    public async Task<IReadOnlyList<EntityColumn>> GetEntityColumnsAsync(
        string logicalName,
        CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  $"EntityDefinitions(LogicalName='{Uri.EscapeDataString(logicalName.ToLowerInvariant())}')/Attributes" +
                  "?$select=LogicalName,DisplayName,AttributeTypeName,IsValidForRead,IsValidForCreate," +
                  "IsValidForUpdate,IsPrimaryId,IsPrimaryName,AttributeOf";

        var results = new List<EntityColumn>();

        await ForEachRowAsync(url, null, row =>
        {
            if (ReadComparableColumn(row) is { } column) results.Add(column);
        }, ct).ConfigureAwait(false);

        results.Sort((a, b) => string.Compare(a.LogicalName, b.LogicalName, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>A column a comparison can read; null for one it cannot, or one derived from another.</summary>
    private static EntityColumn? ReadComparableColumn(JsonElement row)
    {
        var name = JsonHelper.GetString(row, "LogicalName");
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (!(JsonHelper.GetBool(row, "IsValidForRead") ?? true)) return null;
        if (!string.IsNullOrWhiteSpace(JsonHelper.GetString(row, "AttributeOf"))) return null;

        var typeName = ReadNested(row, "AttributeTypeName", "Value");
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        if (UncomparableColumnTypes.Contains(typeName)) return null;

        return new EntityColumn(
            name,
            ReadLabel(row, "DisplayName"),
            typeName,
            JsonHelper.GetBool(row, "IsPrimaryId") ?? false,
            JsonHelper.GetBool(row, "IsPrimaryName") ?? false)
        {
            IsValidForCreate = JsonHelper.GetBool(row, "IsValidForCreate") ?? true,
            IsValidForUpdate = JsonHelper.GetBool(row, "IsValidForUpdate") ?? true
        };
    }

    /// <summary>
    /// Alternate keys, which are the only ids that mean the same thing in two environments unless
    /// the rows were deployed rather than created independently.
    /// </summary>
    public async Task<IReadOnlyList<AlternateKeyInfo>> GetAlternateKeysAsync(
        string logicalName,
        CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  $"EntityDefinitions(LogicalName='{Uri.EscapeDataString(logicalName.ToLowerInvariant())}')/Keys" +
                  "?$select=LogicalName,DisplayName,KeyAttributes";

        var results = new List<AlternateKeyInfo>();

        try
        {
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

            foreach (var row in JsonHelper.Rows(doc.RootElement))
            {
                var name = JsonHelper.GetString(row, "LogicalName") ?? JsonHelper.GetString(row, "SchemaName");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var attributes = row.TryGetProperty("KeyAttributes", out var array) &&
                                 array.ValueKind == JsonValueKind.Array
                    ? array.EnumerateArray()
                           .Select(a => a.GetString())
                           .Where(a => !string.IsNullOrWhiteSpace(a))
                           .Select(a => a!)
                           .ToList()
                    : new List<string>();

                if (attributes.Count == 0) continue;

                results.Add(new AlternateKeyInfo(name, ReadLabel(row, "DisplayName"), attributes));
            }
        }
        catch (DataverseException)
        {
            // A table that does not support keys answers with an error rather than an empty list.
        }

        return results;
    }

    /// <summary>
    /// Reads rows from one table. The select list is explicit so that a column added to a table
    /// cannot silently change what a saved configuration compares, and so the request stays small
    /// on wide tables.
    /// </summary>
    public async Task<IReadOnlyList<DataRecord>> GetRecordsAsync(
        EntitySummary entity,
        IEnumerable<string> selectNames,
        string? filter,
        int maxRows,
        Action<int>? onRowsRead = null,
        CancellationToken ct = default)
    {
        var select = new List<string>(selectNames);

        // Without the primary id there is no way to tell two rows apart when the chosen key turns
        // out not to be unique.
        if (!select.Contains(entity.PrimaryIdAttribute, StringComparer.OrdinalIgnoreCase))
        {
            select.Insert(0, entity.PrimaryIdAttribute);
        }

        // Ordered by id so that, when the cap cuts a table short, both environments stop at the
        // same place in the same order rather than wherever the server's paging happened to go.
        var url = EnvironmentUrl + ApiPath + entity.EntitySetName +
                  "?$select=" + string.Join(",", select.Distinct(StringComparer.OrdinalIgnoreCase)) +
                  "&$orderby=" + entity.PrimaryIdAttribute;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            url += "&$filter=" + EscapeFilter(filter);
        }

        var results = new List<DataRecord>();

        while (url.Length > 0 && results.Count < maxRows)
        {
            ct.ThrowIfCancellationRequested();

            using var doc = await GetJsonAsync(url, ct, Annotations.All).ConfigureAwait(false);

            foreach (var row in JsonHelper.Rows(doc.RootElement))
            {
                if (results.Count >= maxRows) break;
                results.Add(ReadRecord(row, entity));
            }

            onRowsRead?.Invoke(results.Count);
            url = JsonHelper.NextLink(doc.RootElement);
        }

        return results;
    }

    /// <summary>
    /// The filter is an OData expression the user wrote, so it is left as it stands apart from the
    /// characters that would end the query or change its meaning. Escaping it whole would break
    /// navigation paths and function calls, which are the reason for writing one by hand. '%' goes
    /// first, so a literal percent sign (contains(name,'50%')) reaches the server as itself.
    /// </summary>
    internal static string EscapeFilter(string filter) => filter.Trim()
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("&", "%26", StringComparison.Ordinal)
        .Replace("#", "%23", StringComparison.Ordinal)
        .Replace("+", "%2B", StringComparison.Ordinal);

    private const string FormattedValueSuffix = "@OData.Community.Display.V1.FormattedValue";
    private const string LookupTargetSuffix = "@Microsoft.Dynamics.CRM.lookuplogicalname";
    private const string NavigationSuffix = "@Microsoft.Dynamics.CRM.associatednavigationproperty";

    private static DataRecord ReadRecord(JsonElement row, EntitySummary entity)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var formatted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var lookupTargets = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var navigationProperties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? etag = null;

        // The column annotations a comparison or a later write uses, by the suffix that names them.
        var annotations = new (string Suffix, Dictionary<string, string?> Into)[]
        {
            (FormattedValueSuffix, formatted),
            (LookupTargetSuffix, lookupTargets),
            (NavigationSuffix, navigationProperties)
        };

        foreach (var property in row.EnumerateObject())
        {
            var at = property.Name.IndexOf('@', StringComparison.Ordinal);

            // The row's version, kept so a later write can insist the row has not changed since.
            if (property.Name == "@odata.etag" && property.Value.ValueKind == JsonValueKind.String)
            {
                etag = property.Value.GetString();
                continue;
            }

            // Other "@odata" annotations describe the response, not the row.
            if (at == 0) continue;

            if (at > 0)
            {
                var into = Array.Find(annotations, a => property.Name.EndsWith(a.Suffix, StringComparison.Ordinal)).Into;
                if (into is not null) into[property.Name[..at]] = property.Value.GetString();
                continue;
            }

            values[property.Name] = ReadValue(property.Value);
        }

        var recordId = values.TryGetValue(entity.PrimaryIdAttribute, out var id) && Guid.TryParse(id, out var parsed)
            ? parsed
            : Guid.Empty;

        return new DataRecord
        {
            Id = recordId,
            Values = values,
            Formatted = formatted,
            LookupTargets = lookupTargets,
            NavigationProperties = navigationProperties,
            ETag = etag,
            PrimaryName = entity.PrimaryNameAttribute is { Length: > 0 } name &&
                          values.TryGetValue(name, out var label)
                ? label
                : null
        };
    }

    private static string? ReadValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,

        // Nothing but scalars should arrive here, but a column with an unexpected shape is worth
        // showing as its JSON rather than dropping silently.
        _ => value.GetRawText()
    };
}
