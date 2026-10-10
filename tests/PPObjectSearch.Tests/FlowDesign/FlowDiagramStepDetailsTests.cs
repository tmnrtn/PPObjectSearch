using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.FlowDesign;

/// <summary>The flow diagram's cards for every kind of step and outcome, the run banner, and the step pane in a run.</summary>
public class FlowDiagramStepDetailsTests
{
    private const string RunUrl = "https://api/run";

    private static readonly DateTimeOffset Start = new(2026, 7, 1, 8, 15, 0, TimeSpan.Zero);

    private const string OneStep = """
        { "triggers": { "manual": { "type": "Request", "kind": "Button" } },
          "actions": { "Step": { "type": "Compose", "description": "Builds the body" } } }
        """;

    private const string LoopSample = """
        { "triggers": { "manual": { "type": "Request", "kind": "Button" } },
          "actions": {
            "Loop": { "type": "Foreach", "foreach": "@x", "actions": { "Inner": { "type": "Compose" } } },
            "After": { "type": "Compose", "runAfter": { "Loop": ["Succeeded"] } }
          } }
        """;

    private sealed record UnknownStep : IFlowStep;

    private static FlowActionResult Result(string name, FlowStepOutcome outcome, string? status = null, double seconds = 1,
        string? errorCode = null, string? error = null) =>
        new(name, status ?? outcome.ToString(), outcome, Start, Start.AddSeconds(seconds), errorCode, error,
            $"https://x/in/{name}", $"https://x/out/{name}");

    private static FlowRunDetail Run(IReadOnlyDictionary<string, FlowActionResult> actions,
        FlowStepOutcome outcome = FlowStepOutcome.Succeeded, string? errorCode = null, string? error = null,
        bool viaAdmin = false, DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new("08584501234567890123456789CU01", outcome.ToString(), outcome, start, end,
            Result("manual", FlowStepOutcome.Succeeded, seconds: 0), actions, errorCode, error, RunUrl, viaAdmin);

    private static PowerAutomateClient Client(FakeHttpHandler? handler = null) =>
        new(TestAuth.Tokens(), handler ?? new FakeHttpHandler());

    private static FlowDiagramViewModel Diagram(string definition = OneStep) => new(FlowDesignParser.Parse(definition), "Sample");

    private static FlowCardViewModel Card(FlowDiagramViewModel d, string title) => d.Cards.Single(c => c.Title == title);

    private static string Repetitions(int count, string? nextLink = null) => JsonSerializer.Serialize(new
    {
        value = Enumerable.Range(0, count).Select(i => new
        {
            properties = new { status = "Succeeded", repetitionIndexes = new[] { new { scopeName = "Loop", itemIndex = i } } }
        }),
        nextLink
    });

    // ---------------------------------------------------------------- cards

    [Fact]
    public void Every_outcome_has_its_own_glyph_and_words()
    {
        var outcomes = Enum.GetValues<FlowStepOutcome>().Where(o => o != FlowStepOutcome.NotRun).ToList();
        var cards = new List<FlowCardViewModel>();

        foreach (var outcome in outcomes)
        {
            var d = Diagram();
            d.ShowRun(Run(new Dictionary<string, FlowActionResult>
            {
                ["Step"] = Result("Step", outcome, outcome == FlowStepOutcome.Other ? "Paused" : null)
            }), Client());
            cards.Add(Card(d, "Step"));
        }

        Assert.Equal(
            ["Succeeded", "Failed", "Skipped", "Timed out", "Cancelled", "Running", "Waiting", "Paused"],
            cards.Select(c => c.RunLabel));
        Assert.Equal(outcomes.Count, cards.Select(c => c.RunGlyph).Distinct().Count());
        Assert.All(cards, c => Assert.False(string.IsNullOrEmpty(c.RunGlyph)));
    }

    [Theory]
    [InlineData(FlowStepOutcome.Succeeded, "Succeeded")]
    [InlineData(FlowStepOutcome.Failed, "Failed")]
    [InlineData(FlowStepOutcome.TimedOut, "Timed out")]
    [InlineData(FlowStepOutcome.Cancelled, "Cancelled")]
    [InlineData(FlowStepOutcome.Running, "Running")]
    [InlineData(FlowStepOutcome.Waiting, "Waiting")]
    [InlineData(FlowStepOutcome.Other, "Suspended")]
    public void A_runs_status_reads_as_words(FlowStepOutcome outcome, string expected)
    {
        Assert.Equal(expected, FlowDiagramViewModel.RunStatusLabel(outcome, "Suspended"));
    }

    [Fact]
    public void A_card_reads_out_how_its_step_went_once_a_run_is_shown()
    {
        var d = Diagram();
        var step = Card(d, "Step");

        Assert.Equal("Step, Compose", step.AccessibleName);
        Assert.Equal(string.Empty, step.RunLabel);

        d.ShowRun(Run(new Dictionary<string, FlowActionResult> { ["Step"] = Result("Step", FlowStepOutcome.Succeeded, seconds: 2) }), Client());

        Assert.Equal("Step, Compose, Succeeded · 2 s", step.AccessibleName);
    }

    [Fact]
    public void A_step_with_a_note_shows_it()
    {
        var step = Card(Diagram(), "Step");

        Assert.True(step.HasDescription);
        Assert.Equal("Builds the body", step.Description);
        Assert.Equal(FlowNodeCategory.Data, step.Category);
        Assert.False(Card(Diagram(), "manual").HasDescription);
    }

    [Fact]
    public void A_child_flow_calls_tooltip_names_the_flow_and_its_id()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var d = Diagram($$"""
            { "triggers": {}, "actions": { "Escalate": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "{{id}}" } } } } }
            """);
        var card = Card(d, "Escalate");
        var changes = new List<string?>();
        card.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        d.SetChildFlowNames(new Dictionary<Guid, string> { [id] = "Escalate case" });
        d.SetChildFlowNames(new Dictionary<Guid, string> { [id] = "Escalate case" });

        Assert.True(card.HasDetail);
        Assert.Equal($"Escalate case{Environment.NewLine}Child flow {id}", card.DetailTooltip);
        Assert.Equal(1, changes.Count(c => c == nameof(FlowCardViewModel.DetailTooltip)));
    }

    private static readonly string[] OneStepOfEachKind = ["Route", "Call api", "Run child", "Stop", "Pause", "Odd one"];

    [Fact]
    public void Every_kind_of_control_and_built_in_step_has_its_own_icon()
    {
        var d = Diagram("""
            { "triggers": {}, "actions": {
                "Route": { "type": "Switch", "expression": "@x", "cases": {}, "default": { "actions": {} } },
                "Call_api": { "type": "Http", "runAfter": { "Route": ["Succeeded"] } },
                "Run_child": { "type": "Workflow", "runAfter": { "Call_api": ["Succeeded"] } },
                "Stop": { "type": "Terminate", "runAfter": { "Run_child": ["Succeeded"] } },
                "Pause": { "type": "Wait", "runAfter": { "Stop": ["Succeeded"] } },
                "Odd_one": { "type": "SomethingNew", "runAfter": { "Pause": ["Succeeded"] } }
            } }
            """);

        var glyphs = OneStepOfEachKind.Select(t => Card(d, t).Glyph).ToList();

        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
        Assert.Equal(FlowNodeKind.Switch, Card(d, "Route").Kind);
    }

    [Fact]
    public void A_branch_that_is_neither_yes_nor_no_is_neutral()
    {
        var branch = new FlowBranchViewModel("Case: 3", Array.Empty<FlowStepViewModel>());

        Assert.Equal("Neutral", branch.Tone);
        Assert.True(branch.IsEmpty);
        Assert.True(branch.HasLabel);
    }

    [Fact]
    public void A_step_kind_the_diagram_does_not_know_is_refused()
    {
        var design = new Models.FlowDesign([], new FlowSequence([new UnknownStep()]), 1, []);

        var ex = Assert.Throws<InvalidOperationException>(() => new FlowDiagramViewModel(design));

        Assert.Contains(nameof(UnknownStep), ex.Message);
    }

    [Fact]
    public void The_warnings_are_listed_together()
    {
        var d = Diagram("""
            { "triggers": { "T": { "type": "Request" } },
              "actions": { "A": { "type": "Compose", "runAfter": { "Missing": ["Succeeded"] } },
                           "B": { "type": "Compose", "runAfter": { "Gone": ["Succeeded"] } } } }
            """);

        Assert.Equal(2, d.Design.Warnings.Count);
        Assert.Equal(string.Join("  ", d.Design.Warnings), d.Warnings);
    }

    // ---------------------------------------------------------------- the run banner

    [Fact]
    public void The_banner_and_strip_describe_a_run_read_with_admin_access()
    {
        var d = Diagram();
        var run = Run(new Dictionary<string, FlowActionResult>
        {
            ["Step"] = Result("Step", FlowStepOutcome.Failed, error: "Bad input")
        }, FlowStepOutcome.Failed, error: "An action failed.", viaAdmin: true, start: Start, end: Start.AddSeconds(12));

        d.ShowRun(run, Client());

        Assert.Same(run, d.Run);
        Assert.Equal("Started 2026-07-01 08:15:00 · took 12 s · 1 step failed · read with environment admin access", d.RunDetail);
        Assert.Equal("2026-07-01 08:15 · 12 s · trigger manual", d.RunStripText);
        Assert.Equal("Run failed", d.RunOutcomeText);
        Assert.Equal("An action failed.", d.RunError);
        Assert.True(d.HasRunError);
        Assert.False(d.IsRunSucceeded);
        Assert.Equal(string.Join(Environment.NewLine, d.RunHeading, d.RunDetail, d.RunError), d.RunToolTip);
    }

    [Fact]
    public void A_clean_run_has_no_error_and_no_failure_to_jump_to()
    {
        var d = Diagram();

        d.ShowRun(Run(new Dictionary<string, FlowActionResult> { ["Step"] = Result("Step", FlowStepOutcome.Succeeded) }), Client());
        d.JumpToFailureCommand.Execute(null);

        Assert.Null(d.Selected);
        Assert.False(d.HasRunError);
        Assert.True(d.IsRunSucceeded);
        Assert.Equal(string.Empty, d.RunDetail);
        Assert.Equal("trigger manual", d.RunStripText);
        Assert.False(d.JumpToFailureCommand.CanExecute(null));
    }

    [Fact]
    public void Without_a_run_the_banner_is_empty()
    {
        var d = Diagram();

        Assert.Null(d.Run);
        Assert.Equal(string.Empty, d.RunDetail);
        Assert.Equal(string.Empty, d.RunStripText);
        Assert.Equal(string.Empty, d.RunOutcomeText);
        Assert.Equal(string.Empty, d.RunToolTip);
        Assert.Null(d.RunError);
        Assert.Equal(d.Summary, d.StatusLine);
    }

    [Fact]
    public void Several_failed_steps_are_counted_in_the_banner()
    {
        var d = Diagram("""
            { "triggers": { "manual": { "type": "Request" } },
              "actions": { "A": { "type": "Compose" }, "B": { "type": "Compose", "runAfter": { "A": ["Failed"] } } } }
            """);

        d.ShowRun(Run(new Dictionary<string, FlowActionResult>
        {
            ["A"] = Result("A", FlowStepOutcome.Failed),
            ["B"] = Result("B", FlowStepOutcome.TimedOut)
        }, FlowStepOutcome.Failed, errorCode: "ActionFailed"), Client());

        Assert.Equal("2 steps failed", d.RunDetail);
        Assert.Null(d.RunError);
        Assert.Contains("2 failed", d.StatusLine);
    }

    // ---------------------------------------------------------------- the selected step

    [Fact]
    public void A_skipped_step_shows_no_duration_and_a_step_outside_the_run_shows_nothing()
    {
        var d = Diagram();
        d.Selected = Card(d, "Step");

        Assert.Equal(string.Empty, d.SelectedRunStatus);
        Assert.Null(d.SelectedRunOutcome);

        d.ShowRun(Run(new Dictionary<string, FlowActionResult> { ["Step"] = Result("Step", FlowStepOutcome.Skipped, seconds: 3) }), Client());
        d.Selected = Card(d, "Step");

        Assert.Equal("Skipped · started 08:15:00", d.SelectedRunStatus);
        Assert.Equal(FlowStepOutcome.Skipped, d.SelectedRunOutcome);
    }

    [Fact]
    public async Task Outputs_that_cannot_be_read_say_why_and_the_pane_keeps_the_definition()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Get, "https://x/out/Step", HttpStatusCode.NotFound);
        var d = Diagram();
        d.ShowRun(Run(new Dictionary<string, FlowActionResult> { ["Step"] = Result("Step", FlowStepOutcome.Succeeded) }), Client(handler));
        d.Selected = Card(d, "Step");

        d.IsOutputsShown = true;
        await d.Work.WhenIdleAsync();

        Assert.True(d.HasContentError);
        Assert.StartsWith("Could not read the outputs - The link to this content has expired", d.ContentError);
        Assert.True(d.IsDefinitionShown);
        Assert.False(d.IsOutputsShown);
        Assert.False(d.IsContentLoading);
        Assert.Equal("Definition", d.DetailCodeTitle);
    }

    [Fact]
    public async Task Outputs_that_fail_after_inputs_leave_the_inputs_on_show()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "https://x/in/Step", """{"a":1}""")
            .OnStatus(HttpMethod.Get, "https://x/out/Step", HttpStatusCode.InternalServerError);
        var d = Diagram();
        d.ShowRun(Run(new Dictionary<string, FlowActionResult> { ["Step"] = Result("Step", FlowStepOutcome.Succeeded) }), Client(handler));
        d.Selected = Card(d, "Step");
        d.IsInputsShown = true;
        await d.Work.WhenIdleAsync();

        d.IsInputsShown = true;
        d.IsOutputsShown = true;
        await d.Work.WhenIdleAsync();

        Assert.Equal("Inputs", d.DetailCodeTitle);
        Assert.Contains("\"a\": 1", d.DetailCode);
        Assert.Equal("Could not read the outputs - Could not read the content: HTTP 500.", d.ContentError);
        Assert.Equal(1, handler.Requests.Count(r => r.Url.StartsWith("https://x/in/Step", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Outputs_are_shown_when_asked_and_the_definition_again_when_chosen()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "https://x/out/Step", """{"b":2}""");
        var d = Diagram();
        d.ShowRun(Run(new Dictionary<string, FlowActionResult> { ["Step"] = Result("Step", FlowStepOutcome.Succeeded) }), Client(handler));
        d.Selected = Card(d, "Step");

        d.IsDefinitionShown = true;
        d.IsOutputsShown = true;
        await d.Work.WhenIdleAsync();

        Assert.True(d.IsOutputsShown);
        Assert.Equal("Outputs", d.DetailCodeTitle);
        Assert.False(d.HasContentError);

        d.IsDefinitionShown = true;

        Assert.True(d.IsDefinitionShown);
        Assert.False(d.HasContent);
    }

    [Fact]
    public async Task Content_still_arriving_when_the_window_closes_is_dropped()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "https://x/in/Step", _ => slow.Task);
        var d = Diagram();
        d.ShowRun(Run(new Dictionary<string, FlowActionResult> { ["Step"] = Result("Step", FlowStepOutcome.Succeeded) }), Client(handler));
        d.Selected = Card(d, "Step");
        d.IsInputsShown = true;

        d.Detach();
        slow.SetResult(FakeHttpHandler.Json("""{"late":true}"""));
        await d.Work.WhenIdleAsync();

        Assert.False(d.HasContent);
        Assert.False(d.HasContentError);
        Assert.False(d.IsContentLoading);
    }

    [Fact]
    public async Task Content_still_arriving_when_the_diagram_is_disposed_is_dropped()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "https://x/in/Step", _ => slow.Task);
        var d = Diagram();
        d.ShowRun(Run(new Dictionary<string, FlowActionResult> { ["Step"] = Result("Step", FlowStepOutcome.Succeeded) }), Client(handler));
        d.Selected = Card(d, "Step");
        d.IsInputsShown = true;

        d.Dispose();
        slow.SetResult(FakeHttpHandler.Json("""{"late":true}"""));
        await d.Work.WhenIdleAsync();

        Assert.False(d.HasContent);
        Assert.False(d.HasContentError);
        Assert.False(d.IsContentLoading);
        Assert.DoesNotContain("late", d.DetailCode);
    }

    // ---------------------------------------------------------------- loops

    [Fact]
    public async Task A_loop_with_more_iterations_than_can_be_read_says_so()
    {
        var page = 0;
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/actions/Inner/repetitions", _ =>
            FakeHttpHandler.Json(Repetitions(1, $"{RunUrl}/actions/Inner/repetitions?api-version=2016-11-01&page={++page}")));
        var d = Diagram(LoopSample);
        d.ShowRun(Run(new Dictionary<string, FlowActionResult>
        {
            ["Loop"] = Result("Loop", FlowStepOutcome.Succeeded),
            ["Inner"] = Result("Inner", FlowStepOutcome.Succeeded),
            ["After"] = Result("After", FlowStepOutcome.Succeeded)
        }), Client(handler));

        d.Selected = Card(d, "Inner");
        await d.Work.WhenIdleAsync();

        Assert.Equal("50+ iterations", Card(d, "Loop").IterationLabel);
        Assert.True(Card(d, "Loop").HasIterationLabel);
        Assert.Equal("50 iteration(s) - only the first 50 could be read - pick one to see its inputs and outputs.", d.IterationStatus);
        Assert.Equal(50, d.SelectedIterations.Count);
    }

    [Fact]
    public async Task Iterations_that_cannot_be_read_say_why()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "/actions/Inner/repetitions", HttpStatusCode.BadRequest, "throttled");
        var d = Diagram(LoopSample);

        // The loop has no result of its own, so it is not counted; only the selection asks.
        d.ShowRun(Run(new Dictionary<string, FlowActionResult> { ["Inner"] = Result("Inner", FlowStepOutcome.Succeeded) }), Client(handler));
        d.Selected = Card(d, "Inner");
        await d.Work.WhenIdleAsync();

        Assert.StartsWith("Could not read iterations - ", d.IterationStatus);
        Assert.Empty(d.SelectedIterations);
        Assert.False(Card(d, "Loop").HasIterationLabel);
    }

    [Fact]
    public async Task A_loop_with_no_iterations_recorded_says_so()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/actions/Inner/repetitions", """{"value":[]}""");
        var d = Diagram(LoopSample);
        d.ShowRun(Run(new Dictionary<string, FlowActionResult>
        {
            ["Loop"] = Result("Loop", FlowStepOutcome.Failed),
            ["Inner"] = Result("Inner", FlowStepOutcome.Succeeded)
        }, FlowStepOutcome.Failed), Client(handler));

        d.Selected = Card(d, "Inner");
        await d.Work.WhenIdleAsync();

        Assert.Equal("No iterations recorded.", d.IterationStatus);
        Assert.Equal("0 iterations · failed", Card(d, "Loop").IterationLabel);
    }
}
