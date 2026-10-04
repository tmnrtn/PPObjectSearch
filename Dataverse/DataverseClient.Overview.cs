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
            var outline = SitemapOutline(xml);
            overview.Tables.Add(new OverviewTable
            {
                Title = "Navigation (sitemap)",
                Columns = ["Area", "Group", "Item", "Opens"],
                Rows = outline
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

    private async Task AppComponentsAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        var rows = await ReadRowsAsync(
            EnvironmentUrl + ApiPath + $"appmodulecomponents?$select=componenttype,objectid&$filter=_appmoduleidunique_value eq {await AppUniqueIdAsync(id, ct).ConfigureAwait(false)}",
            row => (IReadOnlyList<string?>)[Shown(row, "componenttype"), JsonHelper.GetString(row, "objectid")], ct).ConfigureAwait(false);

        overview.Tables.Add(new OverviewTable
        {
            Title = "Components in the app",
            Columns = ["Type", "Object id"],
            Rows = rows.OrderBy(r => r[0], StringComparer.CurrentCultureIgnoreCase).ToList()
        });
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
