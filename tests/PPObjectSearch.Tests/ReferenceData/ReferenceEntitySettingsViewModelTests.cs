using System.Net;
using System.Net.Http;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

/// <summary>The per-table settings dialog: what identifies a row, which rows are read, which columns count.</summary>
public class ReferenceEntitySettingsViewModelTests
{
    private const string Attributes = "EntityDefinitions(LogicalName='new_thing')/Attributes";
    private const string Keys = "EntityDefinitions(LogicalName='new_thing')/Keys";

    private const string ColumnsJson = """
        {"value":[
          {"LogicalName":"new_thingid","AttributeTypeName":{"Value":"UniqueidentifierType"},"IsPrimaryId":true},
          {"LogicalName":"new_code","DisplayName":{"UserLocalizedLabel":{"Label":"Code"}},"AttributeTypeName":{"Value":"StringType"}},
          {"LogicalName":"new_name","DisplayName":{"UserLocalizedLabel":{"Label":"Name"}},"AttributeTypeName":{"Value":"StringType"},"IsPrimaryName":true},
          {"LogicalName":"new_region","AttributeTypeName":{"Value":"PicklistType"}},
          {"LogicalName":"createdon","AttributeTypeName":{"Value":"DateTimeType"}}
        ]}
        """;

    private const string KeysJson = """
        {"value":[
          {"LogicalName":"new_codekey","DisplayName":{"UserLocalizedLabel":{"Label":"Code key"}},"KeyAttributes":["new_code"]},
          {"LogicalName":"new_pairkey","KeyAttributes":["new_code","new_region"]}
        ]}
        """;

    private static FakeHttpHandler Metadata(string keys = KeysJson) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, Attributes, ColumnsJson)
        .OnJson(HttpMethod.Get, Keys, keys);

    private static async Task<ReferenceEntitySettingsViewModel> Loaded(ReferenceEntityConfig config, FakeHttpHandler? handler = null)
    {
        var vm = new ReferenceEntitySettingsViewModel(config, Entity(), Fakes.Dataverse(handler ?? Metadata()));
        await vm.LoadAsync();
        return vm;
    }

    private static ColumnChoice Choice(ReferenceEntitySettingsViewModel vm, string name) =>
        vm.Columns.Single(c => c.LogicalName == name);

    private static string[] Compared(ReferenceEntitySettingsViewModel vm) =>
        vm.Columns.Where(c => c.IsCompared).Select(c => c.LogicalName).ToArray();

    [Fact]
    public async Task Loading_lists_the_columns_and_every_way_a_row_can_be_identified()
    {
        var vm = new ReferenceEntitySettingsViewModel(new ReferenceEntityConfig { LogicalName = Table }, Entity(), Fakes.Dataverse(Metadata()));

        Assert.True(vm.IsBusy);
        Assert.Equal("Thing (new_thing) - comparison settings", vm.Title);
        Assert.Equal(string.Empty, vm.CompareSummary);
        Assert.False(vm.CompareAllCommand.CanExecute(null));

        await vm.LoadAsync();

        Assert.False(vm.IsBusy);
        Assert.Equal("5 comparable columns, 2 alternate key(s).", vm.Status);
        Assert.Equal(
            new[] { "Primary key - new_thingid", "Alternate key - Code key (new_code)", "Alternate key - new_pairkey (new_code, new_region)", "Columns I pick" },
            vm.KeyOptions.Select(k => k.ToString()));
        Assert.Equal("Code key", vm.KeyOptions[1].Title);
        Assert.Equal("Alternate key", vm.KeyOptions[2].Title);
        Assert.Equal("Columns: new_code, new_region", vm.KeyOptions[2].Hint);

        // The primary id and the housekeeping columns start excluded.
        Assert.Equal(new[] { "new_code", "new_name", "new_region" }, Compared(vm));
        Assert.Equal("3 of 5 columns compared", vm.CompareSummary);
        Assert.Same(vm.KeyOptions[0], vm.SelectedKey);
        Assert.False(vm.IsCustomKey);
        Assert.Equal(new[] { "new_thingid" }, vm.Columns.Where(c => c.IsKey).Select(c => c.LogicalName));
        Assert.True(vm.CompareAllCommand.CanExecute(null));

        var code = Choice(vm, "new_code");
        Assert.Equal("Code", code.DisplayName);
        Assert.Equal("String", code.TypeLabel);
        Assert.Equal(string.Empty, Choice(vm, "new_region").DisplayName);
    }

    [Fact]
    public async Task A_table_without_alternate_keys_says_so()
    {
        var vm = await Loaded(new ReferenceEntityConfig { LogicalName = Table }, Metadata("""{"value":[]}"""));

        Assert.Equal("5 comparable columns. This table defines no alternate keys.", vm.Status);
        Assert.Equal(2, vm.KeyOptions.Count);
    }

    [Fact]
    public async Task A_saved_alternate_key_is_selected_and_its_columns_ticked()
    {
        var vm = await Loaded(new ReferenceEntityConfig
        {
            LogicalName = Table,
            KeySource = RecordKeySource.AlternateKey,
            AlternateKeyName = "NEW_PAIRKEY"
        });

        Assert.Equal("new_pairkey", vm.SelectedKey!.AlternateKeyName);
        Assert.Equal(new[] { "new_code", "new_region" }, vm.Columns.Where(c => c.IsKey).Select(c => c.LogicalName));
    }

    [Fact]
    public async Task A_saved_alternate_key_that_is_gone_falls_back_to_the_primary_key()
    {
        var vm = await Loaded(new ReferenceEntityConfig
        {
            LogicalName = Table,
            KeySource = RecordKeySource.AlternateKey,
            AlternateKeyName = "new_dropped"
        });

        Assert.Equal(RecordKeySource.PrimaryId, vm.SelectedKey!.Source);
        Assert.Equal(new[] { "new_thingid" }, vm.Columns.Where(c => c.IsKey).Select(c => c.LogicalName));
    }

    [Fact]
    public async Task Hand_picked_key_columns_are_ticked_and_switching_to_them_starts_from_the_key_in_force()
    {
        var vm = await Loaded(new ReferenceEntityConfig
        {
            LogicalName = Table,
            KeySource = RecordKeySource.Columns,
            KeyColumns = new List<string> { "new_code", "new_region" }
        });

        Assert.True(vm.IsCustomKey);
        Assert.Equal(new[] { "new_code", "new_region" }, vm.Columns.Where(c => c.IsKey).Select(c => c.LogicalName));

        vm.SelectedKey = vm.KeyOptions[1];
        Assert.False(vm.IsCustomKey);
        Assert.Equal(new[] { "new_code" }, vm.Columns.Where(c => c.IsKey).Select(c => c.LogicalName));

        var fresh = await Loaded(new ReferenceEntityConfig { LogicalName = Table });
        fresh.SelectedKey = fresh.KeyOptions[^1];
        Assert.True(fresh.IsCustomKey);
        Assert.Empty(fresh.Columns.Where(c => c.IsKey));
    }

    [Fact]
    public async Task Saved_column_choices_are_restored_and_a_column_added_since_starts_unticked()
    {
        var vm = await Loaded(new ReferenceEntityConfig
        {
            LogicalName = Table,
            ExcludedColumns = new List<string> { "new_name" },
            ComparedColumns = new List<string> { "new_code", "createdon" }
        });

        Assert.Equal(new[] { "createdon", "new_code" }, Compared(vm));

        vm.ResetDefaultsCommand.Execute(null);

        Assert.Equal(new[] { "new_code", "new_name", "new_region" }, Compared(vm));
    }

    [Fact]
    public async Task Ticking_all_or_none_applies_to_the_columns_the_search_shows()
    {
        var vm = await Loaded(new ReferenceEntityConfig { LogicalName = Table });
        var summaries = new List<string?>();
        vm.PropertyChanged += (_, e) => summaries.Add(e.PropertyName);

        vm.ColumnSearch = "NAME";
        Assert.Equal(new[] { "new_name" }, vm.ColumnsView.Cast<ColumnChoice>().Select(c => c.LogicalName));

        vm.CompareNoneCommand.Execute(null);
        Assert.Equal(new[] { "new_code", "new_region" }, Compared(vm));
        Assert.Contains(nameof(vm.CompareSummary), summaries);

        vm.ColumnSearch = "code";
        Assert.Equal(new[] { "new_code" }, vm.ColumnsView.Cast<ColumnChoice>().Select(c => c.LogicalName));

        vm.ColumnSearch = string.Empty;
        vm.CompareAllCommand.Execute(null);
        Assert.Equal(5, Compared(vm).Length);
    }

    [Fact]
    public async Task Metadata_that_cannot_be_read_is_reported()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, Attributes, HttpStatusCode.Forbidden, "no metadata");

        var vm = await Loaded(new ReferenceEntityConfig { LogicalName = Table }, handler);

        Assert.StartsWith("Could not read the table metadata - ", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Columns);
        Assert.False(vm.ResetDefaultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelling_the_load_is_not_swallowed()
    {
        var vm = new ReferenceEntitySettingsViewModel(new ReferenceEntityConfig { LogicalName = Table }, Entity(), Fakes.Dataverse(Metadata()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.LoadAsync(new CancellationToken(canceled: true)));

        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Applying_needs_a_key_and_hand_picked_keys_need_a_column()
    {
        var vm = await Loaded(new ReferenceEntityConfig { LogicalName = Table });

        vm.SelectedKey = null;
        Assert.False(vm.Apply(out var error));
        Assert.Equal("Choose what identifies a row.", error);

        vm.SelectedKey = vm.KeyOptions[^1];
        Assert.False(vm.Apply(out error));
        Assert.Equal("Tick at least one column to use as the key.", error);
    }

    [Fact]
    public async Task Applying_hand_picked_columns_writes_them_and_the_column_choices_back()
    {
        var config = new ReferenceEntityConfig { LogicalName = Table, AlternateKeyName = "old", Filter = "old" };
        var vm = await Loaded(config);
        vm.SelectedKey = vm.KeyOptions[^1];
        Choice(vm, "new_code").IsKey = true;
        Choice(vm, "new_region").IsCompared = false;
        vm.Filter = "  statecode eq 0  ";

        Assert.True(vm.Apply(out var error));

        Assert.Null(error);
        Assert.Equal(RecordKeySource.Columns, config.KeySource);
        Assert.Equal(new[] { "new_code" }, config.KeyColumns);
        Assert.Null(config.AlternateKeyName);
        Assert.Equal("statecode eq 0", config.Filter);
        Assert.Equal(new[] { "createdon", "new_region", "new_thingid" }, config.ExcludedColumns!.OrderBy(c => c));
        Assert.Equal(new[] { "new_code", "new_name" }, config.ComparedColumns);
    }

    [Fact]
    public async Task Applying_an_alternate_key_keeps_its_columns_and_the_primary_key_clears_them()
    {
        var config = new ReferenceEntityConfig { LogicalName = Table, Filter = "statecode eq 0" };
        var vm = await Loaded(config);
        Assert.Equal("statecode eq 0", vm.Filter);

        vm.SelectedKey = vm.KeyOptions[2];
        vm.Filter = " ";
        Assert.True(vm.Apply(out _));

        Assert.Equal(RecordKeySource.AlternateKey, config.KeySource);
        Assert.Equal("new_pairkey", config.AlternateKeyName);
        Assert.Equal(new[] { "new_code", "new_region" }, config.KeyColumns);
        Assert.Null(config.Filter);

        vm.SelectedKey = vm.KeyOptions[0];
        Assert.True(vm.Apply(out _));

        Assert.Equal(RecordKeySource.PrimaryId, config.KeySource);
        Assert.Null(config.AlternateKeyName);
        Assert.Null(config.KeyColumns);
    }
}
