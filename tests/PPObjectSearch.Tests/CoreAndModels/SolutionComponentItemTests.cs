using System.ComponentModel;
using PPObjectSearch.Models;

namespace PPObjectSearch.Tests.CoreAndModels;

public class SolutionComponentItemTests
{
    private static SolutionInfo Solution(string uniqueName = "MySolution", string friendlyName = "My Solution", bool managed = false)
        => new() { SolutionId = Guid.NewGuid(), UniqueName = uniqueName, FriendlyName = friendlyName, IsManaged = managed };

    private static SolutionComponentItem Item(string name = "account", string? displayName = null, string? schemaName = null)
        => new() { Name = name, DisplayName = displayName, SchemaName = schemaName, ComponentTypeName = "Table" };

    /// <summary>
    /// Mirrors how EnvironmentSessionViewModel.ApplyFilter/FilterItem use the index: the search
    /// text is split on spaces, lower-cased, and every term must be an ordinal substring.
    /// </summary>
    private static bool Matches(SolutionComponentItem item, string searchText) =>
        searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .All(t => item.SearchIndex.Contains(t, StringComparison.Ordinal));

    [Theory]
    [InlineData("Default", true)]
    [InlineData("default", true)]
    [InlineData("DEFAULT", true)]
    [InlineData("DefaultSolution", false)]
    [InlineData("Active", false)]
    public void SolutionInfo_IsDefaultSolution_matches_the_default_unique_name_ignoring_case(string uniqueName, bool expected)
    {
        Assert.Equal(expected, Solution(uniqueName).IsDefaultSolution);
    }

    [Theory]
    [InlineData(false, "My Solution")]
    [InlineData(true, "My Solution  (managed)")]
    public void SolutionInfo_DisplayLabel_marks_managed_solutions(bool managed, string expected)
    {
        var solution = Solution(managed: managed);

        Assert.Equal(expected, solution.DisplayLabel);
        Assert.Equal(expected, solution.ToString());
    }

    [Theory]
    [InlineData("Account", "Account")]
    [InlineData(null, "account")]
    [InlineData("", "account")]
    [InlineData("   ", "account")]
    public void PrimaryLabel_prefers_display_name_over_name(string? displayName, string expected)
    {
        Assert.Equal(expected, Item(displayName: displayName).PrimaryLabel);
    }

    [Theory]
    [InlineData("Account", "Account", "")]          // schema name equals the label
    [InlineData("Account", "account", "account")]   // compared ordinally, so case differs
    [InlineData("Account", null, "account")]        // falls back to name
    [InlineData(null, null, "")]                    // name is already the label
    [InlineData(null, "Account", "Account")]
    [InlineData("Account", "  ", "account")]
    public void SecondaryLabel_shows_schema_or_name_unless_it_repeats_the_primary_label(
        string? displayName, string? schemaName, string expected)
    {
        Assert.Equal(expected, Item(displayName: displayName, schemaName: schemaName).SecondaryLabel);
    }

    [Theory]
    [InlineData(true, "Managed")]
    [InlineData(false, "Unmanaged")]
    public void ManagedLabel_reflects_IsManaged(bool managed, string expected)
    {
        var item = new SolutionComponentItem { Name = "x", ComponentTypeName = "Table", IsManaged = managed };
        Assert.Equal(expected, item.ManagedLabel);
    }

    [Theory]
    [InlineData(null, "Not checked")]
    [InlineData(true, "Unmanaged layer")]
    [InlineData(false, "No unmanaged layer")]
    public void UnmanagedLayerLabel_reflects_HasUnmanagedLayer(bool? hasLayer, string expected)
    {
        var item = Item();
        item.HasUnmanagedLayer = hasLayer;
        Assert.Equal(expected, item.UnmanagedLayerLabel);
    }

    private static readonly string[] UnmanagedLayerProperties = ["HasUnmanagedLayer", "UnmanagedLayerLabel"];

    [Fact]
    public void Setting_HasUnmanagedLayer_raises_change_for_it_and_its_label()
    {
        var item = Item();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.HasUnmanagedLayer = true;

        Assert.Equal(UnmanagedLayerProperties, raised);
    }

    [Fact]
    public void Setting_HasUnmanagedLayer_to_the_same_value_raises_nothing()
    {
        var item = Item();
        item.HasUnmanagedLayer = false;
        var raised = 0;
        ((INotifyPropertyChanged)item).PropertyChanged += (_, _) => raised++;

        item.HasUnmanagedLayer = false;

        Assert.Equal(0, raised);
    }

    [Fact]
    public void SearchIndex_is_empty_until_built()
    {
        Assert.Equal(string.Empty, Item(displayName: "Account").SearchIndex);
    }

    [Fact]
    public void BuildSearchIndex_joins_the_searchable_fields_lower_cased_skipping_blanks()
    {
        var id = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
        var item = new SolutionComponentItem
        {
            Name = "new_Thing",
            DisplayName = "My Thing",
            SchemaName = " ",
            ComponentTypeName = "Process",
            SubType = "Cloud Flow",
            ComponentLogicalName = "workflow",
            PrimaryEntityName = null,
            Owner = "Tom Norton",
            ObjectId = id,
            MakerUrl = "https://should-not-be-indexed"
        };

        item.BuildSearchIndex();

        Assert.Equal(
            "new_thing my thing process cloud flow workflow tom norton aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            item.SearchIndex);
    }

    [Fact]
    public void BuildSearchIndex_leaves_out_an_empty_object_id()
    {
        var item = Item(name: "Only");
        item.BuildSearchIndex();

        Assert.Equal("only table", item.SearchIndex);
    }

    [Fact]
    public void BuildSearchIndex_must_be_called_again_to_pick_up_a_changed_sub_type()
    {
        var item = Item();
        item.BuildSearchIndex();
        item.SubType = "Virtual";

        Assert.DoesNotContain("virtual", item.SearchIndex);

        item.BuildSearchIndex();
        Assert.Contains("virtual", item.SearchIndex);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("thing", true)]
    [InlineData("THING", true)]
    [InlineData("cloud flow", true)]    // terms match independently
    [InlineData("flow cloud", true)]    // in any order
    [InlineData("tom workflow", true)]  // across different fields
    [InlineData("aaaaaaaa", true)]      // by object id
    [InlineData("thing missing", false)]
    [InlineData("https", false)]        // maker URL is not indexed
    public void Search_terms_all_have_to_appear_in_the_index(string searchText, bool expected)
    {
        var item = new SolutionComponentItem
        {
            Name = "new_Thing",
            DisplayName = "My Thing",
            ComponentTypeName = "Process",
            SubType = "Cloud Flow",
            ComponentLogicalName = "workflow",
            Owner = "Tom Norton",
            ObjectId = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"),
            MakerUrl = "https://make.example"
        };
        item.BuildSearchIndex();

        Assert.Equal(expected, Matches(item, searchText));
    }
}
