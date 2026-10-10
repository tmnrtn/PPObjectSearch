using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>The Overview tab's parts for apps, agents, roles and business process flows.</summary>
public class ComponentOverviewPartsTests
{
    private static readonly Guid Id = Guid.Parse("34343434-3434-3434-3434-343434343434");
    private static readonly Guid UniqueId = Guid.Parse("56565656-5656-5656-5656-565656565656");

    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";

    private static SolutionComponentItem Item(int type, string? logical = null, string? subType = null) => new()
    {
        Name = "x", ComponentTypeName = "T", ComponentType = type, ComponentLogicalName = logical, SubType = subType, ObjectId = Id
    };

    private static string? Value(ComponentOverview overview, string label) =>
        overview.Properties.Single(p => p.Label == label).Value;

    [Theory]
    [InlineData(10001, "canvasapp", ObjectKind.CanvasApp)]
    [InlineData(10002, "APPMODULE", ObjectKind.ModelDrivenApp)]
    [InlineData(10003, "something", ObjectKind.Other)]
    [InlineData(10004, null, ObjectKind.Other)]
    public void Types_without_a_fixed_number_are_recognised_by_logical_name(int type, string? logical, ObjectKind expected)
    {
        Assert.Equal(expected, DataverseClient.OverviewKindOf(Item(type, logical)));
    }

    [Fact]
    public void A_process_labelled_as_a_business_process_flow_is_one_whatever_its_category()
    {
        Assert.Equal(ObjectKind.BusinessProcessFlow, DataverseClient.OverviewKindOf(Item(29, subType: "Business Process Flow")));
    }

    [Fact]
    public async Task A_canvas_app_lists_its_properties_and_connections()
    {
        var connections = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["c1"] = new { displayName = "Office 365 Users", id = "/providers/Microsoft.PowerApps/apis/shared_office365users", connectionReferenceLogicalName = "new_users" },
            ["c2"] = new { apiName = "shared_sql" }
        });
        var body = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["displayname"] = "Expenses",
            ["name"] = "new_expenses_1a2b3",
            ["canvasapptype"] = 0,
            ["canvasapptype" + Formatted] = "Canvas app",
            ["_ownerid_value" + Formatted] = "Ada Lovelace",
            ["appversion"] = "2024-01-01T00:00:00Z",
            ["lastpublishtime" + Formatted] = "01/02/2024 10:00",
            ["commitmessage"] = "Fixed totals",
            ["connectionreferences"] = connections
        });
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"canvasapps({Id})", body);

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(300));

        Assert.Empty(overview.Problems);
        Assert.Equal("Expenses", Value(overview, "Name"));
        Assert.Equal("Canvas app", Value(overview, "Type"));
        Assert.Equal("Ada Lovelace", Value(overview, "Owner"));
        Assert.Equal("Fixed totals", Value(overview, "Last commit message"));
        var table = Assert.Single(overview.Tables);
        Assert.Equal("Connections (2)", table.Heading);
        Assert.Equal(["Office 365 Users", "shared_office365users", "new_users"], table.Rows[0]);
        Assert.Equal(["shared_sql", null, null], table.Rows[1]);
    }

    [Fact]
    public async Task A_canvas_app_whose_connection_list_does_not_parse_says_so()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"canvasapps({Id})",
            """{"displayname":"Broken","connectionreferences":"{not json"}""");

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(300));

        Assert.Equal(["connections: the app's connection list did not parse"], overview.Problems);
        Assert.Empty(Assert.Single(overview.Tables).Rows);
    }

    [Fact]
    public async Task A_canvas_app_whose_connection_list_is_not_an_object_has_no_connections()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"canvasapps({Id})",
            """{"displayname":"Odd","connectionreferences":"[1,2]"}""");

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(300));

        Assert.Empty(overview.Problems);
        Assert.Empty(Assert.Single(overview.Tables).Rows);
    }

    [Fact]
    public async Task A_model_driven_app_shows_its_navigation_components_and_roles()
    {
        const string sitemap = """<SiteMap><Area Id="a" Title="Sales"><Group Id="g" Title="Customers"><SubArea Id="s" Entity="account" Title="Accounts" /></Group></Area></SiteMap>""";
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"appmodules({Id})?$select=appmoduleidunique", $$"""{"appmoduleidunique":"{{UniqueId}}"}""")
            .OnJson(HttpMethod.Get, $"appmodules({Id})/appmoduleroles_association", """{"value":[{"name":"Sales Manager"},{"name":"Basic User"}]}""")
            .OnJson(HttpMethod.Get, $"appmodules({Id})?", $$"""
                {"name":"Sales Hub","uniquename":"new_sales","description":"For sellers","appmoduleversion":"1.0.0.0",
                 "publishedon{{Formatted}}":"03/04/2024","navigationtype{{Formatted}}":"Single session"}
                """)
            .OnJson(HttpMethod.Get, "sitemaps?", System.Text.Json.JsonSerializer.Serialize(new { value = new[] { new { sitemapxml = sitemap } } }))
            .OnJson(HttpMethod.Get, "appmodulecomponents?", $$"""
                {"value":[{"componenttype":60,"componenttype{{Formatted}}":"System Form","objectid":"f1"},
                          {"componenttype":1,"componenttype{{Formatted}}":"Entity","objectid":"e1"}]}
                """);

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(80));

        Assert.Empty(overview.Problems);
        Assert.Equal("Sales Hub", Value(overview, "Name"));
        Assert.Equal("Single session", Value(overview, "Navigation"));
        Assert.Equal(["Sales", "Customers", "Accounts", "Table: account"], overview.Tables.Single(t => t.Title == "Navigation (sitemap)").Rows.Single());
        Assert.Equal([["Entity", "e1"], ["System Form", "f1"]], overview.Tables.Single(t => t.Title == "Components in the app").Rows);
        Assert.Equal([["Basic User"], ["Sales Manager"]], overview.Tables.Single(t => t.Title == "Security roles with access").Rows);
        Assert.Contains(handler.Requests, r => r.Url.Contains("sitemapnameunique eq 'new_sales'"));
        Assert.Contains(handler.Requests, r => r.Url.Contains($"_appmoduleidunique_value eq {UniqueId}"));
    }

    [Fact]
    public async Task A_model_driven_app_without_a_unique_name_or_sitemap_still_lists_its_parts()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"appmodules({Id})?$select=appmoduleidunique", "{}")
            .OnJson(HttpMethod.Get, $"appmodules({Id})/appmoduleroles_association", """{"value":[]}""")
            .OnJson(HttpMethod.Get, $"appmodules({Id})?", """{"name":"Nameless"}""")
            .OnJson(HttpMethod.Get, "appmodulecomponents?", """{"value":[]}""");

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(80));

        Assert.Empty(overview.Problems);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("sitemaps"));
        Assert.DoesNotContain(overview.Tables, t => t.Title.StartsWith("Navigation", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, r => r.Url.Contains($"_appmoduleidunique_value eq {Id}"));
    }

    [Fact]
    public async Task A_model_driven_app_with_an_empty_sitemap_shows_no_navigation()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"appmodules({Id})?$select=appmoduleidunique", "{}")
            .OnJson(HttpMethod.Get, $"appmodules({Id})/appmoduleroles_association", """{"value":[]}""")
            .OnJson(HttpMethod.Get, $"appmodules({Id})?", """{"name":"App","uniquename":"new_app"}""")
            .OnJson(HttpMethod.Get, "sitemaps?", """{"value":[]}""")
            .OnJson(HttpMethod.Get, "appmodulecomponents?", """{"value":[]}""");

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(80));

        Assert.Empty(overview.Problems);
        Assert.DoesNotContain(overview.Tables, t => t.Title.StartsWith("Navigation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_failing_part_of_a_model_driven_app_is_reported_on_its_own()
    {
        var overview = await Fakes.Dataverse(new FakeHttpHandler()).GetOverviewAsync(Item(80));

        Assert.Equal(3, overview.Problems.Count);
        Assert.StartsWith("app:", overview.Problems[0]);
        Assert.StartsWith("components:", overview.Problems[1]);
        Assert.StartsWith("security roles:", overview.Problems[2]);
    }

    [Fact]
    public async Task An_agent_lists_its_settings_and_topics_sorted_by_kind_then_name()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"bots({Id})", $$"""
                {"name":"Helpdesk","schemaname":"new_helpdesk","language{{Formatted}}":"English",
                 "authenticationmode{{Formatted}}":"Integrated","accesscontrolpolicy{{Formatted}}":"Any"}
                """)
            .OnJson(HttpMethod.Get, "botcomponents?", $$"""
                {"value":[
                  {"name":"Reset password","schemaname":"t2","componenttype{{Formatted}}":"Topic"},
                  {"name":"Greeting","schemaname":"t1","componenttype{{Formatted}}":"Topic"},
                  {"name":"Policies","schemaname":"k1","componenttype{{Formatted}}":"Knowledge"}]}
                """);

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(10070, "bot"));

        Assert.Empty(overview.Problems);
        Assert.Equal("Helpdesk", Value(overview, "Name"));
        Assert.Equal("English", Value(overview, "Language"));
        Assert.Equal("Integrated", Value(overview, "Authentication"));
        Assert.Equal(
            [["Knowledge", "Policies", "k1"], ["Topic", "Greeting", "t1"], ["Topic", "Reset password", "t2"]],
            Assert.Single(overview.Tables).Rows);
        Assert.Contains(handler.Requests, r => r.Url.Contains($"_parentbotid_value eq {Id}"));
    }

    [Fact]
    public async Task A_security_role_shows_its_table_grid_and_other_privileges()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"RetrieveRolePrivilegesRole(RoleId={Id})", """
            {"RolePrivileges":[
              {"PrivilegeId":"00000000-0000-0000-0000-000000000001","PrivilegeName":"prvReadAccount","Depth":"Global"},
              {"PrivilegeId":"00000000-0000-0000-0000-000000000002","PrivilegeName":"prvWriteAccount","Depth":"Basic"},
              {"PrivilegeId":"00000000-0000-0000-0000-000000000003","PrivilegeName":"prvExportToExcel","Depth":"Local"}]}
            """);

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(20));

        Assert.Empty(overview.Problems);
        Assert.Equal("1", Value(overview, "Tables with privileges"));
        Assert.Equal("1", Value(overview, "Other privileges"));
        var grid = overview.Tables.Single(t => t.Title == "Table privileges");
        Assert.Equal(["Account", null, "Organization", "User", null, null, null, null, null], grid.Rows.Single());
        Assert.Equal(["ExportToExcel", "Business unit"], overview.Tables.Single(t => t.Title == "Other privileges").Rows.Single());
    }

    [Fact]
    public async Task A_business_process_flow_lists_its_stages()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "processstages?", $$"""
            {"value":[{"stagename":"Qualify","stagecategory{{Formatted}}":"Qualify","primaryentitytypecode":"lead"},
                      {"stagename":"Develop","primaryentitytypecode":"opportunity"}]}
            """);

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(29, subType: "Business Process Flow"));

        Assert.Empty(overview.Problems);
        var stages = Assert.Single(overview.Tables);
        Assert.Equal("Stages (2)", stages.Heading);
        Assert.Equal(["Qualify", "Qualify", "lead"], stages.Rows[0]);
        Assert.Equal(["Develop", null, "opportunity"], stages.Rows[1]);
        Assert.Contains(handler.Requests, r => r.Url.Contains($"_processid_value eq {Id}"));
    }

    [Fact]
    public async Task A_function_custom_api_that_is_private_says_so()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"customapis({Id})", """{"uniquename":"new_Get","isfunction":true,"isprivate":true}""")
            .OnJson(HttpMethod.Get, "customapirequestparameters?", """{"value":[{"uniquename":"Id","isoptional":false,"logicalentityname":"account"}]}""")
            .OnJson(HttpMethod.Get, "customapiresponseproperties?", """{"value":[{"uniquename":"Total","logicalentityname":null}]}""");

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(10101, "customapi"));

        Assert.Equal("Function (GET)", Value(overview, "Kind"));
        Assert.Equal("Yes", Value(overview, "Private"));
        Assert.Equal(["Id", null, "Required", "account"], overview.Tables.Single(t => t.Title == "Request parameters").Rows.Single());
        Assert.Equal(["Total", null, null], overview.Tables.Single(t => t.Title == "Response properties").Rows.Single());
    }

    [Fact]
    public async Task A_choice_without_options_or_labels_shows_an_empty_list()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"GlobalOptionSetDefinitions({Id})",
            """{"Name":"new_empty","DisplayName":{"UserLocalizedLabel":null},"Options":[{"Value":"x","Label":"plain"}]}""");

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(9));

        Assert.Null(Value(overview, "Display name"));
        Assert.Equal([null, null, null, null], Assert.Single(overview.Tables).Rows.Single());
    }

    [Fact]
    public async Task A_component_without_an_overview_asks_for_nothing()
    {
        var handler = new FakeHttpHandler();

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(26));

        Assert.Empty(overview.Properties);
        Assert.Empty(overview.Tables);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_cancelled_overview_is_not_reported_as_a_problem()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "canvasapps", _ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fakes.Dataverse(handler).GetOverviewAsync(Item(300)));
    }

    [Fact]
    public void A_sitemap_title_falls_back_to_the_id_and_a_subarea_may_open_nothing()
    {
        var rows = DataverseClient.SitemapOutline("""<SiteMap><Area Id="area1"><Group Id="group1"><SubArea Id="sub1" /></Group></Area></SiteMap>""");

        Assert.Equal(["area1", "group1", "sub1", null], rows.Single());
    }

    [Fact]
    public void An_overview_property_without_a_value_shows_a_dash()
    {
        Assert.Equal("—", new OverviewProperty("Owner", " ").Shown);
        Assert.Equal("Ada", new OverviewProperty("Owner", "Ada").Shown);
    }
}
