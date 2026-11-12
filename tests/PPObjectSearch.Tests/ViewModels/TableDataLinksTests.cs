using System.Net.Http;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The table data window's links: a lookup to its row, and a row to the rows of a table that point at it.</summary>
public class TableDataLinksTests
{
    private const string Tables = """
        {"value":[
          {"LogicalName":"new_taxcode","DisplayName":{"UserLocalizedLabel":{"Label":"Tax code"}},"EntitySetName":"new_taxcodes",
           "PrimaryIdAttribute":"new_taxcodeid","PrimaryNameAttribute":"new_name","IsPrivate":false,"IsManaged":false,"IsActivity":false},
          {"LogicalName":"new_group","DisplayName":{"UserLocalizedLabel":{"Label":"Group"}},"EntitySetName":"new_groups",
           "PrimaryIdAttribute":"new_groupid","PrimaryNameAttribute":"new_name","IsPrivate":false,"IsManaged":false,"IsActivity":false},
          {"LogicalName":"new_rateline","DisplayName":{"UserLocalizedLabel":{"Label":"Rate line"}},"EntitySetName":"new_ratelines",
           "PrimaryIdAttribute":"new_ratelineid","PrimaryNameAttribute":"new_name","IsPrivate":false,"IsManaged":false,"IsActivity":false}
        ]}
        """;

    private static string Attribute(string name, string type, string display) =>
        $$$"""{"LogicalName":"{{{name}}}","DisplayName":{"UserLocalizedLabel":{"Label":"{{{display}}}"}},"AttributeTypeName":{"Value":"{{{type}}}"},"IsValidForRead":true}""";

    private static readonly string Columns = "{\"value\":[" + string.Join(",",
        Attribute("new_taxcodeid", "UniqueidentifierType", "Tax code"),
        Attribute("new_name", "StringType", "Name"),
        Attribute("new_parentid", "LookupType", "Parent")) + "]}";

    private const string Rows = """
        {"value":[
          {"new_taxcodeid":"00000000-0000-0000-0000-000000000001","new_name":"Reduced",
           "_new_parentid_value":"aaaaaaaa-0000-0000-0000-000000000001",
           "_new_parentid_value@OData.Community.Display.V1.FormattedValue":"Group A",
           "_new_parentid_value@Microsoft.Dynamics.CRM.lookuplogicalname":"new_group"},
          {"new_taxcodeid":"00000000-0000-0000-0000-000000000002","new_name":"Standard","_new_parentid_value":null}
        ]}
        """;

    private static FakeHttpHandler Handler() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_taxcode')/Attributes", Columns)
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_taxcode')/OneToManyRelationships", """
            {"value":[
              {"SchemaName":"new_taxcode_rateline","ReferencingEntity":"new_rateline","ReferencingAttribute":"new_taxcodeid","ReferencedAttribute":"new_taxcodeid"},
              {"SchemaName":"new_taxcode_hidden","ReferencingEntity":"new_hidden","ReferencingAttribute":"new_taxcodeid","ReferencedAttribute":"new_taxcodeid"}
            ]}
            """)
        .OnJson(HttpMethod.Get, "EntityDefinitions?", Tables)
        .OnJson(HttpMethod.Get, "savedqueries", """{"value":[{"isdefault":true,"layoutxml":"<grid><row><cell name=\"new_name\" /><cell name=\"new_parentid\" /></row></grid>"}]}""")
        .OnJson(HttpMethod.Get, "new_taxcodes?", Rows);

    private static async Task<TableDataViewModel> Opened(FakeHttpHandler handler)
    {
        var vm = new TableDataViewModel(TestSessions.Connected(handler), "new_taxcode");
        await vm.LoadAsync();
        return vm;
    }

    private static int Index(TableDataViewModel vm, string logicalName) =>
        vm.ShownColumns.Select((c, i) => (c, i)).First(x => x.c.LogicalName == logicalName).i;

    [Fact]
    public async Task A_lookup_links_to_its_row_in_the_table_it_points_at()
    {
        var vm = await Opened(Handler());
        var parent = Index(vm, "new_parentid");

        Assert.True(vm.ShownColumns[parent].IsLink);
        var link = vm.LinkTarget(vm.Rows[0], parent)!.Value;

        Assert.Equal("new_group", link.Table.LogicalName);
        Assert.Equal("new_groupid eq aaaaaaaa-0000-0000-0000-000000000001", link.Filter.Filter);
        Assert.Equal("Group is 'Group A'", link.Filter.Label);

        // An empty lookup, and a column that is not one, link nowhere.
        Assert.Null(vm.LinkTarget(vm.Rows[1], parent));
        Assert.Null(vm.LinkTarget(vm.Rows[0], Index(vm, "new_name")));
    }

    [Fact]
    public async Task Related_rows_are_a_column_to_add_that_is_never_read_or_exported()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        // Only the relationship whose table can be read here, after the table's own columns - and not to filter by.
        var related = Assert.Single(vm.Columns, c => c.IsRelationship);
        Assert.Same(related, vm.Columns[^1]);
        Assert.Equal("Rate line (new_taxcodeid)", related.Header);
        Assert.Equal("Related rows", related.TypeLabel);
        Assert.DoesNotContain(related, vm.FilterColumns);

        related.IsShown = true;
        await vm.LoadRowsAsync();

        var read = handler.Requests.Last(r => r.Url.Contains("new_taxcodes?", StringComparison.Ordinal));
        Assert.DoesNotContain("new_taxcode_rateline", read.Url);

        var column = Index(vm, "new_taxcode_rateline");
        Assert.Null(vm.Rows[0].Cells[column]);

        var (headers, rows) = vm.Shown();
        Assert.DoesNotContain(related.Header, headers);
        Assert.All(rows, r => Assert.Equal(headers.Count, r.Count));

        var link = vm.LinkTarget(vm.Rows[0], column)!.Value;
        Assert.Equal("new_rateline", link.Table.LogicalName);
        Assert.Equal("_new_taxcodeid_value eq 00000000-0000-0000-0000-000000000001", link.Filter.Filter);
        Assert.Equal("new_taxcodeid is 'Reduced'", link.Filter.Label);
    }

    [Fact]
    public async Task A_columns_of_related_rows_cells_cannot_be_copied()
    {
        var vm = await Opened(Handler());
        Assert.Single(vm.Columns, c => c.IsRelationship).IsShown = true;
        await vm.LoadRowsAsync();

        vm.SetCurrentCell(vm.Rows[0], Index(vm, "new_taxcode_rateline"));

        Assert.False(vm.CopyValueCommand.CanExecute(null));
        Assert.False(vm.CopyColumnCommand.CanExecute(null));
        Assert.True(vm.FollowLinkCommand.CanExecute(null));
        Assert.True(vm.AuditHistoryCommand.CanExecute(null));
    }
}
