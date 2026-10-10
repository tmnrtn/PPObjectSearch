using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>"Where is this used?" - searching inside the definitions listed in a tab.</summary>
public class ContentSearchViewModelTests
{
    private static readonly Guid FlowId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid ViewId = Guid.Parse("c0000000-0000-0000-0000-000000000002");

    private static readonly SolutionComponentItem Flow = TestSessions.Item("Notify on new account", 29, "Process", FlowId);
    private static readonly SolutionComponentItem View = TestSessions.Item("Active accounts", 26, "View", ViewId);
    private static readonly SolutionComponentItem Table = TestSessions.Item("account");

    private static FakeHttpHandler Handler() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "workflows?", $$"""{"value":[{"workflowid":"{{FlowId}}","clientdata":"{\"x\":\"new_status\"}","xaml":null}]}""")
        .OnJson(HttpMethod.Get, "savedqueries?", $$"""
            {"value":[{"savedqueryid":"{{ViewId}}","fetchxml":"<fetch><attribute name=\"new_status\" /></fetch>","layoutxml":null}]}
            """);

    private static ContentSearchViewModel Search(FakeHttpHandler handler, IReadOnlyList<SolutionComponentItem>? scope = null,
        string? label = "Contoso Core", string? term = null, EnvironmentSessionViewModel? session = null) =>
        new(session ?? TestSessions.Disconnected(), Fakes.Dataverse(handler), new DefinitionBodyCache(),
            scope ?? [Flow, View, Table], label, term);

    [Fact]
    public void The_heading_counts_only_what_has_a_definition()
    {
        var search = Search(Handler());

        Assert.Equal("Search inside definitions — contoso", search.Title);
        Assert.Equal("Across 2 definitions in Contoso Core", search.ScopeLine);
        Assert.Equal("Search the definitions", search.EmptyHeading);
        Assert.StartsWith("Type a column, table", search.EmptyText);
        Assert.Equal(string.Empty, search.MatchCountLabel);
        Assert.Equal(string.Empty, search.StatusLine);
        Assert.False(search.HasHits);
        Assert.Equal("contoso", search.Session.Title);
    }

    [Fact]
    public void A_scope_with_nothing_searchable_says_so()
    {
        var search = Search(Handler(), scope: [Table], label: null);

        Assert.Equal("the loaded list", search.ScopeLabel);
        Assert.Equal("Across 0 definitions in the loaded list", search.ScopeLine);
        Assert.Equal("Nothing to search", search.EmptyHeading);
        Assert.StartsWith("Nothing in the loaded list has a definition to search", search.EmptyText);
    }

    [Fact]
    public void One_definition_is_singular()
    {
        Assert.Equal("Across 1 definition in Contoso Core", Search(Handler(), scope: [View]).ScopeLine);
    }

    [Fact]
    public void A_term_needs_two_characters_before_it_can_be_searched()
    {
        var search = Search(Handler(), term: "n");
        var raised = 0;
        search.SearchCommand.CanExecuteChanged += (_, _) => raised++;

        Assert.False(search.SearchCommand.CanExecute(null));

        search.Term = " ne ";

        Assert.True(search.SearchCommand.CanExecute(null));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Too_short_a_term_reads_nothing()
    {
        var handler = Handler();
        var search = Search(handler, term: "x");

        await search.SearchAsync();

        Assert.Empty(handler.Requests);
        Assert.Equal("Search the definitions", search.EmptyHeading);
    }

    [Fact]
    public async Task Matches_are_listed_with_what_was_searched()
    {
        var search = Search(Handler(), term: "new_status");
        var statuses = new List<string>();
        search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContentSearchViewModel.Status)) statuses.Add(search.Status);
        };

        await search.SearchAsync();

        Assert.Equal(2, search.Hits.Count);
        Assert.True(search.HasHits);
        Assert.Equal("2 matches", search.MatchCountLabel);
        Assert.Equal("Reading 2 definition(s)...", statuses[0]);
        Assert.Equal(string.Empty, search.Status);
        Assert.StartsWith("Searched 2 definitions in ", search.ReadSummary);
        Assert.EndsWith(", found in 2 components - double-click a match to open it", search.ReadSummary);
        Assert.Equal(search.ReadSummary, search.StatusLine);
        Assert.False(search.IsSearching);
    }

    [Fact]
    public async Task Searching_again_uses_the_definitions_already_read()
    {
        var handler = Handler();
        var search = Search(handler, scope: [View], term: "new_status");
        await search.SearchAsync();
        var requests = handler.Requests.Count;

        search.Term = "attribute";
        await search.SearchAsync();

        Assert.Equal(requests, handler.Requests.Count);
        Assert.Equal("1 match", search.MatchCountLabel);
        Assert.EndsWith(", found in 1 component - double-click a match to open it", search.ReadSummary);
    }

    [Fact]
    public async Task No_matches_explains_itself_in_the_list()
    {
        var search = Search(Handler(), term: "nowhere");

        await search.SearchAsync();

        Assert.Empty(search.Hits);
        Assert.Equal("0 matches", search.MatchCountLabel);
        Assert.Equal("No matches", search.EmptyHeading);
        Assert.Equal("'nowhere' does not appear in any of the 2 definition(s) in Contoso Core. Try a shorter or different term.", search.EmptyText);
        Assert.DoesNotContain("found in", search.ReadSummary);
    }

    [Fact]
    public async Task A_failed_read_is_reported()
    {
        var search = Search(new FakeHttpHandler(), term: "new_status");

        await search.SearchAsync();

        Assert.StartsWith("Could not search - ", search.Status);
        Assert.Equal(search.Status, search.StatusLine);
        Assert.False(search.IsSearching);
    }

    [Fact]
    public async Task Stopping_keeps_what_was_read()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "", async _ =>
        {
            await release.Task;
            return FakeHttpHandler.Json("""{"value":[]}""");
        });
        var search = Search(handler, term: "new_status");

        var running = search.SearchCommand.ExecuteAsync(null);

        Assert.True(search.IsSearching);
        Assert.True(search.CancelCommand.CanExecute(null));
        Assert.False(search.SearchCommand.CanExecute(null));

        search.CancelCommand.Execute(null);
        release.SetResult();
        await running;

        Assert.Equal("Stopped. Definitions read so far are kept for the next search.", search.Status);
        Assert.False(search.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Closing_the_window_stops_a_search_still_reading()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "workflows?", async _ =>
        {
            await release.Task;
            return FakeHttpHandler.Json($$"""{"value":[{"workflowid":"{{FlowId}}","clientdata":"{\"x\":\"new_status\"}","xaml":null}]}""");
        });
        var search = Search(handler, scope: [Flow], term: "new_status");
        var running = search.SearchCommand.ExecuteAsync(null);

        search.Dispose();
        release.SetResult();
        await running;

        Assert.Equal("Stopped. Definitions read so far are kept for the next search.", search.Status);
        Assert.Empty(search.Hits);
        Assert.False(search.IsSearching);
        Assert.False(search.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_hit_can_be_opened_once_one_is_chosen()
    {
        var session = TestSessions.Disconnected();
        var search = Search(Handler(), term: "new_status", session: session);
        await search.SearchAsync();

        Assert.False(search.OpenHitCommand.CanExecute(null));
        Assert.True(search.OpenHitCommand.CanExecute(search.Hits[0]));

        search.SelectedHit = search.Hits[1];
        Assert.True(search.OpenHitCommand.CanExecute(null));

        // A tab that is not connected has nothing to open the details from, so nothing happens.
        search.OpenHitCommand.Execute(null);
        search.OpenHitCommand.Execute(search.Hits[0]);
        Assert.Empty(session.RecentObjects);
    }

    [Fact]
    public void The_summary_reads_in_seconds()
    {
        Assert.Equal($"Searched 1 definition in {1.5:0.0} s", ContentSearchViewModel.Describe(1, TimeSpan.FromSeconds(1.5)));
        Assert.Equal($"Searched 3 definitions in {0.2:0.0} s", ContentSearchViewModel.Describe(3, TimeSpan.FromMilliseconds(200)));
    }
}
