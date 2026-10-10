using PPObjectSearch.Models;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The window that shows what one solution layer changed, against the layer beneath it.</summary>
public class LayerChangesViewModelTests
{
    private static ComponentLayer Layer(string solution, string? json = null, string? changes = null) => new()
    {
        SolutionName = solution, ComponentJson = json, ChangesRaw = changes
    };

    /// <summary>The shared half on its own, with nothing overridden.</summary>
    private sealed class PlainDiff : DefinitionDiffViewModel
    {
        public PlainDiff() : base("Left", "Right")
        {
        }

        public void Show(IEnumerable<DefinitionChange>? changes) => ReplaceChanges(changes);

        public void Rename(string before, string after)
        {
            BeforeHeading = before;
            AfterHeading = after;
        }
    }

    [Fact]
    public void A_layer_is_compared_with_the_one_beneath_it()
    {
        var layer = Layer("Contoso Patch", """{"name":"New","description":"Same","added":1}""", "name");
        var below = Layer("Contoso Core", """{"name":"Old","description":"Same"}""");

        var vm = new LayerChangesViewModel("Account form", layer, below);

        Assert.Equal("Account form - changes in 'Contoso Patch'", vm.Title);
        Assert.Equal("Contoso Core", vm.BeforeHeading);
        Assert.Equal("Contoso Patch", vm.AfterHeading);
        Assert.Equal("Compared against the layer beneath it: 'Contoso Core'.", vm.BelowLabel);
        Assert.Equal("2 properties changed by this layer.", vm.Summary);
        Assert.Equal("2 changes", vm.ChangeCountLabel);
        Assert.Equal("name", vm.Changes[0].PropertyName);
        Assert.Same(vm.Changes[0], vm.SelectedChange);
        Assert.Same(below, vm.Below);
        Assert.Same(layer, vm.Layer);
        Assert.Equal("Account form", vm.ComponentLabel);
    }

    [Fact]
    public void The_panes_are_marked_as_the_layer_and_what_lies_beneath()
    {
        var vm = new LayerChangesViewModel("x", Layer("Top", "{}"), null);

        Assert.Equal("Beneath", vm.BeforeMarker);
        Assert.Equal("Layer", vm.AfterMarker);
    }

    [Fact]
    public void The_bottom_layer_adds_everything_in_it()
    {
        var vm = new LayerChangesViewModel("Account", Layer("Active", """{"a":1}"""), null);

        Assert.Equal("Nothing beneath", vm.BeforeHeading);
        Assert.Equal("This is the bottom layer, so everything in it is new.", vm.BelowLabel);
        var change = Assert.Single(vm.Changes);
        Assert.Equal(DefinitionChangeKind.Added, change.Kind);
        Assert.Equal("1 property changed by this layer.", vm.Summary);
        Assert.Equal("1 change", vm.ChangeCountLabel);
    }

    [Fact]
    public void The_definition_is_indented_so_it_can_be_read()
    {
        var vm = new LayerChangesViewModel("x", Layer("Top", """{"a":{"b":1}}"""), null);

        Assert.True(vm.HasDefinition);
        Assert.Contains("\"b\": 1", vm.Definition);
        Assert.Contains('\n', vm.Definition);
    }

    [Fact]
    public void Without_a_definition_the_named_properties_are_still_listed()
    {
        var vm = new LayerChangesViewModel("x", Layer("Top", changes: "name,description"), Layer("Below"));

        Assert.False(vm.HasDefinition);
        Assert.Equal(["name", "description"], vm.Changes.Select(c => c.PropertyName));
        Assert.All(vm.Changes, c => Assert.Equal(DefinitionChangeKind.Modified, c.Kind));
        Assert.Equal("2 properties changed by this layer.", vm.Summary);
    }

    [Fact]
    public void Without_a_definition_or_named_changes_the_window_says_it_cannot_show_them()
    {
        var vm = new LayerChangesViewModel("x", Layer("Top"), null);

        Assert.Empty(vm.Changes);
        Assert.Null(vm.SelectedChange);
        Assert.Equal("0 changes", vm.ChangeCountLabel);
        Assert.Equal("Dataverse returned no definition for this layer, so its changes cannot be shown.", vm.Summary);
    }

    [Fact]
    public void A_layer_identical_to_the_one_beneath_reports_no_differences()
    {
        var vm = new LayerChangesViewModel("x", Layer("Top", """{"a":1}"""), Layer("Below", """{"a":1}"""));

        Assert.Empty(vm.Changes);
        Assert.Equal("Dataverse reported no property-level differences for this layer.", vm.Summary);
    }

    [Fact]
    public void Each_side_can_be_copied_only_when_it_has_a_value()
    {
        var vm = new LayerChangesViewModel("x", Layer("Top", """{"a":1,"b":2}"""), Layer("Below", """{"a":0}"""));
        vm.SelectedChange = null;
        var raised = 0;
        vm.CopyBeforeCommand.CanExecuteChanged += (_, _) => raised++;

        vm.SelectedChange = vm.Changes.Single(c => c.PropertyName == "b");

        Assert.False(vm.CopyBeforeCommand.CanExecute(null));
        Assert.True(vm.CopyAfterCommand.CanExecute(null));
        Assert.Equal(1, raised);

        vm.SelectedChange = vm.Changes.Single(c => c.PropertyName == "a");

        Assert.True(vm.CopyBeforeCommand.CanExecute(null));
        Assert.True(vm.CopyAfterCommand.CanExecute(null));
    }

    [Fact]
    public void Nothing_selected_has_nothing_to_copy()
    {
        var vm = new LayerChangesViewModel("x", Layer("Top"), null);

        Assert.False(vm.CopyBeforeCommand.CanExecute(null));
        Assert.False(vm.CopyAfterCommand.CanExecute(null));
    }

    [Fact]
    public void Copying_with_nothing_selected_does_nothing()
    {
        var vm = new LayerChangesViewModel("x", Layer("Top"), null);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.CopyBeforeCommand.Execute(null);
        vm.CopyAfterCommand.Execute(null);

        Assert.Null(vm.SelectedChange);
        Assert.Empty(changed);
    }

    [Fact]
    public void The_shared_half_shows_whatever_changes_it_is_given()
    {
        var vm = new PlainDiff();
        var labels = new List<string?>();
        vm.PropertyChanged += (_, e) => labels.Add(e.PropertyName);

        vm.Show([new DefinitionChange { PropertyName = "a", Kind = DefinitionChangeKind.Removed, PreviousValue = "1" }]);

        Assert.Null(vm.BeforeMarker);
        Assert.Null(vm.AfterMarker);
        Assert.Equal("1 change", vm.ChangeCountLabel);
        Assert.Equal("a", vm.SelectedChange?.PropertyName);
        Assert.Contains(nameof(DefinitionDiffViewModel.ChangeCountLabel), labels);

        vm.Show(null);

        Assert.Empty(vm.Changes);
        Assert.Null(vm.SelectedChange);
    }

    [Fact]
    public void The_shared_half_can_rename_its_panes()
    {
        var vm = new PlainDiff();

        Assert.Equal("Left", vm.BeforeHeading);
        Assert.Equal("Right", vm.AfterHeading);

        vm.Rename("Dev", "Prod");

        Assert.Equal("Dev", vm.BeforeHeading);
        Assert.Equal("Prod", vm.AfterHeading);
    }
}
