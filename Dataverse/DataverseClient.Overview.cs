using System.Text.Json;
using System.Xml.Linq;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    public const int CanvasAppType = 300;
    public const int ModelDrivenAppType = 80;
    public const int SecurityRoleType = 20;
    public const int OptionSetType = 9;

    /// <summary>The kind of Overview a component gets, or Other where it has none.</summary>
    public static ObjectKind OverviewKindOf(SolutionComponentItem item)
    {
        var logical = item.ComponentLogicalName ?? string.Empty;

        return item.ComponentType switch
        {
            CanvasAppType => ObjectKind.CanvasApp,
            ModelDrivenAppType => ObjectKind.ModelDrivenApp,
            SecurityRoleType => ObjectKind.SecurityRole,
            OptionSetType => ObjectKind.OptionSet,
            29 when (item.ProcessCategory is { } category ? category == 2 : item.SubType == "Business Rule") => ObjectKind.BusinessRule,
            29 when item.ProcessCategory == 4 || item.SubType == "Business Process Flow" => ObjectKind.BusinessProcessFlow,
            _ when logical.Equals("canvasapp", StringComparison.OrdinalIgnoreCase) => ObjectKind.CanvasApp,
            _ when logical.Equals("appmodule", StringComparison.OrdinalIgnoreCase) => ObjectKind.ModelDrivenApp,
            _ when logical.Equals("bot", StringComparison.OrdinalIgnoreCase) => ObjectKind.Agent,
            _ when logical.Equals("customapi", StringComparison.OrdinalIgnoreCase) => ObjectKind.CustomApi,
            _ => ObjectKind.Other
        };
    }

    /// <summary>The Overview of one component. Each part is read on its own; one that fails is noted, not fatal.</summary>
    public async Task<ComponentOverview> GetOverviewAsync(SolutionComponentItem item, CancellationToken ct = default)
    {
        var overview = new ComponentOverview();

        async Task Part(string what, Func<Task> read)
        {
            try
            {
                await read().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                overview.Problems.Add($"{what}: {ex.Message}");
            }
        }

        var id = item.ObjectId;

        switch (OverviewKindOf(item))
        {
            case ObjectKind.CanvasApp:
                await Part("app", () => CanvasAppAsync(id, overview, ct)).ConfigureAwait(false);
                break;

            case ObjectKind.ModelDrivenApp:
                await Part("app", () => ModelDrivenAppAsync(id, overview, ct)).ConfigureAwait(false);
                await Part("components", () => AppComponentsAsync(id, overview, ct)).ConfigureAwait(false);
                await Part("security roles", () => AppRolesAsync(id, overview, ct)).ConfigureAwait(false);
                break;

            case ObjectKind.Agent:
                await Part("agent", () => AgentAsync(id, overview, ct)).ConfigureAwait(false);
                break;

            case ObjectKind.CustomApi:
                await Part("custom API", () => CustomApiAsync(id, overview, ct)).ConfigureAwait(false);
                break;

            case ObjectKind.SecurityRole:
                await Part("privileges", () => RoleAsync(id, overview, ct)).ConfigureAwait(false);
                break;

            case ObjectKind.OptionSet:
                await Part("choice", () => OptionSetAsync(id, overview, ct)).ConfigureAwait(false);
                break;

            case ObjectKind.BusinessProcessFlow:
                await Part("stages", () => ProcessStagesAsync(id, overview, ct)).ConfigureAwait(false);
                break;

            case ObjectKind.BusinessRule:
                await Part("business rule", () => BusinessRuleAsync(id, overview, ct)).ConfigureAwait(false);
                break;
        }

        return overview;
    }

    private async Task<JsonDocument> GetOneAsync(string query, CancellationToken ct) =>
        await GetJsonAsync(EnvironmentUrl + ApiPath + query, ct, Annotations.Formatted, maxPageSize: false).ConfigureAwait(false);

    private static string? Shown(JsonElement row, string column) =>
        JsonHelper.GetString(row, column + "@" + Annotations.Formatted) ?? JsonHelper.GetString(row, column);

    private async Task CanvasAppAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        using var doc = await GetOneAsync(
            $"canvasapps({id})?$select=displayname,name,appversion,lastpublishtime,canvasapptype,_ownerid_value,connectionreferences,commitmessage",
            ct).ConfigureAwait(false);
        var app = doc.RootElement;

        overview.Properties.Add(new("Name", JsonHelper.GetString(app, "displayname")));
        overview.Properties.Add(new("Unique name", JsonHelper.GetString(app, "name")));
        overview.Properties.Add(new("Type", Shown(app, "canvasapptype")));
        overview.Properties.Add(new("Owner", Shown(app, "_ownerid_value")));
        overview.Properties.Add(new("App version", JsonHelper.GetString(app, "appversion")));
        overview.Properties.Add(new("Last published", Shown(app, "lastpublishtime")));
        overview.Properties.Add(new("Last commit message", JsonHelper.GetString(app, "commitmessage")));

        // The app's own record of its connections, as JSON keyed by connection id.
        var connections = new List<IReadOnlyList<string?>>();
        if (JsonHelper.GetString(app, "connectionreferences") is { Length: > 0 } json)
        {
            try
            {
                using var refs = JsonDocument.Parse(json);
                if (refs.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var reference in refs.RootElement.EnumerateObject())
                    {
                        var value = reference.Value;
                        connections.Add([
                            JsonHelper.GetString(value, "displayName") ?? JsonHelper.GetString(value, "apiName"),
                            JsonHelper.GetString(value, "id")?.Split('/').LastOrDefault(),
                            JsonHelper.GetString(value, "connectionReferenceLogicalName")
                        ]);
                    }
                }
            }
            catch (JsonException)
            {
                overview.Problems.Add("connections: the app's connection list did not parse");
            }
        }

        overview.Tables.Add(new OverviewTable
        {
            Title = "Connections",
            Columns = ["Connector", "API", "Connection reference"],
            Rows = connections
        });
    }

    private async Task ModelDrivenAppAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        using var doc = await GetOneAsync(
            $"appmodules({id})?$select=name,uniquename,description,appmoduleversion,publishedon,clienttype,navigationtype", ct).ConfigureAwait(false);
        var app = doc.RootElement;
        var unique = JsonHelper.GetString(app, "uniquename");

        overview.Properties.Add(new("Name", JsonHelper.GetString(app, "name")));
        overview.Properties.Add(new("Unique name", unique));
        overview.Properties.Add(new("Description", JsonHelper.GetString(app, "description")));
        overview.Properties.Add(new("Version", JsonHelper.GetString(app, "appmoduleversion")));
        overview.Properties.Add(new("Published", Shown(app, "publishedon")));
        overview.Properties.Add(new("Navigation", Shown(app, "navigationtype")));

        if (string.IsNullOrWhiteSpace(unique)) return;

        // The app's sitemap shares its unique name.
        using var sitemaps = await GetOneAsync(
            $"sitemaps?$select=sitemapxml&$filter=sitemapnameunique eq '{Escape(unique!)}'", ct).ConfigureAwait(false);
        if (sitemaps.RootElement.TryGetProperty("value", out var rows) && rows.GetArrayLength() > 0 &&
            JsonHelper.GetString(rows[0], "sitemapxml") is { Length: > 0 } xml)
        {
            overview.Tables.Add(new OverviewTable
            {
                Title = "Navigation (sitemap)",
                Columns = ["Navigation", "Opens"],
                Rows = SitemapTree(xml)
            });
        }
    }

    /// <summary>A sitemap as rows of area, group, subarea and what the subarea opens.</summary>
    public static IReadOnlyList<IReadOnlyList<string?>> SitemapOutline(string xml)
    {
        var rows = new List<IReadOnlyList<string?>>();
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return rows;
        }

        static string? Title(XElement e) =>
            e.Element("Titles")?.Elements("Title").FirstOrDefault()?.Attribute("Title")?.Value
            ?? (string?)e.Attribute("Title") ?? (string?)e.Attribute("Id");

        foreach (var area in root.Descendants("Area"))
        {
            foreach (var group in area.Elements("Group"))
            {
                foreach (var sub in group.Elements("SubArea"))
                {
                    var opens = (string?)sub.Attribute("Entity") is { Length: > 0 } entity ? $"Table: {entity}"
                        : (string?)sub.Attribute("Url") is { Length: > 0 } url ? url
                        : null;
                    rows.Add([Title(area), Title(group), Title(sub), opens]);
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// A sitemap as an indented tree - areas, their groups, and each group's items - with what each
    /// item opens, read top to bottom as the app's navigation shows it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string?>> SitemapTree(string xml)
    {
        var rows = new List<IReadOnlyList<string?>>();
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return rows;
        }

        static string Title(XElement e) =>
            e.Element("Titles")?.Elements("Title").FirstOrDefault()?.Attribute("Title")?.Value
            ?? (string?)e.Attribute("Title") ?? (string?)e.Attribute("Id") ?? "(untitled)";

        foreach (var area in root.Descendants("Area"))
        {
            rows.Add([Title(area), null]);
            foreach (var group in area.Elements("Group"))
            {
                rows.Add(["    " + Title(group), null]);
                foreach (var sub in group.Elements("SubArea"))
                {
                    var opens = (string?)sub.Attribute("Entity") is { Length: > 0 } entity ? $"Table: {entity}"
                        : (string?)sub.Attribute("DashboardId") is { Length: > 0 } dashboard ? $"Dashboard: {dashboard}"
                        : (string?)sub.Attribute("Url") is { Length: > 0 } url ? url
                        : null;
                    rows.Add(["        " + Title(sub), opens]);
                }
            }
        }

        return rows;
    }

    /// <summary>App component types by what a maker calls them, and where each one's name is read.</summary>
    private static readonly Dictionary<int, (string Kind, string Set, string Id, string Name, string? Table)> AppComponentSources = new()
    {
        [26] = ("View", "savedqueries", "savedqueryid", "name", "returnedtypecode"),
        [59] = ("Chart", "savedqueryvisualizations", "savedqueryvisualizationid", "name", "primaryentitytypecode"),
        [60] = ("Form", "systemforms", "formid", "name", "objecttypecode"),
        [29] = ("Business process flow", "workflows", "workflowid", "name", "primaryentity"),
        [62] = ("Sitemap", "sitemaps", "sitemapid", "sitemapname", null)
    };

    private async Task AppComponentsAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        var components = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + $"appmodulecomponents?$select=componenttype,objectid&$filter=_appmoduleidunique_value eq {await AppUniqueIdAsync(id, ct).ConfigureAwait(false)}",
            row => Guid.TryParse(JsonHelper.GetString(row, "objectid"), out var objectId)
                ? Tuple.Create(JsonHelper.GetInt(row, "componenttype") ?? 0, objectId, AppComponentKind(JsonHelper.GetInt(row, "componenttype") ?? 0, Label(row, "componenttype")))
                : null,
            ct).ConfigureAwait(false);

        // Each type's names are read on their own; a type that cannot be read keeps its ids.
        var names = new Dictionary<Guid, (string Kind, string Name, string? Table)>();
        var unnamed = new List<string>();

        foreach (var group in components.GroupBy(c => c.Item1))
        {
            try
            {
                if (group.Key == 1)
                {
                    foreach (var (metadataId, logicalName, display) in await TableNamesAsync(ct).ConfigureAwait(false))
                    {
                        names[metadataId] = ("Table", display ?? logicalName, logicalName);
                    }
                    continue;
                }

                if (!AppComponentSources.TryGetValue(group.Key, out var source)) continue;

                foreach (var chunk in group.Select(c => c.Item2).Distinct().Chunk(50))
                {
                    var select = source.Table is null ? $"{source.Id},{source.Name}" : $"{source.Id},{source.Name},{source.Table}";
                    if (group.Key == 60) select += ",type";

                    var rows = await ReadRowsAsync(
                        EnvironmentUrl + ApiPath + $"{source.Set}?$select={select}&$filter=" + InFilter(source.Id, chunk.Select(c => c.ToString())),
                        row => Guid.TryParse(JsonHelper.GetString(row, source.Id), out var rowId)
                            ? Tuple.Create(rowId,
                                // A dashboard is a form of type Dashboard.
                                group.Key == 60 && JsonHelper.GetInt(row, "type") is 0 or 10 ? "Dashboard" : source.Kind,
                                JsonHelper.GetString(row, source.Name) ?? rowId.ToString(),
                                source.Table is null ? null : JsonHelper.GetString(row, source.Table))
                            : null,
                        ct).ConfigureAwait(false);

                    foreach (var (rowId, kind, name, table) in rows) names[rowId] = (kind, name, table == "none" ? null : table);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                unnamed.Add($"{group.First().Item3}: {ex.Message}");
            }
        }

        if (unnamed.Count > 0) overview.Problems.Add("component names: " + string.Join("; ", unnamed));

        var order = new[] { "Table", "Form", "View", "Dashboard", "Chart", "Business process flow", "Sitemap" };
        overview.Tables.Add(new OverviewTable
        {
            Title = "Tables, forms, views and dashboards in the app",
            Columns = ["Kind", "Name", "Table"],
            Rows = components
                .Select(c => names.TryGetValue(c.Item2, out var n)
                    ? (IReadOnlyList<string?>)[n.Kind, n.Name, n.Table]
                    : [c.Item3, c.Item2.ToString(), null])
                .OrderBy(r => Array.IndexOf(order, r[0]) is var i and >= 0 ? i : order.Length)
                .ThenBy(r => r[0], StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r[2], StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r[1], StringComparer.CurrentCultureIgnoreCase)
                .ToList()
        });
    }

    private static string AppComponentKind(int type, string? label) =>
        type == 1 ? "Table"
        : AppComponentSources.TryGetValue(type, out var source) ? source.Kind
        : label ?? ComponentTypes.GetName(type);

    /// <summary>Every table's metadata id, logical name and display name - app components name tables by metadata id.</summary>
    private async Task<IReadOnlyList<(Guid MetadataId, string LogicalName, string? DisplayName)>> TableNamesAsync(CancellationToken ct)
    {
        using var doc = await GetOneAsync("EntityDefinitions?$select=MetadataId,LogicalName,DisplayName", ct).ConfigureAwait(false);
        var tables = new List<(Guid, string, string?)>();

        if (doc.RootElement.TryGetProperty("value", out var value))
        {
            foreach (var row in value.EnumerateArray())
            {
                if (Guid.TryParse(JsonHelper.GetString(row, "MetadataId"), out var metadataId) &&
                    JsonHelper.GetString(row, "LogicalName") is { Length: > 0 } logicalName)
                {
                    tables.Add((metadataId, logicalName, LocalizedLabel(row, "DisplayName")));
                }
            }
        }

        return tables;
    }

    private async Task<Guid> AppUniqueIdAsync(Guid id, CancellationToken ct)
    {
        using var doc = await GetOneAsync($"appmodules({id})?$select=appmoduleidunique", ct).ConfigureAwait(false);
        return Guid.TryParse(JsonHelper.GetString(doc.RootElement, "appmoduleidunique"), out var unique) ? unique : id;
    }

    private async Task AppRolesAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        var roles = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + $"appmodules({id})/appmoduleroles_association?$select=name",
            row => (IReadOnlyList<string?>)[JsonHelper.GetString(row, "name")], ct).ConfigureAwait(false);

        overview.Tables.Add(new OverviewTable
        {
            Title = "Security roles with access",
            Columns = ["Role"],
            Rows = roles.OrderBy(r => r[0], StringComparer.CurrentCultureIgnoreCase).ToList()
        });
    }

    private async Task AgentAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        using var doc = await GetOneAsync($"bots({id})?$select=name,schemaname,language,publishedon,authenticationmode,accesscontrolpolicy", ct)
            .ConfigureAwait(false);
        var bot = doc.RootElement;

        overview.Properties.Add(new("Name", JsonHelper.GetString(bot, "name")));
        overview.Properties.Add(new("Schema name", JsonHelper.GetString(bot, "schemaname")));
        overview.Properties.Add(new("Language", Shown(bot, "language")));
        overview.Properties.Add(new("Last published", Shown(bot, "publishedon")));
        overview.Properties.Add(new("Authentication", Shown(bot, "authenticationmode")));
        overview.Properties.Add(new("Access", Shown(bot, "accesscontrolpolicy")));

        var components = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + $"botcomponents?$select=name,componenttype,schemaname&$filter=_parentbotid_value eq {id}",
            row => (IReadOnlyList<string?>)[Shown(row, "componenttype"), JsonHelper.GetString(row, "name"), JsonHelper.GetString(row, "schemaname")],
            ct).ConfigureAwait(false);

        await AgentChannelsAsync(id, overview, ct).ConfigureAwait(false);
        await AgentFlowsAsync(id, overview, ct).ConfigureAwait(false);

        overview.Tables.Add(new OverviewTable
        {
            Title = "Topics, knowledge and actions",
            Columns = ["Kind", "Name", "Schema name"],
            Rows = components.OrderBy(r => r[0]).ThenBy(r => r[1], StringComparer.CurrentCultureIgnoreCase).ToList()
        });
    }

    private async Task CustomApiAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        using var doc = await GetOneAsync(
            $"customapis({id})?$select=uniquename,displayname,description,isfunction,isprivate,bindingtype,boundentitylogicalname," +
            "executeprivilegename,allowedcustomprocessingsteptype,_plugintypeid_value", ct).ConfigureAwait(false);
        var api = doc.RootElement;

        overview.Properties.Add(new("Unique name", JsonHelper.GetString(api, "uniquename")));
        overview.Properties.Add(new("Display name", JsonHelper.GetString(api, "displayname")));
        overview.Properties.Add(new("Description", JsonHelper.GetString(api, "description")));
        overview.Properties.Add(new("Kind", JsonHelper.GetBool(api, "isfunction") == true ? "Function (GET)" : "Action (POST)"));
        overview.Properties.Add(new("Binding", Shown(api, "bindingtype")));
        overview.Properties.Add(new("Bound table", JsonHelper.GetString(api, "boundentitylogicalname")));
        overview.Properties.Add(new("Plug-in type", Shown(api, "_plugintypeid_value")));
        overview.Properties.Add(new("Privilege to run it", JsonHelper.GetString(api, "executeprivilegename")));
        overview.Properties.Add(new("Custom steps allowed", Shown(api, "allowedcustomprocessingsteptype")));
        overview.Properties.Add(new("Private", JsonHelper.GetBool(api, "isprivate") == true ? "Yes" : "No"));

        var requests = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + $"customapirequestparameters?$select=uniquename,type,isoptional,logicalentityname&$filter=_customapiid_value eq {id}",
            row => (IReadOnlyList<string?>)[JsonHelper.GetString(row, "uniquename"), Shown(row, "type"),
                JsonHelper.GetBool(row, "isoptional") == true ? "Optional" : "Required", JsonHelper.GetString(row, "logicalentityname")],
            ct).ConfigureAwait(false);
        overview.Tables.Add(new OverviewTable { Title = "Request parameters", Columns = ["Name", "Type", "", "Table"], Rows = requests });

        var responses = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + $"customapiresponseproperties?$select=uniquename,type,logicalentityname&$filter=_customapiid_value eq {id}",
            row => (IReadOnlyList<string?>)[JsonHelper.GetString(row, "uniquename"), Shown(row, "type"), JsonHelper.GetString(row, "logicalentityname")],
            ct).ConfigureAwait(false);
        overview.Tables.Add(new OverviewTable { Title = "Response properties", Columns = ["Name", "Type", "Table"], Rows = responses });
    }

    private async Task RoleAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        var (tables, misc) = PrivilegeMatrix.Build(await GetRolePrivilegesAsync(id, ct).ConfigureAwait(false));

        overview.Properties.Add(new("Tables with privileges", tables.Count.ToString("N0")));
        overview.Properties.Add(new("Other privileges", misc.Count.ToString("N0")));

        static string? D(PrivilegeDepth depth) => depth == PrivilegeDepth.None ? null : PrivilegeMatrix.DepthLabel(depth);

        overview.Tables.Add(new OverviewTable
        {
            Title = "Table privileges",
            Columns = ["Table", "Create", "Read", "Write", "Delete", "Append", "Append to", "Assign", "Share"],
            Rows = tables.Select(t => (IReadOnlyList<string?>)[t.Table, D(t.Create), D(t.Read), D(t.Write), D(t.Delete),
                D(t.Append), D(t.AppendTo), D(t.Assign), D(t.Share)]).ToList()
        });
        overview.Tables.Add(new OverviewTable
        {
            Title = "Other privileges",
            Columns = ["Privilege", "Depth"],
            Rows = misc.Select(m => (IReadOnlyList<string?>)[m.Name, m.DepthLabel]).ToList()
        });
    }

    /// <summary>
    /// The channels an agent is published to, from its configuration. Only channels Copilot Studio
    /// records there are known; the rest are configured in Copilot Studio itself.
    /// </summary>
    private async Task AgentChannelsAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        try
        {
            using var doc = await GetOneAsync($"bots({id})?$select=configuration", ct).ConfigureAwait(false);
            var channels = AgentChannels(JsonHelper.GetString(doc.RootElement, "configuration"));
            overview.Properties.Add(new("Channels", channels.Count > 0
                ? string.Join(", ", channels)
                : "None recorded in Dataverse - see the agent's Channels page in Copilot Studio"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            overview.Problems.Add("channels: " + ex.Message);
        }
    }

    /// <summary>Channel names in an agent's configuration JSON, wherever a "channels" list appears in it.</summary>
    internal static IReadOnlyList<string> AgentChannels(string? configuration)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(configuration)) return found;

        void Walk(JsonElement element, int depth)
        {
            if (depth > 8) return;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals("channels", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var channel in property.Value.EnumerateArray())
                        {
                            var name = channel.ValueKind == JsonValueKind.String
                                ? channel.GetString()
                                : JsonHelper.GetString(channel, "channelId") ?? JsonHelper.GetString(channel, "id") ??
                                  JsonHelper.GetString(channel, "name");
                            if (!string.IsNullOrWhiteSpace(name) && !found.Contains(name!, StringComparer.OrdinalIgnoreCase)) found.Add(name!);
                        }
                    }
                    else
                    {
                        Walk(property.Value, depth + 1);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Walk(item, depth + 1);
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(configuration);
            Walk(doc.RootElement, 0);
        }
        catch (JsonException)
        {
            // not JSON - nothing to report
        }

        return found;
    }

    /// <summary>The cloud flows an agent's topics and actions call, through their flow relationship.</summary>
    private async Task AgentFlowsAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        try
        {
            var rows = new List<IReadOnlyList<string?>>();
            var url = EnvironmentUrl + ApiPath +
                      $"botcomponents?$select=name&$filter=_parentbotid_value eq {id}" +
                      "&$expand=botcomponent_workflow($select=name,workflowid)";

            while (url.Length > 0)
            {
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("value", out var value))
                {
                    foreach (var component in value.EnumerateArray())
                    {
                        if (!component.TryGetProperty("botcomponent_workflow", out var flows) || flows.ValueKind != JsonValueKind.Array) continue;
                        foreach (var flow in flows.EnumerateArray())
                        {
                            rows.Add([JsonHelper.GetString(flow, "name"), JsonHelper.GetString(component, "name")]);
                        }
                    }
                }

                url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
            }

            overview.Tables.Add(new OverviewTable
            {
                Title = "Flows called",
                Columns = ["Flow", "Called from"],
                Rows = rows.OrderBy(r => r[0], StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r[1]).ToList()
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            overview.Problems.Add("flows called: " + ex.Message);
        }
    }

    private async Task OptionSetAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        using var doc = await GetOneAsync($"GlobalOptionSetDefinitions({id})", ct).ConfigureAwait(false);
        var set = doc.RootElement;

        overview.Properties.Add(new("Name", JsonHelper.GetString(set, "Name")));
        overview.Properties.Add(new("Display name", LocalizedLabel(set, "DisplayName")));
        overview.Properties.Add(new("Kind", JsonHelper.GetString(set, "OptionSetType")));

        var rows = new List<IReadOnlyList<string?>>();
        if (set.TryGetProperty("Options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                rows.Add([
                    option.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32().ToString() : null,
                    LocalizedLabel(option, "Label"),
                    JsonHelper.GetString(option, "Color"),
                    LocalizedLabel(option, "Description")
                ]);
            }
        }

        overview.Tables.Add(new OverviewTable { Title = "Options", Columns = ["Value", "Label", "Colour", "Description"], Rows = rows });

        try
        {
            overview.Tables.Add(new OverviewTable
            {
                Title = "Columns that use this choice",
                Columns = ["Table", "Column", "Display name"],
                Rows = await ChoiceColumnsAsync(id, ct).ConfigureAwait(false)
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            overview.Problems.Add("columns using it: " + ex.Message);
        }
    }

    /// <summary>
    /// The columns that use a global choice: its dependent components of type Attribute, named
    /// from the default solution's component summaries, which list every column.
    /// </summary>
    private async Task<IReadOnlyList<IReadOnlyList<string?>>> ChoiceColumnsAsync(Guid id, CancellationToken ct)
    {
        var columnIds = (await GetDependenciesAsync(id, OptionSetType, Models.DependencyDirection.Dependent, ct).ConfigureAwait(false))
            .Where(d => d.ComponentType == 2)
            .Select(d => d.ObjectId)
            .Distinct()
            .ToList();
        if (columnIds.Count == 0) return Array.Empty<IReadOnlyList<string?>>();

        using var solution = await GetOneAsync("solutions?$select=solutionid&$filter=uniquename eq 'Default'", ct).ConfigureAwait(false);
        var defaultId = solution.RootElement.TryGetProperty("value", out var found) && found.GetArrayLength() > 0
            ? JsonHelper.GetString(found[0], "solutionid")
            : null;

        var named = new Dictionary<Guid, IReadOnlyList<string?>>();
        if (defaultId is not null)
        {
            foreach (var chunk in columnIds.Chunk(25))
            {
                var filter = $"(msdyn_solutionid eq {defaultId}) and (msdyn_componenttype eq 2) and (" +
                             string.Join(" or ", chunk.Select(c => $"msdyn_objectid eq '{c}'")) + ")";
                var rows = await ReadRowsAsync(
                    EnvironmentUrl + ApiPath + "msdyn_solutioncomponentsummaries?$select=msdyn_objectid,msdyn_name,msdyn_displayname,msdyn_primaryentityname&$filter=" +
                    Uri.EscapeDataString(filter),
                    row => Guid.TryParse(JsonHelper.GetString(row, "msdyn_objectid"), out var columnId)
                        ? Tuple.Create(columnId, (IReadOnlyList<string?>)[
                            JsonHelper.GetString(row, "msdyn_primaryentityname"),
                            JsonHelper.GetString(row, "msdyn_name"),
                            JsonHelper.GetString(row, "msdyn_displayname")])
                        : null,
                    ct).ConfigureAwait(false);

                foreach (var (columnId, row) in rows) named[columnId] = row;
            }
        }

        return columnIds
            .Select(c => named.TryGetValue(c, out var row) ? row : (IReadOnlyList<string?>)[null, c.ToString(), null])
            .OrderBy(r => r[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r[1], StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? LocalizedLabel(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var label) && label.ValueKind == JsonValueKind.Object &&
        label.TryGetProperty("UserLocalizedLabel", out var localized) && localized.ValueKind == JsonValueKind.Object
            ? JsonHelper.GetString(localized, "Label")
            : null;

    private async Task ProcessStagesAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        var stages = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + $"processstages?$select=stagename,stagecategory,primaryentitytypecode&$filter=_processid_value eq {id}",
            row => (IReadOnlyList<string?>)[JsonHelper.GetString(row, "stagename"), Shown(row, "stagecategory"), JsonHelper.GetString(row, "primaryentitytypecode")],
            ct).ConfigureAwait(false);

        overview.Tables.Add(new OverviewTable { Title = "Stages", Columns = ["Stage", "Category", "Table"], Rows = stages });
    }
}
