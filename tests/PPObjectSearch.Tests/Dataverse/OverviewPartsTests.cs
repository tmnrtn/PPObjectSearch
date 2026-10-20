using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.PowerApps;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>The Overview parts that name things rather than list ids, and the Power Apps sharing read.</summary>
public class OverviewPartsTests
{
    private static readonly Guid Id = Guid.Parse("34343434-3434-3434-3434-343434343434");
    private static readonly Guid Unique = Guid.Parse("56565656-5656-5656-5656-565656565656");
    private static readonly Guid Account = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid MainForm = Guid.Parse("f0f0f0f0-0000-0000-0000-000000000001");
    private static readonly Guid Dashboard = Guid.Parse("f0f0f0f0-0000-0000-0000-000000000002");
    private static readonly Guid View = Guid.Parse("e0e0e0e0-0000-0000-0000-000000000001");
    private static readonly Guid Column = Guid.Parse("c0c0c0c0-0000-0000-0000-000000000001");

    private static SolutionComponentItem Item(int type, string? logical = null, int? category = null) => new()
    {
        Name = "x", ComponentTypeName = "T", ComponentType = type, ComponentLogicalName = logical, ProcessCategory = category, ObjectId = Id
    };

    [Fact]
    public void A_sitemap_reads_as_an_indented_tree()
    {
        var rows = DataverseClient.SitemapTree("""
            <SiteMap>
              <Area Id="sales" Title="Sales">
                <Group Id="customers" Title="Customers">
                  <SubArea Id="accounts" Entity="account" Title="Accounts" />
                  <SubArea Id="home" DashboardId="{1}" Title="Home" />
                </Group>
              </Area>
            </SiteMap>
            """);

        Assert.Equal(
        [
            ["Sales", null],
            ["    Customers", null],
            ["        Accounts", "Table: account"],
            ["        Home", "Dashboard: {1}"]
        ], rows);
        Assert.Empty(DataverseClient.SitemapTree("<not xml"));
    }

    [Fact]
    public async Task A_model_driven_apps_components_are_named_and_grouped_by_kind()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"appmodules({Id})?$select=appmoduleidunique", $$"""{"appmoduleidunique":"{{Unique}}"}""")
            .OnJson(HttpMethod.Get, $"appmodules({Id})?$select=name", """{"name":"Sales Hub","uniquename":"salesHub"}""")
            .OnJson(HttpMethod.Get, "sitemaps?", """{"value":[]}""")
            .OnJson(HttpMethod.Get, "appmoduleroles_association", """{"value":[]}""")
            .OnJson(HttpMethod.Get, "appmodulecomponents?", $$"""
                {"value":[
                  {"componenttype":1,"objectid":"{{Account}}"},
                  {"componenttype":60,"objectid":"{{MainForm}}"},
                  {"componenttype":60,"objectid":"{{Dashboard}}"},
                  {"componenttype":26,"objectid":"{{View}}"}
                ]}
                """)
            .OnJson(HttpMethod.Get, "EntityDefinitions?$select=MetadataId", $$$$"""
                {"value":[{"MetadataId":"{{{{Account}}}}","LogicalName":"account","DisplayName":{"UserLocalizedLabel":{"Label":"Account"}}}]}
                """)
            .OnJson(HttpMethod.Get, "systemforms?", $$"""
                {"value":[{"formid":"{{MainForm}}","name":"Account main","objecttypecode":"account","type":2},
                          {"formid":"{{Dashboard}}","name":"Sales overview","objecttypecode":"none","type":0}]}
                """)
            .OnStatus(HttpMethod.Get, "savedqueries?", HttpStatusCode.Forbidden);

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(80));

        var components = overview.Tables.Single(t => t.Title.StartsWith("Tables, forms"));
        Assert.Equal(
        [
            ["Table", "Account", "account"],
            ["Form", "Account main", "account"],
            ["View", View.ToString(), null],
            ["Dashboard", "Sales overview", null]
        ], components.Rows);
        Assert.Contains(overview.Problems, p => p.StartsWith("component names:"));
    }

    [Theory]
    [InlineData("""{"channels":[{"channelId":"MsTeams"},{"channelId":"directline"}]}""", "MsTeams,directline")]
    [InlineData("""{"settings":{"publishing":{"channels":["Microsoft365Copilot"]}}}""", "Microsoft365Copilot")]
    [InlineData("""{"settings":{}}""", "")]
    [InlineData("not json", "")]
    public void An_agents_channels_are_found_in_its_configuration(string configuration, string expected)
    {
        Assert.Equal(expected, string.Join(",", DataverseClient.AgentChannels(configuration)));
    }

    [Fact]
    public async Task An_agent_lists_its_channels_and_the_flows_its_topics_call()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"bots({Id})?$select=configuration", """{"configuration":"{\"channels\":[{\"channelId\":\"MsTeams\"}]}"}""")
            .OnJson(HttpMethod.Get, $"bots({Id})?$select=name", """{"name":"Helpdesk"}""")
            .OnJson(HttpMethod.Get, "botcomponent_workflow", """
                {"value":[{"name":"Reset password","botcomponent_workflow":[{"name":"Reset AD password","workflowid":"x"}]},
                          {"name":"Greeting","botcomponent_workflow":[]}]}
                """)
            .OnJson(HttpMethod.Get, "botcomponents?", """{"value":[]}""");

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(10070, "bot"));

        Assert.Contains(overview.Properties, p => p is { Label: "Channels", Value: "MsTeams" });
        var flows = overview.Tables.Single(t => t.Title == "Flows called");
        Assert.Equal(["Reset AD password", "Reset password"], flows.Rows.Single());
    }

    [Fact]
    public async Task A_choice_lists_the_columns_that_use_it()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"GlobalOptionSetDefinitions({Id})", """{"Name":"new_priority","Options":[]}""")
            .OnJson(HttpMethod.Get, "RetrieveDependentComponents", $$"""
                {"value":[{"dependentcomponentobjectid":"{{Column}}","dependentcomponenttype":2},
                          {"dependentcomponentobjectid":"{{View}}","dependentcomponenttype":26}]}
                """)
            .OnJson(HttpMethod.Get, "solutions?", """{"value":[{"solutionid":"d0d0d0d0-0000-0000-0000-000000000001"}]}""")
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries?", $$"""
                {"value":[{"msdyn_objectid":"{{Column}}","msdyn_name":"new_priority","msdyn_displayname":"Priority","msdyn_primaryentityname":"incident"}]}
                """);

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(9));

        var used = overview.Tables.Single(t => t.Title == "Columns that use this choice");
        Assert.Equal(["incident", "new_priority", "Priority"], used.Rows.Single());
        Assert.Empty(overview.Problems);
    }

    [Fact]
    public async Task A_business_rule_reads_as_its_scope_and_steps()
    {
        var rule = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = "Credit checks",
            ["primaryentity"] = "account",
            ["statecode"] = 1,
            ["processtriggerscope"] = 1,
            ["xaml"] = PPObjectSearch.Tests.Services.BusinessRuleReaderTests.Sample
        });
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"workflows({Id})", rule);

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(29, category: 2));

        Assert.Empty(overview.Problems);
        Assert.Contains(overview.Properties, p => p is { Label: "Scope", Value: "All forms" });
        Assert.Contains(overview.Properties, p => p is { Label: "State", Value: "Active" });
        var steps = overview.Tables.Single(t => t.Title == "Steps");
        Assert.StartsWith("If creditlimit contains data", steps.Rows[0][0]);
    }

    [Fact]
    public async Task A_business_rule_whose_definition_cannot_be_read_says_so()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"workflows({Id})", """{"name":"Odd","xaml":"<oops"}""");

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(29, category: 2));

        Assert.Contains("steps: the rule's definition could not be read as steps", overview.Problems);
    }

    [Fact]
    public async Task A_canvas_apps_sharing_is_read_with_owners_first_and_falls_back_to_the_admin_route()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, $"apps/{Id}/permissions?api-version=2016-11-01&$filter", HttpStatusCode.Forbidden, "not an owner")
            .OnJson(HttpMethod.Get, $"scopes/admin/environments/env-1/apps/{Id}/permissions", """
                {"value":[
                  {"properties":{"roleName":"CanView","principal":{"type":"Group","displayName":"Sales team"}}},
                  {"properties":{"roleName":"Owner","principal":{"type":"User","displayName":"Ana Lee","email":"ana@contoso.com"}}},
                  {"properties":{"roleName":"CanView","principal":{"type":"Tenant","id":"t"}}}
                ]}
                """);

        var permissions = await new PowerAppsClient(TestAuth.Tokens(), handler).GetAppPermissionsAsync("env-1", Id);

        Assert.Equal(["Ana Lee", "Everyone in the organisation", "Sales team"], permissions.Select(p => p.Principal));
        Assert.Equal("Owner", permissions[0].Role);
        Assert.Equal("User", permissions[2].Role);
        Assert.Equal("Bearer token:https://service.powerapps.com/", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task Without_admin_rights_the_sharing_read_says_who_can_see_it()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "permissions", HttpStatusCode.Forbidden, "no");

        var ex = await Assert.ThrowsAsync<PowerAppsException>(() =>
            new PowerAppsClient(TestAuth.Tokens(), handler).GetAppPermissionsAsync("env-1", Id));

        Assert.StartsWith("Only the app's owners", ex.Message);
    }
}
