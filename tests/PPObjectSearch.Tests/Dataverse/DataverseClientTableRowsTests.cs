using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>Reading a table's rows to look at them: the default view's columns, the sort, and a read the cap cuts short.</summary>
public class DataverseClientTableRowsTests
{
    private static readonly EntitySummary TaxCode = new("new_taxcode", "Tax code", "new_taxcodes", "new_taxcodeid", "new_name", false, false);
    private static readonly EntitySummary Nameless = new("new_setting", "Setting", "new_settings", "new_settingid", null, false, false);

    private static readonly string[] NameColumn = ["new_name"];

    private static string Rows(int count, string? nextLink = null)
    {
        var rows = Enumerable.Range(1, count)
            .Select(i => $$"""{"new_taxcodeid":"{{Guid.NewGuid()}}","new_name":"Code {{i}}"}""");
        var next = nextLink is null ? string.Empty : $$""","@odata.nextLink":"{{nextLink}}" """;
        return $$"""{"value":[{{string.Join(",", rows)}}]{{next}}}""";
    }

    [Fact]
    public async Task The_default_public_view_gives_the_columns_in_its_order()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "savedqueries", """
            {"value":[
              {"savedqueryid":"00000000-0000-0000-0000-000000000001","isdefault":false,
               "layoutxml":"<grid><row><cell name=\"new_other\" /></row></grid>"},
              {"savedqueryid":"00000000-0000-0000-0000-000000000002","isdefault":true,
               "layoutxml":"<grid name=\"resultset\"><row name=\"result\" id=\"new_taxcodeid\"><cell name=\"new_name\" width=\"300\" /><cell name=\"new_rate\" /><cell name=\"a_1.name\" /><cell name=\"new_rate\" /><cell name=\"statecode\" /></row></grid>"}
            ]}
            """);

        var columns = await Fakes.Dataverse(handler).GetDefaultViewColumnsAsync("New_TaxCode");

        Assert.Equal(["new_name", "new_rate", "statecode"], columns);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("returnedtypecode eq 'new_taxcode' and querytype eq 0", request.Url);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Fact]
    public async Task Without_a_default_view_the_first_public_view_answers()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "savedqueries", """
            {"value":[{"isdefault":false,"layoutxml":"<grid><row><cell name=\"new_code\" /></row></grid>"},
                      {"isdefault":false,"layoutxml":"<grid><row><cell name=\"new_later\" /></row></grid>"}]}
            """);

        Assert.Equal(["new_code"], await Fakes.Dataverse(handler).GetDefaultViewColumnsAsync("new_taxcode"));
    }

    [Fact]
    public async Task A_table_whose_views_cannot_be_read_has_no_view_columns()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "savedqueries", HttpStatusCode.BadRequest, "not for virtual tables");

        Assert.Empty(await Fakes.Dataverse(handler).GetDefaultViewColumnsAsync("new_virtual"));
    }

    [Theory]
    [InlineData("<grid><row>")]
    [InlineData("not xml")]
    [InlineData("<grid />")]
    public void Layout_xml_that_cannot_be_read_gives_no_columns(string layout)
    {
        Assert.Empty(DataverseClient.ReadLayoutColumns(layout));
    }

    [Fact]
    public async Task Rows_are_read_with_the_id_first_sorted_by_name_then_id_with_their_labels()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "new_taxcodes?", """
            {"value":[{"new_taxcodeid":"00000000-0000-0000-0000-00000000000a","new_name":"Standard","statecode":0,
                       "statecode@OData.Community.Display.V1.FormattedValue":"Active"}]}
            """);

        var result = await Fakes.Dataverse(handler).GetTableRowsAsync(TaxCode, ["new_name", "statecode"], "new_rate gt 5", 100);

        var row = Assert.Single(result.Rows);
        Assert.False(result.IsTruncated);
        Assert.Equal("Standard", row.PrimaryName);
        Assert.Equal("Active", row.Label("statecode"));

        var request = Assert.Single(handler.Requests);
        Assert.Contains("$select=new_taxcodeid,new_name,statecode", request.Url);
        Assert.Contains("$orderby=new_name,new_taxcodeid", request.Url);
        Assert.Contains("$filter=new_rate gt 5", request.Url);
        Assert.Contains("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"", request.Header("Prefer"));
    }

    [Fact]
    public async Task A_table_without_a_primary_name_is_sorted_by_its_id()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "new_settings?", """{"value":[]}""");

        await Fakes.Dataverse(handler).GetTableRowsAsync(Nameless, ["new_value"], null, 100);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("$orderby=new_settingid", request.Url);
        Assert.DoesNotContain("$filter", request.Url);
    }

    [Fact]
    public async Task A_read_the_cap_stops_inside_a_page_is_truncated()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "new_taxcodes?", Rows(5));

        var result = await Fakes.Dataverse(handler).GetTableRowsAsync(TaxCode, NameColumn, null, 3);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public async Task A_read_the_cap_stops_at_a_page_end_with_more_pages_is_truncated_and_reads_no_further()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "page=2", Rows(2))
            .OnJson(HttpMethod.Get, "new_taxcodes?", Rows(3, Fakes.EnvironmentUrl + "/api/data/v9.2/new_taxcodes?page=2"));

        var counts = new List<int>();
        var result = await Fakes.Dataverse(handler).GetTableRowsAsync(TaxCode, NameColumn, null, 3, counts.Add);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.IsTruncated);
        Assert.Equal([3], counts);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_table_that_runs_out_exactly_at_the_cap_is_not_truncated()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "page=2", Rows(2))
            .OnJson(HttpMethod.Get, "new_taxcodes?", Rows(3, Fakes.EnvironmentUrl + "/api/data/v9.2/new_taxcodes?page=2"));

        var counts = new List<int>();
        var result = await Fakes.Dataverse(handler).GetTableRowsAsync(TaxCode, NameColumn, null, 5, counts.Add);

        Assert.Equal(5, result.Rows.Count);
        Assert.False(result.IsTruncated);
        Assert.Equal([3, 5], counts);
    }

    [Fact]
    public async Task A_table_that_refuses_the_sort_is_read_unsorted()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$orderby", HttpStatusCode.BadRequest, "$orderby is not supported")
            .OnJson(HttpMethod.Get, "new_taxcodes?", Rows(2));

        var result = await Fakes.Dataverse(handler).GetTableRowsAsync(TaxCode, NameColumn, null, 10);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("$orderby", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Any_other_failure_is_not_retried()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "new_taxcodes?", HttpStatusCode.Forbidden, "no read privilege");

        await Assert.ThrowsAsync<DataverseException>(() =>
            Fakes.Dataverse(handler).GetTableRowsAsync(TaxCode, NameColumn, null, 10));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_lookup_reaches_each_table_it_points_at_through_its_navigation_property()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/ManyToOneRelationships", """
                {"value":[
                  {"ReferencingAttribute":"parentcustomerid","ReferencingEntityNavigationPropertyName":"parentcustomerid_account","ReferencedEntity":"account"},
                  {"ReferencingAttribute":"ParentCustomerId","ReferencingEntityNavigationPropertyName":"parentcustomerid_contact","ReferencedEntity":"contact"},
                  {"ReferencingAttribute":"parentcustomerid","ReferencingEntityNavigationPropertyName":"parentcustomerid_gone","ReferencedEntity":"new_gone"},
                  {"ReferencingAttribute":"parentcustomerid","ReferencingEntityNavigationPropertyName":"parentcustomerid_nameless","ReferencedEntity":"new_nameless"},
                  {"ReferencingAttribute":"ownerid","ReferencingEntityNavigationPropertyName":"ownerid","ReferencedEntity":"owner"}
                ]}
                """)
            .OnJson(HttpMethod.Get, "LogicalName='account')?", """{"PrimaryNameAttribute":"name"}""")
            .OnJson(HttpMethod.Get, "LogicalName='contact')?", """{"PrimaryNameAttribute":"fullname"}""")
            .OnJson(HttpMethod.Get, "LogicalName='new_nameless')?", """{"PrimaryNameAttribute":null}""")
            .OnError(HttpMethod.Get, "LogicalName='new_gone')?", HttpStatusCode.NotFound, "no such table");

        var names = await Fakes.Dataverse(handler).GetLookupNavigationsAsync("Contact", "parentcustomerid");

        Assert.Equal(
            [
                new LookupNavigation("parentcustomerid_account", "account", "name"),
                new LookupNavigation("parentcustomerid_contact", "contact", "fullname")
            ],
            names);
        Assert.Contains("EntityDefinitions(LogicalName='contact')/ManyToOneRelationships", handler.Requests[0].Url);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task A_lookup_whose_relationships_cannot_be_read_has_no_names_to_filter_on()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "/ManyToOneRelationships", HttpStatusCode.BadRequest, "not for virtual tables");

        Assert.Empty(await Fakes.Dataverse(handler).GetLookupNavigationsAsync("new_virtual", "new_parentid"));
    }
}
