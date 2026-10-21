using System.Globalization;
using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>
/// Where a table's data goes beyond Dataverse itself: auditing, change tracking, Synapse Link
/// (and Fabric link, which is a Synapse Link profile too) and Dataverse search. Everything here
/// only reads.
/// </summary>
public sealed partial class DataverseClient
{
    /// <summary>
    /// Every table's audit, change tracking and search settings, in one metadata read - the
    /// attribute-free form of RetrieveAllEntities is small enough to fetch whole.
    /// </summary>
    public async Task<IReadOnlyList<TableSetting>> GetTableSettingsAsync(CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath + "EntityDefinitions" +
                  "?$select=LogicalName,DisplayName,MetadataId,IsCustomEntity,IsManaged,IsAuditEnabled,ChangeTrackingEnabled," +
                  "SyncToExternalSearchIndex,CanEnableSyncToExternalSearchIndex";

        var tables = new List<TableSetting>();

        await ForEachRowAsync(url, null, row =>
        {
            if (ReadTableSetting(row) is { } table) tables.Add(table);
        }, ct).ConfigureAwait(false);

        return tables;
    }

    internal static TableSetting? ReadTableSetting(JsonElement row)
    {
        var logicalName = JsonHelper.GetString(row, "LogicalName");
        if (string.IsNullOrWhiteSpace(logicalName)) return null;

        return new TableSetting(
            logicalName.ToLowerInvariant(),
            ReadLabel(row, "DisplayName"),
            Guid.TryParse(JsonHelper.GetString(row, "MetadataId"), out var id) ? id : Guid.Empty,
            JsonHelper.GetBool(row, "IsCustomEntity") ?? false,
            JsonHelper.GetBool(row, "IsManaged") ?? false,
            ManagedValue(row, "IsAuditEnabled"),
            JsonHelper.GetBool(row, "ChangeTrackingEnabled"),
            JsonHelper.GetBool(row, "SyncToExternalSearchIndex"),
            ManagedValue(row, "CanEnableSyncToExternalSearchIndex"));
    }

    /// <summary>A managed property's value - <c>{"Value":true,"CanBeChanged":...}</c> - or a plain flag.</summary>
    private static bool? ManagedValue(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? JsonHelper.GetBool(value, "Value")
            : JsonHelper.GetBool(row, property);

    /// <summary>
    /// The columns of one table a person could meaningfully choose to audit: custom, writable and
    /// not a computed companion of another column. Managed ones are kept - a solution is unmanaged
    /// where it is built and managed everywhere it is deployed to, so leaving them out would empty
    /// the comparison in exactly the environments most worth checking.
    /// </summary>
    public async Task<IReadOnlyList<ColumnAudit>> GetColumnAuditAsync(string table, CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  $"EntityDefinitions(LogicalName='{Uri.EscapeDataString(table.ToLowerInvariant())}')/Attributes" +
                  "?$select=LogicalName,DisplayName,IsAuditEnabled,IsCustomAttribute,IsValidForUpdate,IsLogical,IsManaged";

        var columns = new List<ColumnAudit>();

        await ForEachRowAsync(url, null, row =>
        {
            if (ReadColumnAudit(row) is { } column) columns.Add(column);
        }, ct).ConfigureAwait(false);

        return columns;
    }

    internal static ColumnAudit? ReadColumnAudit(JsonElement row)
    {
        var logicalName = JsonHelper.GetString(row, "LogicalName");
        if (string.IsNullOrWhiteSpace(logicalName)) return null;

        if (JsonHelper.GetBool(row, "IsCustomAttribute") != true) return null;
        if (JsonHelper.GetBool(row, "IsValidForUpdate") != true) return null;
        if (JsonHelper.GetBool(row, "IsLogical") == true) return null;

        return new ColumnAudit(
            logicalName,
            ReadLabel(row, "DisplayName"),
            ManagedValue(row, "IsAuditEnabled"),
            JsonHelper.GetBool(row, "IsManaged") ?? false);
    }

    /// <summary>
    /// Every Synapse Link profile and the state of each table in each - Fabric links included,
    /// since they are profiles too. Inactive and deleted profiles are kept: a link that has
    /// stopped is something to see, not something to hide.
    /// </summary>
    public async Task<SynapseLinkSetup> GetSynapseLinkAsync(CancellationToken ct = default)
    {
        var profiles = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + "synapselinkprofiles" +
            "?$select=synapselinkprofileid,name,profilestate,profiletype,createdon,_datalakefolder_value,_createdby_value",
            ReadSynapseProfile, ct).ConfigureAwait(false);

        if (profiles.Count == 0) return SynapseLinkSetup.Empty;

        var tables = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + "synapselinkprofileentitystates" +
            "?$select=entityname,syncstate,initialsyncstate,metadatastate,lakerecordcount,sourcerecordcount," +
            "lastsynceddatatime,statecode,_profile_value",
            ReadSynapseTable, ct).ConfigureAwait(false);

        return new SynapseLinkSetup(profiles, tables);
    }

    internal static SynapseLinkProfile? ReadSynapseProfile(JsonElement row)
    {
        if (!Guid.TryParse(JsonHelper.GetString(row, "synapselinkprofileid"), out var id)) return null;

        return new SynapseLinkProfile(
            id,
            JsonHelper.GetString(row, "name") ?? "(unnamed)",
            JsonHelper.GetInt(row, "profilestate"),
            Label(row, "profilestate"),
            Label(row, "profiletype"),
            Label(row, "_datalakefolder_value"),
            Label(row, "_createdby_value"),
            JsonHelper.GetDate(row, "createdon"));
    }

    internal static SynapseLinkTable? ReadSynapseTable(JsonElement row)
    {
        var table = JsonHelper.GetString(row, "entityname");
        if (string.IsNullOrWhiteSpace(table)) return null;
        if (!Guid.TryParse(JsonHelper.GetString(row, "_profile_value"), out var profile)) return null;

        return new SynapseLinkTable(
            profile,
            table.ToLowerInvariant(),
            JsonHelper.GetInt(row, "syncstate"),
            Label(row, "syncstate"),
            Label(row, "initialsyncstate"),
            Label(row, "metadatastate"),
            GetLong(row, "lakerecordcount"),
            GetLong(row, "sourcerecordcount"),
            JsonHelper.GetDate(row, "lastsynceddatatime"),
            (JsonHelper.GetInt(row, "statecode") ?? 0) == 0);
    }

    /// <summary>
    /// The organization row's audit and search switches. Read whole rather than by column: a
    /// $select naming one column an older environment lacks would fail the lot.
    /// </summary>
    public async Task<EnvironmentDataSettings> GetEnvironmentDataSettingsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(EnvironmentUrl + ApiPath + "organizations", ct).ConfigureAwait(false);

        var row = JsonHelper.Rows(doc.RootElement).FirstOrDefault();

        return new EnvironmentDataSettings(
            JsonHelper.GetBool(row, "isauditenabled"),
            JsonHelper.GetBool(row, "isuseraccessauditenabled"),
            JsonHelper.GetBool(row, "isreadauditenabled"),
            JsonHelper.GetInt(row, "auditretentionperiodv2") ?? JsonHelper.GetInt(row, "auditretentionperiod"),
            JsonHelper.GetBool(row, "isexternalsearchindexenabled"));
    }

    /// <summary>
    /// Dataverse search's status - provisioned or not, and each indexed table's sync state - with
    /// the index's size where the statistics call answers. The status call answers even where
    /// search is off; statistics does not, so its failure only leaves the size blank.
    /// </summary>
    public async Task<SearchIndexStatus> GetSearchStatusAsync(CancellationToken ct = default)
    {
        using var status = await GetJsonAsync(EnvironmentUrl + ApiPath + "searchstatus", ct, maxPageSize: false).ConfigureAwait(false);

        long? documents = null, megabytes = null;

        try
        {
            using var statistics = await GetJsonAsync(EnvironmentUrl + ApiPath + "searchstatistics", ct, maxPageSize: false)
                .ConfigureAwait(false);

            if (InnerResponse(statistics.RootElement) is { } inner)
            {
                using (inner)
                {
                    var value = Value(inner.RootElement);
                    documents = GetLong(value, "documentcount");
                    megabytes = GetLong(value, "storagesizeinmb");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Services.Log.Warn("Dataverse search statistics could not be read", ex);
        }

        return ParseSearchStatus(status.RootElement, documents, megabytes);
    }

    internal static SearchIndexStatus ParseSearchStatus(JsonElement root, long? documents = null, long? megabytes = null)
    {
        using var inner = InnerResponse(root) ?? throw new DataverseException("Dataverse search returned no status.");
        var value = Value(inner.RootElement);

        var tables = new List<SearchTableStatus>();

        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("entitystatusresults", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                var table = JsonHelper.GetString(result, "entitylogicalname");
                if (string.IsNullOrWhiteSpace(table)) continue;

                var columns = result.TryGetProperty("searchableindexedfieldinfomap", out var map) && map.ValueKind == JsonValueKind.Object
                    ? map.EnumerateObject().Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();

                tables.Add(new SearchTableStatus(
                    table,
                    JsonHelper.GetString(result, "entitystatus"),
                    ParseSearchTimestamp(JsonHelper.GetString(result, "lastdatasynctimestamp")),
                    columns));
            }
        }

        return new SearchIndexStatus(
            JsonHelper.GetString(value, "status") ?? "unknown",
            JsonHelper.GetString(value, "lockboxstatus"),
            JsonHelper.GetString(value, "cmkstatus"),
            tables,
            documents,
            megabytes);
    }

    /// <summary>
    /// Search's sync timestamps carry a version ahead of the time - <c>73630169!09/12/2022 14:26:14</c>,
    /// month first, in UTC. Anything else is taken to mean there is no time to show.
    /// </summary>
    internal static DateTimeOffset? ParseSearchTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var bang = text.IndexOf('!');
        var time = bang >= 0 ? text[(bang + 1)..] : text;

        return DateTimeOffset.TryParseExact(time.Trim(), "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToLocalTime()
            : null;
    }

    /// <summary>The search functions return their answer as a JSON string inside <c>response</c>.</summary>
    private static JsonDocument? InnerResponse(JsonElement root)
    {
        var text = JsonHelper.GetString(root, "response");
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement Value(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) ? value : root;
}
