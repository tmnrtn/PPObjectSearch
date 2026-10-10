using PPObjectSearch.Auth;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>Narrowing an environment tab's objects: the search box, the type, sub type, state and layer filters, and the selection.</summary>
public class EnvironmentSessionFilterTests
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>Two tables, a cloud flow, a classic workflow and a web resource; two of them managed.</summary>
    internal static async Task<(FakeEnvironment Env, EnvironmentSessionViewModel Session)> ConnectedAsync(AppSettings? settings = null)
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 1, "account", "Account");
        env.AddComponent(solution, 1, "contact", "Contact", managed: true);
        env.AddComponent(solution, 29, "Notify owner", workflowCategory: 5);
        env.AddComponent(solution, 29, "Assign case", managed: true, workflowCategory: 0);
        env.AddComponent(solution, 61, "new_script.js");
        return (env, await env.ConnectedAsync(settings));
    }

    private static List<string> Shown(EnvironmentSessionViewModel session) =>
        session.ItemsView.Cast<SolutionComponentItem>().Select(i => i.PrimaryLabel).ToList();

    private static SolutionComponentItem Row(EnvironmentSessionViewModel session, string label) =>
        session.AllItems.Single(i => i.PrimaryLabel == label);

    [Fact]
    public Task The_filters_count_what_was_loaded() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();

        Assert.Equal(["All types (5)", "Process (2)", "Table (2)", "Web Resource (1)"], session.TypeFilters.Select(f => f.Label));
        Assert.Equal("All", session.TypeFilters[0].ShortLabel);
        Assert.Equal("Table (2)", session.TypeFilters[2].ShortLabel);
        Assert.Equal("Table (2)", session.TypeFilters[2].ToString());
        Assert.Equal(["Managed and unmanaged (5)", "Unmanaged (3)", "Managed (2)"], session.StateFilters.Select(f => f.Label));
        Assert.Equal(["Any / not checked (5)", "Unmanaged layer (0)", "No unmanaged layer (0)", "Not checked (5)"],
            session.LayerFilters.Select(f => f.Label));
        Assert.Equal(["All sub types (2)", "Cloud Flow (1)", "Workflow (classic) (1)"], session.SubTypeFilters.Select(f => f.Label));
        Assert.True(session.HasSubTypes);
        Assert.Equal(["Assign case", "Notify owner", "Account", "Contact", "new_script.js"], Shown(session));
        Assert.False(session.HasActiveFilters);
        Assert.True(session.ExportCsvCommand.CanExecute(null));
    });

    [Fact]
    public Task Choosing_a_type_narrows_the_list_and_its_sub_types() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();

        session.SelectedTypeFilter = session.TypeFilters.Single(f => f.Name == "Table");

        Assert.Equal(["Account", "Contact"], Shown(session));
        Assert.Equal("2 of 5 objects", session.ResultSummary);
        Assert.Equal(["All sub types (0)"], session.SubTypeFilters.Select(f => f.Label));
        Assert.False(session.HasSubTypes);
        Assert.True(session.HasActiveFilters);

        session.SelectedTypeFilter = session.TypeFilters.Single(f => f.Name == "Process");
        session.SelectedSubTypeFilter = session.SubTypeFilters.Single(f => f.Name == "Cloud Flow");

        Assert.Equal(["Notify owner"], Shown(session));
    });

    [Fact]
    public Task State_and_layer_filters_narrow_the_list() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();

        session.SelectedStateFilter = session.StateFilters.Single(f => f.Name == "Managed");
        Assert.Equal(["Assign case", "Contact"], Shown(session));

        session.SelectedLayerFilter = session.LayerFilters.Single(f => f.Name == "Not checked");
        Assert.Equal(["Assign case", "Contact"], Shown(session));

        session.SelectedLayerFilter = session.LayerFilters.Single(f => f.Name == "Unmanaged layer");
        Assert.Empty(Shown(session));
        Assert.False(session.ExportCsvCommand.CanExecute(null));

        session.SelectedLayerFilter = session.LayerFilters.Single(f => f.Name == "No unmanaged layer");
        Assert.Empty(Shown(session));
        Assert.Equal("0 of 5 objects", session.ResultSummary);
    });

    [Fact]
    public Task Typed_words_narrow_the_list_once_typing_pauses() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();

        session.SearchText = "NOTIFY  owner";
        Assert.Equal(5, Shown(session).Count);

        EnvironmentSessionThread.Elapse(session, SearchDebounce);

        Assert.Equal(["Notify owner"], Shown(session));
        Assert.True(session.HasActiveFilters);

        session.SearchText = "owner script";
        EnvironmentSessionThread.Elapse(session, SearchDebounce);
        Assert.Empty(Shown(session));

        session.ClearSearchCommand.Execute(null);
        EnvironmentSessionThread.Elapse(session, SearchDebounce);
        Assert.Equal(5, Shown(session).Count);
    });

    [Fact]
    public Task Clearing_the_filters_shows_everything_again() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();
        session.SelectedTypeFilter = session.TypeFilters.Single(f => f.Name == "Process");
        session.SelectedStateFilter = session.StateFilters.Single(f => f.Name == "Unmanaged");
        session.SelectedLayerFilter = session.LayerFilters.Single(f => f.Name == "Not checked");
        session.FavouritesOnly = true;
        session.SearchText = "notify";

        session.ClearFiltersCommand.Execute(null);

        Assert.True(session.SelectedTypeFilter?.IsAll);
        Assert.True(session.SelectedSubTypeFilter?.IsAll);
        Assert.True(session.SelectedStateFilter?.IsAll);
        Assert.True(session.SelectedLayerFilter?.IsAll);
        Assert.False(session.FavouritesOnly);
        Assert.Equal(string.Empty, session.SearchText);
        Assert.False(session.HasActiveFilters);
        Assert.Equal("5 objects", session.ResultSummary);
    });

    [Fact]
    public Task Clearing_the_filters_lets_go_of_a_chosen_sub_type_too() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();
        session.SelectedTypeFilter = session.TypeFilters.Single(f => f.Name == "Process");
        session.SelectedSubTypeFilter = session.SubTypeFilters.Single(f => f.Name == "Cloud Flow");

        session.ClearFiltersCommand.Execute(null);

        Assert.True(session.SelectedSubTypeFilter?.IsAll);
        Assert.False(session.HasActiveFilters);
        Assert.Equal("5 objects", session.ResultSummary);
    });

    [Fact]
    public Task Typing_in_the_type_filter_narrows_its_list_and_lets_go_of_the_chosen_type() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();
        var tables = session.TypeFilters.Single(f => f.Name == "Table");
        session.SelectedTypeFilter = tables;

        // The box showing the chosen type's own label is not a search.
        session.TypeFilterSearchText = tables.Label;
        Assert.Equal(4, session.TypeFiltersView.Cast<TypeFilterOption>().Count());
        Assert.Same(tables, session.SelectedTypeFilter);

        session.TypeFilterSearchText = "web";

        Assert.Equal(["All types (5)", "Web Resource (1)"], session.TypeFiltersView.Cast<TypeFilterOption>().Select(f => f.Label));
        Assert.True(session.SelectedTypeFilter?.IsAll);
        Assert.Equal(5, Shown(session).Count);

        session.TypeFilterSearchText = string.Empty;
        Assert.Equal(4, session.TypeFiltersView.Cast<TypeFilterOption>().Count());
    });

    // ---------------------------------------------------------------- selection

    [Fact]
    public Task Selecting_several_rows_words_the_menu_for_all_of_them() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();
        Assert.Equal("Copy name", session.CopyNameHeader);
        Assert.False(session.CopyAsTableCommand.CanExecute(null));
        Assert.False(session.ExportSelectionCommand.CanExecute(null));

        session.SetSelection([Row(session, "Account"), Row(session, "Contact")]);

        Assert.True(session.HasMultipleSelected);
        Assert.Equal(2, session.Selection.Count);
        Assert.Equal("Copy 2 names", session.CopyNameHeader);
        Assert.Equal("Copy 2 maker portal links", session.CopyLinkHeader);
        Assert.Equal("Copy 2 object ids", session.CopyIdHeader);
        Assert.Equal("2 selected", session.SelectionHeader);
        Assert.Equal("Add to favourites (2)", session.FavouriteHeader);
        Assert.True(session.CopyAsTableCommand.CanExecute(null));
        Assert.True(session.OpenSelectionDetailsCommand.CanExecute(null));
        Assert.True(session.ExportSelectionCommand.CanExecute(null));
        Assert.True(session.CheckSelectionLayersCommand.CanExecute(null));

        session.SetSelection([Row(session, "Account")]);

        Assert.False(session.HasMultipleSelected);
        Assert.Equal("Copy maker portal link", session.CopyLinkHeader);
        Assert.Equal("Copy object id", session.CopyIdHeader);
        Assert.False(session.OpenSelectionDetailsCommand.CanExecute(null));
    });

    [Fact]
    public Task Copying_what_none_of_the_rows_has_says_so_without_touching_the_clipboard() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment { WithOrganizationEnvironmentId = false };
        env.Handler.OnJson(System.Net.Http.HttpMethod.Get, "/api/discovery/v2.0/Instances", "{\"value\":[]}");
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 1, "account", "Account");
        env.AddComponent(solution, 1, "contact", "Contact");
        var session = await env.ConnectedAsync();

        session.SetSelection(session.AllItems);
        session.CopyLinkCommand.Execute(null);

        Assert.Equal("Nothing to copy - none of the selected objects has a maker portal link.", session.Status);

        session.SetSelection([]);
        session.SelectedItem = session.AllItems[0];
        Assert.False(session.OpenLinkCommand.CanExecute(null));
        session.CopyLinkCommand.Execute(null);

        Assert.Equal("Nothing to copy - this object has no maker portal link.", session.Status);

        var unsaved = new SolutionComponentItem { Name = "draft", ComponentTypeName = "Table" };
        session.CopyIdCommand.Execute(unsaved);

        Assert.Equal("Nothing to copy - this object has no object id.", session.Status);
    });

    [Fact]
    public Task A_row_with_a_maker_link_can_be_opened() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();

        session.SelectedItem = Row(session, "Account");

        Assert.NotNull(session.SelectedItem.MakerUrl);
        Assert.True(session.OpenLinkCommand.CanExecute(null));
        Assert.True(session.ShowDetailsCommand.CanExecute(null));
        Assert.True(session.ExploreDependenciesCommand.CanExecute(null));
        Assert.NotEmpty(session.DetailShortcuts);
        Assert.False(session.IsSelectedEnvironmentVariable);
        Assert.Same(Row(session, "Account"), session.FindLoaded(Row(session, "Account").ObjectId));
        Assert.Null(session.FindLoaded(Guid.NewGuid()));
    });

    [Fact]
    public void Nothing_selected_offers_nothing_to_act_on()
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings());

        Assert.Empty(session.DetailShortcuts);
        Assert.False(session.IsSelectedEnvironmentVariable);
        Assert.Equal("Add to favourites", session.FavouriteHeader);
        Assert.False(session.ShowDetailsCommand.CanExecute(null));
        Assert.False(session.ExploreDependenciesCommand.CanExecute(null));
        Assert.False(session.CopyTenantIdCommand.CanExecute(null));
        Assert.False(session.SaveSearchCommand.CanExecute(null));
        Assert.False(session.ToggleFavouriteCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- favourites

    [Fact]
    public Task Starring_rows_marks_them_and_keeps_them_for_the_next_visit() => EnvironmentSessionThread.Run(async () =>
    {
        var (env, session) = await ConnectedAsync();
        var account = Row(session, "Account");

        Assert.True(session.ToggleFavouriteCommand.CanExecute(account));
        session.ToggleFavouriteCommand.Execute(account);

        Assert.True(account.IsFavourite);
        Assert.Equal("Added Account to favourites.", session.Status);

        session.FavouritesOnly = true;
        Assert.Equal(["Account"], Shown(session));
        Assert.True(session.HasActiveFilters);

        session.SetSelection([account, Row(session, "Contact")]);
        session.ToggleFavouriteCommand.Execute(null);

        Assert.Equal("Added 2 objects to favourites.", session.Status);
        Assert.Equal("Remove from favourites (2)", session.FavouriteHeader);
        Assert.Equal(["Account", "Contact"], Shown(session));

        var again = await env.ConnectedAsync();
        Assert.True(again.AllItems.Single(i => i.Name == "contact").IsFavourite);
        Assert.False(again.AllItems.Single(i => i.Name == "new_script.js").IsFavourite);

        session.ToggleFavouriteCommand.Execute(null);
        Assert.Equal("Removed 2 objects from favourites.", session.Status);
        Assert.Empty(Shown(session));

        session.SelectedItem = account;
        session.SetSelection([]);
        session.ToggleFavouriteCommand.Execute(null);
        Assert.Equal("Added Account to favourites.", session.Status);
        session.ToggleFavouriteCommand.Execute(null);
        Assert.Equal("Removed Account from favourites.", session.Status);
    });

    // ---------------------------------------------------------------- saved searches and recent objects

    [Fact]
    public Task A_saved_search_puts_back_its_words_and_filters() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 29, "Notify owner", workflowCategory: 5);
        env.AddComponent(solution, 29, "Notify manager", managed: true, workflowCategory: 5);
        env.AddComponent(solution, 1, "account", "Account");
        UserLibrary.Shared.SaveSearch(env.Url, new SavedSearch
        {
            Name = "Unmanaged flows", SearchText = "notify", Type = "Process", SubType = "Cloud Flow", State = "Unmanaged",
            Layer = "Not checked"
        });
        var session = await env.ConnectedAsync();
        var search = Assert.Single(session.SavedSearches);
        Assert.True(session.HasSavedSearches);
        Assert.True(session.ApplySavedSearchCommand.CanExecute(search));
        Assert.False(session.ApplySavedSearchCommand.CanExecute(null));

        session.ApplySavedSearchCommand.Execute(search);

        Assert.Equal("Notify owner", Assert.Single(session.ItemsView.Cast<SolutionComponentItem>()).Name);
        Assert.Equal("notify", session.SearchText);
        Assert.Equal("Process", session.SelectedTypeFilter?.Name);
        Assert.Equal("Cloud Flow", session.SelectedSubTypeFilter?.Name);
        Assert.Equal("Unmanaged", session.SelectedStateFilter?.Name);
        Assert.Equal("Not checked", session.SelectedLayerFilter?.Name);
        Assert.Equal("Showing the saved search 'Unmanaged flows'.", session.Status);

        Assert.True(session.DeleteSavedSearchCommand.CanExecute(search));
        session.DeleteSavedSearchCommand.Execute(search);

        Assert.Empty(session.SavedSearches);
        Assert.Empty(UserLibrary.Shared.For(env.Url).Searches);
        Assert.Equal("Deleted the saved search 'Unmanaged flows'.", session.Status);
    });

    [Fact]
    public Task A_saved_search_naming_filters_that_are_gone_falls_back_to_all() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();

        session.ApplySavedSearchCommand.Execute(new SavedSearch { Name = "Old", Type = "Plug-in step", FavouritesOnly = true });

        Assert.True(session.SelectedTypeFilter?.IsAll);
        Assert.True(session.FavouritesOnly);
        Assert.Empty(Shown(session));
    });

    [Fact]
    public Task Recent_objects_are_listed_and_one_not_on_show_says_where_to_look() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        env.AddSolution("Default Solution", "Default");
        for (var i = 0; i < 17; i++)
        {
            UserLibrary.Shared.AddRecent(env.Url, new RecentObject { ObjectId = Guid.NewGuid(), Label = $"Object {i}", TypeName = "Table" });
        }

        var session = await env.ConnectedAsync();

        Assert.Equal(15, session.RecentObjects.Count);
        Assert.True(session.HasRecentObjects);
        Assert.Equal("Object 16", session.RecentObjects[0].Label);
        Assert.True(session.OpenRecentCommand.CanExecute(session.RecentObjects[0]));
        Assert.False(session.OpenRecentCommand.CanExecute(null));

        session.OpenRecentCommand.Execute(session.RecentObjects[0]);

        Assert.StartsWith("Object 16 is not in the solution on show", session.Status);
    });

    // ---------------------------------------------------------------- the detail pane and browser profile

    [Fact]
    public Task The_detail_pane_is_shared_and_remembered_in_settings() => EnvironmentSessionThread.Run(() =>
    {
        var settings = new AppSettings { IsDetailPaneOpen = true };
        var session = new FakeEnvironment().Session(settings);

        session.ToggleDetailPaneCommand.Execute(null);

        Assert.False(session.IsDetailPaneOpen);
        Assert.False(settings.IsDetailPaneOpen);

        session.IsDetailPaneOpen = false;
        session.ToggleDetailPaneCommand.Execute(null);
        Assert.True(settings.IsDetailPaneOpen);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Choosing_a_browser_profile_saves_it_for_the_environment() => EnvironmentSessionThread.Run(() =>
    {
        var settings = new AppSettings();
        var env = new FakeEnvironment();
        var session = env.Session(settings);
        Assert.Null(session.BrowserProfile);
        Assert.Equal("the default browser", session.BrowserProfileLabel);
        Assert.Equal(string.Empty, session.BrowserProfileDescription);
        var profile = new BrowserProfileSetting { Browser = BrowserKind.Edge, ProfileDirectory = "Profile 2" };
        var changed = new List<string?>();
        session.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        session.SetBrowserProfileCommand.Execute(BrowserProfileOption.Build([], profile)[1]);

        Assert.Same(profile, settings.GetBrowserProfile(env.Url));
        Assert.Same(profile, session.BrowserProfile);
        Assert.Contains(nameof(EnvironmentSessionViewModel.BrowserProfileLabel), changed);

        session.SetBrowserProfileCommand.Execute(null);
        Assert.Same(profile, session.BrowserProfile);

        session.SetBrowserProfileCommand.Execute(BrowserProfileOption.Build([], profile)[0]);
        Assert.Null(session.BrowserProfile);
        return Task.CompletedTask;
    });
}
