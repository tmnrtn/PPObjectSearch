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

        while (url.Length > 0)
        {
            ct.ThrowIfCancellationRequested();

            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    var logicalName = JsonHelper.GetString(row, "LogicalName");
                    if (string.IsNullOrWhiteSpace(logicalName)) continue;
                    if (JsonHelper.GetBool(row, "IsPrivate") ?? false) continue;

                    var entitySetName = JsonHelper.GetString(row, "EntitySetName");
                    if (string.IsNullOrWhiteSpace(entitySetName)) continue;

                    results.Add(new EntitySummary(
                        logicalName!,
                        ReadLabel(row, "DisplayName"),
                        entitySetName,
                        JsonHelper.GetString(row, "PrimaryIdAttribute") ?? logicalName + "id",
                        JsonHelper.GetString(row, "PrimaryNameAttribute"),
                        JsonHelper.GetBool(row, "IsManaged") ?? false,
                        JsonHelper.GetBool(row, "IsActivity") ?? false));
                }
            }

            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        results.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
        return results;
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
                  "?$select=LogicalName,DisplayName,AttributeTypeName,IsValidForRead,IsPrimaryId,IsPrimaryName,AttributeOf";

        var results = new List<EntityColumn>();

        while (url.Length > 0)
        {
            ct.ThrowIfCancellationRequested();

            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    var name = JsonHelper.GetString(row, "LogicalName");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (!(JsonHelper.GetBool(row, "IsValidForRead") ?? true)) continue;
                    if (!string.IsNullOrWhiteSpace(JsonHelper.GetString(row, "AttributeOf"))) continue;

                    var typeName = ReadNested(row, "AttributeTypeName", "Value");
                    if (string.IsNullOrWhiteSpace(typeName)) continue;
                    if (UncomparableColumnTypes.Contains(typeName!)) continue;

                    results.Add(new EntityColumn(
                        name!,
                        ReadLabel(row, "DisplayName"),
                        typeName!,
                        JsonHelper.GetBool(row, "IsPrimaryId") ?? false,
                        JsonHelper.GetBool(row, "IsPrimaryName") ?? false));
                }
            }

            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        results.Sort((a, b) => string.Compare(a.LogicalName, b.LogicalName, StringComparison.OrdinalIgnoreCase));
        return results;
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

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
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

                    results.Add(new AlternateKeyInfo(name!, ReadLabel(row, "DisplayName"), attributes));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
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

        var url = EnvironmentUrl + ApiPath + entity.EntitySetName +
                  "?$select=" + string.Join(",", select.Distinct(StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(filter))
        {
            url += "&$filter=" + EscapeFilter(filter);
        }

        var results = new List<DataRecord>();

        while (url.Length > 0 && results.Count < maxRows)
        {
            ct.ThrowIfCancellationRequested();

            using var doc = await GetJsonAsync(url, ct, includeFormattedValues: true).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    if (results.Count >= maxRows) break;
                    results.Add(ReadRecord(row, entity));
                }
            }

            onRowsRead?.Invoke(results.Count);
            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        return results;
    }

    /// <summary>
    /// The filter is an OData expression the user wrote, so it is left as it stands apart from the
    /// three characters that would end the query or change its meaning. Escaping it whole would
    /// break navigation paths and function calls, which are the reason for writing one by hand.
    /// </summary>
    private static string EscapeFilter(string filter) => filter.Trim()
        .Replace("&", "%26", StringComparison.Ordinal)
        .Replace("#", "%23", StringComparison.Ordinal)
        .Replace("+", "%2B", StringComparison.Ordinal);

    private const string FormattedValueSuffix = "@OData.Community.Display.V1.FormattedValue";

    private static DataRecord ReadRecord(JsonElement row, EntitySummary entity)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var formatted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in row.EnumerateObject())
        {
            var at = property.Name.IndexOf('@', StringComparison.Ordinal);

            // "@odata.etag" and friends describe the response, not the row.
            if (at == 0) continue;

            if (at > 0)
            {
                if (property.Name.EndsWith(FormattedValueSuffix, StringComparison.Ordinal))
                {
                    formatted[property.Name[..at]] = property.Value.GetString();
                }

                continue;
            }

            values[property.Name] = ReadValue(property.Value);
        }

        Guid.TryParse(values.TryGetValue(entity.PrimaryIdAttribute, out var id) ? id : null, out var recordId);

        return new DataRecord
        {
            Id = recordId,
            Values = values,
            Formatted = formatted,
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
