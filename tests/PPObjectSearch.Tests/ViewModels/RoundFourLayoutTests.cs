using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// What the fourth layout round added behind the views: the search box's dropdown, the detail
/// pane's Change group, and the failures window's summary band and component detail.
/// </summary>
public class RoundFourLayoutTests
{
    private static EnvironmentSessionViewModel Session() =>
        new(new AuthenticationService(), new AppSettings(), new TabState { EnvironmentUrl = "https://contoso.crm11.dynamics.com" });

    private static SolutionComponentItem Item(int type, int? category = null) => new()
    {
        Name = "thing", ComponentType = type, ComponentTypeName = "Type", ObjectId = Guid.NewGuid(),
        ProcessCategory = category, WorkflowIdUnique = Guid.NewGuid()
    };

    // ---------------------------------------------------------------- the search dropdown

    [Fact]
    public void The_dropdown_leads_with_searching_inside_definitions_for_the_words_typed()
    {
        var session = Session();

        Assert.Equal("Search inside definitions…", session.SearchInsideLabel);

        session.SearchText = "  case ";

        Assert.Equal("Search inside definitions for ", session.SearchInsideLabel);
        Assert.Equal("case", session.SearchInsideTerm);

        session.SearchDropdownOpen = true;
        Assert.True(session.ContentSuggestion.IsHighlighted);
    }

    [Fact]
    public void Arrows_walk_the_rows_and_stop_at_either_end()
    {
        var session = Session();
        session.SavedSearches.Add(new SavedSearch { Name = "Unmanaged flows", Type = "Process", State = "Unmanaged" });
        session.RecentObjects.Add(new RecentObject { ObjectId = Guid.NewGuid(), Label = "Case", TypeName = "Table" });

        // The first ↓ only opens it.
        session.MoveSuggestion(+1);
        Assert.True(session.SearchDropdownOpen);
        Assert.True(session.ContentSuggestion.IsHighlighted);

        session.MoveSuggestion(+1);
        Assert.True(Assert.Single(session.SavedSuggestions).IsHighlighted);
        Assert.False(session.ContentSuggestion.IsHighlighted);

        session.MoveSuggestion(+5);
        Assert.True(Assert.Single(session.RecentSuggestions).IsHighlighted);

        session.MoveSuggestion(-9);
        Assert.True(session.ContentSuggestion.IsHighlighted);
    }

    [Fact]
    public void Only_the_five_latest_objects_are_listed()
    {
        var session = Session();
        for (var i = 0; i < 8; i++) session.RecentObjects.Add(new RecentObject { ObjectId = Guid.NewGuid(), Label = $"Object {i}" });

        session.SearchDropdownOpen = true;

        Assert.Equal(["Object 0", "Object 1", "Object 2", "Object 3", "Object 4"], session.RecentSuggestions.Select(r => r.Title));
    }

    [Fact]
    public void Enter_on_a_saved_search_applies_it_and_closes()
    {
        var session = Session();
        session.SavedSearches.Add(new SavedSearch { Name = "Case plug-ins", SearchText = "case", FavouritesOnly = true });

        session.MoveSuggestion(+1);
        session.MoveSuggestion(+1);

        Assert.True(session.RunSuggestion());
        Assert.False(session.SearchDropdownOpen);
        Assert.Equal("case", session.SearchText);
        Assert.True(session.FavouritesOnly);
    }

    [Fact]
    public void Enter_with_the_dropdown_shut_is_left_to_the_box()
    {
        Assert.False(Session().RunSuggestion());
    }

    [Fact]
    public void Escape_closes_the_dropdown_then_clears_the_search()
    {
        var session = Session();
        session.SearchText = "case";
        session.SearchDropdownOpen = true;

        session.Escape();
        Assert.False(session.SearchDropdownOpen);
        Assert.Equal("case", session.SearchText);

        session.Escape();
        Assert.Equal(string.Empty, session.SearchText);
    }

    [Theory]
    [InlineData("case", null, "Plug-in step", null, false, "“case” · Plug-in step")]
    [InlineData("", "Process", null, "Unmanaged", false, "Process · Unmanaged")]
    [InlineData("  ", null, null, null, true, "Favourites")]
    [InlineData("", null, null, null, false, "Everything")]
    public void A_saved_search_is_summed_up_in_a_line(string text, string? type, string? subType, string? state, bool favourites, string expected)
    {
        var search = new SavedSearch { Name = "x", SearchText = text, Type = type, SubType = subType, State = state, FavouritesOnly = favourites };

        Assert.Equal(expected, EnvironmentSessionViewModel.Summary(search));
    }

    // ---------------------------------------------------------------- the detail pane's Change group

    [Fact]
    public void The_change_group_shows_only_for_what_can_be_switched()
    {
        var session = Session();

        session.SetSelection([Item(1)]);
        Assert.False(session.HasQuickActions);
        Assert.False(session.ShowSwitchOn);
        Assert.False(session.ShowSwitchOff);

        session.SetSelection([Item(29, 5)]);
        Assert.True(session.HasQuickActions);
    }

    [Fact]
    public void A_switch_says_what_it_does_where_and_that_it_asks_first()
    {
        var session = Session();
        var flow = Item(29, 5);
        session.SelectedItem = flow;
        session.SetSelection([flow]);

        Assert.Equal("Turn off…", session.SwitchOffLabel);
        Assert.Equal("Turns the flow off in contoso. Asks first; recorded in the run log.", session.SwitchOffToolTip);

        // Until its state is read, both are offered.
        Assert.True(session.ShowSwitchOn);
        Assert.True(session.ShowSwitchOff);
    }

    [Fact]
    public void Several_selected_are_switched_together()
    {
        var session = Session();
        session.SetSelection([Item(29, 5), Item(92)]);

        Assert.Equal("Turn on 2 selected…", session.SwitchOnLabel);
        Assert.StartsWith("Turns on the 2 selected in ", session.SwitchOnToolTip);
        Assert.True(session.ShowSwitchOn);
        Assert.True(session.ShowSwitchOff);
    }

    [Theory]
    [InlineData(SwitchableKind.CloudFlow, false, "Turns the flow off")]
    [InlineData(SwitchableKind.Process, true, "Activates the process")]
    [InlineData(SwitchableKind.PluginStep, false, "Disables the plug-in step")]
    public void Each_kind_names_its_own_switch(SwitchableKind kind, bool on, string expected)
    {
        Assert.Equal(expected, EnvironmentSessionViewModel.Describe(kind, on));
    }

    // ---------------------------------------------------------------- the failures summary band

    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    private static FailureEvent Failure(FailureSource source, string component, int hoursIn, string error = "Boom", string? code = null) => new()
    {
        Source = source, ComponentKey = component, ComponentName = component, When = Monday.AddHours(hoursIn),
        ErrorMessage = error, ErrorCode = code
    };

    [Fact]
    public void A_week_of_days_is_a_bar_each_the_tallest_full_height_and_empty_days_faint()
    {
        var buckets = Enumerable.Range(0, 7)
            .Select(d => new FailureBucket(Monday.AddDays(d), $"Day {d}", d switch { 5 => 22, 3 => 0, _ => 11 }, 0))
            .ToList();

        var bars = FailureOverview.Bars(buckets);

        Assert.Equal(7, bars.Count);
        Assert.All(bars, b => Assert.Equal(18, b.Width));
        Assert.Equal(FailureOverview.TrendHeight, bars[5].Height);
        Assert.Equal(15, bars[0].Height);
        Assert.Equal(2, bars[3].Height);
        Assert.Equal(0.25, bars[3].Opacity);
        Assert.Equal(1, bars[0].Opacity);
        Assert.Equal("Day 5: 22 failures", bars[5].ToolTip);
        Assert.All(bars, b => Assert.Equal(1, b.Short.Length));
    }

    [Fact]
    public void A_month_of_days_narrows_the_bars_and_drops_their_letters()
    {
        var buckets = Enumerable.Range(0, 31).Select(d => new FailureBucket(Monday.AddDays(d), "d", 1, 0)).ToList();

        var bars = FailureOverview.Bars(buckets);

        Assert.All(bars, b => Assert.True(b.Width < 18));
        Assert.All(bars, b => Assert.Equal(string.Empty, b.Short));
    }

    [Fact]
    public void The_source_pills_narrow_a_read_without_losing_what_was_read()
    {
        var data = new FailureData { FlowRunsRead = 12, SystemJobsRead = 3, TraceLogsRead = 0 };
        data.Events.Add(Failure(FailureSource.CloudFlow, "Flow", 1));
        data.Events.Add(Failure(FailureSource.ClassicWorkflow, "Job", 2));
        data.Notes.Add("Plug-in tracing is off");
        data.RunCounts["Flow"] = 12;

        var flowsOnly = FailureOverview.Only(data, new HashSet<FailureSource> { FailureSource.CloudFlow });

        Assert.Equal("Flow", Assert.Single(flowsOnly.Events).ComponentName);
        Assert.Equal(12, flowsOnly.RunCounts["Flow"]);
        Assert.Equal(3, flowsOnly.SystemJobsRead);
        Assert.Single(flowsOnly.Notes);
    }

    [Fact]
    public void The_status_bar_says_how_much_was_read_and_how_long_it_took()
    {
        var data = new FailureData { FlowRunsRead = 1284, SystemJobsRead = 96, TraceLogsRead = 1 };

        Assert.Equal("Read 1,284 flow runs, 96 system jobs and 1 trace log in 6.1 s",
            FailuresViewModel.Describe(data, TimeSpan.FromSeconds(6.08)));
    }

    [Theory]
    [InlineData(FailureSource.CloudFlow, "ActionFailed", null, "ActionFailed")]
    [InlineData(FailureSource.CloudFlow, null, null, "Flow run")]
    [InlineData(FailureSource.Plugin, null, "Update of account", "Update of account")]
    [InlineData(FailureSource.ClassicWorkflow, "-2147220891", null, "System job")]
    public void A_failure_card_is_titled_with_where_it_failed(FailureSource source, string? code, string? context, string expected)
    {
        var failure = new FailureEvent
        {
            Source = source, ComponentKey = "k", ComponentName = "n", When = Monday, ErrorCode = code, Context = context
        };

        Assert.Equal(expected, failure.StepLabel);
    }

    [Fact]
    public void One_components_failures_are_grouped_by_error_most_first()
    {
        var events = new[]
        {
            Failure(FailureSource.CloudFlow, "Flow", 3, "Mailbox 'a@b.com' not found"),
            Failure(FailureSource.CloudFlow, "Flow", 2, "Owner not found"),
            Failure(FailureSource.CloudFlow, "Flow", 1, "Mailbox 'c@d.com' not found")
        };

        var groups = FailureOverview.ByError(events);

        Assert.Equal([2, 1], groups.Select(g => g.Failures));
    }
}
