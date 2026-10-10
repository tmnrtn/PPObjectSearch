using System.Runtime.CompilerServices;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The compare window: diffing two tabs' loaded lists, and filtering, searching and swapping the result.</summary>
public class CompareViewModelTests
{
    private const string DevUrl = "https://dev.crm11.dynamics.com";
    private const string ProdUrl = "https://prod.crm11.dynamics.com";

    private static readonly Guid Account = Guid.Parse("a0000000-0000-0000-0000-000000000001");

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_comparedAt")]
    private static extern ref DateTimeOffset? ComparedAt(CompareViewModel vm);

    private static SolutionInfo Solution(string name) => new() { SolutionId = Guid.NewGuid(), UniqueName = name.Replace(" ", ""), FriendlyName = name };

    private static SolutionComponentItem Item(string name, string type, int typeCode, Guid? id = null) =>
        new() { Name = name, ComponentTypeName = type, ComponentType = typeCode, ObjectId = id ?? Guid.NewGuid() };

    /// <summary>A table in both by id, a flow in both by name, and a web resource only on each side.</summary>
    private static (EnvironmentSessionViewModel Dev, EnvironmentSessionViewModel Prod) Pair(string rightSolution = "Core")
    {
        var dev = TestSessions.Connected(new FakeHttpHandler(), DevUrl, Solution("Core"),
            Item("account", "Table", 1, Account), Item("Notify", "Process", 29), Item("new_a.js", "Web Resource", 61));
        var prod = TestSessions.Connected(new FakeHttpHandler(), ProdUrl, Solution(rightSolution),
            Item("account", "Table", 1, Account), Item("Notify", "Process", 29), Item("new_b.js", "Web Resource", 61));
        return (dev, prod);
    }

    private static IEnumerable<string> Shown(CompareViewModel vm) => vm.RowsView.Cast<CompareRow>().Select(r => r.Name);

    [Fact]
    public void Two_connected_tabs_are_compared_as_soon_as_the_window_opens()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod, TestSessions.Disconnected()]);

        Assert.Equal([dev, prod], vm.Sessions);
        Assert.Same(dev, vm.ComparedLeft);
        Assert.Same(prod, vm.ComparedRight);
        Assert.Equal(("dev", "prod"), (vm.LeftHeader, vm.RightHeader));
        Assert.Equal(4, vm.Rows.Count);
        Assert.Equal("1 only in dev  |  1 only in prod  |  2 in both", vm.Summary);
        Assert.Equal("2 differences · 2 in both", vm.ResultSummary);
        Assert.Equal((4, 1, 1, 2), (vm.CountAll, vm.CountOnlyLeft, vm.CountOnlyRight, vm.CountBoth));
        Assert.Equal(["All types (4)", "Process (1)", "Table (1)", "Web Resource (2)"], vm.TypeFilters.Select(t => t.Label));
        Assert.True(vm.SelectedTypeFilter!.IsAll);
        Assert.Equal("Compared just now", vm.ComparedAgo);
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("Core", "Matched on object id, falling back to type and name · Core on both sides")]
    [InlineData("Core UAT", "Matched on object id, falling back to type and name · Core vs Core UAT")]
    public void The_status_bar_says_how_rows_were_paired_and_from_which_solutions(string rightSolution, string expected)
    {
        var (dev, prod) = Pair(rightSolution);
        using var vm = new CompareViewModel([dev, prod]);

        Assert.Equal(expected, vm.MatchDescription);
    }

    [Fact]
    public void The_status_filter_shows_one_side_or_both()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);

        vm.StatusFilter = CompareStatusFilter.OnlyLeft;
        Assert.Equal(["new_a.js"], Shown(vm));

        vm.StatusFilter = CompareStatusFilter.OnlyRight;
        Assert.Equal(["new_b.js"], Shown(vm));

        vm.StatusFilter = CompareStatusFilter.Both;
        Assert.Equal(new[] { "Notify", "account" }.Order(StringComparer.Ordinal), Shown(vm).Order(StringComparer.Ordinal));

        vm.StatusFilter = CompareStatusFilter.All;
        Assert.Equal(4, vm.RowsView.Count);
    }

    [Fact]
    public void A_type_narrows_the_rows_and_the_counts()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);

        vm.SelectedTypeFilter = vm.TypeFilters.Single(t => t.Name == "Web Resource");

        Assert.Equal(["new_a.js", "new_b.js"], Shown(vm).Order(StringComparer.Ordinal));
        Assert.Equal((2, 1, 1, 0), (vm.CountAll, vm.CountOnlyLeft, vm.CountOnlyRight, vm.CountBoth));
        Assert.Equal("1 only in dev  |  1 only in prod  |  2 in both", vm.Summary);
    }

    [Fact]
    public void A_search_waits_for_typing_to_pause_and_then_narrows_by_name()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);

        vm.SearchText = "NEW_";

        Assert.Equal(4, vm.RowsView.Count);

        // The pause is a dispatcher timer; any other refresh applies the search straight away.
        vm.StatusFilter = CompareStatusFilter.OnlyLeft;
        vm.StatusFilter = CompareStatusFilter.All;

        Assert.Equal(["new_a.js", "new_b.js"], Shown(vm).Order(StringComparer.Ordinal));
        Assert.Equal((2, 1, 1, 0), (vm.CountAll, vm.CountOnlyLeft, vm.CountOnlyRight, vm.CountBoth));
    }

    [Fact]
    public void Typing_into_the_type_picker_narrows_its_options_and_drops_a_choice_it_no_longer_matches()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);
        var table = vm.TypeFilters.Single(t => t.Name == "Table");
        vm.SelectedTypeFilter = table;

        vm.TypeFilterSearchText = table.Label;

        Assert.Same(table, vm.SelectedTypeFilter);
        Assert.Equal(4, vm.TypeFiltersView.Cast<TypeFilterOption>().Count());

        vm.TypeFilterSearchText = "web";

        Assert.True(vm.SelectedTypeFilter.IsAll);
        Assert.Equal(["All types (4)", "Web Resource (2)"], vm.TypeFiltersView.Cast<TypeFilterOption>().Select(t => t.Label));

        vm.TypeFilterSearchText = string.Empty;

        Assert.Equal(4, vm.TypeFiltersView.Cast<TypeFilterOption>().Count());
    }

    [Fact]
    public void Swapping_the_sides_compares_again_with_the_labels_reversed()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SwapCommand.Execute(null);

        Assert.Same(prod, vm.Left);
        Assert.Same(dev, vm.Right);
        Assert.Same(prod, vm.ComparedLeft);
        Assert.Equal("1 only in prod  |  1 only in dev  |  2 in both", vm.Summary);
        Assert.Contains(nameof(CompareViewModel.Left), raised);
        Assert.Contains(nameof(CompareViewModel.Right), raised);
    }

    [Fact]
    public void One_tab_alone_waits_for_a_second_to_compare_with()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev]);

        Assert.Same(dev, vm.Left);
        Assert.Null(vm.Right);
        Assert.Empty(vm.Rows);
        Assert.Equal("Pick two environments to compare.", vm.Summary);
        Assert.Equal(("dev", "Right"), (vm.LeftHeader, vm.RightHeader));
        Assert.Equal("Matched on object id, falling back to type and name", vm.MatchDescription);
        Assert.Equal(string.Empty, vm.ComparedAgo);
        Assert.False(vm.CompareCommand.CanExecute(null));
        Assert.True(vm.SwapCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));

        vm.SwapCommand.Execute(null);

        Assert.Null(vm.Left);
        Assert.Same(dev, vm.Right);
        Assert.Equal("Left", vm.LeftHeader);
        Assert.Equal("Matched on object id, falling back to type and name", vm.MatchDescription);
        Assert.Empty(vm.Rows);

        vm.Left = prod;
        Assert.True(vm.CompareCommand.CanExecute(null));

        vm.CompareCommand.Execute(null);

        Assert.Equal("1 only in prod  |  1 only in dev  |  2 in both", vm.Summary);
    }

    [Fact]
    public void A_tab_cannot_be_compared_with_itself()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);

        vm.Right = dev;

        Assert.False(vm.CompareCommand.CanExecute(null));

        vm.Left = null;
        vm.Right = null;

        Assert.False(vm.SwapCommand.CanExecute(null));
    }

    [Fact]
    public void Only_a_component_on_both_sides_can_have_its_definition_compared()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);
        var both = vm.Rows.First(r => r.Status == CompareStatus.Same);
        var oneSide = vm.Rows.First(r => r.Status == CompareStatus.OnlyInLeft);

        Assert.True(vm.CompareDefinitionCommand.CanExecute(both));
        Assert.False(vm.CompareDefinitionCommand.CanExecute(oneSide));
        Assert.False(vm.CompareDefinitionCommand.CanExecute(null));

        vm.SelectedRow = both;

        Assert.True(vm.CompareDefinitionCommand.CanExecute(null));

        // Without a live connection on a side there is nothing to read the definitions from.
        TestSessions.DropClient(prod);
        vm.CompareDefinitionCommand.Execute(null);
        TestSessions.DropClient(dev);
        vm.CompareDefinitionCommand.Execute(null);
        vm.CompareDefinitionCommand.Execute(oneSide);

        Assert.Same(both, vm.SelectedRow);
    }

    [Fact]
    public void With_no_connected_tabs_comparing_does_nothing()
    {
        using var vm = new CompareViewModel([TestSessions.Disconnected()]);

        vm.CompareCommand.Execute(null);

        Assert.Null(vm.Left);
        Assert.Null(vm.ComparedLeft);
        Assert.Empty(vm.Rows);
        Assert.Equal("Pick two environments to compare.", vm.Summary);
        Assert.False(vm.SwapCommand.CanExecute(null));
    }

    [Fact]
    public void A_side_without_a_solution_leaves_the_solutions_out_of_the_status_bar()
    {
        var dev = TestSessions.Connected(new FakeHttpHandler(), DevUrl, Solution("Core"), Item("account", "Table", 1, Account));
        var prod = TestSessions.Connected(new FakeHttpHandler(), ProdUrl, null, Item("account", "Table", 1, Account));

        using var vm = new CompareViewModel([dev, prod]);

        Assert.Equal("Matched on object id, falling back to type and name", vm.MatchDescription);
        Assert.Equal("0 only in dev  |  0 only in prod  |  1 in both", vm.Summary);
    }

    [Fact]
    public void Choosing_what_is_already_chosen_changes_nothing()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.StatusFilter = CompareStatusFilter.All;
        vm.SelectedRow = null;
        vm.Left = dev;
        vm.Right = prod;
        vm.SearchText = string.Empty;
        vm.TypeFilterSearchText = string.Empty;
        vm.SelectedTypeFilter = vm.TypeFilters[0];

        Assert.Empty(raised);
    }

    [Fact]
    public void Typing_into_the_type_picker_with_nothing_chosen_only_narrows_the_options()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);
        vm.SelectedTypeFilter = null;

        vm.TypeFilterSearchText = "tab";

        Assert.Equal("tab", vm.TypeFilterSearchText);
        Assert.Null(vm.SelectedTypeFilter);
        Assert.Equal(["All types (4)", "Table (1)"], vm.TypeFiltersView.Cast<TypeFilterOption>().Select(t => t.Label));
        Assert.Equal(4, vm.RowsView.Count);
    }

    [Fact]
    public void How_long_ago_the_comparison_ran_is_kept_readable()
    {
        var (dev, prod) = Pair();
        using var vm = new CompareViewModel([dev, prod]);

        ComparedAt(vm) = DateTimeOffset.Now.AddMinutes(-5);
        Assert.Equal("Compared 5 min ago", vm.ComparedAgo);

        var at = DateTimeOffset.Now.AddHours(-2);
        ComparedAt(vm) = at;
        Assert.Equal($"Compared at {at:HH:mm}", vm.ComparedAgo);
    }
}
