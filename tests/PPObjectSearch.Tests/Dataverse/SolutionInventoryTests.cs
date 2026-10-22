using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>The support-scoping inventory: what is in scope across several solutions, and the reads behind it.</summary>
public class SolutionInventoryTests
{
    private static readonly Guid Flow = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Rule = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid Classic = Guid.Parse("c0000000-0000-0000-0000-000000000003");
    private static readonly Guid Assembly = Guid.Parse("c0000000-0000-0000-0000-000000000004");
    private static readonly Guid PluginClass = Guid.Parse("c0000000-0000-0000-0000-000000000005");
    private static readonly Guid PluginStep = Guid.Parse("c0000000-0000-0000-0000-000000000006");
    private static readonly Guid EndpointStep = Guid.Parse("c0000000-0000-0000-0000-000000000007");
    private static readonly Guid Endpoint = Guid.Parse("c0000000-0000-0000-0000-000000000008");
    private static readonly Guid Reference = Guid.Parse("c0000000-0000-0000-0000-000000000009");
    private static readonly Guid Variable = Guid.Parse("c0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Widget = Guid.Parse("c0000000-0000-0000-0000-00000000000b");
    private static readonly Guid AccountTier = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid Api = Guid.Parse("c0000000-0000-0000-0000-00000000000d");
    private static readonly Guid Connector = Guid.Parse("c0000000-0000-0000-0000-00000000000e");
    private static readonly Guid Missing = Guid.Parse("c0000000-0000-0000-0000-00000000000f");
    private static readonly Guid OtherFlow = Guid.Parse("c0000000-0000-0000-0000-000000000010");
    private static readonly Guid Account = Guid.Parse("c0000000-0000-0000-0000-000000000011");

    private static readonly SolutionInfo Core = new()
    {
        SolutionId = Guid.Parse("d0000000-0000-0000-0000-000000000001"), UniqueName = "contoso_core", FriendlyName = "Contoso Core",
        Version = "1.0.0.0", PublisherName = "Contoso", PublisherPrefix = "contoso"
    };

    private static readonly SolutionInfo Extra = new()
    {
        SolutionId = Guid.Parse("d0000000-0000-0000-0000-000000000002"), UniqueName = "contoso_extra", FriendlyName = "Contoso Extra",
        Version = "2.0.0.0", PublisherName = "Contoso", PublisherPrefix = "contoso"
    };

    private static SolutionComponentItem Item(string name, int type, Guid id, string typeName, string? logical = null) => new()
    {
        Name = name, ComponentTypeName = typeName, ComponentType = type, ObjectId = id, ComponentLogicalName = logical
    };

    private static DocumentationScope Scope() => new(
        [Core, Extra],
        new Dictionary<Guid, IReadOnlyList<SolutionComponentItem>>
        {
            [Core.SolutionId] =
            [
                Item("Notify", 29, Flow, "Process"), Item("Validate", 29, Rule, "Process"), Item("Classic WF", 29, Classic, "Process"),
                Item("Contoso.Plugins", 91, Assembly, "Plug-in Assembly"), Item("Contoso step", 92, PluginStep, "Plug-in step"),
                Item("Contoso hook", 95, Endpoint, "Service Endpoint"),
                Item("contoso_office", 10029, Reference, "Connection Reference", "connectionreference"),
                Item("contoso_ApiUrl", 380, Variable, "Environment Variable Definition"),
                Item("new_widget", 1, Widget, "Table"), Item("contoso_tier", 2, AccountTier, "Column")
            ],
            [Extra.SolutionId] =
            [
                Item("Validate", 29, Rule, "Process"),
                Item("contoso_Recalculate", 10018, Api, "Custom API", "customapi"),
                Item("Contoso API", 372, Connector, "Connector")
            ]
        });

    private const string FlowDefinition =
        "{\\\"properties\\\":{\\\"definition\\\":{\\\"triggers\\\":{\\\"When_a_row_is_added\\\":{\\\"type\\\":\\\"OpenApiConnectionWebhook\\\"," +
        "\\\"inputs\\\":{\\\"host\\\":{\\\"apiId\\\":\\\"/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps\\\"," +
        "\\\"operationId\\\":\\\"SubscribeWebhookTrigger\\\"}}}},\\\"actions\\\":{\\\"Send_an_email\\\":{\\\"type\\\":\\\"OpenApiConnection\\\"," +
        "\\\"inputs\\\":{\\\"host\\\":{\\\"apiId\\\":\\\"/providers/Microsoft.PowerApps/apis/shared_office365\\\",\\\"operationId\\\":\\\"SendEmailV2\\\"}}}}}}}";

    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";

    private static FakeHttpHandler Environment() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "workflows?$select=workflowid,clientdata", $$"""{"value":[{"workflowid":"{{Flow}}","clientdata":"{{FlowDefinition}}"}]}""")
        .OnJson(HttpMethod.Get, "workflows?$select=workflowid,name", $$"""
            {"value":[
              {"workflowid":"{{Flow}}","name":"Notify","category":5,"category{{Formatted}}":"Modern Flow","statecode":1,"primaryentity":"none"},
              {"workflowid":"{{Rule}}","name":"Validate","category":2,"category{{Formatted}}":"Business Rule","statecode":1,"primaryentity":"account"},
              {"workflowid":"{{Classic}}","name":"Classic WF","category":0,"category{{Formatted}}":"Workflow","statecode":0,"primaryentity":"account",
               "mode{{Formatted}}":"Background","scope{{Formatted}}":"Organization","triggeroncreate":true,"triggeronupdateattributelist":"name"}]}
            """)
        .OnJson(HttpMethod.Get, "pluginassemblies?", $$"""
            {"value":[{"pluginassemblyid":"{{Assembly}}","name":"Contoso.Plugins","version":"1.0.0.0","isolationmode{{Formatted}}":"Sandbox"}]}
            """)
        .OnJson(HttpMethod.Get, "plugintypes?", $$"""
            {"value":[{"plugintypeid":"{{PluginClass}}","typename":"Contoso.Plugins.Validate","isworkflowactivity":false,
              "_pluginassemblyid_value":"{{Assembly}}","_pluginassemblyid_value{{Formatted}}":"Contoso.Plugins"}]}
            """)
        .OnJson(HttpMethod.Get, "sdkmessageprocessingsteps?", $$$"""
            {"value":[
              {"sdkmessageprocessingstepid":"{{{PluginStep}}}","name":"Validate account","mode":0,"statecode":0,
               "_sdkmessageid_value{{{Formatted}}}":"Update","_eventhandler_value":"{{{PluginClass}}}","sdkmessagefilterid":{"primaryobjecttypecode":"account"}},
              {"sdkmessageprocessingstepid":"{{{EndpointStep}}}","name":"Post account","mode":1,"statecode":0,
               "_sdkmessageid_value{{{Formatted}}}":"Create","_eventhandler_value":"{{{Endpoint}}}","sdkmessagefilterid":{"primaryobjecttypecode":"account"}}]}
            """)
        .OnJson(HttpMethod.Get, "customapis?", $$"""
            {"value":[{"customapiid":"{{Api}}","uniquename":"contoso_Recalculate","displayname":"Recalculate","bindingtype{{Formatted}}":"Global",
              "isfunction":false,"isprivate":false}]}
            """)
        .OnJson(HttpMethod.Get, "serviceendpoints?", $$"""
            {"value":[{"serviceendpointid":"{{Endpoint}}","name":"Contoso hook","contract{{Formatted}}":"Webhook",
              "url":"https://hooks.contoso.com/account?code=secret"}]}
            """)
        .OnJson(HttpMethod.Get, "connectionreferences?", $$"""
            {"value":[{"connectionreferenceid":"{{Reference}}","connectionreferencelogicalname":"contoso_office",
              "connectorid":"/providers/Microsoft.PowerApps/apis/shared_office365","connectionid":"abc"}]}
            """)
        .OnJson(HttpMethod.Get, "connectors?", $$"""{"value":[{"connectorid":"{{Connector}}","displayname":"Contoso API","connectortype{{Formatted}}":"Custom"}]}""")
        .OnJson(HttpMethod.Get, "systemusers?", """
            {"value":[{"systemuserid":"e0000000-0000-0000-0000-000000000001","fullname":"# Dynamics 365 service","applicationid":"e0000000-0000-0000-0000-0000000000a1"},
                      {"systemuserid":"e0000000-0000-0000-0000-000000000002","fullname":"Contoso Integration","applicationid":"e0000000-0000-0000-0000-0000000000a2",
                       "isdisabled":false,"systemuserroles_association":[{"name":"Contoso Integration Role"}]}]}
            """)
        .OnJson(HttpMethod.Get, "sharepointsites?", """{"value":[{"name":"Default Site","absoluteurl":"https://contoso.sharepoint.com/sites/crm"}]}""")
        .OnJson(HttpMethod.Get, "emailserverprofiles?", """{"value":[{"name":"Microsoft Exchange Online"}]}""")
        .OnJson(HttpMethod.Get, "mailboxes?fetchXml", $$"""{"value":[{"cnt":12,"g0":2,"g0{{Formatted}}":"Server-Side Synchronization","g1":2,"g1{{Formatted}}":"Server-Side Synchronization"}]}""")
        .OnJson(HttpMethod.Get, "EntityDefinitions?$select=MetadataId", $$$"""
            {"value":[
              {"MetadataId":"{{{Widget}}}","LogicalName":"new_widget","DisplayName":{"UserLocalizedLabel":{"Label":"Widget"}},"IsCustomEntity":true,
               "IsValidForAdvancedFind":true,"TableType":"Standard","OwnershipType":"UserOwned","IsAuditEnabled":{"Value":true},
               "Attributes":[{"MetadataId":"f0000000-0000-0000-0000-000000000001","LogicalName":"new_size"},
                             {"MetadataId":"f0000000-0000-0000-0000-000000000002","LogicalName":"new_colour"},
                             {"MetadataId":"f0000000-0000-0000-0000-000000000003","LogicalName":"new_partname","AttributeOf":"new_part"}]},
              {"MetadataId":"{{{Account}}}","LogicalName":"account","IsCustomEntity":false,"IsValidForAdvancedFind":true,"TableType":"Standard",
               "Attributes":[{"MetadataId":"{{{AccountTier}}}","LogicalName":"contoso_tier"},
                             {"MetadataId":"f0000000-0000-0000-0000-000000000004","LogicalName":"msdyn_score"}]},
              {"MetadataId":"f0000000-0000-0000-0000-000000000005","LogicalName":"contact","IsCustomEntity":false,"IsValidForAdvancedFind":true,"Attributes":[]},
              {"MetadataId":"f0000000-0000-0000-0000-000000000006","LogicalName":"new_other","IsCustomEntity":true,"IsValidForAdvancedFind":true,"Attributes":[]},
              {"MetadataId":"f0000000-0000-0000-0000-000000000007","LogicalName":"msdyn_hidden","IsCustomEntity":true,"IsPrivate":true,"Attributes":[]}]}
            """)
        .OnJson(HttpMethod.Get, "solutioncomponents?", $$"""{"value":[{"objectid":"{{Widget}}","rootcomponentbehavior":0}]}""")
        .OnJson(HttpMethod.Get, "RetrieveTotalRecordCount", """
            {"EntityRecordCountCollection":{"Keys":["new_widget","account","contact","new_other"],"Values":[120,500,300,40]}}
            """)
        .OnJson(HttpMethod.Get, "annotations?fetchXml", """{"value":[{"cnt":10,"total":2097152}]}""")
        .OnJson(HttpMethod.Get, "activitymimeattachments?fetchXml", """{"value":[{"cnt":4,"total":1048576}]}""")
        .OnJson(HttpMethod.Get, "value='10'", $$"""
            {"value":[
              {"cnt":5,"g0":"{{Guid.NewGuid()}}","g0{{Formatted}}":"Classic WF","g1":30,"g1{{Formatted}}":"Succeeded"},
              {"cnt":1,"g0":"{{Guid.NewGuid()}}","g0{{Formatted}}":"Classic WF","g1":31,"g1{{Formatted}}":"Failed"},
              {"cnt":9,"g0":"{{Guid.NewGuid()}}","g0{{Formatted}}":"Someone else's workflow","g1":30,"g1{{Formatted}}":"Succeeded"}]}
            """)
        .OnJson(HttpMethod.Get, "asyncoperations?fetchXml", $$"""
            {"value":[{"cnt":40,"g0":1,"g0{{Formatted}}":"System Event","g1":30},{"cnt":6,"g0":10,"g0{{Formatted}}":"Workflow","g1":31}]}
            """)
        .OnJson(HttpMethod.Get, "flowruns?fetchXml", $$"""
            {"value":[{"cnt":7,"g0":"{{Flow}}","g1":"Succeeded"},{"cnt":2,"g0":"{{Flow}}","g1":"Failed"},{"cnt":3,"g0":"{{OtherFlow}}","g1":"Succeeded"}]}
            """)
        .OnJson(HttpMethod.Get, "RetrieveMissingDependencies", $$$"""
            {"EntityCollection":{"Entities":[
              {"requiredcomponentobjectid":"{{{Missing}}}","requiredcomponenttype":1,"dependentcomponentobjectid":"{{{Flow}}}","dependentcomponenttype":29},
              {"requiredcomponentobjectid":"{{{Api}}}","requiredcomponenttype":10018,"dependentcomponentobjectid":"{{{Flow}}}","dependentcomponenttype":29}]}}
            """);

    private static readonly DocumentationOptions InventoryOnly = new()
    {
        Tables = false, Flows = false, EnvironmentVariables = false, PluginSteps = false, SecurityRoles = false
    };

    [Fact]
    public async Task Several_solutions_are_inventoried_together_with_a_scope_summary()
    {
        var pages = await new SolutionDocumenter(Fakes.Dataverse(Environment())).BuildAsync(Scope(), "Dev", InventoryOnly);

        var index = pages[0].Markdown;
        Assert.Equal("Solution documentation", pages[0].Title);
        Assert.StartsWith("# Solution documentation", index);
        Assert.Contains("2 solutions, read from Dev", index);
        Assert.Contains("| Contoso Extra | `contoso_extra` | 2.0.0.0 | Contoso | No | No |", index);
        Assert.Contains("| Type | Distinct | contoso_core | contoso_extra |", index);
        Assert.Contains("| Process | 3 | 3 | 1 |", index);
        Assert.DoesNotContain("Not documented", index);

        Assert.Contains("## Scope summary", index);
        Assert.Contains("| Solutions | Solutions in scope | 2 | publisher prefix(es): contoso |", index);
        Assert.Contains("| Automation | Business Rule (active / total) | 1 / 1 |", index);
        Assert.Contains("| Plug-ins | Plug-in steps (enabled / total) | 1 / 1 |", index);
        Assert.Contains("| Integrations | Application users (enabled, not Microsoft's) | 1 | Contoso Integration |", index);
        Assert.Contains("| Tables | Custom tables | 1 |", index);

        // What one solution needs and the other holds is not missing.
        Assert.Contains($"`{Missing}`", index);
        Assert.DoesNotContain($"`{Api}`", index);
        Assert.Contains("Components the solutions need but do not contain", index);

        Assert.Equal(["Processes", "Plug-ins and custom APIs", "Integrations", "Data volumes", "Execution volumes"], pages.Skip(1).Select(p => p.Title));
        Assert.All(pages.Skip(1), p => Assert.Equal("Inventory", p.Section));
        Assert.All(pages, p => Assert.DoesNotContain("incident", p.Markdown, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Processes_are_counted_by_category_with_their_triggers_and_solutions()
    {
        var pages = await new SolutionDocumenter(Fakes.Dataverse(Environment())).BuildAsync(Scope(), "Dev", InventoryOnly);
        var processes = pages.Single(p => p.Title == "Processes").Markdown;

        Assert.Contains("| Business Rule | 1 | 1 |", processes);
        Assert.Contains("| Workflow | 0 | 1 |", processes);
        Assert.Contains("| account | 1 | 1 |", processes);
        Assert.Contains("| Classic WF | Workflow | account | Draft | Background | Organization | Create, Update (name) | contoso_core |", processes);
        Assert.Contains("| Validate | Business Rule | account | Active |", processes);
        Assert.Contains("contoso_core, contoso_extra |", processes);
        Assert.Contains("| Notify | On |", processes);
        Assert.DoesNotContain("_definition not read_", processes);
    }

    [Fact]
    public async Task Plug_ins_take_in_the_classes_of_their_assemblies_and_leave_endpoint_steps_to_integrations()
    {
        var pages = await new SolutionDocumenter(Fakes.Dataverse(Environment())).BuildAsync(Scope(), "Dev", InventoryOnly);
        var plugins = pages.Single(p => p.Title == "Plug-ins and custom APIs").Markdown;

        Assert.Contains("| Contoso.Plugins | 1.0.0.0 | Sandbox |", plugins);
        Assert.Contains("| Contoso.Plugins.Validate | Contoso.Plugins | Plug-in |", plugins);
        Assert.Contains("| account | 1 | 1 | 1 | 0 | Update |", plugins);
        Assert.Contains("| contoso_Recalculate | Recalculate | Global |", plugins);

        var integrations = pages.Single(p => p.Title == "Integrations").Markdown;
        Assert.Contains("| Contoso hook | Webhook | https://hooks.contoso.com/account |", integrations);
        Assert.DoesNotContain("secret", integrations);
        Assert.Contains("| 1 | 1 | contoso_core |", integrations);
        Assert.Contains("| contoso_office | contoso_office | shared_office365 | Yes | contoso_core |", integrations);
        Assert.Contains("| Contoso API | Custom | contoso_extra |", integrations);
        Assert.Contains("**Environment variables:** 1 definition(s).", integrations);
        Assert.Contains("1 Microsoft service identities left out", integrations);
        Assert.Contains("| Contoso Integration |", integrations);
        Assert.DoesNotContain("# Dynamics 365 service", integrations);
        Assert.Contains("| Server-Side Synchronization | Server-Side Synchronization | 12 |", integrations);
        Assert.DoesNotContain("**Connectors used by cloud flows**\r\n\r\nNone.", integrations);
        Assert.DoesNotContain("**Connectors used by cloud flows**\n\nNone.", integrations);
    }

    [Fact]
    public async Task Data_volumes_count_the_tables_in_scope_and_the_columns_the_solutions_add()
    {
        var pages = await new SolutionDocumenter(Fakes.Dataverse(Environment())).BuildAsync(Scope(), "Dev", InventoryOnly);
        var data = pages.Single(p => p.Title == "Data volumes").Markdown;

        Assert.Contains("| `new_widget` | Widget | 120 | 2 | UserOwned | On | Off | contoso_core |", data);
        Assert.Contains("| `account` |  | 1 | 500 |", data);
        Assert.DoesNotContain("new_other` |", data);
        Assert.DoesNotContain("`contact`", data);
        Assert.Contains("**Records in every counted table:** 960 across 4 tables.", data);
        Assert.Contains("| Note attachments | 10 | 2.0 |", data);
        Assert.Contains("| Email attachments | 4 | 1.0 |", data);
    }

    [Fact]
    public async Task Execution_volumes_keep_to_the_processes_in_scope()
    {
        var pages = await new SolutionDocumenter(Fakes.Dataverse(Environment())).BuildAsync(Scope(), "Dev", InventoryOnly);
        var runs = pages.Single(p => p.Title == "Execution volumes").Markdown;

        Assert.Contains("| Classic WF | 6 | 5 | 1 | 0 |", runs);
        Assert.DoesNotContain("Someone else's workflow", runs);
        Assert.Contains("| Notify | 9 | 7 | 2 | 0 |", runs);
        Assert.Contains("| System Event | 40 | 40 | 0 | 0 |", runs);
        Assert.Contains("| Workflow | 6 | 0 | 6 | 0 |", runs);
    }

    [Fact]
    public async Task Without_environment_wide_facts_nothing_outside_the_solutions_is_read()
    {
        var handler = Environment();
        var options = new DocumentationOptions
        {
            Tables = false, Flows = false, EnvironmentVariables = false, PluginSteps = false, SecurityRoles = false, Dependencies = false,
            EnvironmentWide = false
        };

        var pages = await new SolutionDocumenter(Fakes.Dataverse(handler)).BuildAsync(Scope(), "Dev", options);

        Assert.DoesNotContain("Environment-wide", string.Concat(pages.Select(p => p.Markdown)));
        Assert.DoesNotContain(handler.Requests, r =>
            r.Url.Contains("systemusers") || r.Url.Contains("sharepointsites") || r.Url.Contains("mailboxes") ||
            r.Url.Contains("annotations") || r.Url.Contains("name='operationtype'"));

        // Only the tables in scope are counted.
        var count = Assert.Single(handler.Requests, r => r.Url.Contains("RetrieveTotalRecordCount"));
        Assert.Contains("\"new_widget\"", count.Url);
        Assert.Contains("\"account\"", count.Url);
        Assert.DoesNotContain("\"contact\"", count.Url);
    }

    [Fact]
    public async Task One_solution_keeps_its_own_heading_and_leaves_out_the_solution_column()
    {
        var scope = new DocumentationScope(Core, Scope().Components[Core.SolutionId]);
        var pages = await new SolutionDocumenter(Fakes.Dataverse(Environment())).BuildAsync(scope, "Dev", InventoryOnly);

        Assert.StartsWith("# Contoso Core", pages[0].Markdown);
        Assert.Contains("| Version | 1.0.0.0 |", pages[0].Markdown);
        Assert.Contains("## Scope summary", pages[0].Markdown);
        Assert.Contains("Components the solution needs but does not contain", pages[0].Markdown);
        Assert.DoesNotContain("| Solution |", pages.Single(p => p.Title == "Processes").Markdown);
    }

    [Fact]
    public async Task Without_inventory_sections_there_is_no_scope_summary()
    {
        var options = new DocumentationOptions
        {
            Tables = false, Flows = false, EnvironmentVariables = false, PluginSteps = false, SecurityRoles = false, Dependencies = false,
            Processes = false, PluginsAndApis = false, Integrations = false, DataVolumes = false, ExecutionVolumes = false
        };

        var pages = await new SolutionDocumenter(Fakes.Dataverse(new FakeHttpHandler())).BuildAsync(Scope(), "Dev", options);

        Assert.Single(pages);
        Assert.DoesNotContain("Scope summary", pages[0].Markdown);
    }

    // ---------------------------------------------------------------- classification

    private static InventoryTable Table(Guid id, string name, bool custom, params (Guid Id, string Name)[] columns) =>
        new(id, name, null, custom, false, false, "Standard", null, false, false, false, true,
            columns.Select(c => new InventoryColumn(c.Id, c.Name)).ToList());

    [Fact]
    public void A_whole_table_brings_its_own_columns_but_not_another_publishers()
    {
        var size = Guid.NewGuid();
        var score = Guid.NewGuid();
        var tier = Guid.NewGuid();
        var custom = Table(Widget, "new_widget", true, (size, "new_size"));
        var account = Table(Account, "account", false, (tier, "contoso_tier"), (score, "msdyn_score"));

        var whole = new HashSet<Guid> { Widget, Account };
        var held = new HashSet<Guid> { Widget, Account };

        Assert.Equal(new SolutionInventory.TableScope(custom, true, false, 1), SolutionInventory.ClassifyTable(custom, held, whole, ["contoso"]));
        Assert.Equal(new SolutionInventory.TableScope(account, false, true, 1), SolutionInventory.ClassifyTable(account, held, whole, ["contoso"]));

        // Not whole: only the columns listed one by one.
        Assert.Equal(0, SolutionInventory.ClassifyTable(custom, held, new HashSet<Guid>(), ["contoso"]).ScopedColumns);
        var byColumn = SolutionInventory.ClassifyTable(account, new HashSet<Guid> { score }, new HashSet<Guid>(), ["contoso"]);
        Assert.True(byColumn.Extended);
        Assert.Equal(1, byColumn.ScopedColumns);

        var untouched = SolutionInventory.ClassifyTable(account, new HashSet<Guid>(), new HashSet<Guid>(), ["contoso"]);
        Assert.False(untouched.Extended);
        Assert.False(untouched.CustomInScope);
    }

    [Theory]
    [InlineData("Standard", false, true, false, true)]
    [InlineData("Elastic", false, true, true, false)]
    [InlineData("Virtual", false, true, true, false)]
    [InlineData("Standard", true, true, true, false)]
    [InlineData("Standard", false, false, true, true)]
    [InlineData("Standard", false, false, false, false)]
    public void Countable_tables_are_public_stored_and_findable_or_custom(string type, bool isPrivate, bool findable, bool custom, bool expected)
    {
        var table = new InventoryTable(Guid.NewGuid(), "new_x", null, custom, false, false, type, null, false, false, isPrivate, findable, []);
        Assert.Equal(expected, SolutionInventory.IsCountable(table));
    }

    // ---------------------------------------------------------------- reads

    [Fact]
    public async Task An_aggregate_over_the_limit_is_halved_and_the_halves_added()
    {
        var requests = 0;
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "asyncoperations?fetchXml", _ =>
            Interlocked.Increment(ref requests) == 1
                ? FakeHttpHandler.Json(FakeHttpHandler.ErrorJson("AggregateQueryRecordLimit exceeded. Cannot perform this operation."), HttpStatusCode.BadRequest)
                : FakeHttpHandler.Json($$"""{"value":[{"cnt":40000,"g0":10,"g0{{Formatted}}":"Workflow"}]}"""));

        var rows = await Fakes.Dataverse(handler).AggregateAsync("asyncoperation", "asyncoperations", "asyncoperationid", ["operationtype"],
            from: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), to: new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));

        var row = Assert.Single(rows);
        Assert.Equal(80000, row.Count);
        Assert.Equal("Workflow", row.Labels[0]);
        Assert.Equal("10", row.Keys[0]);
        Assert.Equal(3, requests);
        Assert.Contains(handler.Requests, r => r.Url.Contains("value='2026-01-03T00:00:00Z'"));
    }

    [Fact]
    public async Task An_aggregate_refused_for_another_reason_is_not_halved()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "fetchXml", HttpStatusCode.BadRequest, "Aggregate queries are not supported");

        await Assert.ThrowsAsync<DataverseException>(() =>
            Fakes.Dataverse(handler).AggregateAsync("flowrun", "flowruns", "flowrunid", ["status"], dateColumn: "starttime"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_table_the_count_cannot_take_is_found_by_halving_and_left_out()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "RetrieveTotalRecordCount", r =>
        {
            if (r.Url.Contains("\"bad\"")) return FakeHttpHandler.Json(FakeHttpHandler.ErrorJson("not supported"), HttpStatusCode.BadRequest);
            var names = new[] { "a", "c" }.Where(n => r.Url.Contains($"\"{n}\"")).ToList();
            return FakeHttpHandler.Json(System.Text.Json.JsonSerializer.Serialize(new
            {
                EntityRecordCountCollection = new { Keys = names, Values = names.Select(n => n == "a" ? 1 : 3) }
            }));
        });

        var counts = await Fakes.Dataverse(handler).GetRecordCountsAsync(["a", "bad", "c"]);

        Assert.Equal(new Dictionary<string, long> { ["a"] = 1, ["c"] = 3 }, counts);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task Flow_runs_are_counted_row_by_row_when_the_table_will_not_aggregate()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "flowruns?fetchXml", HttpStatusCode.BadRequest, "Aggregate queries are not supported on elastic tables")
            .OnJson(HttpMethod.Get, "flowruns?$select=status", $$"""
                {"value":[{"status":"Succeeded","_workflow_value":"{{Flow}}","_workflow_value{{Formatted}}":"Notify"},
                          {"status":"Failed","_workflow_value":"{{Flow}}"},{"status":"Succeeded","_workflow_value":"{{Flow}}"}]}
                """);

        var (runs, truncated) = await Fakes.Dataverse(handler).GetFlowRunCountsAsync(DateTimeOffset.UtcNow.AddDays(-30));

        Assert.False(truncated);
        Assert.Equal(2, runs.Single(r => r.Keys[1] == "Succeeded").Count);
        Assert.Equal(1, runs.Single(r => r.Keys[1] == "Failed").Count);
        Assert.Equal(Flow.ToString(), runs[0].Keys[0]);
    }

    [Fact]
    public async Task Custom_columns_are_filtered_here_when_the_server_will_not_filter_the_expansion()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$filter=IsCustomAttribute", HttpStatusCode.BadRequest, "Query option filter is not supported")
            .OnJson(HttpMethod.Get, "EntityDefinitions?", """
                {"value":[{"MetadataId":"f0000000-0000-0000-0000-000000000001","LogicalName":"account","Attributes":[
                  {"MetadataId":"f0000000-0000-0000-0000-000000000002","LogicalName":"contoso_tier","IsCustomAttribute":true},
                  {"MetadataId":"f0000000-0000-0000-0000-000000000003","LogicalName":"name","IsCustomAttribute":false}]}]}
                """);

        var tables = await Fakes.Dataverse(handler).GetInventoryTablesAsync();

        var column = Assert.Single(Assert.Single(tables).CustomColumns);
        Assert.Equal("contoso_tier", column.LogicalName);
    }

    [Fact]
    public void A_component_in_two_solutions_is_in_scope_once_and_names_both()
    {
        var scope = Scope();

        Assert.Single(scope.Items, i => i.ObjectId == Rule);
        Assert.Equal("contoso_core, contoso_extra", scope.SolutionsOf(Rule));
        Assert.Equal("contoso_extra", scope.SolutionsOf(Api));
        Assert.Equal(string.Empty, scope.SolutionsOf(Guid.NewGuid()));
        Assert.True(scope.IsMultiple);
        Assert.Contains(Widget, scope.ObjectIds);
    }
}
