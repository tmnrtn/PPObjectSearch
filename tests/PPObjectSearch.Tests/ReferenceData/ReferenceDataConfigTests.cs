using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

public class ReferenceDataConfigTests
{
    private static ReferenceEntityConfig FullEntity() => new()
    {
        LogicalName = "new_thing",
        DisplayName = "Thing",
        KeySource = RecordKeySource.Columns,
        AlternateKeyName = "new_key",
        KeyColumns = new List<string> { "new_code" },
        Filter = "statecode eq 0",
        ExcludedColumns = new List<string> { "createdon" },
        IsEnabled = false
    };

    [Fact]
    public void Entity_config_defaults_to_primary_id_enabled_and_unconfigured_exclusions()
    {
        var config = new ReferenceEntityConfig();

        Assert.Equal(RecordKeySource.PrimaryId, config.KeySource);
        Assert.True(config.IsEnabled);
        Assert.Null(config.ExcludedColumns);
        Assert.Null(config.KeyColumns);
    }

    [Fact]
    public void Entity_config_clone_copies_every_property()
    {
        var clone = FullEntity().Clone();

        Assert.Equal("new_thing", clone.LogicalName);
        Assert.Equal("Thing", clone.DisplayName);
        Assert.Equal(RecordKeySource.Columns, clone.KeySource);
        Assert.Equal("new_key", clone.AlternateKeyName);
        Assert.Equal(new[] { "new_code" }, clone.KeyColumns);
        Assert.Equal("statecode eq 0", clone.Filter);
        Assert.Equal(new[] { "createdon" }, clone.ExcludedColumns);
        Assert.False(clone.IsEnabled);
    }

    [Fact]
    public void Entity_config_clone_does_not_share_lists_with_the_original()
    {
        var original = FullEntity();
        var clone = original.Clone();

        Assert.NotSame(original.KeyColumns, clone.KeyColumns);
        Assert.NotSame(original.ExcludedColumns, clone.ExcludedColumns);

        clone.KeyColumns!.Add("new_region");
        clone.ExcludedColumns!.Clear();
        clone.LogicalName = "changed";
        clone.IsEnabled = true;

        Assert.Equal(new[] { "new_code" }, original.KeyColumns);
        Assert.Equal(new[] { "createdon" }, original.ExcludedColumns);
        Assert.Equal("new_thing", original.LogicalName);
        Assert.False(original.IsEnabled);
    }

    [Fact]
    public void Entity_config_clone_keeps_null_lists_null()
    {
        var clone = new ReferenceEntityConfig { LogicalName = "x" }.Clone();

        // Null exclusions mean "take the defaults", which an empty list would silently change.
        Assert.Null(clone.ExcludedColumns);
        Assert.Null(clone.KeyColumns);
    }

    [Fact]
    public void Entity_config_clone_keeps_an_empty_exclusion_list_empty()
    {
        var clone = new ReferenceEntityConfig { ExcludedColumns = new List<string>() }.Clone();

        Assert.NotNull(clone.ExcludedColumns);
        Assert.Empty(clone.ExcludedColumns!);
    }

    [Fact]
    public void Config_defaults_to_matching_lookups_by_name_and_default_row_cap()
    {
        var config = new ReferenceDataConfig();

        Assert.True(config.MatchLookupsByName);
        Assert.Equal(DataverseClient.DefaultMaxRecordsPerEntity, config.MaxRowsPerEntity);
        Assert.Equal(5000, config.MaxRowsPerEntity);
    }

    [Fact]
    public void Config_clone_deep_copies_its_entities()
    {
        var original = new ReferenceDataConfig
        {
            Name = "Currencies",
            Entities = new List<ReferenceEntityConfig> { FullEntity() },
            MatchLookupsByName = false,
            MaxRowsPerEntity = 250
        };

        var clone = original.Clone();

        Assert.Equal("Currencies", clone.Name);
        Assert.False(clone.MatchLookupsByName);
        Assert.Equal(250, clone.MaxRowsPerEntity);
        Assert.Single(clone.Entities!);
        Assert.NotSame(original.Entities, clone.Entities);
        Assert.NotSame(original.Entities![0], clone.Entities![0]);

        clone.Name = "Other";
        clone.Entities.Add(new ReferenceEntityConfig { LogicalName = "new_other" });
        clone.Entities[0].Filter = "changed";
        clone.Entities[0].KeyColumns!.Add("new_region");
        clone.MaxRowsPerEntity = 1;

        Assert.Equal("Currencies", original.Name);
        Assert.Single(original.Entities);
        Assert.Equal("statecode eq 0", original.Entities[0].Filter);
        Assert.Equal(new[] { "new_code" }, original.Entities[0].KeyColumns);
        Assert.Equal(250, original.MaxRowsPerEntity);
    }

    [Fact]
    public void Config_clone_keeps_null_entities_null()
    {
        Assert.Null(new ReferenceDataConfig().Clone().Entities);
    }

    [Theory]
    [InlineData("createdon")]
    [InlineData("modifiedby")]
    [InlineData("ownerid")]
    [InlineData("versionnumber")]
    [InlineData("exchangerate")]
    [InlineData("OverriddenCreatedOn")]
    [InlineData("ModifiedOn")]
    public void System_columns_treat_housekeeping_columns_as_noise(string name)
    {
        Assert.True(SystemColumns.IsNoise(name));
    }

    [Theory]
    [InlineData("new_name")]
    [InlineData("new_code")]
    [InlineData("statecode")]
    [InlineData("statuscode")]
    [InlineData("new_thingid")]
    public void System_columns_do_not_treat_data_columns_as_noise(string name)
    {
        Assert.False(SystemColumns.IsNoise(name));
    }

    [Fact]
    public void Default_exclusions_are_the_primary_id_and_housekeeping_columns()
    {
        var columns = new[]
        {
            Col("new_thingid", "UniqueidentifierType", primaryId: true),
            Col("new_name", primaryName: true),
            Col("new_code"),
            Col("createdon", "DateTimeType"),
            Col("ModifiedBy", "LookupType"),
            Col("statecode", "StateType")
        };

        Assert.Equal(new[] { "new_thingid", "createdon", "ModifiedBy" }, SystemColumns.DefaultExclusions(columns));
    }

    [Fact]
    public void Default_exclusions_of_no_columns_is_empty()
    {
        Assert.Empty(SystemColumns.DefaultExclusions(Array.Empty<PPObjectSearch.Models.EntityColumn>()));
    }
}
