using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>Ctrl+K: every command, every other tab and the objects in the list, by typing.</summary>
public sealed class CommandPaletteViewModelTests : IDisposable
{
    private const string TwoTabs = """
        { "CheckForUpdates": false, "Theme": "Light",
          "Tabs": [ { "EnvironmentUrl": "https://contoso.crm11.dynamics.com" }, { "EnvironmentUrl": "https://fabrikam.crm4.dynamics.com" } ] }
        """;

    private readonly TestShell _test = new(TwoTabs);

    public void Dispose() => _test.Dispose();

    private ShellViewModel Shell => _test.Shell;

    private static string[] Titles(CommandPaletteViewModel palette) => palette.Results.Select(r => r.Title).ToArray();

    /// <summary>The selected tab connected, with two objects listed, the second one selected.</summary>
    private EnvironmentSessionViewModel Connect()
    {
        var session = Shell.SelectedSession!;
        TestSessions.Connect(session, new FakeHttpHandler(),
            [TestSessions.Item("account"), TestSessions.Item("Account sync", 29, "Process")]);
        session.SelectedItem = session.Items[1];
        return session;
    }

    [Fact]
    public void Without_a_connected_tab_it_offers_the_app_commands_and_the_other_tabs()
    {
        var palette = new CommandPaletteViewModel(Shell);

        Assert.Equal(
        [
            "Search all environments", "Compare objects", "Compare data", "Readiness check", "Table settings", "Close all details",
            "New tab", "Ask AI about this screen", "Ask AI settings…", "Theme: light", "Theme: dark", "Theme: follow Windows",
            "Open log folder", "Switch to fabrikam"
        ], Titles(palette));
        Assert.Same(palette.Results[0], palette.Selected);
        Assert.Equal("Ctrl+Shift+F", palette.Results[0].Shortcut);
        Assert.Equal("fabrikam.crm4.dynamics.com", palette.Results[^1].Detail);
    }

    [Fact]
    public void Typing_ranks_the_closest_title_first()
    {
        var palette = new CommandPaletteViewModel(Shell)
        {
            Query = "dark"
        };

        Assert.Equal("Theme: dark", palette.Results[0].Title);
        Assert.Same(palette.Results[0], palette.Selected);
        Assert.False(palette.NothingMatches);
    }

    [Fact]
    public void A_query_that_matches_nothing_says_so()
    {
        var palette = new CommandPaletteViewModel(Shell)
        {
            Query = "zzqqxx"
        };

        Assert.Empty(palette.Results);
        Assert.True(palette.NothingMatches);
        Assert.Equal("Nothing matches 'zzqqxx'", palette.NothingMatchesText);
    }

    [Fact]
    public void The_group_is_searched_too_at_a_lower_rank()
    {
        var palette = new CommandPaletteViewModel(Shell)
        {
            Query = "settings"
        };

        // Table settings and Ask AI settings match by their titles; the rest only by their group.
        Assert.Equal(["Ask AI settings…", "Open log folder", "Table settings", "Theme: dark", "Theme: follow Windows", "Theme: light"],
            Titles(palette).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Running_a_command_closes_the_palette_and_does_the_thing()
    {
        var palette = new CommandPaletteViewModel(Shell)
        {
            Query = "theme dark"
        };

        var ran = palette.Execute();

        Assert.True(ran);
        Assert.Equal(AppTheme.Dark, Shell.Theme);
        Assert.Equal([AppTheme.Dark], _test.AppliedThemes);
    }

    [Fact]
    public void Switching_to_another_tab_selects_it()
    {
        var palette = new CommandPaletteViewModel(Shell)
        {
            Query = "switch"
        };

        Assert.True(palette.Execute());

        Assert.Same(Shell.Sessions[1], Shell.SelectedSession);
    }

    [Fact]
    public void Nothing_runs_without_a_selection_or_when_the_command_cannot_run()
    {
        var palette = new CommandPaletteViewModel(Shell)
        {
            Query = "close all details"
        };

        Assert.Equal("Close all details", palette.Selected!.Title);
        Assert.False(palette.Execute());

        palette.Query = "qqqqzzzz";

        Assert.Empty(palette.Results);
        Assert.Null(palette.Selected);
        Assert.False(palette.Execute());
    }

    [Fact]
    public void Arrow_keys_move_the_selection_within_the_results()
    {
        var palette = new CommandPaletteViewModel(Shell);

        palette.Move(1);
        Assert.Same(palette.Results[1], palette.Selected);

        palette.Move(-5);
        Assert.Same(palette.Results[0], palette.Selected);

        palette.Move(100);
        Assert.Same(palette.Results[^1], palette.Selected);

        palette.Selected = null;
        palette.Move(1);
        Assert.Same(palette.Results[0], palette.Selected);

        palette.Query = "qqqqzzzz";
        palette.Move(1);
        Assert.Null(palette.Selected);
    }

    [Fact]
    public void A_connected_tab_adds_its_own_commands_and_the_selected_object()
    {
        var session = Connect();
        session.RecentObjects.Add(new RecentObject { ObjectId = Guid.NewGuid(), Label = "Contact form", TypeName = "Form" });
        session.SavedSearches.Add(new SavedSearch { Name = "Flows off", SearchText = "flow" });

        var titles = Titles(new CommandPaletteViewModel(Shell));

        Assert.Contains("Refresh", titles);
        Assert.Contains("Solution history", titles);
        Assert.Contains("Allow writes here", titles);
        Assert.DoesNotContain("Stop allowing writes here", titles);
        Assert.Contains("Disconnect", titles);
        Assert.Contains("Dependency explorer…", titles);
        Assert.Contains("Contact form", titles);
        Assert.Contains("Saved search: Flows off", titles);
        Assert.DoesNotContain("account", titles);

        var items = CommandPaletteViewModel.BuildItems(Shell, session);
        Assert.Equal("contoso admin", items.Single(i => i.Title == "Users").Group);
        Assert.Equal("Users", items.Single(i => i.Title == "Users").Parameter);
        Assert.Equal("Account sync", items.Single(i => i.Title == "Dependency explorer…").Detail);

        // A process has an audit history, opened for the object selected.
        var audit = items.Single(i => i.Title == "Audit history");
        Assert.Same(session.AuditHistoryCommand, audit.Command);
        Assert.Same(session.Items[1], audit.Parameter);
    }

    [Fact]
    public void Audit_history_is_offered_for_one_flow_step_or_variable_only()
    {
        var session = Connect();
        Assert.True(session.CanShowAuditHistory);

        // A table has no audit history here.
        session.SelectedItem = session.Items[0];
        Assert.DoesNotContain(CommandPaletteViewModel.BuildItems(Shell, session), i => i.Title == "Audit history");
        Assert.False(session.CanShowAuditHistory);
        Assert.False(session.AuditHistoryCommand.CanExecute(null));

        // The grid menu offers it for one row at a time.
        session.SelectedItem = session.Items[1];
        session.SetSelection(session.Items);
        Assert.False(session.CanShowAuditHistory);
        session.SetSelection([session.Items[1]]);
        Assert.True(session.CanShowAuditHistory);
        Assert.True(session.AuditHistoryCommand.CanExecute(null));
    }

    private const string RowId = "0f6a1c2d-3e4f-4a5b-8c7d-9e0f1a2b3c4d";

    [Fact]
    public void A_table_and_id_typed_in_offer_the_rows_audit_history_first()
    {
        var session = Connect();
        var palette = new CommandPaletteViewModel(Shell) { Query = $"account {RowId}" };

        var audit = palette.Results[0];
        Assert.Same(audit, palette.Selected);
        Assert.Equal($"Audit history of account {RowId}", audit.Title);
        Assert.Equal(session.Title, audit.Group);
        Assert.Equal(new RecordReference("account", Guid.Parse(RowId)), audit.Parameter);
        Assert.True(audit.Command.CanExecute(audit.Parameter));

        Assert.Contains(CommandPaletteViewModel.BuildItems(Shell, session), i => i.Title == "Audit history of a row…" && i.Command == session.AuditRowCommand);
    }

    [Fact]
    public void A_link_is_audited_in_the_tab_connected_to_its_environment_or_says_none_is()
    {
        Connect();
        var link = $"https://fabrikam.crm4.dynamics.com/main.aspx?pagetype=entityrecord&etn=contact&id={RowId}";

        var notConnected = new CommandPaletteViewModel(Shell) { Query = link }.Results[0];
        Assert.Equal("fabrikam.crm4.dynamics.com - no tab is connected to it", notConnected.Detail);
        Assert.False(notConnected.Command.CanExecute(notConnected.Parameter));

        var fabrikam = Shell.Sessions[1];
        TestSessions.Connect(fabrikam, new FakeHttpHandler(), []);

        var audit = new CommandPaletteViewModel(Shell) { Query = link }.Results[0];
        Assert.Equal(fabrikam.Title, audit.Group);
        Assert.Same(fabrikam, EnvironmentSessionViewModel.SessionFor((RecordReference)audit.Parameter!, Shell.SelectedSession, Shell.Sessions));
    }

    [Fact]
    public void A_table_and_id_need_a_connected_tab_and_other_text_is_searched_as_usual()
    {
        Assert.Null(CommandPaletteViewModel.AuditItem($"account {RowId}", null, Shell.Sessions));
        Assert.Null(CommandPaletteViewModel.AuditItem("account", Connect(), Shell.Sessions));
    }

    [Fact]
    public void Objects_in_the_list_are_offered_once_two_letters_are_typed()
    {
        var session = Connect();
        var palette = new CommandPaletteViewModel(Shell)
        {
            Query = "a"
        };
        Assert.DoesNotContain(palette.Results, r => r.Group == "Open");

        palette.Query = "account";

        var open = palette.Results.Where(r => r.Group == "Open").ToList();
        Assert.Equal(["Account sync", "account"], open.Select(r => r.Title).Order(StringComparer.Ordinal));
        Assert.All(open, r => Assert.Same(session.ShowDetailsCommand, r.Command));
        Assert.Contains(open, r => ReferenceEquals(r.Parameter, session.Items[0]) && r.Detail == "Entity");
    }

    [Fact]
    public void A_tab_allowed_to_write_offers_to_stop_it()
    {
        using var test = new TestShell("""
            { "CheckForUpdates": false, "AllowProductionWrites": [ "https://contoso.crm11.dynamics.com" ],
              "Tabs": [ { "EnvironmentUrl": "https://contoso.crm11.dynamics.com" } ] }
            """);
        var session = test.Shell.SelectedSession!;
        TestSessions.Connect(session, new FakeHttpHandler(), []);

        var items = CommandPaletteViewModel.BuildItems(test.Shell, session);

        var revoke = items.Single(i => i.Title == "Stop allowing writes here");
        Assert.Same(session.RevokeWritesCommand, revoke.Command);
        Assert.DoesNotContain(items, i => i.Title.StartsWith("Switch to", StringComparison.Ordinal));
        Assert.DoesNotContain(items, i => i.Title == "Dependency explorer…");
    }

    [Fact]
    public void The_search_text_of_an_item_is_its_title_group_and_detail()
    {
        var item = new PaletteItem { Title = "Users", Group = "contoso admin", Detail = "people", Command = Shell.AddTabCommand };

        Assert.Equal("Users contoso admin people", item.Haystack);
    }
}
