using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

/// <summary>How one configured table describes itself in the reference data window's table list.</summary>
public class ReferenceEntityViewModelTests
{
    [Fact]
    public void The_label_prefers_the_table_read_then_the_saved_display_name_then_the_logical_name()
    {
        Assert.Equal("Thing (new_thing)", new ReferenceEntityViewModel(new ReferenceEntityConfig { LogicalName = Table }, Entity()).Label);
        Assert.Equal("Saved (new_thing)", new ReferenceEntityViewModel(new ReferenceEntityConfig { LogicalName = Table, DisplayName = "Saved" }).Label);
        Assert.Equal("new_thing", new ReferenceEntityViewModel(new ReferenceEntityConfig { LogicalName = Table, DisplayName = " " }).Label);
        Assert.Equal("(unnamed)", new ReferenceEntityViewModel(new ReferenceEntityConfig()).Label);
    }

    [Fact]
    public void The_key_is_described_by_where_it_comes_from()
    {
        static string Key(Action<ReferenceEntityConfig> configure)
        {
            var config = new ReferenceEntityConfig { LogicalName = Table };
            configure(config);
            return new ReferenceEntityViewModel(config).KeyDescription;
        }

        Assert.Equal("Key: primary id", Key(_ => { }));
        Assert.Equal("Key: new_codekey", Key(c => { c.KeySource = RecordKeySource.AlternateKey; c.AlternateKeyName = "new_codekey"; }));
        Assert.Equal("Key: alternate key", Key(c => c.KeySource = RecordKeySource.AlternateKey));
        Assert.Equal("Key: new_code + new_region", Key(c => { c.KeySource = RecordKeySource.Columns; c.KeyColumns = new List<string> { "new_code", "new_region" }; }));
        Assert.Equal("Key: ", Key(c => c.KeySource = RecordKeySource.Columns));
    }

    [Fact]
    public void The_detail_line_lists_the_filter_and_how_columns_were_chosen()
    {
        var config = new ReferenceEntityConfig { LogicalName = Table };
        var entity = new ReferenceEntityViewModel(config);

        Assert.Equal("Key: primary id · default columns", entity.Detail);

        config.Filter = "statecode eq 0";
        config.ExcludedColumns = new List<string> { "new_notes", "new_url" };
        Assert.Equal("Key: primary id · filter: statecode eq 0 · 2 column(s) excluded", entity.Detail);

        config.ExcludedColumns = new List<string>();
        Assert.Equal("Key: primary id · filter: statecode eq 0", entity.Detail);
    }

    private static readonly string[] SwitchedOffChanges = ["IsEnabled", "ResultLabel", "Result"];

    [Fact]
    public void Switching_a_table_off_is_reported_once_and_shows_as_off()
    {
        var entity = new ReferenceEntityViewModel(new ReferenceEntityConfig { LogicalName = Table }) { DifferenceCount = 8 };
        var changed = new List<string?>();
        entity.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("8 diff", entity.ResultLabel);

        entity.IsEnabled = true;
        Assert.Empty(changed);

        entity.IsEnabled = false;
        Assert.False(entity.Config.IsEnabled);
        Assert.Equal("Off", entity.Result);
        Assert.Equal(SwitchedOffChanges, changed);

        changed.Clear();
        entity.DifferenceCount = 8;
        Assert.Empty(changed);
    }

    private static readonly string[] DescriptionChanges = ["Label", "KeyDescription", "Detail"];

    [Fact]
    public void Refresh_announces_the_descriptions_that_follow_the_configuration()
    {
        var entity = new ReferenceEntityViewModel(new ReferenceEntityConfig { LogicalName = Table });
        var changed = new List<string?>();
        entity.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        entity.Refresh();

        Assert.Equal(DescriptionChanges, changed);
    }
}
