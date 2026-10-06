using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class ComponentOverviewTests
{
    private static readonly Guid Id = Guid.Parse("12121212-1212-1212-1212-121212121212");

    private static SolutionComponentItem Item(int type, string? logical = null, int? category = null) => new()
    {
        Name = "x", ComponentTypeName = "T", ComponentType = type, ComponentLogicalName = logical, ProcessCategory = category, ObjectId = Id
    };

    [Theory]
    [InlineData(300, null, null, ObjectKind.CanvasApp)]
    [InlineData(80, null, null, ObjectKind.ModelDrivenApp)]
    [InlineData(20, null, null, ObjectKind.SecurityRole)]
    [InlineData(9, null, null, ObjectKind.OptionSet)]
    [InlineData(29, null, 4, ObjectKind.BusinessProcessFlow)]
    [InlineData(10070, "bot", null, ObjectKind.Agent)]
    [InlineData(10101, "customapi", null, ObjectKind.CustomApi)]
    [InlineData(29, null, 2, ObjectKind.BusinessRule)]
    [InlineData(26, null, null, ObjectKind.Other)]
    public void Each_type_with_an_overview_is_recognised(int type, string? logical, int? category, ObjectKind expected)
    {
        Assert.Equal(expected, DataverseClient.OverviewKindOf(Item(type, logical, category)));
        Assert.Equal(expected, DetailsTabs.KindOf(Item(type, logical, category)) is var kind && DetailsTabs.HasOverview(kind) ? kind : ObjectKind.Other);
    }

    [Fact]
    public void A_sitemap_reads_as_area_group_item_and_target()
    {
        const string xml = """
            <SiteMap>
              <Area Id="sales"><Titles><Title LCID="1033" Title="Sales" /></Titles>
                <Group Id="customers" Title="Customers">
                  <SubArea Id="accounts" Entity="account" Title="Accounts" />
                  <SubArea Id="portal" Url="/WebResources/new_portal.html"><Titles><Title LCID="1033" Title="Portal" /></Titles></SubArea>
                </Group>
              </Area>
            </SiteMap>
            """;

        var rows = DataverseClient.SitemapOutline(xml);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["Sales", "Customers", "Accounts", "Table: account"], rows[0]);
        Assert.Equal(["Sales", "Customers", "Portal", "/WebResources/new_portal.html"], rows[1]);
        Assert.Empty(DataverseClient.SitemapOutline("<not xml"));
    }

    [Fact]
    public async Task A_custom_api_shows_its_shape_and_parameters()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"customapis({Id})", """
                {"uniquename":"new_Recalculate","isfunction":false,"bindingtype":1,"bindingtype@OData.Community.Display.V1.FormattedValue":"Entity",
                 "boundentitylogicalname":"account","_plugintypeid_value@OData.Community.Display.V1.FormattedValue":"Contoso.Recalculate","isprivate":false}
                """)
            .OnJson(HttpMethod.Get, "customapirequestparameters?", """
                {"value":[{"uniquename":"Mode","type":10,"type@OData.Community.Display.V1.FormattedValue":"String","isoptional":true}]}
                """)
            .OnJson(HttpMethod.Get, "customapiresponseproperties?", """{"value":[]}""");

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(10101, "customapi"));

        Assert.Empty(overview.Problems);
        Assert.Contains(overview.Properties, p => p is { Label: "Kind", Value: "Action (POST)" });
        Assert.Contains(overview.Properties, p => p is { Label: "Binding", Value: "Entity" });
        Assert.Contains(overview.Properties, p => p is { Label: "Plug-in type", Value: "Contoso.Recalculate" });
        var parameters = overview.Tables.Single(t => t.Title == "Request parameters");
        Assert.Equal(["Mode", "String", "Optional", null], parameters.Rows.Single());
        Assert.Equal("Response properties (0)", overview.Tables.Single(t => t.Title == "Response properties").Heading);
    }

    [Fact]
    public async Task A_choice_lists_its_options_and_a_failing_part_is_reported()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"GlobalOptionSetDefinitions({Id})", """
                {"Name":"new_priority","OptionSetType":"Picklist","DisplayName":{"UserLocalizedLabel":{"Label":"Priority"}},
                 "Options":[{"Value":100000000,"Color":"#ff0000","Label":{"UserLocalizedLabel":{"Label":"High"}}}]}
                """);

        var overview = await Fakes.Dataverse(handler).GetOverviewAsync(Item(9));
        Assert.Contains(overview.Properties, p => p is { Label: "Display name", Value: "Priority" });
        Assert.Equal(["100000000", "High", "#ff0000", null], overview.Tables.Single().Rows.Single());

        var failing = await Fakes.Dataverse(new FakeHttpHandler()).GetOverviewAsync(Item(300));
        Assert.Single(failing.Problems);
        Assert.StartsWith("app:", failing.Problems[0]);
    }
}
