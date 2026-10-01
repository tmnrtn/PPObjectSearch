using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.FlowDesign;

public class FlowDiagramViewModelTests
{
    /// <summary>
    /// Trigger → Init → Try (scope: List → Loop → Condition yes/no) → [Email | Catch (on failure)].
    /// </summary>
    private const string Sample = """
        { "properties": { "definition": {
          "triggers": { "manual": { "type": "Request", "kind": "Button" } },
          "actions": {
            "Init": { "type": "InitializeVariable", "inputs": { "variables": [ { "name": "n", "type": "integer" } ] } },
            "Try": { "type": "Scope", "runAfter": { "Init": ["Succeeded"] }, "actions": {
              "List_rows": { "type": "OpenApiConnection", "inputs": { "host": { "apiId": "/x/shared_commondataserviceforapps", "operationId": "ListRecords" } } },
              "Loop": { "type": "Foreach", "foreach": "@x", "runAfter": { "List_rows": ["Succeeded"] }, "actions": {
                "Check": { "type": "If", "expression": {}, "actions": { "Close_task": { "type": "Compose", "inputs": 1 } },
                           "else": { "actions": { "Skip_it": { "type": "Compose", "inputs": 2 } } } } } } } },
            "Email": { "type": "OpenApiConnection", "runAfter": { "Try": ["Succeeded"] },
                       "inputs": { "host": { "apiId": "/x/shared_office365", "operationId": "SendEmailV2" } } },
            "Catch": { "type": "Terminate", "runAfter": { "Try": ["Failed", "TimedOut", "Skipped"] }, "inputs": { "runStatus": "Failed" } }
          } } } }
        """;

    private static FlowDiagramViewModel Diagram() => new(FlowDesignParser.Parse(Sample));

    private static FlowCardViewModel Card(FlowDiagramViewModel d, string title) => d.Cards.Single(c => c.Title == title);

    [Fact]
    public void The_trigger_comes_first_and_has_no_connector_above_it()
    {
        var d = Diagram();

        var trigger = Assert.IsType<FlowCardViewModel>(d.Steps[0]);
        Assert.Equal(FlowNodeKind.Trigger, trigger.Kind);
        Assert.True(trigger.IsFirst);
        Assert.All(d.Steps.Skip(1), s => Assert.False(s.IsFirst));
    }

    [Fact]
    public void The_first_step_of_every_branch_has_no_connector_above_it()
    {
        var d = Diagram();

        Assert.True(Card(d, "List rows").IsFirst);
        Assert.True(Card(d, "Close task").IsFirst);
        Assert.True(Card(d, "Skip it").IsFirst);
        Assert.False(Card(d, "Loop").IsFirst);
    }

    [Fact]
    public void Every_trigger_and_action_becomes_a_card()
    {
        var d = Diagram();

        Assert.Equal(d.Design.ActionCount + d.Design.Triggers.Count, d.Cards.Count);
    }

    [Fact]
    public void Containers_know_their_parent_and_how_much_they_hold()
    {
        var d = Diagram();

        Assert.Same(Card(d, "Loop"), Card(d, "Check").Parent);
        Assert.Same(Card(d, "Try"), Card(d, "Loop").Parent);
        Assert.Null(Card(d, "Try").Parent);
        Assert.Equal(5, Card(d, "Try").InnerCount);
        Assert.Equal("5 steps inside", Card(d, "Try").CollapsedLabel);
        Assert.Equal("1 step inside", new FlowDiagramViewModel(FlowDesignParser.Parse(
            """{ "triggers": {}, "actions": { "S": { "type": "Scope", "actions": { "A": { "type": "Compose" } } } } }""")).Cards[0].CollapsedLabel);
    }

    [Fact]
    public void A_condition_shows_yes_and_no_coloured_branches()
    {
        var branches = Card(Diagram(), "Check").Branches;

        Assert.Equal(["Yes", "No"], branches.Select(b => b.Label));
        Assert.Equal(["Yes", "No"], branches.Select(b => b.Tone));
        Assert.All(branches, b => Assert.True(b.HasLabel));
    }

    [Fact]
    public void Steps_after_the_same_step_are_a_parallel_group()
    {
        var d = Diagram();

        var parallel = Assert.IsType<FlowParallelViewModel>(d.Steps.Last());
        Assert.Equal("Parallel · 2 branches", parallel.Heading);
        Assert.All(parallel.Branches, b => Assert.False(b.HasLabel));
    }

    [Fact]
    public void Error_handling_paths_carry_a_badge_in_plain_words()
    {
        var d = Diagram();

        Assert.Equal(["Runs if Try failed or timed out or was skipped"], Card(d, "Catch").RunAfterBadges);
        Assert.False(Card(d, "Email").HasRunAfterBadges);
    }

    [Theory]
    [InlineData("Succeeded", "succeeded")]
    [InlineData("Failed", "failed")]
    [InlineData("TimedOut", "timed out")]
    [InlineData("Skipped", "was skipped")]
    [InlineData("Unexpected", "unexpected")]
    public void Statuses_read_as_words(string status, string expected) =>
        Assert.Equal(expected, FlowCardViewModel.Outcome(status));

    [Fact]
    public void Selecting_a_card_marks_it_and_unmarks_the_last()
    {
        var d = Diagram();
        var email = Card(d, "Email");
        var catcher = Card(d, "Catch");

        d.SelectCommand.Execute(email);
        Assert.True(email.IsSelected);
        Assert.True(d.HasSelection);

        d.SelectCommand.Execute(catcher);
        Assert.False(email.IsSelected);
        Assert.True(catcher.IsSelected);
        Assert.Equal("Try: Failed, TimedOut, Skipped", d.SelectedRunAfter);
    }

    [Fact]
    public void The_selected_runs_after_says_so_for_the_trigger_and_first_steps()
    {
        var d = Diagram();

        d.Selected = Card(d, "manual");
        Assert.Equal("The trigger starts the flow.", d.SelectedRunAfter);

        d.Selected = Card(d, "List rows");
        Assert.Equal("Runs first in its container.", d.SelectedRunAfter);
    }

    [Fact]
    public void Search_highlights_matches_and_opens_the_containers_holding_them()
    {
        var d = Diagram();
        d.CollapseAllCommand.Execute(null);
        Assert.False(Card(d, "Try").IsExpanded);

        d.SearchText = "skip";

        Assert.True(Card(d, "Skip it").IsMatch);
        Assert.Equal(1, d.MatchCount);
        Assert.Equal("1 match", d.MatchLabel);
        Assert.True(Card(d, "Check").IsExpanded);
        Assert.True(Card(d, "Loop").IsExpanded);
        Assert.True(Card(d, "Try").IsExpanded);
    }

    [Fact]
    public void Search_matches_connectors_and_every_keyword()
    {
        var d = Diagram();

        d.SearchText = "dataverse list";
        Assert.Equal(["List rows"], d.Cards.Where(c => c.IsMatch).Select(c => c.Title));

        d.SearchText = "outlook";
        Assert.Equal(["Email"], d.Cards.Where(c => c.IsMatch).Select(c => c.Title));
    }

    [Fact]
    public void Clearing_the_search_clears_the_highlights()
    {
        var d = Diagram();
        d.SearchText = "check";

        d.SearchText = "";

        Assert.DoesNotContain(d.Cards, c => c.IsMatch);
        Assert.Equal(string.Empty, d.MatchLabel);
    }

    [Fact]
    public void Expand_and_collapse_all_reach_every_container()
    {
        var d = Diagram();

        d.CollapseAllCommand.Execute(null);
        Assert.All(d.Cards.Where(c => c.IsContainer), c => Assert.False(c.IsExpanded));

        d.ExpandAllCommand.Execute(null);
        Assert.All(d.Cards.Where(c => c.IsContainer), c => Assert.True(c.IsExpanded));
    }

    [Fact]
    public void A_card_toggles_its_own_branches()
    {
        var loop = Card(Diagram(), "Loop");

        loop.ToggleCommand.Execute(null);
        Assert.False(loop.IsExpanded);

        loop.ToggleCommand.Execute(null);
        Assert.True(loop.IsExpanded);
    }

    [Fact]
    public void Zoom_is_clamped_and_reset()
    {
        var d = Diagram();

        for (var i = 0; i < 20; i++) d.ZoomInCommand.Execute(null);
        Assert.Equal(FlowDiagramViewModel.MaxZoom, d.Zoom);

        for (var i = 0; i < 20; i++) d.ZoomOutCommand.Execute(null);
        Assert.Equal(FlowDiagramViewModel.MinZoom, d.Zoom);

        d.ResetZoomCommand.Execute(null);
        Assert.Equal(1, d.Zoom);
        Assert.Equal(1.0.ToString("P0"), d.ZoomLabel);
    }

    [Fact]
    public void Several_triggers_sit_side_by_side()
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse("""
            { "triggers": { "A": { "type": "Request" }, "B": { "type": "Recurrence", "recurrence": { "frequency": "Hour", "interval": 2 } } },
              "actions": { "Do": { "type": "Compose" } } }
            """));

        var triggers = Assert.IsType<FlowParallelViewModel>(d.Steps[0]);
        Assert.Equal(2, triggers.Branches.Count);
        Assert.Equal("Every 2 hours", Card(d, "B").Detail);
        Assert.False(d.Steps[1].IsFirst);
    }

    [Fact]
    public void The_summary_counts_steps_and_warnings()
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse("""
            { "triggers": { "T": { "type": "Request" } }, "actions": { "A": { "type": "Compose", "runAfter": { "Missing": ["Succeeded"] } } } }
            """));

        Assert.Equal("1 step(s) after the trigger · 1 warning(s)", d.Summary);
        Assert.True(d.HasWarnings);
    }

    [Fact]
    public void Each_kind_of_step_has_an_icon()
    {
        var d = Diagram();

        Assert.All(d.Cards, c => Assert.False(string.IsNullOrEmpty(c.Glyph)));
        Assert.NotEqual(Card(d, "Try").Glyph, Card(d, "Loop").Glyph);
        Assert.NotEqual(Card(d, "Email").Glyph, Card(d, "Init").Glyph);
    }
}
