using System.ComponentModel;
using PPObjectSearch.Auth;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The table picker for a reference-data comparison: ticks, filtering and the counts that follow them.</summary>
public class EntityPickerViewModelTests
{
    private static EntitySummary Table(string logicalName, string? displayName, bool managed) =>
        new(logicalName, displayName, logicalName + "s", logicalName + "id", "name", managed, IsActivity: false);

    private static EntityPickerViewModel Picker(params string[] alreadyAdded) => new(
        [
            Table("account", "Account", managed: true),
            Table("contact", "Contact", managed: true),
            Table("new_country", "Country", managed: false),
            Table("new_region", null, managed: false)
        ],
        alreadyAdded);

    private static SolutionInfo Solution(string uniqueName, string friendlyName) => new()
    {
        SolutionId = Guid.NewGuid(),
        UniqueName = uniqueName,
        FriendlyName = friendlyName
    };

    /// <summary>An environment with its solutions already listed, as a connected tab has them.</summary>
    private static EnvironmentSessionViewModel Session(string name, params SolutionInfo[] solutions)
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(),
            new TabState { EnvironmentUrl = $"https://{name}.crm11.dynamics.com" });
        foreach (var solution in solutions) session.Solutions.Add(solution);
        return session;
    }

    /// <summary>A picker whose solution filter reads <paramref name="tables"/> by solution unique name.</summary>
    private static EntityPickerViewModel SolutionPicker(
        IReadOnlyList<EnvironmentSessionViewModel> sessions,
        Func<EnvironmentSessionViewModel, SolutionInfo, Task<IReadOnlyCollection<string>>> read) => new(
        [
            Table("account", "Account", managed: true),
            Table("contact", "Contact", managed: true),
            Table("new_country", "Country", managed: false),
            Table("new_region", null, managed: false)
        ],
        [],
        sessions,
        (session, solution, _) => read(session, solution));

    private static SolutionOption Option(EntityPickerViewModel picker, string uniqueName) =>
        picker.SolutionOptions.Single(o => o.Solution?.UniqueName == uniqueName);

    private static EntityPick Pick(EntityPickerViewModel picker, string logicalName) =>
        picker.Items.Single(i => i.LogicalName == logicalName);

    private static string[] Shown(EntityPickerViewModel picker) =>
        picker.ItemsView.Cast<EntityPick>().Select(p => p.LogicalName).ToArray();

    [Fact]
    public void Every_table_is_listed_and_one_already_added_reads_as_ticked_but_not_chosen()
    {
        var picker = Picker("NEW_COUNTRY");

        var country = Pick(picker, "new_country");

        Assert.Equal(4, picker.Items.Count);
        Assert.True(country.IsAlreadyAdded);
        Assert.True(country.IsTicked);
        Assert.False(country.IsSelected);
        Assert.Equal("Already added", country.StateLabel);
        Assert.Equal("Managed", Pick(picker, "account").StateLabel);
        Assert.Equal("Unmanaged", Pick(picker, "new_region").StateLabel);
        Assert.Equal("0 selected · 4 of 4 tables shown", picker.Summary);
        Assert.Equal("Add tables", picker.AddLabel);
        Assert.False(picker.HasSelection);
    }

    [Fact]
    public void A_table_without_a_display_name_is_labelled_by_its_logical_name()
    {
        var picker = Picker();

        Assert.Equal("new_region", Pick(picker, "new_region").DisplayLabel);
        Assert.Equal("new_region", Pick(picker, "new_region").Label);
        Assert.Equal("Country", Pick(picker, "new_country").DisplayLabel);
        Assert.Equal("Country (new_country)", Pick(picker, "new_country").Label);
    }

    [Fact]
    public void Ticking_tables_updates_the_count_and_the_add_button()
    {
        var picker = Picker();
        var changed = new List<string?>();
        picker.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Pick(picker, "account").IsTicked = true;

        Assert.Equal("Add 1 table", picker.AddLabel);
        Assert.Contains(nameof(EntityPickerViewModel.AddLabel), changed);

        Pick(picker, "new_country").IsTicked = true;

        Assert.Equal(2, picker.SelectedCount);
        Assert.True(picker.HasSelection);
        Assert.Equal("Add 2 tables", picker.AddLabel);
        Assert.Equal("2 selected · 4 of 4 tables shown", picker.Summary);
        Assert.Equal(["account", "new_country"], picker.SelectedEntities().Select(e => e.LogicalName));
    }

    [Fact]
    public void A_table_already_added_cannot_be_ticked_again()
    {
        var picker = Picker("account");
        var account = Pick(picker, "account");

        account.IsTicked = false;
        account.IsTicked = true;

        Assert.False(account.IsSelected);
        Assert.True(account.IsTicked);
        Assert.Empty(picker.SelectedEntities());
    }

    [Fact]
    public void Ticking_a_table_raises_its_ticked_state_too()
    {
        var pick = Pick(Picker(), "contact");
        var changed = new List<string?>();
        pick.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pick.IsSelected = true;
        pick.IsSelected = true;

        Assert.Equal([nameof(EntityPick.IsSelected), nameof(EntityPick.IsTicked)], changed);
    }

    [Fact]
    public void Clearing_the_selection_unticks_every_table()
    {
        var picker = Picker();
        Pick(picker, "account").IsSelected = true;
        Pick(picker, "new_region").IsSelected = true;

        picker.ClearSelectionCommand.Execute(null);

        Assert.All(picker.Items, i => Assert.False(i.IsSelected));
        Assert.Equal(0, picker.SelectedCount);
    }

    [Fact]
    public void Search_text_matches_the_logical_or_display_name()
    {
        var picker = Picker();

        picker.SearchText = "COUNT";
        Assert.Equal(["account", "new_country"], Shown(picker));

        picker.SearchText = "region";
        Assert.Equal(["new_region"], Shown(picker));
        Assert.Equal("0 selected · 1 of 4 tables shown", picker.Summary);

        picker.SearchText = "nothing like it";
        Assert.Empty(Shown(picker));

        picker.SearchText = " ";
        Assert.Equal(4, Shown(picker).Length);
    }

    [Fact]
    public void Hiding_managed_tables_keeps_a_ticked_one_and_counts_the_rest()
    {
        var picker = Picker();
        Pick(picker, "contact").IsSelected = true;

        picker.HideManaged = true;

        Assert.Equal(["contact", "new_country", "new_region"], Shown(picker));
        Assert.Equal("1 selected · 1 managed tables hidden", picker.Summary);

        picker.HideManaged = true;
        picker.HideManaged = false;

        Assert.Equal(4, Shown(picker).Length);
    }

    [Fact]
    public void Setting_the_same_search_twice_does_not_refresh_again()
    {
        var picker = Picker();
        picker.SearchText = "acc";
        var changed = 0;
        ((INotifyPropertyChanged)picker).PropertyChanged += (_, _) => changed++;

        picker.SearchText = "acc";

        Assert.Equal(0, changed);
    }

    [Fact]
    public void Anything_but_a_table_pick_is_filtered_out()
    {
        var picker = Picker();

        Assert.False(picker.ItemsView.Filter("not a pick"));
    }

    // ---------------------------------------------------------------- solution filter

    [Fact]
    public void Without_a_way_to_read_solutions_there_is_no_solution_filter()
    {
        var picker = Picker();

        Assert.False(picker.CanFilterBySolution);
        Assert.Empty(picker.SolutionSessions);
        Assert.Empty(picker.SolutionOptions);
    }

    [Fact]
    public void The_source_solutions_are_offered_by_name_after_any_solution_without_the_default()
    {
        var source = Session("dev", Solution("Default", "Default Solution"), Solution("zeta", "Zeta"), Solution("core", "Core"));
        var target = Session("test");

        var picker = SolutionPicker([source, target], (_, _) => Task.FromResult<IReadOnlyCollection<string>>([]));

        Assert.True(picker.CanFilterBySolution);
        Assert.Same(source, picker.SolutionSession);
        Assert.Equal(["Any solution", "Core", "Zeta"], picker.SolutionOptions.Select(o => o.Label));
        Assert.Null(picker.SelectedSolution?.Solution);
        Assert.Equal(4, Shown(picker).Length);
    }

    [Fact]
    public void A_solution_narrows_the_list_to_its_tables_but_keeps_a_ticked_one()
    {
        var source = Session("dev", Solution("core", "Core"));
        var picker = SolutionPicker([source], (_, _) => Task.FromResult<IReadOnlyCollection<string>>(["NEW_COUNTRY", "new_region"]));
        Pick(picker, "account").IsSelected = true;

        picker.SelectedSolution = Option(picker, "core");

        Assert.Equal(["account", "new_country", "new_region"], Shown(picker));
        Assert.Equal("1 selected · 3 of 4 tables shown", picker.Summary);

        picker.SelectedSolution = picker.SolutionOptions[0];

        Assert.Equal(4, Shown(picker).Length);
    }

    [Fact]
    public void Tables_of_a_target_solution_missing_from_the_source_are_counted()
    {
        var source = Session("dev");
        var target = Session("test", Solution("core", "Core"));
        var read = new List<string>();
        var picker = SolutionPicker([source, target], (session, solution) =>
        {
            read.Add($"{session.Title}/{solution.UniqueName}");
            return Task.FromResult<IReadOnlyCollection<string>>(["new_country", "new_only_in_test"]);
        });

        picker.SolutionSession = target;
        Assert.Equal(["Any solution", "Core"], picker.SolutionOptions.Select(o => o.Label));

        picker.SelectedSolution = Option(picker, "core");

        Assert.Equal(["test/core"], read);
        Assert.Equal(["new_country"], Shown(picker));
        Assert.Equal("0 selected · 1 of 4 tables shown · 1 of the tables in Core not in dev", picker.Summary);

        // Another environment's list starts over, unfiltered.
        picker.SolutionSession = source;

        Assert.Null(picker.SelectedSolution?.Solution);
        Assert.Equal(4, Shown(picker).Length);
    }

    [Fact]
    public async Task Nothing_is_listed_while_the_solution_is_read()
    {
        var source = Session("dev", Solution("core", "Core"));
        var pending = new TaskCompletionSource<IReadOnlyCollection<string>>();
        var picker = SolutionPicker([source], (_, _) => pending.Task);

        picker.SelectedSolution = Option(picker, "core");

        Assert.True(picker.IsReadingSolution);
        Assert.Empty(Shown(picker));
        Assert.Equal("0 selected · 0 of 4 tables shown · reading Core...", picker.Summary);

        pending.SetResult(["contact"]);
        await picker.SolutionRead;

        Assert.False(picker.IsReadingSolution);
        Assert.Equal(["contact"], Shown(picker));
    }

    [Fact]
    public void A_solution_that_cannot_be_read_leaves_the_list_whole_and_says_why()
    {
        var source = Session("dev", Solution("core", "Core"));
        var picker = SolutionPicker([source], (_, _) => Task.FromException<IReadOnlyCollection<string>>(new InvalidOperationException("Forbidden")));

        picker.SelectedSolution = Option(picker, "core");

        Assert.False(picker.IsReadingSolution);
        Assert.Equal(4, Shown(picker).Length);
        Assert.Equal("The tables in Core could not be read - Forbidden", picker.SolutionProblem);

        picker.SelectedSolution = picker.SolutionOptions[0];

        Assert.Null(picker.SolutionProblem);
    }

    [Fact]
    public void A_solution_picked_before_is_picked_again_by_unique_name()
    {
        var source = Session("dev", Solution("core", "Core"));
        var target = Session("test", Solution("CORE", "Core"), Solution("other", "Other"));
        var picker = SolutionPicker([source, target], (_, _) => Task.FromResult<IReadOnlyCollection<string>>(["account"]));

        picker.SelectSolution(target, "core");

        Assert.Same(target, picker.SolutionSession);
        Assert.Equal("CORE", picker.SelectedSolution?.Solution?.UniqueName);
        Assert.Equal(["account"], Shown(picker));

        // An environment no longer offered, or a solution gone, changes nothing.
        picker.SelectSolution(Session("gone", Solution("core", "Core")), "core");
        picker.SelectSolution(target, "deleted");

        Assert.Same(target, picker.SolutionSession);
        Assert.Equal("CORE", picker.SelectedSolution?.Solution?.UniqueName);
    }
}
