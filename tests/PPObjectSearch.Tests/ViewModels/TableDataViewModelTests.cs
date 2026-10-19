using System.Net;
using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The table data window: picking a table, its default columns, filtering, the row limit - and that it only reads.</summary>
public class TableDataViewModelTests
{
    private const string Tables = """
        {"value":[
          {"LogicalName":"new_taxcode","DisplayName":{"UserLocalizedLabel":{"Label":"Tax code"}},"EntitySetName":"new_taxcodes",
           "PrimaryIdAttribute":"new_taxcodeid","PrimaryNameAttribute":"new_name","IsPrivate":false,"IsManaged":false,"IsActivity":false},
          {"LogicalName":"msdyn_solutioncomponentsummary","DisplayName":{"UserLocalizedLabel":{"Label":"Solution component summary"}},
           "EntitySetName":"msdyn_solutioncomponentsummaries","PrimaryIdAttribute":"msdyn_solutioncomponentsummaryid",
           "PrimaryNameAttribute":"msdyn_name","IsPrivate":false,"IsManaged":true,"IsActivity":false},
          {"LogicalName":"new_hidden","EntitySetName":"new_hiddens","PrimaryIdAttribute":"new_hiddenid","IsPrivate":true}
        ]}
        """;

    private static string Attribute(string name, string type, string? display = null) =>
        $$"""{"LogicalName":"{{name}}","DisplayName":{"UserLocalizedLabel":{{(display is null ? "null" : $$"""{"Label":"{{display}}"}""")}}},"AttributeTypeName":{"Value":"{{type}}"},"IsValidForRead":true}""";

    private static readonly string TaxCodeColumns = "{\"value\":[" + string.Join(",",
        Attribute("new_taxcodeid", "UniqueidentifierType", "Tax code"),
        Attribute("new_name", "StringType", "Name"),
        Attribute("new_rate", "DecimalType", "Rate"),
        Attribute("new_region", "PicklistType", "Region"),
        Attribute("statecode", "StateType", "Status"),
        Attribute("new_notes", "MemoType", "Notes"),
        Attribute("new_parentid", "LookupType", "Parent"),
        Attribute("msdyn_modifiedon", "StringType", "Modified on")) + "]}";

    private const string View = """
        {"value":[{"isdefault":true,"layoutxml":"<grid><row><cell name=\"new_name\" /><cell name=\"new_rate\" /><cell name=\"new_region\" /></row></grid>"}]}
        """;

    private const string TaxCodeRows = """
        {"value":[
          {"new_taxcodeid":"00000000-0000-0000-0000-000000000001","new_name":"Reduced","new_rate":5,
           "new_rate@OData.Community.Display.V1.FormattedValue":"5.00",
           "new_region":100000001,"new_region@OData.Community.Display.V1.FormattedValue":"Europe",
           "_new_parentid_value":"aaaaaaaa-0000-0000-0000-000000000001","_new_parentid_value@OData.Community.Display.V1.FormattedValue":"Group A"},
          {"new_taxcodeid":"00000000-0000-0000-0000-000000000002","new_name":"Standard","new_rate":20,
           "new_rate@OData.Community.Display.V1.FormattedValue":"20.00",
           "new_region":100000000,"new_region@OData.Community.Display.V1.FormattedValue":"Americas"},
          {"new_taxcodeid":"00000000-0000-0000-0000-000000000003","new_name":"Zero","new_rate":0,
           "new_region":null}
        ]}
        """;

    private static FakeHttpHandler Handler(string rows = TaxCodeRows) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_taxcode')/Attributes", TaxCodeColumns)
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_taxcode')/ManyToOneRelationships", """
            {"value":[{"ReferencingAttribute":"new_parentid","ReferencingEntityNavigationPropertyName":"new_ParentId","ReferencedEntity":"new_group"}]}
            """)
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_group')?", """{"PrimaryNameAttribute":"new_name"}""")
        .OnJson(HttpMethod.Get, "EntityDefinitions?", Tables)
        .OnJson(HttpMethod.Get, "savedqueries", View)
        .OnJson(HttpMethod.Get, "new_taxcodes?", rows);

    private static async Task<TableDataViewModel> Opened(FakeHttpHandler handler)
    {
        var vm = new TableDataViewModel(TestSessions.Connected(handler), "NEW_TAXCODE");
        await vm.LoadAsync();
        return vm;
    }

    private static List<RecordedRequest> RowReads(FakeHttpHandler handler) =>
        handler.Requests.Where(r => r.Url.Contains("new_taxcodes?", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task The_readable_tables_are_listed_and_searched_by_either_name()
    {
        var handler = Handler();
        var vm = new TableDataViewModel(TestSessions.Connected(handler));

        await vm.LoadAsync();

        Assert.Equal(["msdyn_solutioncomponentsummary", "new_taxcode"], vm.Tables.Select(t => t.LogicalName).Order());
        Assert.Null(vm.SelectedTable);
        Assert.Equal("Choose a table", vm.EmptyHeading);
        Assert.Equal("2 tables - choose one to read its rows.", vm.StatusLine);

        vm.TableSearch = "TAX";
        Assert.Equal(["new_taxcode"], vm.TablesView.Cast<EntitySummary>().Select(t => t.LogicalName));

        vm.TableSearch = "component summary";
        Assert.Equal(["msdyn_solutioncomponentsummary"], vm.TablesView.Cast<EntitySummary>().Select(t => t.LogicalName));
        Assert.Empty(RowReads(handler));
    }

    [Fact]
    public async Task A_table_opens_with_its_primary_name_and_default_view_columns_showing_labels()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        Assert.Equal("new_taxcode", vm.SelectedTable?.LogicalName);
        Assert.Equal("Tax code (new_taxcode)", vm.TableHeading);
        Assert.Equal(["Name", "Rate", "Region"], vm.ShownColumns.Select(c => c.Header));
        Assert.Equal("Columns (3)", vm.ColumnsLabel);

        // The default ones first, in the view's order, then the rest by name.
        Assert.Equal(["new_name", "new_rate", "new_region", "msdyn_modifiedon", "new_notes", "new_parentid", "statecode", "new_taxcodeid"],
            vm.Columns.Select(c => c.LogicalName));

        Assert.Equal(["Reduced", "5.00", "Europe"], vm.Rows[0].Cells);
        Assert.Equal(["Zero", "0", null], vm.Rows[2].Cells);
        Assert.Equal("3 rows", vm.CountLabel);
        Assert.True(vm.HasRows);
        Assert.True(vm.HasStateCode);
        Assert.Equal(string.Empty, vm.Warnings);
        Assert.StartsWith("Read 3 rows of Tax code (new_taxcode) in ", vm.StatusLine);

        var read = Assert.Single(RowReads(handler));
        Assert.Contains("$select=new_taxcodeid,new_name,new_rate,new_region", read.Url);
        Assert.DoesNotContain("$filter", read.Url);
    }

    [Fact]
    public async Task Nothing_but_reads_is_ever_sent()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_name");
        vm.FilterValue = "Standard";
        await vm.ApplyFilterCommand.ExecuteAsync(null);
        vm.ActiveOnly = true;
        await vm.Pending;
        vm.RowCap = 500;
        await vm.Pending;
        await vm.ClearFilterCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(handler.Requests.Count >= 8);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Raw_values_show_ids_and_option_numbers()
    {
        var vm = await Opened(Handler());

        vm.ShowRawValues = true;

        Assert.Equal(["Reduced", "5", "100000001"], vm.Rows[0].Cells);
    }

    [Fact]
    public async Task The_quick_filter_narrows_the_rows_read_without_reading_again()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.QuickFilter = "  americas ";

        Assert.Equal(["Standard"], vm.RowsView.Cast<TableDataRow>().Select(r => r.Cells[0]));
        Assert.Equal("1 of 3 rows", vm.CountLabel);
        Assert.Single(RowReads(handler));

        vm.QuickFilter = "nowhere";

        Assert.False(vm.HasRows);
        Assert.Equal("Nothing matches", vm.EmptyHeading);
        Assert.Equal("0 of 3 rows", vm.CountLabel);
    }

    [Fact]
    public async Task A_read_the_row_limit_cuts_short_says_so()
    {
        var vm = await Opened(Handler());

        vm.RowCap = 500;
        await vm.Pending;
        Assert.Equal(string.Empty, vm.Warnings);

        var many = "{\"value\":[" + string.Join(",", Enumerable.Range(0, 501).Select(i => $$"""{"new_taxcodeid":"{{Guid.NewGuid()}}","new_name":"Code {{i}}"}""")) + "]}";
        var truncated = await Opened(Handler(many));
        truncated.RowCap = 500;
        await truncated.Pending;

        Assert.Equal(500, truncated.Rows.Count);
        Assert.Equal("Only the first 500 rows were read - Tax code (new_taxcode) has more. " +
                     "Add a server filter, or raise the row limit, to see the rest.", truncated.Warnings);
        Assert.Contains(truncated.Warnings, truncated.ExportNotes());
    }

    [Fact]
    public async Task A_date_held_as_text_is_filtered_as_text()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "msdyn_modifiedon");

        Assert.Equal(TableFilterOperator.Contains, vm.FilterOperator?.Operator);
        Assert.DoesNotContain(vm.FilterOperators, o => o.Operator == TableFilterOperator.On);
        Assert.Equal("text", vm.FilterHint);

        vm.FilterValue = "2026-10";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Contains("$filter=contains(msdyn_modifiedon,'2026-10')", RowReads(handler)[^1].Url);
        Assert.True(vm.HasServerFilter);
        Assert.Equal("Modified on contains '2026-10'", vm.ServerFilterLabel);
        Assert.Contains("Filter: Modified on contains '2026-10'", vm.ExportNotes());
    }

    [Fact]
    public async Task A_choice_can_be_filtered_by_a_label_already_read()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_region");
        vm.FilterValue = "europe";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Contains("$filter=new_region eq 100000001", RowReads(handler)[^1].Url);
        Assert.Equal("Region equals 'europe'", vm.ServerFilterLabel);
    }

    [Fact]
    public async Task A_choice_label_not_read_yet_asks_for_its_number_and_reads_nothing()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_region");
        vm.FilterValue = "Asia";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal("No row read so far has \"Asia\" in Region - type the choice's number instead.", vm.StatusLine);
        Assert.Single(RowReads(handler));
        Assert.False(vm.HasServerFilter);
    }

    [Fact]
    public async Task A_value_that_does_not_fit_the_column_reads_nothing()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_rate");
        vm.FilterValue = "high";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal("\"high\" is not a number.", vm.StatusLine);
        Assert.Single(RowReads(handler));
    }

    [Fact]
    public async Task Is_empty_needs_no_value()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_notes");
        vm.FilterOperator = vm.FilterOperators.First(o => o.Operator == TableFilterOperator.IsEmpty);

        Assert.False(vm.NeedsFilterValue);
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Contains("$filter=new_notes eq null", RowReads(handler)[^1].Url);
        Assert.Equal("Notes is empty", vm.ServerFilterLabel);
    }

    [Fact]
    public async Task Active_only_combines_with_the_filter_and_clearing_keeps_it()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_rate");
        vm.FilterOperator = vm.FilterOperators.First(o => o.Operator == TableFilterOperator.GreaterThan);
        vm.FilterValue = "1";
        await vm.ApplyFilterCommand.ExecuteAsync(null);
        vm.ActiveOnly = true;
        await vm.Pending;

        Assert.Contains("$filter=(new_rate gt 1) and (statecode eq 0)", RowReads(handler)[^1].Url);
        Assert.Contains("Active rows only", vm.ExportNotes());

        await vm.ClearFilterCommand.ExecuteAsync(null);

        Assert.Contains("$filter=statecode eq 0", RowReads(handler)[^1].Url);
        Assert.False(vm.HasServerFilter);
        Assert.Equal(string.Empty, vm.FilterValue);
    }

    [Fact]
    public async Task No_row_matching_the_filter_says_how_to_widen_it()
    {
        var vm = await Opened(Handler("""{"value":[]}"""));

        vm.ActiveOnly = true;
        await vm.Pending;

        Assert.Equal("No rows", vm.EmptyHeading);
        Assert.Equal("No row matches the filters. Remove a filter, or untick Active only.", vm.EmptyText);
        Assert.False(vm.ExportCsvCommand.CanExecute(null));
    }

    [Fact]
    public async Task Ticked_columns_are_read_when_applied_and_the_defaults_come_back()
    {
        var handler = Handler();
        var vm = await Opened(handler);
        vm.IsColumnPickerOpen = true;

        vm.ColumnSearch = "note";
        Assert.Equal(["new_notes"], vm.ColumnsView.Cast<TableDataColumn>().Select(c => c.LogicalName));

        vm.Columns.First(c => c.LogicalName == "new_notes").IsShown = true;
        vm.Columns.First(c => c.LogicalName == "new_rate").IsShown = false;
        Assert.Equal(3, vm.ShownColumns.Count);

        await vm.ApplyColumnsCommand.ExecuteAsync(null);

        Assert.Equal(["Name", "Region", "Notes"], vm.ShownColumns.Select(c => c.Header));
        Assert.Contains("$select=new_taxcodeid,new_name,new_region,new_notes", RowReads(handler)[^1].Url);
        Assert.False(vm.IsColumnPickerOpen);

        vm.DefaultColumnsCommand.Execute(null);
        Assert.Equal(["new_name", "new_rate", "new_region"], vm.Columns.Where(c => c.IsShown).Select(c => c.LogicalName));
    }

    [Fact]
    public async Task No_column_ticked_reads_nothing()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        foreach (var column in vm.Columns) column.IsShown = false;
        await vm.ApplyColumnsCommand.ExecuteAsync(null);

        Assert.Equal("Tick at least one column to show.", vm.StatusLine);
        Assert.Single(RowReads(handler));
        Assert.Equal(3, vm.ShownColumns.Count);
    }

    [Fact]
    public void A_table_with_no_view_shows_its_name_and_status_and_one_with_neither_its_id()
    {
        var table = new EntitySummary("new_thing", "Thing", "new_things", "new_thingid", "new_name", false, false);
        EntityColumn[] columns =
        [
            new("new_thingid", null, "UniqueidentifierType", true, false),
            new("new_name", null, "StringType", false, true),
            new("statecode", null, "StateType", false, false)
        ];

        Assert.Equal(["new_name", "statecode"], TableDataViewModel.DefaultColumns(table, columns, []));
        Assert.Equal(["new_name"], TableDataViewModel.DefaultColumns(table, columns, ["NEW_NAME", "a_1.name", "gone"]));
        Assert.Equal(["new_thingid"], TableDataViewModel.DefaultColumns(table with { PrimaryNameAttribute = null }, columns[..1], []));
    }

    [Fact]
    public async Task A_table_that_is_not_listed_says_so()
    {
        var handler = Handler();
        var vm = new TableDataViewModel(TestSessions.Connected(handler), "new_missing");

        await vm.LoadAsync();

        Assert.Null(vm.SelectedTable);
        Assert.Equal("new_missing is not among the tables whose rows can be read here.", vm.StatusLine);
    }

    [Fact]
    public async Task A_failed_read_says_why()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_taxcode')/Attributes", TaxCodeColumns)
            .OnJson(HttpMethod.Get, "EntityDefinitions?", Tables)
            .OnJson(HttpMethod.Get, "savedqueries", View)
            .OnError(HttpMethod.Get, "new_taxcodes?", HttpStatusCode.Forbidden, "no read privilege on new_taxcode");

        var vm = await Opened(handler);

        Assert.StartsWith("Could not read Tax code (new_taxcode) - ", vm.StatusLine);
        Assert.Contains("no read privilege", vm.StatusLine);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Stop_ends_a_read_and_says_so()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_taxcode')/Attributes", TaxCodeColumns)
            .OnJson(HttpMethod.Get, "EntityDefinitions?", Tables)
            .OnJson(HttpMethod.Get, "savedqueries", View)
            .OnAsync(HttpMethod.Get, "new_taxcodes?", _ => release.Task);

        var vm = new TableDataViewModel(TestSessions.Connected(handler), "new_taxcode");
        var load = vm.LoadAsync();
        await TestSessions.Until(() => RowReads(handler).Count == 1);

        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);

        // The fake answers whatever the token says; the read then finds itself stopped.
        release.SetResult(FakeHttpHandler.Json(TaxCodeRows));
        await load;

        Assert.Equal("Stopped.", vm.StatusLine);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task A_tab_that_is_not_connected_reads_nothing()
    {
        var vm = new TableDataViewModel(TestSessions.Disconnected(), "new_taxcode");

        await vm.LoadAsync();

        Assert.Empty(vm.Tables);
        Assert.Equal("Table data — contoso", vm.Title);
        Assert.NotNull(vm.Session);
        Assert.Equal([500, 5000, 50_000], vm.RowCaps);
        Assert.False(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task What_is_exported_is_what_is_shown()
    {
        var vm = await Opened(Handler());
        vm.QuickFilter = "standard";

        var (headers, rows) = vm.Shown();

        Assert.Equal(["Name", "Rate", "Region"], headers);
        var row = Assert.Single(rows);
        Assert.Equal(["Standard", "20.00", "Americas"], row);
        Assert.Equal(["Environment: contoso", "Quick filter: standard", "Rows: 1 of 3 rows"], vm.ExportNotes());
        Assert.True(vm.ExportCsvCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choosing_another_table_starts_it_afresh()
    {
        var handler = Handler()
            .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='msdyn_solutioncomponentsummary')/Attributes",
                "{\"value\":[" + Attribute("msdyn_name", "StringType", "Name") + "]}")
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries?", """{"value":[{"msdyn_name":"account"}]}""");
        var vm = await Opened(handler);
        vm.QuickFilter = "standard";
        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_name");
        vm.FilterValue = "x";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        vm.SelectedTable = vm.Tables.First(t => t.LogicalName == "msdyn_solutioncomponentsummary");
        await vm.Pending;

        Assert.Equal(["Name"], vm.ShownColumns.Select(c => c.Header));
        Assert.Equal(["account"], vm.Rows.Single().Cells);
        Assert.False(vm.HasServerFilter);
        Assert.Equal(string.Empty, vm.QuickFilter);
        Assert.False(vm.HasStateCode);
        Assert.DoesNotContain("$filter", handler.Requests[^1].Url);
    }

    [Fact]
    public async Task A_lookup_is_filtered_by_the_name_of_the_row_it_points_at()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_parentid");

        Assert.Equal(TableFilterOperator.Contains, vm.FilterOperator?.Operator);
        Assert.Equal("the related row's name, or its id", vm.FilterHint);

        vm.FilterValue = "Group";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Contains("$filter=contains(new_ParentId/new_name,'Group')", RowReads(handler)[^1].Url);
        Assert.Equal("Parent contains 'Group'", vm.ServerFilterLabel);

        // The second condition on the same lookup uses what was read for the first.
        vm.FilterOperator = vm.FilterOperators.First(o => o.Operator == TableFilterOperator.Equals);
        vm.FilterValue = "Group A";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Contains("$filter=(contains(new_ParentId/new_name,'Group')) and (new_ParentId/new_name eq 'Group A')", RowReads(handler)[^1].Url);
        Assert.Single(handler.Requests, r => r.Url.Contains("ManyToOneRelationships", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lookup_compared_with_an_id_reads_no_relationships()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_parentid");
        vm.FilterOperator = vm.FilterOperators.First(o => o.Operator == TableFilterOperator.Equals);
        vm.FilterValue = "aaaaaaaa-0000-0000-0000-000000000001";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Contains("$filter=_new_parentid_value eq aaaaaaaa-0000-0000-0000-000000000001", RowReads(handler)[^1].Url);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("ManyToOneRelationships", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lookup_whose_targets_cannot_be_read_asks_for_an_id_and_reads_nothing()
    {
        // First match wins, so the failing relationships route goes ahead of the usual ones.
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "/ManyToOneRelationships", HttpStatusCode.BadRequest, "not here")
            .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_taxcode')/Attributes", TaxCodeColumns)
            .OnJson(HttpMethod.Get, "EntityDefinitions?", Tables)
            .OnJson(HttpMethod.Get, "savedqueries", View)
            .OnJson(HttpMethod.Get, "new_taxcodes?", TaxCodeRows);
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_parentid");
        vm.FilterValue = "Group";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal("The name of the row Parent points at cannot be filtered on here - type the row's id (GUID) instead.", vm.StatusLine);
        Assert.Single(RowReads(handler));
        Assert.False(vm.HasServerFilter);
    }

    [Fact]
    public async Task Filters_add_up_and_each_can_be_removed()
    {
        var handler = Handler();
        var vm = await Opened(handler);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_rate");
        vm.FilterOperator = vm.FilterOperators.First(o => o.Operator == TableFilterOperator.GreaterThan);
        vm.FilterValue = "1";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.FilterValue);

        vm.FilterColumn = vm.Columns.First(c => c.LogicalName == "new_name");
        vm.FilterValue = "a";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Contains("$filter=(new_rate gt 1) and (contains(new_name,'a'))", RowReads(handler)[^1].Url);
        Assert.Equal(["Rate greater than '1'", "Name contains 'a'"], vm.ServerFilters.Select(f => f.Label));
        Assert.Equal("Rate greater than '1' and Name contains 'a'", vm.ServerFilterLabel);
        Assert.Contains("Filter: Rate greater than '1'", vm.ExportNotes());
        Assert.Contains("Filter: Name contains 'a'", vm.ExportNotes());

        // The same condition twice reads nothing more.
        var reads = RowReads(handler).Count;
        vm.FilterValue = "a";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Equal("That filter is already applied.", vm.StatusLine);
        Assert.Equal(reads, RowReads(handler).Count);

        Assert.True(vm.RemoveFilterCommand.CanExecute(vm.ServerFilters[0]));
        Assert.False(vm.RemoveFilterCommand.CanExecute(null));
        await vm.RemoveFilterCommand.ExecuteAsync(vm.ServerFilters[0]);

        Assert.Contains("$filter=contains(new_name,'a')", RowReads(handler)[^1].Url);
        Assert.Equal(["Name contains 'a'"], vm.ServerFilters.Select(f => f.Label));

        await vm.ClearFilterCommand.ExecuteAsync(null);

        Assert.Empty(vm.ServerFilters);
        Assert.DoesNotContain("$filter", RowReads(handler)[^1].Url);
    }
}
