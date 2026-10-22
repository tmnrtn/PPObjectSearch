using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PPObjectSearch.Dataverse;

/// <summary>A table as the inventory describes it, with its custom columns.</summary>
public sealed record InventoryTable(
    Guid MetadataId, string LogicalName, string? DisplayName, bool IsCustom, bool IsManaged, bool IsIntersect,
    string? TableType, string? Ownership, bool IsAuditEnabled, bool IsChangeTracked, bool IsPrivate, bool IsValidForAdvancedFind,
    IReadOnlyList<InventoryColumn> CustomColumns);

/// <summary>A custom column: its metadata id, which the solution component rows name it by.</summary>
public sealed record InventoryColumn(Guid MetadataId, string LogicalName);

/// <summary>A process definition - classic workflow, business rule, action, business process flow or cloud flow.</summary>
public sealed record ProcessInfo(
    Guid Id, string Name, int Category, string? CategoryLabel, string? Table, bool IsActive,
    string? Mode, string? Scope, string Triggers, string? Owner, DateTimeOffset? ModifiedOn);

public sealed record PluginAssemblyInfo(Guid Id, string Name, string? Version, string? Isolation, string? Source);

public sealed record PluginTypeInfo(Guid Id, string TypeName, Guid? AssemblyId, string? Assembly, bool IsWorkflowActivity);

/// <summary>A plug-in or service endpoint step, with what handles it.</summary>
public sealed record InventoryStep(
    Guid Id, string Name, string? Message, string? Table, string? Stage, bool IsAsync, bool IsEnabled, Guid? HandlerId, string? Handler);

public sealed record CustomApiInfo(
    Guid Id, string UniqueName, string? DisplayName, string? Binding, string? BoundTable, bool IsFunction, bool IsPrivate, string? Implementation);

/// <summary>An Azure Service Bus, Event Hub or webhook endpoint that steps post to.</summary>
public sealed record ServiceEndpointInfo(Guid Id, string Name, string? Contract, string? Target, string? Format, string? Auth);

public sealed record CustomConnectorInfo(Guid Id, string Name, string? Type);

/// <summary>An application user - a server-to-server identity an integration signs in as.</summary>
public sealed record ApplicationUserInfo(
    Guid Id, string Name, Guid? ApplicationId, bool IsEnabled, IReadOnlyList<string> Roles, string? BusinessUnit)
{
    /// <summary>Microsoft's own service identities, which every environment has and nobody supports.</summary>
    public bool IsLikelyFirstParty =>
        Name.StartsWith('#') ||
        System.Text.RegularExpressions.Regex.IsMatch(Name,
            @"^(Microsoft|Dynamics|Power ?(Apps|Automate|Platform|BI|Pages)|Dataverse|Common Data|AI ?Builder|Customer (Insights|Voice)|" +
            "Teams|SharePoint|Exchange|Office|Outlook|Copilot|Omnichannel|Field Service|Project)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

/// <summary>A row of something with a name, a detail and a state - a SharePoint site, an email server profile.</summary>
public sealed record NamedRow(string Name, string? Detail, string? State);

/// <summary>One group of an aggregate: its keys as raw values and as labels, its row count and, if asked for, a sum.</summary>
public sealed record AggregateRow(IReadOnlyList<string> Keys, IReadOnlyList<string> Labels, long Count, decimal Sum);

/// <summary>
/// The reads behind the solution inventory: what is in scope, how big it is and how often it runs.
/// Read-only throughout.
/// </summary>
public sealed partial class DataverseClient
{
    /// <summary>Most flow runs read one by one when the flowrun table will not aggregate.</summary>
    public const int FlowRunScanLimit = 250_000;

    /// <summary>
    /// Every table, with its custom columns. One request: the column list is filtered to custom
    /// columns on the server, or - where that nested filter is refused - here.
    /// </summary>
    public async Task<IReadOnlyList<InventoryTable>> GetInventoryTablesAsync(CancellationToken ct = default)
    {
        const string select = "EntityDefinitions?$select=MetadataId,LogicalName,DisplayName,IsCustomEntity,IsManaged,IsIntersect," +
                              "TableType,OwnershipType,IsAuditEnabled,ChangeTrackingEnabled,IsPrivate,IsValidForAdvancedFind";
        try
        {
            return await ReadInventoryTablesAsync(
                select + "&$expand=Attributes($select=MetadataId,LogicalName,AttributeOf;$filter=IsCustomAttribute eq true)",
                filterHere: false, ct).ConfigureAwait(false);
        }
        catch (DataverseException ex) when (ex.StatusCode is HttpStatusCode.BadRequest)
        {
            return await ReadInventoryTablesAsync(
                select + "&$expand=Attributes($select=MetadataId,LogicalName,AttributeOf,IsCustomAttribute)",
                filterHere: true, ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<InventoryTable>> ReadInventoryTablesAsync(string query, bool filterHere, CancellationToken ct)
    {
        var tables = new List<InventoryTable>();

        await ForEachRowAsync(EnvironmentUrl + ApiPath + query, null, row =>
        {
            var logicalName = JsonHelper.GetString(row, "LogicalName");
            if (logicalName is null || !Guid.TryParse(JsonHelper.GetString(row, "MetadataId"), out var id)) return;

            var columns = new List<InventoryColumn>();
            if (row.TryGetProperty("Attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in attributes.EnumerateArray())
                {
                    // A lookup's name column and the like belong to the column they serve.
                    if (JsonHelper.GetString(a, "AttributeOf") is { Length: > 0 }) continue;
                    if (filterHere && JsonHelper.GetBool(a, "IsCustomAttribute") != true) continue;
                    if (!Guid.TryParse(JsonHelper.GetString(a, "MetadataId"), out var columnId)) continue;

                    columns.Add(new InventoryColumn(columnId, JsonHelper.GetString(a, "LogicalName") ?? columnId.ToString()));
                }
            }

            tables.Add(new InventoryTable(
                id,
                logicalName,
                LocalizedLabel(row, "DisplayName"),
                JsonHelper.GetBool(row, "IsCustomEntity") == true,
                JsonHelper.GetBool(row, "IsManaged") == true,
                JsonHelper.GetBool(row, "IsIntersect") == true,
                JsonHelper.GetString(row, "TableType"),
                JsonHelper.GetString(row, "OwnershipType"),
                ManagedBool(row, "IsAuditEnabled"),
                JsonHelper.GetBool(row, "ChangeTrackingEnabled") == true,
                JsonHelper.GetBool(row, "IsPrivate") == true,
                JsonHelper.GetBool(row, "IsValidForAdvancedFind") == true,
                columns));
        }, ct).ConfigureAwait(false);

        return tables;
    }

    /// <summary>A BooleanManagedProperty's value - {"Value": true, ...}.</summary>
    private static bool ManagedBool(JsonElement row, string property) =>
        row.TryGetProperty(property, out var managed) && JsonHelper.GetBool(managed, "Value") == true;

    /// <summary>
    /// Dataverse's stored row counts (RetrieveTotalRecordCount), a hundred tables to a request. One
    /// table the message does not support fails its whole batch, so a failing batch is halved until
    /// that table is found and left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, long>> GetRecordCountsAsync(IEnumerable<string> logicalNames, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        async Task Count(string[] batch)
        {
            try
            {
                var names = Uri.EscapeDataString(JsonSerializer.Serialize(batch));
                using var doc = await GetJsonAsync(
                    EnvironmentUrl + ApiPath + $"RetrieveTotalRecordCount(EntityNames=@p1)?@p1={names}", ct).ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("EntityRecordCountCollection", out var collection) ||
                    !collection.TryGetProperty("Keys", out var keys) || !collection.TryGetProperty("Values", out var values))
                {
                    return;
                }

                for (var i = 0; i < keys.GetArrayLength() && i < values.GetArrayLength(); i++)
                {
                    if (keys[i].GetString() is { } key && values[i].TryGetInt64(out var count)) counts[key] = count;
                }
            }
            catch (DataverseException ex) when (ex.StatusCode is HttpStatusCode.BadRequest)
            {
                if (batch.Length == 1) return;
                await Count(batch[..(batch.Length / 2)]).ConfigureAwait(false);
                await Count(batch[(batch.Length / 2)..]).ConfigureAwait(false);
            }
        }

        foreach (var chunk in logicalNames.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            await Count(chunk).ConfigureAwait(false);
        }

        return counts;
    }

    /// <summary>
    /// The tables these solutions hold with "include all subcomponents" - their columns are then not
    /// listed one by one, but every one of them is the solution's.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> GetTablesWithAllSubcomponentsAsync(IEnumerable<Guid> solutionIds, CancellationToken ct = default)
    {
        var tables = new HashSet<Guid>();

        foreach (var chunk in solutionIds.Distinct().Chunk(25))
        {
            var url = EnvironmentUrl + ApiPath + "solutioncomponents?$select=objectid,rootcomponentbehavior&$filter=" +
                      Uri.EscapeDataString($"componenttype eq 1 and rootcomponentbehavior eq 0 and {OrFilter("_solutionid_value", chunk)}");

            await ForEachRowAsync(url, null, row =>
            {
                if (Guid.TryParse(JsonHelper.GetString(row, "objectid"), out var id)) tables.Add(id);
            }, ct).ConfigureAwait(false);
        }

        return tables;
    }

    /// <summary>(a eq x or a eq y) - for lookups, which In() does not address by their _value name.</summary>
    private static string OrFilter(string property, IEnumerable<Guid> ids) =>
        "(" + string.Join(" or ", ids.Select(id => $"{property} eq {id}")) + ")";

    /// <summary>Rows of one query, by id fifty to a request, and by a lookup twenty-five to a request.</summary>
    private async Task<List<T>> ReadByIdsAsync<T>(
        string select, string idColumn, IEnumerable<Guid> ids, Func<JsonElement, T?> read, CancellationToken ct, string? lookup = null,
        IEnumerable<Guid>? lookupIds = null, Func<T, Guid>? key = null)
        where T : class
    {
        var rows = new List<T>();

        foreach (var chunk in ids.Distinct().Chunk(50))
        {
            rows.AddRange(await ReadRowsAsync(
                EnvironmentUrl + ApiPath + select + "&$filter=" + InFilter(idColumn, chunk.Select(id => id.ToString())), read, ct)
                .ConfigureAwait(false));
        }

        if (lookup is not null && lookupIds is not null)
        {
            foreach (var chunk in lookupIds.Distinct().Chunk(25))
            {
                rows.AddRange(await ReadRowsAsync(
                    EnvironmentUrl + ApiPath + select + "&$filter=" + Uri.EscapeDataString(OrFilter(lookup, chunk)), read, ct)
                    .ConfigureAwait(false));
            }
        }

        return key is null ? rows : rows.DistinctBy(key).ToList();
    }

    /// <summary>Process definitions by id: category, table, state, mode, scope and what starts them.</summary>
    public Task<List<ProcessInfo>> GetProcessesAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
        ReadByIdsAsync(
            "workflows?$select=workflowid,name,category,primaryentity,statecode,mode,scope,ondemand,triggeroncreate," +
            "triggerondelete,triggeronupdateattributelist,_ownerid_value,modifiedon",
            "workflowid", ids, row =>
            {
                if (!Guid.TryParse(JsonHelper.GetString(row, "workflowid"), out var id)) return null;
                var category = JsonHelper.GetInt(row, "category") ?? -1;

                return new ProcessInfo(
                    id,
                    JsonHelper.GetString(row, "name") ?? id.ToString(),
                    category,
                    Label(row, "category") ?? ComponentTypes.GetProcessCategoryName(category),
                    JsonHelper.GetString(row, "primaryentity") is { } table && table != "none" ? table : null,
                    JsonHelper.GetInt(row, "statecode") == 1,
                    Label(row, "mode"),
                    Label(row, "scope"),
                    ProcessTriggers(row),
                    Label(row, "_ownerid_value"),
                    JsonHelper.GetDate(row, "modifiedon"));
            }, ct);

    /// <summary>On demand, on create, on delete, on update of which columns - a classic process's starts.</summary>
    internal static string ProcessTriggers(JsonElement row)
    {
        var triggers = new List<string>();
        if (JsonHelper.GetBool(row, "ondemand") == true) triggers.Add("On demand");
        if (JsonHelper.GetBool(row, "triggeroncreate") == true) triggers.Add("Create");
        if (JsonHelper.GetBool(row, "triggerondelete") == true) triggers.Add("Delete");
        if (JsonHelper.GetString(row, "triggeronupdateattributelist") is { Length: > 0 } columns) triggers.Add($"Update ({columns})");
        return string.Join(", ", triggers);
    }

    /// <summary>Cloud flow definitions (clientdata) by workflow id - ten to a request, since each can be large.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetFlowDefinitionsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var definitions = new Dictionary<Guid, string>();

        foreach (var chunk in ids.Distinct().Chunk(10))
        {
            await ForEachRowAsync(
                EnvironmentUrl + ApiPath + "workflows?$select=workflowid,clientdata&$filter=" +
                InFilter("workflowid", chunk.Select(id => id.ToString())),
                null, row =>
                {
                    if (Guid.TryParse(JsonHelper.GetString(row, "workflowid"), out var id) &&
                        JsonHelper.GetString(row, "clientdata") is { Length: > 0 } definition)
                    {
                        definitions[id] = definition;
                    }
                }, ct).ConfigureAwait(false);
        }

        return definitions;
    }

    public Task<List<PluginAssemblyInfo>> GetPluginAssembliesAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
        ReadByIdsAsync("pluginassemblies?$select=pluginassemblyid,name,version,isolationmode,sourcetype", "pluginassemblyid", ids,
            row => Guid.TryParse(JsonHelper.GetString(row, "pluginassemblyid"), out var id)
                ? new PluginAssemblyInfo(id, JsonHelper.GetString(row, "name") ?? id.ToString(), JsonHelper.GetString(row, "version"),
                    Label(row, "isolationmode"), Label(row, "sourcetype"))
                : null, ct);

    /// <summary>Plug-in types by id, and every type in the given assemblies - a type is the solution's when its assembly is.</summary>
    public Task<List<PluginTypeInfo>> GetPluginTypesAsync(IEnumerable<Guid> ids, IEnumerable<Guid> assemblyIds, CancellationToken ct = default) =>
        ReadByIdsAsync("plugintypes?$select=plugintypeid,typename,isworkflowactivity,_pluginassemblyid_value", "plugintypeid", ids,
            row => Guid.TryParse(JsonHelper.GetString(row, "plugintypeid"), out var id)
                ? new PluginTypeInfo(
                    id,
                    JsonHelper.GetString(row, "typename") ?? id.ToString(),
                    Guid.TryParse(JsonHelper.GetString(row, "_pluginassemblyid_value"), out var assembly) ? assembly : null,
                    Label(row, "_pluginassemblyid_value"),
                    JsonHelper.GetBool(row, "isworkflowactivity") == true)
                : null,
            ct, "_pluginassemblyid_value", assemblyIds, t => t.Id);

    /// <summary>Steps by id, and every step handled by the given plug-in types or service endpoints.</summary>
    public Task<List<InventoryStep>> GetInventoryStepsAsync(IEnumerable<Guid> ids, IEnumerable<Guid> handlerIds, CancellationToken ct = default) =>
        ReadByIdsAsync(
            "sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid,name,stage,mode,statecode,_sdkmessageid_value,_eventhandler_value" +
            "&$expand=sdkmessagefilterid($select=primaryobjecttypecode)",
            "sdkmessageprocessingstepid", ids, row =>
            {
                if (!Guid.TryParse(JsonHelper.GetString(row, "sdkmessageprocessingstepid"), out var id)) return null;
                var filter = row.TryGetProperty("sdkmessagefilterid", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;

                return new InventoryStep(
                    id,
                    JsonHelper.GetString(row, "name") ?? id.ToString(),
                    Label(row, "_sdkmessageid_value"),
                    JsonHelper.GetString(filter, "primaryobjecttypecode") is { } table && table != "none" ? table : null,
                    Label(row, "stage") ?? JsonHelper.GetInt(row, "stage")?.ToString(CultureInfo.InvariantCulture),
                    JsonHelper.GetInt(row, "mode") == 1,
                    JsonHelper.GetInt(row, "statecode") == 0,
                    Guid.TryParse(JsonHelper.GetString(row, "_eventhandler_value"), out var handler) ? handler : null,
                    Label(row, "_eventhandler_value"));
            }, ct, "_eventhandler_value", handlerIds, s => s.Id);

    public Task<List<CustomApiInfo>> GetCustomApisAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
        ReadByIdsAsync(
            "customapis?$select=customapiid,uniquename,displayname,bindingtype,boundentitylogicalname,isfunction,isprivate,_plugintypeid_value",
            "customapiid", ids,
            row => Guid.TryParse(JsonHelper.GetString(row, "customapiid"), out var id)
                ? new CustomApiInfo(
                    id,
                    JsonHelper.GetString(row, "uniquename") ?? id.ToString(),
                    JsonHelper.GetString(row, "displayname"),
                    Label(row, "bindingtype"),
                    JsonHelper.GetString(row, "boundentitylogicalname"),
                    JsonHelper.GetBool(row, "isfunction") == true,
                    JsonHelper.GetBool(row, "isprivate") == true,
                    Label(row, "_plugintypeid_value"))
                : null, ct);

    public Task<List<ServiceEndpointInfo>> GetServiceEndpointsAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
        ReadByIdsAsync(
            "serviceendpoints?$select=serviceendpointid,name,contract,namespaceaddress,path,url,messageformat,authtype",
            "serviceendpointid", ids, row =>
            {
                if (!Guid.TryParse(JsonHelper.GetString(row, "serviceendpointid"), out var id)) return null;

                // A webhook's address can carry a key in its query string; only the path is documented.
                var url = JsonHelper.GetString(row, "url");
                var target = !string.IsNullOrEmpty(url)
                    ? Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : url.Split('?')[0]
                    : JsonHelper.GetString(row, "namespaceaddress") + JsonHelper.GetString(row, "path");

                return new ServiceEndpointInfo(
                    id, JsonHelper.GetString(row, "name") ?? id.ToString(), Label(row, "contract"), target,
                    Label(row, "messageformat"), Label(row, "authtype"));
            }, ct);

    public Task<List<CustomConnectorInfo>> GetCustomConnectorsAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
        ReadByIdsAsync("connectors?$select=connectorid,name,displayname,connectortype", "connectorid", ids,
            row => Guid.TryParse(JsonHelper.GetString(row, "connectorid"), out var id)
                ? new CustomConnectorInfo(
                    id, JsonHelper.GetString(row, "displayname") ?? JsonHelper.GetString(row, "name") ?? id.ToString(), Label(row, "connectortype"))
                : null, ct);

    /// <summary>Every application user, with its security roles.</summary>
    public async Task<IReadOnlyList<ApplicationUserInfo>> GetApplicationUsersAsync(CancellationToken ct = default) =>
        await ReadRowsAsync(
            EnvironmentUrl + ApiPath +
            "systemusers?$select=systemuserid,fullname,applicationid,isdisabled,_businessunitid_value" +
            "&$filter=applicationid ne null&$expand=systemuserroles_association($select=name)",
            row =>
            {
                if (!Guid.TryParse(JsonHelper.GetString(row, "systemuserid"), out var id)) return null;

                var roles = new List<string>();
                if (row.TryGetProperty("systemuserroles_association", out var assigned) && assigned.ValueKind == JsonValueKind.Array)
                {
                    roles.AddRange(assigned.EnumerateArray().Select(r => JsonHelper.GetString(r, "name")).OfType<string>()
                        .Distinct().Order(StringComparer.CurrentCultureIgnoreCase));
                }

                return new ApplicationUserInfo(
                    id,
                    JsonHelper.GetString(row, "fullname") ?? id.ToString(),
                    Guid.TryParse(JsonHelper.GetString(row, "applicationid"), out var app) ? app : null,
                    JsonHelper.GetBool(row, "isdisabled") != true,
                    roles,
                    Label(row, "_businessunitid_value"));
            }, ct).ConfigureAwait(false);

    /// <summary>The SharePoint sites document management is set up with.</summary>
    public async Task<IReadOnlyList<NamedRow>> GetSharePointSitesAsync(CancellationToken ct = default) =>
        await ReadRowsAsync(EnvironmentUrl + ApiPath + "sharepointsites?$select=name,absoluteurl,statecode",
            row => new NamedRow(JsonHelper.GetString(row, "name") ?? "(no name)", JsonHelper.GetString(row, "absoluteurl"), Label(row, "statecode")),
            ct).ConfigureAwait(false);

    /// <summary>The email server profiles server-side synchronisation uses.</summary>
    public async Task<IReadOnlyList<NamedRow>> GetEmailServerProfilesAsync(CancellationToken ct = default) =>
        await ReadRowsAsync(EnvironmentUrl + ApiPath + "emailserverprofiles?$select=name,statecode",
            row => new NamedRow(JsonHelper.GetString(row, "name") ?? "(no name)", null, Label(row, "statecode")),
            ct).ConfigureAwait(false);

    // ---------------------------------------------------------------- aggregates

    /// <summary>
    /// A FetchXML aggregate: a row count (and a sum, if asked for) per group. Dataverse refuses an
    /// aggregate over more than 50,000 rows, so when it does the period is halved and each half
    /// aggregated on its own, the results added together.
    /// </summary>
    /// <param name="groupBy">Column names; "column:month" and the like group a date.</param>
    /// <param name="dateColumn">The column the period applies to; null for no period, and no halving.</param>
    public async Task<IReadOnlyList<AggregateRow>> AggregateAsync(
        string table, string entitySet, string primaryId, IReadOnlyList<string> groupBy,
        string filterXml = "", string? sumColumn = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? dateColumn = "createdon", CancellationToken ct = default)
    {
        var groups = new Dictionary<string, AggregateRow>();
        var lo = from ?? new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var hi = to ?? DateTimeOffset.UtcNow.AddDays(1);

        await AggregateRangeAsync(table, entitySet, primaryId, groupBy, filterXml, sumColumn, dateColumn, lo, hi, groups, ct)
            .ConfigureAwait(false);

        return groups.Values.ToList();
    }

    private async Task AggregateRangeAsync(
        string table, string entitySet, string primaryId, IReadOnlyList<string> groupBy, string filterXml, string? sumColumn,
        string? dateColumn, DateTimeOffset lo, DateTimeOffset hi, Dictionary<string, AggregateRow> groups, CancellationToken ct)
    {
        var fetch = new StringBuilder($"<fetch aggregate='true'><entity name='{table}'>")
            .Append($"<attribute name='{primaryId}' alias='cnt' aggregate='count'/>");
        if (sumColumn is not null) fetch.Append($"<attribute name='{sumColumn}' alias='total' aggregate='sum'/>");

        for (var i = 0; i < groupBy.Count; i++)
        {
            var parts = groupBy[i].Split(':');
            fetch.Append($"<attribute name='{parts[0]}' alias='g{i}' groupby='true'{(parts.Length > 1 ? $" dategrouping='{parts[1]}'" : "")}/>");
        }

        var conditions = (dateColumn is null
            ? string.Empty
            : $"<condition attribute='{dateColumn}' operator='ge' value='{Iso(lo)}'/><condition attribute='{dateColumn}' operator='lt' value='{Iso(hi)}'/>") +
            filterXml;
        if (conditions.Length > 0) fetch.Append("<filter>").Append(conditions).Append("</filter>");
        fetch.Append("</entity></fetch>");

        JsonDocument doc;
        try
        {
            doc = await GetJsonAsync(
                EnvironmentUrl + ApiPath + $"{entitySet}?fetchXml={Uri.EscapeDataString(fetch.ToString())}",
                ct, Annotations.Formatted, maxPageSize: false).ConfigureAwait(false);
        }
        catch (DataverseException ex) when (IsAggregateLimit(ex) && dateColumn is not null && hi - lo > TimeSpan.FromMinutes(1))
        {
            var mid = lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
            await AggregateRangeAsync(table, entitySet, primaryId, groupBy, filterXml, sumColumn, dateColumn, lo, mid, groups, ct).ConfigureAwait(false);
            await AggregateRangeAsync(table, entitySet, primaryId, groupBy, filterXml, sumColumn, dateColumn, mid, hi, groups, ct).ConfigureAwait(false);
            return;
        }

        using (doc)
        {
            foreach (var row in JsonHelper.Rows(doc.RootElement))
            {
                var keys = groupBy.Select((_, i) => JsonHelper.GetString(row, $"g{i}") ?? "(blank)").ToArray();
                var labels = groupBy.Select((_, i) => Label(row, $"g{i}") ?? keys[i]).ToArray();
                var count = long.TryParse(JsonHelper.GetString(row, "cnt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
                var sum = sumColumn is not null &&
                          decimal.TryParse(JsonHelper.GetString(row, "total"), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : 0m;

                var key = string.Join('\u001f', keys);
                groups[key] = groups.TryGetValue(key, out var before)
                    ? before with { Count = before.Count + count, Sum = before.Sum + sum }
                    : new AggregateRow(keys, labels, count, sum);
            }
        }
    }

    /// <summary>Dataverse's AggregateQueryRecordLimit (0x8004E023) - over 50,000 rows to aggregate.</summary>
    internal static bool IsAggregateLimit(DataverseException ex) =>
        ex.StatusCode is HttpStatusCode.BadRequest &&
        (ex.Message.Contains("AggregateQueryRecordLimit", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("maximum record limit", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("8004E023", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cloud flow runs since a time, per flow and status. The flowrun table is elastic and may refuse
    /// to aggregate; then its rows are read and counted here, up to <see cref="FlowRunScanLimit"/>.
    /// </summary>
    /// <returns>Groups keyed by flow id then status, and whether the scan stopped at its limit.</returns>
    public async Task<(IReadOnlyList<AggregateRow> Runs, bool Truncated)> GetFlowRunCountsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        try
        {
            var aggregate = await AggregateAsync("flowrun", "flowruns", "flowrunid", ["workflow", "status"],
                from: since, dateColumn: "starttime", ct: ct).ConfigureAwait(false);
            return (aggregate, false);
        }
        catch (DataverseException ex) when (!IsAggregateLimit(ex))
        {
            var groups = new Dictionary<(string Flow, string Status), (string Name, long Count)>();
            var read = 0;
            var url = EnvironmentUrl + ApiPath + "flowruns?$select=status,_workflow_value&$filter=" +
                      Uri.EscapeDataString($"starttime ge {Iso(since)}");

            while (url.Length > 0 && read < FlowRunScanLimit)
            {
                using var doc = await GetJsonAsync(url, ct, Annotations.Formatted).ConfigureAwait(false);

                foreach (var row in JsonHelper.Rows(doc.RootElement))
                {
                    read++;
                    var key = (JsonHelper.GetString(row, "_workflow_value") ?? "(blank)", JsonHelper.GetString(row, "status") ?? "(blank)");
                    var before = groups.GetValueOrDefault(key);
                    groups[key] = (Label(row, "_workflow_value") ?? before.Name ?? key.Item1, before.Count + 1);
                }

                url = JsonHelper.NextLink(doc.RootElement);
            }

            return (groups.Select(g => new AggregateRow([g.Key.Flow, g.Key.Status], [g.Value.Name, g.Key.Status], g.Value.Count, 0m)).ToList(),
                url.Length > 0);
        }
    }
}
