using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.CoreAndModels;

public class DefinitionDiffTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "   ")]
    [InlineData("not json", "{broken")]
    [InlineData("[1,2]", "\"just a string\"")]
    public void Compare_returns_null_when_neither_side_is_a_json_object(string? before, string? after)
    {
        Assert.Null(DefinitionDiff.Compare(before, after));
    }

    [Fact]
    public void Compare_identical_definitions_returns_an_empty_list()
    {
        var changes = DefinitionDiff.Compare("{\"a\":1,\"b\":\"x\"}", "{\"b\":\"x\",\"a\":1}");

        Assert.NotNull(changes);
        Assert.Empty(changes);
    }

    [Fact]
    public void Compare_with_no_before_reports_every_property_added()
    {
        var changes = DefinitionDiff.Compare(null, "{\"a\":1,\"b\":\"x\"}")!;

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(DefinitionChangeKind.Added, c.Kind));
        Assert.Equal(("a", (string?)null, (string?)"1"), (changes[0].PropertyName, changes[0].PreviousValue, changes[0].CurrentValue));
        Assert.Equal(("b", (string?)null, (string?)"x"), (changes[1].PropertyName, changes[1].PreviousValue, changes[1].CurrentValue));
    }

    [Fact]
    public void Compare_with_unreadable_after_reports_every_property_removed()
    {
        var changes = DefinitionDiff.Compare("{\"a\":true}", "garbage")!;

        var change = Assert.Single(changes);
        Assert.Equal(DefinitionChangeKind.Removed, change.Kind);
        Assert.Equal("a", change.PropertyName);
        Assert.Equal("true", change.PreviousValue);
        Assert.Null(change.CurrentValue);
    }

    [Fact]
    public void Compare_reports_added_modified_and_removed_properties()
    {
        var changes = DefinitionDiff.Compare(
            "{\"keep\":1,\"change\":\"old\",\"gone\":2}",
            "{\"keep\":1,\"change\":\"new\",\"fresh\":3}")!;

        Assert.Equal(3, changes.Count);

        var modified = Assert.Single(changes, c => c.PropertyName == "change");
        Assert.Equal(DefinitionChangeKind.Modified, modified.Kind);
        Assert.Equal("old", modified.PreviousValue);
        Assert.Equal("new", modified.CurrentValue);

        Assert.Equal(DefinitionChangeKind.Added, Assert.Single(changes, c => c.PropertyName == "fresh").Kind);
        Assert.Equal(DefinitionChangeKind.Removed, Assert.Single(changes, c => c.PropertyName == "gone").Kind);
        Assert.DoesNotContain(changes, c => c.PropertyName == "keep");
    }

    [Fact]
    public void Compare_flattens_nested_objects_into_dotted_paths()
    {
        var change = Assert.Single(DefinitionDiff.Compare("{\"o\":{\"p\":1,\"q\":2}}", "{\"o\":{\"p\":9,\"q\":2}}")!);

        Assert.Equal("o.p", change.PropertyName);
        Assert.Equal("1", change.PreviousValue);
        Assert.Equal("9", change.CurrentValue);
    }

    [Fact]
    public void Compare_stops_flattening_below_three_levels_and_diffs_the_subtree_whole()
    {
        var change = Assert.Single(DefinitionDiff.Compare(
            "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":1}}}}}",
            "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":2}}}}}")!);

        Assert.Equal("a.b.c.d", change.PropertyName);
        Assert.Equal("{\"e\":1}", change.PreviousValue);
        Assert.Equal("{\"e\":2}", change.CurrentValue);
    }

    [Theory]
    [InlineData("null", "(null)")]
    [InlineData("\"text\"", "text")]
    [InlineData("42", "42")]
    [InlineData("false", "false")]
    [InlineData("[1,2]", "[1,2]")]
    public void Compare_renders_values_by_json_kind(string json, string expected)
    {
        var change = Assert.Single(DefinitionDiff.Compare("{}", "{\"v\":" + json + "}")!);
        Assert.Equal(expected, change.CurrentValue);
    }

    [Fact]
    public void Compare_treats_null_to_value_as_modified()
    {
        var change = Assert.Single(DefinitionDiff.Compare("{\"v\":null}", "{\"v\":1}")!);

        Assert.Equal(DefinitionChangeKind.Modified, change.Kind);
        Assert.Equal("(null)", change.PreviousValue);
    }

    [Fact]
    public void Compare_is_case_sensitive_on_property_names()
    {
        var changes = DefinitionDiff.Compare("{\"Name\":1}", "{\"name\":1}")!;

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.PropertyName == "Name" && c.Kind == DefinitionChangeKind.Removed);
        Assert.Contains(changes, c => c.PropertyName == "name" && c.Kind == DefinitionChangeKind.Added);
    }

    [Fact]
    public void Compare_sorts_changes_by_property_name_ignoring_case()
    {
        var changes = DefinitionDiff.Compare("{}", "{\"b\":1,\"C\":1,\"a\":1}")!;

        Assert.Equal(new[] { "a", "b", "C" }, changes.Select(c => c.PropertyName));
    }

    [Fact]
    public void Compare_puts_prioritized_properties_first_matching_case_insensitively()
    {
        var changes = DefinitionDiff.Compare("{}", "{\"b\":1,\"C\":1,\"a\":1,\"d\":1}", new[] { "c", "D" })!;

        Assert.Equal(new[] { "C", "d", "a", "b" }, changes.Select(c => c.PropertyName));
    }
}
