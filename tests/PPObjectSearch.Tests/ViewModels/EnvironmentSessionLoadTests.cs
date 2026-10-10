using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>An environment tab loading a solution's objects: from the cache first, then afresh, and when loads overlap or fail.</summary>
public class EnvironmentSessionLoadTests
{
    private static List<string> Statuses(EnvironmentSessionViewModel session)
    {
        var statuses = new List<string>();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EnvironmentSessionViewModel.Status)) statuses.Add(session.Status);
        };
        return statuses;
    }

    private static Task CachedAsync(FakeEnvironment env, Guid solutionId) =>
        EnvironmentSessionThread.Until(() => ComponentCache.TryLoad(env.Url, solutionId) is not null, "the solution to be cached");

    [Fact]
    public Task Picking_a_solution_loads_its_objects_and_remembers_the_choice() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var fallback = env.AddSolution("Default Solution", "Default");
        var core = env.AddSolution("ECT Core", "ect_core");
        env.AddComponent(fallback, 1, "account", "Account");
        env.AddComponent(core, 61, "new_script.js");
        env.AddComponent(core, 61, "new_style.css");
        var session = await env.ConnectedAsync();
        var stateChanges = 0;
        session.StateChanged += (_, _) => stateChanges++;
        var statuses = Statuses(session);

        session.SelectedSolution = session.Solutions.Single(s => s.UniqueName == "ect_core");
        await EnvironmentSessionThread.Until(() => !session.IsBusy, "the load");

        Assert.Equal(1, stateChanges);
        Assert.Equal("ect_core", session.ToState().SolutionUniqueName);
        Assert.Equal(["new_script.js", "new_style.css"], session.Items.Select(i => i.Name));
        Assert.Contains("Loading objects in 'ECT Core'...", statuses);
        Assert.Contains("Loading 'ECT Core'... 2", statuses);
        Assert.StartsWith("Loaded 2 objects from 'ECT Core'", session.Status);
    });

    [Fact]
    public Task A_solution_seen_before_shows_from_the_cache_while_it_is_read_again() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var fallback = env.AddSolution("Default Solution", "Default");
        env.AddSolution("ECT Core", "ect_core");
        env.AddComponent(fallback, 1, "account", "Account");
        env.AddComponent(fallback, 1, "contact", "Contact");
        var session = await env.ConnectedAsync();
        await CachedAsync(env, fallback);
        session.SelectedSolution = session.Solutions.Single(s => s.UniqueName == "ect_core");
        await EnvironmentSessionThread.Until(() => !session.IsBusy, "the other solution to load");
        var gate = env.Gate(fallback);

        session.SelectedSolution = session.Solutions.Single(s => s.UniqueName == "Default");
        await EnvironmentSessionThread.Until(() => session.Status.EndsWith("- refreshing...", StringComparison.Ordinal), "the cached copy");

        Assert.StartsWith("Showing 2 objects cached at ", session.Status);
        Assert.Equal(["Account", "Contact"], session.Items.Select(i => i.PrimaryLabel));
        Assert.True(session.IsBusy);
        Assert.False(session.IsSolutionPickerEnabled);

        gate.SetResult();
        await EnvironmentSessionThread.Until(() => !session.IsBusy, "the refresh");

        Assert.StartsWith("Loaded 2 objects from 'Default Solution'", session.Status);
    });

    [Fact]
    public Task A_failed_refresh_keeps_the_cached_objects_on_screen() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var fallback = env.AddSolution("Default Solution", "Default");
        env.AddSolution("ECT Core", "ect_core");
        env.AddComponent(fallback, 1, "account", "Account");
        var session = await env.ConnectedAsync();
        await CachedAsync(env, fallback);
        session.SelectedSolution = session.Solutions.Single(s => s.UniqueName == "ect_core");
        await EnvironmentSessionThread.Until(() => !session.IsBusy, "the other solution to load");
        env.Failing.Add(fallback);

        session.SelectedSolution = session.Solutions.Single(s => s.UniqueName == "Default");
        await EnvironmentSessionThread.Until(() => !session.IsBusy, "the refresh");

        Assert.StartsWith("Showing objects cached at ", session.Status);
        Assert.EndsWith("Refresh failed: 500 Internal Server Error: The solution could not be read", session.Status);
        Assert.Equal("account", Assert.Single(session.Items).Name);
    });

    [Fact]
    public Task Refreshing_reads_afresh_and_a_failure_says_so() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var fallback = env.AddSolution("Default Solution", "Default");
        env.AddComponent(fallback, 1, "account", "Account");
        var session = await env.ConnectedAsync();
        await CachedAsync(env, fallback);
        Assert.True(session.RefreshCommand.CanExecute(null));
        env.Failing.Add(fallback);

        await session.RefreshCommand.ExecuteAsync(null);

        // Refresh skips the cache, so there is nothing left to show.
        Assert.Equal("Load failed: 500 Internal Server Error: The solution could not be read", session.Status);
        Assert.Empty(session.Items);
        Assert.Empty(session.TypeFilters);
        Assert.Equal(string.Empty, session.ResultSummary);
        Assert.False(session.IsBusy);
    });

    [Fact]
    public Task A_load_overtaken_by_another_pick_leaves_the_newer_one_on_screen() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var fallback = env.AddSolution("Default Solution", "Default");
        var slow = env.AddSolution("Slow", "slow");
        var quick = env.AddSolution("Quick", "quick");
        env.AddComponent(fallback, 1, "account", "Account");
        env.AddComponent(slow, 61, "slow.js");
        env.AddComponent(quick, 61, "quick.js");
        var session = await env.ConnectedAsync();
        var gate = env.Gate(slow);

        session.SelectedSolution = session.Solutions.Single(s => s.UniqueName == "slow");
        await EnvironmentSessionThread.Until(() => env.Handler.Requests.Any(r => r.Url.Contains(slow.ToString(), StringComparison.Ordinal)), "the slow read");
        session.SelectedSolution = session.Solutions.Single(s => s.UniqueName == "quick");
        await EnvironmentSessionThread.Until(() => !session.IsBusy, "the quick load");
        gate.SetResult();
        await Task.Delay(100);

        Assert.Equal("quick.js", Assert.Single(session.Items).Name);
        Assert.StartsWith("Loaded 1 objects from 'Quick'", session.Status);
    });

    [Fact]
    public Task Refreshing_keeps_the_chosen_filters() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var fallback = env.AddSolution("Default Solution", "Default");
        env.AddComponent(fallback, 1, "account", "Account");
        env.AddComponent(fallback, 1, "contact", "Contact", managed: true);
        env.AddComponent(fallback, 61, "new_script.js");
        var session = await env.ConnectedAsync();
        session.SelectedTypeFilter = session.TypeFilters.Single(f => f.Name == "Table");
        session.SelectedStateFilter = session.StateFilters.Single(f => f.Name == "Managed");
        session.SelectedLayerFilter = session.LayerFilters.Single(f => f.Name == "Not checked");

        await session.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Table", session.SelectedTypeFilter?.Name);
        Assert.Equal("Managed", session.SelectedStateFilter?.Name);
        Assert.Equal("Not checked", session.SelectedLayerFilter?.Name);
        Assert.Equal("contact", Assert.Single(session.ItemsView.Cast<SolutionComponentItem>()).Name);
        Assert.Equal("1 of 3 objects", session.ResultSummary);
    });

    [Fact]
    public Task Refreshing_keeps_the_chosen_sub_type() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var fallback = env.AddSolution("Default Solution", "Default");
        env.AddComponent(fallback, 29, "Notify owner", workflowCategory: 5);
        env.AddComponent(fallback, 29, "Assign case", workflowCategory: 0);
        var session = await env.ConnectedAsync();
        session.SelectedSubTypeFilter = session.SubTypeFilters.Single(f => f.Name == "Cloud Flow");

        await session.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Cloud Flow", session.SelectedSubTypeFilter?.Name);
        Assert.Equal("Notify owner", Assert.Single(session.ItemsView.Cast<SolutionComponentItem>()).Name);
    });
}
