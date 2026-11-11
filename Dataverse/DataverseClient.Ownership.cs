namespace PPObjectSearch.Dataverse;

/// <summary>A table whose rows each have an owner - a user or a team.</summary>
public sealed record OwnedTable(string LogicalName, string? DisplayName, string EntitySet, string PrimaryId)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? LogicalName : $"{DisplayName} ({LogicalName})";
}

/// <summary>
/// Which rows a user or a team owns - the reads behind Owned records. Read-only. Only rows owned
/// by the principal itself count: a user's share of what their teams own is not theirs.
/// </summary>
public sealed partial class DataverseClient
{
    /// <summary>
    /// Every table with user or team ownership whose rows can be read. The activity pointer is left
    /// out: its rows are the emails, tasks and appointments counted in their own tables.
    /// </summary>
    public async Task<IReadOnlyList<OwnedTable>> GetOwnedTablesAsync(CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  "EntityDefinitions?$select=LogicalName,DisplayName,EntitySetName,PrimaryIdAttribute,OwnershipType,TableType,IsPrivate";

        var tables = new List<OwnedTable>();

        await ForEachRowAsync(url, null, row =>
        {
            var logicalName = JsonHelper.GetString(row, "LogicalName");
            var entitySet = JsonHelper.GetString(row, "EntitySetName");

            if (string.IsNullOrWhiteSpace(logicalName) || string.IsNullOrWhiteSpace(entitySet)) return;
            if (JsonHelper.GetString(row, "OwnershipType") != "UserOwned") return;
            if (JsonHelper.GetBool(row, "IsPrivate") == true || JsonHelper.GetString(row, "TableType") == "Virtual") return;
            if (logicalName == "activitypointer") return;

            tables.Add(new OwnedTable(logicalName, ReadLabel(row, "DisplayName"), entitySet,
                JsonHelper.GetString(row, "PrimaryIdAttribute") ?? logicalName + "id"));
        }, ct).ConfigureAwait(false);

        tables.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
        return tables;
    }

    /// <summary>
    /// How many of a table's rows this user or team owns, exactly: an aggregate count, or past
    /// 50,000 rows the same page search a table's row count uses.
    /// </summary>
    public async Task<long> CountOwnedRowsAsync(OwnedTable table, Guid ownerId, CancellationToken ct = default)
    {
        var definition = new TableDefinition(table.LogicalName, table.EntitySet, table.PrimaryId, null);
        var filter = $"<filter><condition attribute='ownerid' operator='eq' value='{ownerId}' /></filter>";

        if (await TryAggregateCountAsync(definition, ct, filter).ConfigureAwait(false) is { } rows) return rows;

        var (pageRows, _) = await CountByPagesAsync(definition, null, ct, filter).ConfigureAwait(false);
        return pageRows;
    }
}
