using PPObjectSearch.Core;
using PPObjectSearch.Models;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>The rows of the details window: dependencies, containing solutions, definition changes and solution layers.</summary>
public class ObjectDetailTests
{
    private static readonly Guid Id = Guid.Parse("12345678-0000-0000-0000-000000000001");

    private static DependencyRef Dependency(DependencyDirection direction, string? resolvedName = null) => new()
    {
        ObjectId = Id, ComponentType = 60, ComponentTypeName = "System Form", Direction = direction, ResolvedName = resolvedName
    };

    private static ComponentLayer Layer(string solution = "Contoso", string? json = null, string? changes = null) => new()
    {
        SolutionName = solution, ComponentJson = json, ChangesRaw = changes
    };

    [Fact]
    public void A_dependency_is_named_where_it_is_known_and_by_its_id_where_not()
    {
        Assert.Equal("Main form", Dependency(DependencyDirection.Dependent, "Main form").DisplayName);
        Assert.Equal(Id.ToString(), Dependency(DependencyDirection.Dependent).DisplayName);
        Assert.Equal(Id.ToString(), Dependency(DependencyDirection.Dependent, "  ").DisplayName);
    }

    [Fact]
    public void A_dependency_says_which_way_it_points()
    {
        Assert.Equal("Depends on this", Dependency(DependencyDirection.Dependent).DirectionLabel);
        Assert.Equal("Required by this", Dependency(DependencyDirection.Required).DirectionLabel);
    }

    [Fact]
    public void A_containing_solution_says_whether_it_is_managed()
    {
        var managed = new ContainingSolution { SolutionId = Id, FriendlyName = "Core", UniqueName = "core", IsManaged = true, Version = "1.0" };
        var unmanaged = new ContainingSolution { SolutionId = Id, FriendlyName = "Core", UniqueName = "core" };

        Assert.Equal("Managed", managed.StateLabel);
        Assert.Equal("Unmanaged", unmanaged.StateLabel);
        Assert.Null(unmanaged.Version);
    }

    [Theory]
    [InlineData(DefinitionChangeKind.Added, "Added")]
    [InlineData(DefinitionChangeKind.Modified, "Modified")]
    [InlineData(DefinitionChangeKind.Removed, "Removed")]
    public void A_change_names_its_kind(DefinitionChangeKind kind, string expected)
    {
        Assert.Equal(expected, new DefinitionChange { PropertyName = "name", Kind = kind }.KindLabel);
    }

    [Fact]
    public void A_changes_values_are_indented_and_diffed_line_by_line()
    {
        var change = new DefinitionChange
        {
            PropertyName = "formxml",
            Kind = DefinitionChangeKind.Modified,
            PreviousValue = """{"a":1}""",
            CurrentValue = """{"a":2}"""
        };

        Assert.Contains("\"a\": 1", change.PreviousText);
        Assert.Contains("\"a\": 2", change.CurrentText);
        Assert.Contains(change.Diff, row => row.Kind != DiffKind.Unchanged);
        Assert.Same(change.Diff, change.Diff);
    }

    [Fact]
    public void A_change_with_one_side_missing_reads_as_empty_there()
    {
        var change = new DefinitionChange { PropertyName = "name", Kind = DefinitionChangeKind.Added, CurrentValue = "new" };

        Assert.Equal(string.Empty, change.PreviousText);
        Assert.Equal("new", change.CurrentText);
    }

    [Theory]
    [InlineData("Active", true, "Unmanaged (Active)")]
    [InlineData("active", true, "Unmanaged (Active)")]
    [InlineData("ContosoCore", false, "Managed")]
    public void The_unmanaged_layer_is_the_one_called_active(string solution, bool unmanaged, string state)
    {
        var layer = Layer(solution);

        Assert.Equal(unmanaged, layer.IsUnmanagedLayer);
        Assert.Equal(state, layer.StateLabel);
    }

    [Fact]
    public void Changes_listed_as_a_json_array_are_read_once_each()
    {
        var layer = Layer(changes: """["name", "Description", " ", "description", 42, null]""");

        Assert.Equal(["name", "Description", "42"], layer.ChangedProperties);
        Assert.Same(layer.ChangedProperties, layer.ChangedProperties);
    }

    [Theory]
    [InlineData("name,description;formxml", 3)]
    [InlineData("name\r\ndescription\nname", 2)]
    [InlineData("[name, description", 2)]
    [InlineData("""{"not":"an array"}""", 1)]
    public void Changes_listed_as_text_are_split_on_any_delimiter(string raw, int count)
    {
        Assert.Equal(count, Layer(changes: raw).ChangedProperties.Count);
    }

    [Theory]
    [InlineData(null, null, "-")]
    [InlineData("""{"a":1}""", null, "Full definition")]
    [InlineData(null, "name", "1 property")]
    [InlineData("""{"a":1}""", "name,description", "2 properties")]
    public void The_layers_grid_summarises_what_a_layer_changed(string? json, string? changes, string expected)
    {
        var layer = Layer(json: json, changes: changes);

        Assert.Equal(expected, layer.ChangeSummary);
        Assert.Equal(json is not null, layer.HasDefinition);
    }

    [Fact]
    public void Blank_changes_name_nothing()
    {
        Assert.Empty(Layer(changes: "   ").ChangedProperties);
        Assert.Empty(Layer().ChangedProperties);
    }
}
