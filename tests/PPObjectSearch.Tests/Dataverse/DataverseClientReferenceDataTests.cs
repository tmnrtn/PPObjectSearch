using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>Reading tables, their columns and keys, and their rows for the reference data comparison.</summary>
public class DataverseClientReferenceDataTests
{
    private static readonly EntitySummary Thing = new("new_thing", "Thing", "new_things", "new_thingid", "new_name", false, false);

    private static readonly string[] NameColumn = ["new_name"];

    [Fact]
    public async Task Rows_keep_their_annotations_beside_their_values()
    {
        var id = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "new_things?", $$"""
            {"@odata.context":"ignored","value":[{
              "@odata.etag":"W/\"42\"",
              "new_thingid":"{{id}}",
              "new_name":"Alpha",
              "new_count":7,
              "new_active":true,
              "new_hidden":false,
              "new_notes":null,
              "new_shape":{"x":1},
              "statuscode":1,
              "statuscode@OData.Community.Display.V1.FormattedValue":"Active",
              "_new_parentid_value":"{{parent}}",
              "_new_parentid_value@Microsoft.Dynamics.CRM.lookuplogicalname":"new_parent",
              "_new_parentid_value@Microsoft.Dynamics.CRM.associatednavigationproperty":"new_ParentId",
              "_new_parentid_value@Some.Other.Annotation":"skipped"
            }]}
            """);

        var rows = await Fakes.Dataverse(handler).GetRecordsAsync(Thing, NameColumn, null, 10);

        var row = Assert.Single(rows);
        Assert.Equal(id, row.Id);
        Assert.Equal("W/\"42\"", row.ETag);
        Assert.Equal("Alpha", row.PrimaryName);
        Assert.Equal("7", row.Values["new_count"]);
        Assert.Equal("true", row.Values["new_active"]);
        Assert.Equal("false", row.Values["new_hidden"]);
        Assert.Null(row.Values["new_notes"]);
        Assert.Equal("{\"x\":1}", row.Values["new_shape"]);
        Assert.Equal("Active", row.Formatted["statuscode"]);
        Assert.Equal("new_parent", row.LookupTargets["_new_parentid_value"]);
        Assert.Equal("new_ParentId", row.NavigationProperties["_new_parentid_value"]);
        Assert.False(row.Values.ContainsKey("@odata.etag"));
        Assert.False(row.Formatted.ContainsKey("_new_parentid_value"));

        var url = handler.Requests[0].Url;
        Assert.Contains("$select=new_thingid,new_name&$orderby=new_thingid", url);
        Assert.DoesNotContain("$filter", url);
    }

    private static readonly string[] PrimaryIdTwiceInDifferentCase = ["new_thingid", "NEW_THINGID"];

    [Fact]
    public async Task A_filter_is_sent_with_only_its_unsafe_characters_escaped()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "new_things?", """{"value":[]}""");

        await Fakes.Dataverse(handler).GetRecordsAsync(Thing, PrimaryIdTwiceInDifferentCase, "  contains(new_name,'50% & #1+') ", 10);

        var raw = handler.Requests[0].Uri.OriginalString;
        Assert.Contains("$select=new_thingid&", raw);
        Assert.EndsWith("&$filter=contains(new_name,'50%25 %26 %231%2B')", raw);
    }

    private static readonly int[] RowsReadAfterEachPage = [1, 2];

    [Fact]
    public async Task Paging_stops_at_the_row_cap_and_reports_progress_per_page()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "page=2", """{"value":[{"new_thingid":"x2"},{"new_thingid":"x3"}],"@odata.nextLink":"https://contoso.crm11.dynamics.com/api/data/v9.2/new_things?page=3"}""")
            .OnJson(HttpMethod.Get, "new_things?", """{"value":[{"new_thingid":"x1"}],"@odata.nextLink":"https://contoso.crm11.dynamics.com/api/data/v9.2/new_things?page=2"}""");
        var progress = new List<int>();
        var nameless = Thing with { PrimaryNameAttribute = null };

        var rows = await Fakes.Dataverse(handler).GetRecordsAsync(nameless, Array.Empty<string>(), null, 2, progress.Add);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(Guid.Empty, r.Id));
        Assert.All(rows, r => Assert.Null(r.PrimaryName));
        Assert.Equal(RowsReadAfterEachPage, progress);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static readonly string[] ReadableTablesSorted = ["account", "zeta"];

    [Fact]
    public async Task Tables_without_a_row_endpoint_or_that_are_private_are_left_out_and_the_rest_sorted()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "skiptoken", """
                {"value":[{"LogicalName":"account","DisplayName":{"UserLocalizedLabel":{"Label":"Account"}},"EntitySetName":"accounts","IsManaged":true,"IsActivity":false}]}
                """)
            .OnJson(HttpMethod.Get, "EntityDefinitions?", """
                {"value":[
                  {"LogicalName":"zeta","EntitySetName":"zetas","PrimaryIdAttribute":"zetaid","PrimaryNameAttribute":"name","IsActivity":true},
                  {"LogicalName":"hidden","EntitySetName":"hiddens","IsPrivate":true},
                  {"LogicalName":"noset"},
                  {"EntitySetName":"nameless"}
                ],"@odata.nextLink":"https://contoso.crm11.dynamics.com/api/data/v9.2/EntityDefinitions?skiptoken=1"}
                """);

        var entities = await Fakes.Dataverse(handler).GetEntitiesAsync();

        Assert.Equal(ReadableTablesSorted, entities.Select(e => e.LogicalName));
        var account = entities[0];
        Assert.Equal("accountid", account.PrimaryIdAttribute);
        Assert.Null(account.PrimaryNameAttribute);
        Assert.True(account.IsManaged);
        Assert.True(entities[1].IsActivity);
    }

    private static readonly string[] ComparableColumns = ["new_amount", "new_name"];

    [Fact]
    public async Task Columns_that_restate_another_or_hold_no_comparable_value_are_dropped()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/Attributes?", """
            {"value":[
              {"LogicalName":"new_name","AttributeTypeName":{"Value":"StringType"},"IsValidForCreate":false,"IsValidForUpdate":false},
              {"LogicalName":"new_amount_base","AttributeTypeName":{"Value":"MoneyType"},"AttributeOf":"new_amount"},
              {"LogicalName":"new_secret","AttributeTypeName":{"Value":"StringType"},"IsValidForRead":false},
              {"LogicalName":"new_image","AttributeTypeName":{"Value":"ImageType"}},
              {"LogicalName":"new_untyped"},
              {"AttributeTypeName":{"Value":"StringType"}},
              {"LogicalName":"new_amount","AttributeTypeName":{"Value":"MoneyType"}}
            ]}
            """);

        var columns = await Fakes.Dataverse(handler).GetEntityColumnsAsync("New_Thing");

        Assert.Equal(ComparableColumns, columns.Select(c => c.LogicalName));
        Assert.True(columns[0].IsValidForCreate);
        Assert.False(columns[1].IsValidForCreate);
        Assert.False(columns[1].IsValidForUpdate);
        Assert.Contains("EntityDefinitions(LogicalName='new_thing')", handler.Requests[0].Url);
    }

    private static readonly string[] NonEmptyKeyColumns = ["new_code"];

    [Fact]
    public async Task Keys_without_columns_are_skipped_and_a_schema_name_stands_in_for_a_missing_logical_name()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/Keys?", """
            {"value":[
              {"SchemaName":"new_CodeKey","KeyAttributes":["new_code",null,""]},
              {"LogicalName":"new_empty","KeyAttributes":[]},
              {"LogicalName":"new_odd","KeyAttributes":"new_code"},
              {"KeyAttributes":["new_code"]}
            ]}
            """);

        var keys = await Fakes.Dataverse(handler).GetAlternateKeysAsync(RefTable);

        var key = Assert.Single(keys);
        Assert.Equal("new_CodeKey", key.LogicalName);
        Assert.Equal(NonEmptyKeyColumns, key.KeyAttributes);
    }

    [Fact]
    public async Task A_table_that_refuses_to_list_keys_has_none()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "/Keys?", HttpStatusCode.BadRequest, "keys not supported");

        var keys = await Fakes.Dataverse(handler).GetAlternateKeysAsync(RefTable);

        Assert.Empty(keys);
    }

    [Fact]
    public async Task Cancelling_a_key_read_is_not_swallowed()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/Keys?", """{"value":[]}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Fakes.Dataverse(handler).GetAlternateKeysAsync(RefTable, new CancellationToken(canceled: true)));
    }

    private const string RefTable = "new_thing";
}
