using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The search box's dropdown on a connected tab: saved searches and recent objects, walked and run from the keyboard.</summary>
public class EnvironmentSessionSearchDropdownTests
{
    private static async Task<(FakeEnvironment Env, EnvironmentSessionViewModel Session)> ConnectedAsync(int recent = 7)
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 1, "account", "Account");
        env.AddComponent(solution, 1, "contact", "Contact", managed: true);
        UserLibrary.Shared.SaveSearch(env.Url, new SavedSearch { Name = "Managed", State = "Managed" });
        for (var i = 0; i < recent; i++)
        {
            UserLibrary.Shared.AddRecent(env.Url, new RecentObject { ObjectId = Guid.NewGuid(), Label = $"Recent {i}", TypeName = "Form" });
        }

        return (env, await env.ConnectedAsync());
    }

    [Fact]
    public Task Opening_lists_the_saved_searches_and_the_latest_recent_objects() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();

        session.SearchDropdownOpen = true;

        var saved = Assert.Single(session.SavedSuggestions);
        Assert.Equal("Managed", saved.Title);
        Assert.Equal("Managed", saved.Detail);
        Assert.Equal(SearchSuggestionKind.Saved, saved.Kind);
        Assert.Equal(EnvironmentSessionViewModel.DropdownRecentCount, session.RecentSuggestions.Count);
        Assert.Equal("Recent 6", session.RecentSuggestions[0].Title);
        Assert.Equal("Form", session.RecentSuggestions[0].Detail);
        Assert.True(session.ContentSuggestion.IsHighlighted);
        Assert.True(session.RunSuggestionCommand.CanExecute(null));
    });

    [Fact]
    public Task Enter_runs_the_highlighted_saved_search_and_closes() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();
        session.MoveSuggestion(+1);
        session.MoveSuggestion(+1);
        Assert.True(session.SavedSuggestions[0].IsHighlighted);

        Assert.True(session.RunSuggestion());

        Assert.False(session.SearchDropdownOpen);
        Assert.Equal("Showing the saved search 'Managed'.", session.Status);
        Assert.Equal("contact", Assert.Single(session.ItemsView.Cast<SolutionComponentItem>()).Name);
        Assert.False(session.RunSuggestion());
    });

    [Fact]
    public Task Clicking_a_recent_object_not_on_show_says_where_to_find_it() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();
        session.SearchDropdownOpen = true;

        session.RunSuggestionCommand.Execute(session.RecentSuggestions[0]);

        Assert.False(session.SearchDropdownOpen);
        Assert.StartsWith("Recent 6 is not in the solution on show", session.Status);
    });

    [Fact]
    public Task A_highlighted_row_that_goes_away_hands_the_highlight_back_to_the_first() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync(recent: 0);
        session.MoveSuggestion(+1);
        session.MoveSuggestion(+1);
        var search = session.SavedSearches[0];

        session.DeleteSavedSearchCommand.Execute(search);

        Assert.Empty(session.SavedSuggestions);
        Assert.True(session.ContentSuggestion.IsHighlighted);
        Assert.False(session.HasSavedSearches);
    });

    [Fact]
    public Task Escape_closes_the_dropdown_then_clears_the_search() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();
        session.SearchText = "acc";
        session.SearchDropdownOpen = true;

        session.Escape();

        Assert.False(session.SearchDropdownOpen);
        Assert.Equal("acc", session.SearchText);

        session.Escape();

        Assert.Equal(string.Empty, session.SearchText);
    });

    [Fact]
    public Task Renaming_needs_a_saved_search() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await ConnectedAsync();
        session.SearchDropdownOpen = true;

        Assert.True(session.RenameSavedSearchCommand.CanExecute(session.SavedSearches[0]));
        Assert.False(session.RenameSavedSearchCommand.CanExecute(null));
        session.RenameSavedSearchCommand.Execute(null);

        Assert.True(session.SearchDropdownOpen);
        Assert.Equal("Managed", Assert.Single(session.SavedSearches).Name);
    });

    [Fact]
    public void Searching_inside_definitions_needs_a_connection()
    {
        var session = new FakeEnvironment().Session();
        session.SearchText = "case";

        Assert.False(session.RunSuggestionCommand.CanExecute(null));
        session.RunSuggestionCommand.Execute(session.ContentSuggestion);

        Assert.Equal("Connect first.", session.Status);
    }
}
