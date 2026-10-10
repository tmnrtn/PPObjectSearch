using PPObjectSearch.Models;
using PPObjectSearch.Services;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

public class ReferenceDataModelsTests
{
    [Fact]
    public void EntitySummary_label_combines_display_and_logical_name()
    {
        Assert.Equal("Thing (new_thing)", Entity().Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EntitySummary_label_falls_back_to_logical_name(string? displayName)
    {
        Assert.Equal("new_thing", Entity(displayName: displayName).Label);
    }

    [Theory]
    [InlineData("new_things", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    public void EntitySummary_is_readable_only_with_an_entity_set_name(string? entitySet, bool expected)
    {
        Assert.Equal(expected, Entity(entitySet: entitySet).IsReadable);
    }

    [Theory]
    [InlineData("LookupType", true)]
    [InlineData("CustomerType", true)]
    [InlineData("OwnerType", true)]
    [InlineData("StringType", false)]
    [InlineData("PicklistType", false)]
    [InlineData("UniqueidentifierType", false)]
    public void EntityColumn_recognises_lookup_types(string type, bool isLookup)
    {
        Assert.Equal(isLookup, Col("new_col", type).IsLookup);
    }

    [Fact]
    public void EntityColumn_selects_a_lookup_through_its_value_shadow_column()
    {
        Assert.Equal("_new_parentid_value", Lookup("new_parentid").SelectName);
        Assert.Equal("new_code", Col("new_code").SelectName);
    }

    [Theory]
    [InlineData("StringType", "String")]
    [InlineData("LookupType", "Lookup")]
    [InlineData("Type", "")]
    [InlineData("Custom", "Custom")]
    public void EntityColumn_type_label_drops_the_type_suffix(string type, string expected)
    {
        Assert.Equal(expected, Col("c", type).TypeLabel);
    }

    [Fact]
    public void EntityColumn_label_prefers_display_name()
    {
        Assert.Equal("Code (new_code)", Col("new_code", displayName: "Code").Label);
        Assert.Equal("new_code", Col("new_code").Label);
    }

    [Theory]
    [InlineData("IntegerType", true, false, false, false)]
    [InlineData("BigIntType", true, false, false, false)]
    [InlineData("DecimalType", true, false, false, false)]
    [InlineData("DoubleType", true, false, false, false)]
    [InlineData("MoneyType", true, false, false, false)]
    [InlineData("DateTimeType", false, true, false, false)]
    [InlineData("BooleanType", false, false, true, false)]
    [InlineData("UniqueidentifierType", false, false, false, true)]
    [InlineData("StringType", false, false, false, false)]
    [InlineData("PicklistType", false, false, false, false)]
    public void EntityColumn_classifies_types(string type, bool numeric, bool dateTime, bool boolean, bool uniqueId)
    {
        var column = Col("c", type);

        Assert.Equal(numeric, column.IsNumeric);
        Assert.Equal(dateTime, column.IsDateTime);
        Assert.Equal(boolean, column.IsBoolean);
        Assert.Equal(uniqueId, column.IsUniqueIdentifier);
    }

    [Fact]
    public void EntityColumn_is_writable_by_default()
    {
        var column = new EntityColumn("c", null, "StringType", false, false);

        Assert.True(column.IsWritable(isCreate: true));
        Assert.True(column.IsWritable(isCreate: false));
    }

    [Fact]
    public void EntityColumn_writability_depends_on_create_or_update()
    {
        var createOnly = Col("c", validForCreate: true, validForUpdate: false);

        Assert.True(createOnly.IsWritable(isCreate: true));
        Assert.False(createOnly.IsWritable(isCreate: false));
    }

    private static readonly string[] PairKeyColumns = ["new_code", "new_region"];
    private static readonly string[] CodeKeyColumns = ["new_code"];

    [Fact]
    public void AlternateKeyInfo_label_lists_its_columns()
    {
        Assert.Equal("Code key (new_code, new_region)",
            new AlternateKeyInfo("new_codekey", "Code key", PairKeyColumns).Label);
        Assert.Equal("new_codekey (new_code)",
            new AlternateKeyInfo("new_codekey", null, CodeKeyColumns).Label);
        Assert.Equal("Code key",
            new AlternateKeyInfo("new_codekey", "Code key", Array.Empty<string>()).Label);
    }

    [Fact]
    public void DataRecord_returns_null_for_columns_it_does_not_hold()
    {
        var record = Row(G(1)).Build();

        Assert.Null(record.Raw("missing"));
        Assert.Null(record.Label("missing"));
        Assert.Null(record.Display("missing"));
    }

    [Fact]
    public void DataRecord_display_shows_label_and_raw_value_together()
    {
        var record = Row(G(1)).With("statuscode", "1", "Active").Build();

        Assert.Equal("Active  (1)", record.Display("statuscode"));
    }

    [Fact]
    public void DataRecord_display_uses_raw_value_when_there_is_no_label()
    {
        var record = Row(G(1)).With("new_code", "ABC").Build();

        Assert.Equal("ABC", record.Display("new_code"));
    }

    [Fact]
    public void DataRecord_display_does_not_repeat_a_label_equal_to_the_raw_value()
    {
        var record = Row(G(1)).With("new_amount", "10", "10").Build();

        Assert.Equal("10", record.Display("new_amount"));
    }

    [Fact]
    public void DataRecord_display_uses_label_when_raw_value_is_empty()
    {
        var record = Row(G(1)).With("new_x", null, "Label only").Build();

        Assert.Equal("Label only", record.Display("new_x"));
    }

    [Fact]
    public void DataRecord_column_lookups_ignore_case()
    {
        var record = Row(G(1)).With("new_Code", "ABC", "abc label").Build();

        Assert.Equal("ABC", record.Raw("NEW_CODE"));
        Assert.Equal("abc label", record.Label("new_code"));
    }

    private static readonly string[] EachColumnOnce = ["new_code", "_new_parentid_value", "new_amount"];

    [Fact]
    public void Compare_plan_selects_key_and_value_columns_once_each()
    {
        var code = Col("new_code");
        var plan = Plan(
            keys: new[] { code },
            values: new[] { Col("NEW_CODE"), Lookup("new_parentid"), Col("new_amount", "MoneyType") });

        Assert.Equal(EachColumnOnce, plan.SelectNames.ToArray());
        Assert.Equal("Thing (new_thing)", plan.EntityLabel);
    }
}
