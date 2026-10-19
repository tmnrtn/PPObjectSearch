using System.Net.Http;
using PPObjectSearch.Core;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>Copying from the table data grid: a cell as shown or raw, a column of the rows shown, a row's id.</summary>
[Collection(nameof(ClipboardText))]
public sealed class TableDataCopyTests : IDisposable
{
    private readonly Action<string> _set = ClipboardText.Set;
    private readonly List<string> _copied = new();
    private string? _busy;

    public TableDataCopyTests()
    {
        ClipboardText.Set = text =>
        {
            if (_busy is not null) throw new InvalidOperationException(_busy);
            _copied.Add(text);
        };
    }

    public void Dispose() => ClipboardText.Set = _set;

    private static async Task<TableDataViewModel> Opened()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_taxcode')/Attributes", """
                {"value":[
                  {"LogicalName":"new_name","DisplayName":{"UserLocalizedLabel":{"Label":"Name"}},"AttributeTypeName":{"Value":"StringType"}},
                  {"LogicalName":"new_parentid","DisplayName":{"UserLocalizedLabel":{"Label":"Parent"}},"AttributeTypeName":{"Value":"LookupType"}},
                  {"LogicalName":"new_region","DisplayName":{"UserLocalizedLabel":{"Label":"Region"}},"AttributeTypeName":{"Value":"PicklistType"}}
                ]}
                """)
            .OnJson(HttpMethod.Get, "EntityDefinitions?", """
                {"value":[{"LogicalName":"new_taxcode","DisplayName":{"UserLocalizedLabel":{"Label":"Tax code"}},"EntitySetName":"new_taxcodes",
                  "PrimaryIdAttribute":"new_taxcodeid","PrimaryNameAttribute":"new_name"}]}
                """)
            .OnJson(HttpMethod.Get, "savedqueries", """
                {"value":[{"isdefault":true,"layoutxml":"<grid><row><cell name=\"new_name\" /><cell name=\"new_parentid\" /><cell name=\"new_region\" /></row></grid>"}]}
                """)
            .OnJson(HttpMethod.Get, "new_taxcodes?", """
                {"value":[
                  {"new_taxcodeid":"00000000-0000-0000-0000-000000000001","new_name":"Reduced",
                   "_new_parentid_value":"aaaaaaaa-0000-0000-0000-000000000001","_new_parentid_value@OData.Community.Display.V1.FormattedValue":"Group A",
                   "new_region":100000001,"new_region@OData.Community.Display.V1.FormattedValue":"Europe"},
                  {"new_taxcodeid":"00000000-0000-0000-0000-000000000002","new_name":"Standard",
                   "new_region":100000000,"new_region@OData.Community.Display.V1.FormattedValue":"Americas"}
                ]}
                """);

        var vm = new TableDataViewModel(TestSessions.Connected(handler), "new_taxcode");
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task Nothing_is_copied_until_a_cell_is_current()
    {
        var vm = await Opened();

        Assert.False(vm.CopyValueCommand.CanExecute(null));
        Assert.False(vm.CopyRawValueCommand.CanExecute(null));
        Assert.False(vm.CopyColumnCommand.CanExecute(null));
        Assert.False(vm.CopyRowIdCommand.CanExecute(null));

        vm.SetCurrentCell(vm.Rows[0], 7);

        Assert.False(vm.CopyValueCommand.CanExecute(null));
        Assert.True(vm.CopyRowIdCommand.CanExecute(null));
        Assert.Empty(_copied);
    }

    [Fact]
    public async Task A_lookup_cell_copies_its_name_or_its_id()
    {
        var vm = await Opened();
        vm.SetCurrentCell(vm.Rows[0], 1);

        vm.CopyValueCommand.Execute(null);
        vm.CopyRawValueCommand.Execute(null);

        Assert.Equal(["Group A", "aaaaaaaa-0000-0000-0000-000000000001"], _copied);
        Assert.Equal("Copied Parent (raw) of Reduced.", vm.StatusLine);
    }

    [Fact]
    public async Task A_choice_cell_copies_its_label_or_its_number()
    {
        var vm = await Opened();
        vm.SetCurrentCell(vm.Rows[1], 2);

        vm.CopyValueCommand.Execute(null);
        vm.CopyRawValueCommand.Execute(null);

        Assert.Equal(["Americas", "100000000"], _copied);
    }

    [Fact]
    public async Task An_empty_cell_copies_as_nothing()
    {
        var vm = await Opened();
        vm.SetCurrentCell(vm.Rows[1], 1);

        vm.CopyValueCommand.Execute(null);

        Assert.Equal([string.Empty], _copied);
        Assert.Equal("Copied Parent of Standard.", vm.StatusLine);
    }

    [Fact]
    public async Task A_column_copies_one_line_per_row_shown()
    {
        var vm = await Opened();
        vm.SetCurrentCell(vm.Rows[0], 2);

        vm.CopyColumnCommand.Execute(null);
        Assert.Equal("Europe" + Environment.NewLine + "Americas", _copied[^1]);
        Assert.Equal("Copied Region of 2 row(s).", vm.StatusLine);

        vm.QuickFilter = "standard";
        vm.CopyColumnCommand.Execute(null);
        Assert.Equal("Americas", _copied[^1]);
    }

    [Fact]
    public async Task A_row_copies_its_id()
    {
        var vm = await Opened();
        vm.SetCurrentCell(vm.Rows[1], 0);

        vm.CopyRowIdCommand.Execute(null);

        Assert.Equal(["00000000-0000-0000-0000-000000000002"], _copied);
        Assert.Equal("Copied the id of Standard.", vm.StatusLine);
    }

    [Fact]
    public async Task Reading_the_rows_again_forgets_the_current_cell()
    {
        var vm = await Opened();
        vm.SetCurrentCell(vm.Rows[0], 0);

        vm.ShowRawValues = true;

        Assert.Null(vm.CurrentRow);
        Assert.Equal(-1, vm.CurrentColumn);
        Assert.False(vm.CopyValueCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_clipboard_in_use_says_the_copy_failed()
    {
        var vm = await Opened();
        vm.SetCurrentCell(vm.Rows[0], 0);
        _busy = "clipboard is busy";

        vm.CopyValueCommand.Execute(null);

        Assert.Equal("Could not copy Name of Reduced - clipboard is busy", vm.StatusLine);
        Assert.Empty(_copied);
    }
}
