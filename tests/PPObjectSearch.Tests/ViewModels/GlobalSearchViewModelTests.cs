using System.ComponentModel;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>One keyword across every connected tab: the rows, the "In" pills with their counts, and the name groups.</summary>
public class GlobalSearchViewModelTests
{
    private const string FlowUrl = "https://make.powerapps.com/flow";

    private static EnvironmentSessionViewModel Contoso() => TestSessions.Connected(new FakeHttpHandler(),
        [
            TestSessions.Item("contact"),
            TestSessions.Item("Account sync", 29, "Process", makerUrl: FlowUrl),
            TestSessions.Item("account")
        ],
        "https://contoso.crm11.dynamics.com", TestSessions.Solution("Contoso Core"));

    private static EnvironmentSessionViewModel Fabrikam() => TestSessions.Connected(new FakeHttpHandler(),
        [TestSessions.Item("invoice"), TestSessions.Item("account")],
        "https://fabrikam.crm4.dynamics.com", TestSessions.Solution("Fabrikam Sales"));

    private static GlobalSearchViewModel Search(params EnvironmentSessionViewModel[] sessions) => new(sessions);

    private static GlobalSearchViewModel Search() =>
        Search(Contoso(), Fabrikam(), TestSessions.Disconnected("https://northwind.crm.dynamics.com"));

    private static string[] Shown(GlobalSearchViewModel search) =>
        search.RowsView.Cast<GlobalSearchRow>().Select(r => $"{r.Item.Name}@{r.Environment}").ToArray();

    /// <summary>
    /// Types into the box, then applies it the way a pill does - the box itself waits for a pause
    /// in typing, on a timer that only runs under a message loop.
    /// </summary>
    private static void Type(GlobalSearchViewModel search, string text)
    {
        search.SearchText = text;
        search.SelectScopeCommand.Execute(search.Scopes[1]);
        search.SelectScopeCommand.Execute(search.Scopes[0]);
    }

    [Fact]
    public void Every_connected_tab_is_listed_by_name_then_tab_order()
    {
        var search = Search();

        Assert.Equal(
            ["account@contoso", "account@fabrikam", "Account sync@contoso", "contact@contoso", "invoice@fabrikam"],
            Shown(search));
        Assert.Equal(2, search.EnvironmentCount);
        Assert.Equal("5 objects across 2 environment(s)", search.Summary);

        var first = search.RowsView.Cast<GlobalSearchRow>().First();
        Assert.Equal("Contoso Core", first.Solution);
        Assert.Equal(0, first.SessionOrder);
        Assert.Equal(EnvironmentSku.Unknown, first.EnvironmentSku);
    }

    [Fact]
    public void The_last_row_of_each_name_closes_its_group()
    {
        var search = Search();

        Assert.Equal([false, true, true, true, false], search.RowsView.Cast<GlobalSearchRow>().Select(r => r.IsGroupEnd));
    }

    [Fact]
    public void Sorting_by_another_column_drops_the_groups_and_sorting_by_name_brings_them_back()
    {
        var search = Search();

        search.RowsView.SortDescriptions.Add(new SortDescription(nameof(GlobalSearchRow.Environment), ListSortDirection.Ascending));
        Assert.All(search.RowsView.Cast<GlobalSearchRow>(), r => Assert.False(r.IsGroupEnd));

        search.RowsView.SortDescriptions.Clear();
        search.RowsView.SortDescriptions.Add(new SortDescription("Item.PrimaryLabel", ListSortDirection.Ascending));
        Assert.Contains(search.RowsView.Cast<GlobalSearchRow>(), r => r.IsGroupEnd);
    }

    [Fact]
    public void The_pills_are_all_then_one_per_tab_and_a_disconnected_tab_is_named_as_skipped()
    {
        var search = Search();

        Assert.Equal(["All", "contoso", "fabrikam", "northwind"], search.Scopes.Select(s => s.Label));
        Assert.Equal(["5", "3", "2", "—"], search.Scopes.Select(s => s.CountLabel));
        Assert.True(search.Scopes[0].IsAll);
        Assert.True(search.Scopes[0].IsSelected);
        Assert.Same(search.Scopes[0], search.SelectedScope);
        Assert.Equal(EnvironmentSku.Unknown, search.Scopes[0].EnvironmentSku);
        Assert.Equal(EnvironmentSku.Unknown, search.Scopes[1].EnvironmentSku);
        Assert.Equal("northwind is not connected and was skipped.", search.SkippedMessage);
    }

    [Fact]
    public void Several_disconnected_tabs_are_named_together()
    {
        var search = Search(TestSessions.Disconnected("https://a.crm.dynamics.com"), TestSessions.Disconnected("https://b.crm.dynamics.com"));

        Assert.Equal("a, b are not connected and were skipped.", search.SkippedMessage);
        Assert.Empty(search.Rows);
        Assert.Equal("0 objects across 0 environment(s)", search.Summary);
        Assert.False(search.ExportCommand.CanExecute(null));
    }

    [Fact]
    public void With_every_tab_connected_nothing_is_skipped()
    {
        Assert.Equal(string.Empty, Search(Contoso()).SkippedMessage);
    }

    [Fact]
    public void Typing_narrows_the_rows_and_recounts_every_pill()
    {
        var search = Search();

        Type(search, "ACCOUNT");

        Assert.Equal(["account@contoso", "account@fabrikam", "Account sync@contoso"], Shown(search));
        Assert.Equal("3 matches across 2 of 2 environment(s)", search.Summary);
        Assert.Equal([3, 2, 1, 0], search.Scopes.Select(s => s.Count));
        Assert.Equal([false, true, false], search.RowsView.Cast<GlobalSearchRow>().Select(r => r.IsGroupEnd));
    }

    [Fact]
    public void Typing_waits_for_a_pause_before_filtering()
    {
        var search = Search();

        search.SearchText = "invoice";
        search.SearchText = "invoice";

        Assert.Equal("invoice", search.SearchText);
        Assert.Equal(5, Shown(search).Length);
        Assert.Equal("5 objects across 2 environment(s)", search.Summary);
    }

    [Fact]
    public void Every_term_must_match_and_an_environment_name_counts_as_a_match()
    {
        var search = Search();

        Type(search, "account fabrikam");

        Assert.Equal(["account@fabrikam"], Shown(search));
        Assert.Equal("1 matches across 1 of 2 environment(s)", search.Summary);
    }

    [Fact]
    public void A_pill_narrows_to_its_tab_and_a_disconnected_one_cannot_be_picked()
    {
        var search = Search();
        var fabrikam = search.Scopes[2];

        search.SelectScopeCommand.Execute(fabrikam);

        Assert.Same(fabrikam, search.SelectedScope);
        Assert.Equal([false, false, true, false], search.Scopes.Select(s => s.IsSelected));
        Assert.Equal(["account@fabrikam", "invoice@fabrikam"], Shown(search));
        Assert.Equal("2 matches across 1 of 2 environment(s)", search.Summary);

        search.SelectScopeCommand.Execute(search.Scopes[3]);
        search.SelectScopeCommand.Execute("not a scope");

        Assert.Same(fabrikam, search.SelectedScope);
    }

    [Fact]
    public void Nothing_found_leaves_nothing_to_export()
    {
        var search = Search();
        Assert.True(search.ExportCommand.CanExecute(null));

        Type(search, "nowhere");

        Assert.Empty(Shown(search));
        Assert.False(search.ExportCommand.CanExecute(null));
    }

    [Fact]
    public void Only_a_row_with_a_maker_link_can_be_opened()
    {
        var search = Search();
        var rows = search.RowsView.Cast<GlobalSearchRow>().ToList();

        Assert.True(search.OpenLinkCommand.CanExecute(rows.Single(r => r.Item.Name == "Account sync")));
        Assert.False(search.OpenLinkCommand.CanExecute(rows[0]));
        Assert.False(search.OpenLinkCommand.CanExecute(null));
    }

    [Fact]
    public void A_pill_count_says_when_it_changes()
    {
        var scope = new SearchScope { Label = "contoso" };
        var changed = new List<string?>();
        scope.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        scope.Count = 1200;
        scope.Count = 1200;

        Assert.Equal([nameof(SearchScope.Count), nameof(SearchScope.CountLabel)], changed);
        Assert.Equal(1200.ToString("N0"), scope.CountLabel);
    }
}
