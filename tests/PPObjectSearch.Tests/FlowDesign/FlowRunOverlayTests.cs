using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.FlowDesign;

/// <summary>A flow run drawn over the flow's design.</summary>
public class FlowRunOverlayTests
{
    private const string Sample = """
        { "properties": { "definition": {
          "triggers": { "manual": { "type": "Request", "kind": "Button" } },
          "actions": {
            "Get_rows": { "type": "Compose" },
            "Loop": { "type": "Foreach", "foreach": "@x", "runAfter": { "Get_rows": ["Succeeded"] }, "actions": {
              "Inner": { "type": "Compose" } } },
            "Check": { "type": "If", "expression": {}, "runAfter": { "Loop": ["Succeeded"] },
                       "actions": { "Yes_step": { "type": "Compose" } },
                       "else": { "actions": { "No_step": { "type": "Compose" } } } },
            "Notify": { "type": "Compose", "runAfter": { "Check": ["Succeeded"] } },
            "Catch": { "type": "Compose", "runAfter": { "Check": ["Failed"] } }
          } } } }
        """;

    private static FlowActionResult Result(string name, FlowStepOutcome outcome, double seconds = 1, string? error = null) =>
        new(name, outcome.ToString(), outcome,
            new DateTimeOffset(2026, 7, 1, 8, 15, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 8, 15, 0, TimeSpan.Zero).AddSeconds(seconds),
            error is null ? null : "BadRequest", error,
            $"https://x/in/{name}", $"https://x/out/{name}");

    /// <summary>
    /// Get_rows and Loop succeed (Inner on its last pass), Check fails in its Yes branch,
    /// Notify is skipped, Catch runs; No_step is never reached.
    /// </summary>
    private static FlowRunDetail Run(string runUrl = "https://api/run") => new(
        "08584501234567890123456789CU01", "Failed", FlowStepOutcome.Failed,
        new DateTimeOffset(2026, 7, 1, 8, 15, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 1, 8, 15, 12, TimeSpan.Zero),
        Result("manual", FlowStepOutcome.Succeeded, 0),
        new Dictionary<string, FlowActionResult>
        {
            ["Get_rows"] = Result("Get_rows", FlowStepOutcome.Succeeded, 0.34),
            ["Loop"] = Result("Loop", FlowStepOutcome.Succeeded, 5),
            ["Inner"] = Result("Inner", FlowStepOutcome.Succeeded, 1.2),
            ["Check"] = Result("Check", FlowStepOutcome.Failed, 2, "Check failed"),
            ["Yes_step"] = Result("Yes_step", FlowStepOutcome.Failed, 2, "Row not found"),
            ["Notify"] = Result("Notify", FlowStepOutcome.Skipped, 0),
            ["Catch"] = Result("Catch", FlowStepOutcome.Succeeded, 0.1),
        },
        "ActionFailed", "An action failed.", runUrl, ViaAdminScope: false);

    /// <summary>A client whose iterations for Inner are: three succeeded, one failed.</summary>
    private static (PowerAutomateClient Client, FakeHttpHandler Handler) Client()
    {
        var reps = Enumerable.Range(0, 4).Select(i => new
        {
            properties = new
            {
                status = i == 2 ? "Failed" : "Succeeded",
                repetitionIndexes = new[] { new { scopeName = "Loop", itemIndex = i } },
                inputsLink = new { uri = $"https://x/in/Inner/{i}" },
                error = i == 2 ? new { code = "BadRequest", message = "Item 3 is bad" } : null
            }
        });

        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/actions/Inner/repetitions", JsonSerializer.Serialize(new { value = reps }))
            .OnJson(HttpMethod.Get, "https://x/in/", """{"value":1}""");

        return (new PowerAutomateClient(TestAuth.Tokens(), handler), handler);
    }

    private static FlowDiagramViewModel Diagram() => new(FlowDesignParser.Parse(Sample), "Sample");

    private static FlowCardViewModel Card(FlowDiagramViewModel d, string title) => d.Cards.Single(c => c.Title == title);

    private static async Task Settle()
    {
        // The overlay counts loops and reads iterations in the background.
        for (var i = 0; i < 20; i++) await Task.Delay(10);
    }

    [Fact]
    public void Without_a_run_cards_show_no_outcome()
    {
        var d = Diagram();

        Assert.False(d.IsRunMode);
        Assert.All(d.Cards, c => Assert.Null(c.RunOutcome));
        Assert.All(d.Cards, c => Assert.False(c.IsDimmed));
    }

    [Fact]
    public async Task Each_card_shows_its_outcome_and_duration()
    {
        var d = Diagram();
        d.ShowRun(Run(), Client().Client);
        await Settle();

        Assert.True(d.IsRunMode);
        Assert.Equal(FlowStepOutcome.Succeeded, Card(d, "manual").RunOutcome);
        Assert.Equal("Succeeded · 340 ms", Card(d, "Get rows").RunBadge);
        Assert.Equal(FlowStepOutcome.Failed, Card(d, "Yes step").RunOutcome);
        Assert.Equal("Failed · 2 s", Card(d, "Yes step").RunBadge);
    }

    [Fact]
    public async Task Skipped_and_unreached_steps_are_dimmed()
    {
        var d = Diagram();
        d.ShowRun(Run(), Client().Client);
        await Settle();

        Assert.Equal(FlowStepOutcome.Skipped, Card(d, "Notify").RunOutcome);
        Assert.Equal("Skipped", Card(d, "Notify").RunBadge);
        Assert.True(Card(d, "Notify").IsDimmed);

        Assert.Equal(FlowStepOutcome.NotRun, Card(d, "No step").RunOutcome);
        Assert.Equal("Did not run", Card(d, "No step").RunLabel);
        Assert.True(Card(d, "No step").IsDimmed);

        Assert.False(Card(d, "Catch").IsDimmed);
    }

    [Fact]
    public async Task The_first_failure_is_selected_and_its_containers_opened()
    {
        var d = Diagram();
        d.CollapseAllCommand.Execute(null);

        d.ShowRun(Run(), Client().Client);
        await Settle();

        // Check fails first in drawing order; Yes step inside it fails too.
        Assert.Same(Card(d, "Check"), d.Selected);
        Assert.True(d.JumpToFailureCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_banner_summarises_the_run()
    {
        var d = Diagram();
        d.ShowRun(Run(), Client().Client);
        await Settle();

        Assert.Equal("Run …6789CU01 · Failed", d.RunHeading);
        Assert.Contains("took 12 s", d.RunDetail);
        Assert.Contains("2 steps failed", d.RunDetail);
        Assert.Equal("ActionFailed: An action failed.", d.RunError);
        Assert.True(d.IsRunFailed);
        Assert.True(d.HasRunBanner);
    }

    [Fact]
    public async Task A_loop_is_labelled_with_its_iterations()
    {
        var d = Diagram();
        d.ShowRun(Run(), Client().Client);
        await Settle();

        Assert.Equal("4 iterations · 1 failed", Card(d, "Loop").IterationLabel);
    }

    [Fact]
    public async Task Selecting_a_step_in_a_loop_lists_its_iterations()
    {
        var d = Diagram();
        d.ShowRun(Run(), Client().Client);

        d.Selected = Card(d, "Inner");
        await Settle();

        Assert.Equal(4, d.SelectedIterations.Count);
        Assert.Contains("4 iteration(s), 1 failed", d.IterationStatus);
        Assert.Contains("last iteration", d.SelectedRunStatus);

        d.SelectedIteration = d.SelectedIterations[2];
        Assert.Equal("BadRequest: Item 3 is bad", d.SelectedError);
    }

    [Fact]
    public async Task Inputs_are_shown_only_when_asked_and_then_the_definition_again()
    {
        var (client, handler) = Client();
        var d = Diagram();
        d.ShowRun(Run(), client);
        await Settle();
        d.Selected = Card(d, "Get rows");

        Assert.Equal("Definition", d.DetailCodeTitle);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/in/Get_rows"));

        d.ShowInputsCommand.Execute(null);
        await Settle();

        Assert.Equal("Inputs", d.DetailCodeTitle);
        Assert.Contains("\"value\": 1", d.DetailCode);
        Assert.True(d.HasContent);

        d.ShowDefinitionCommand.Execute(null);
        Assert.Equal("Definition", d.DetailCodeTitle);
        Assert.Contains("Compose", d.DetailCode);
    }

    [Fact]
    public async Task An_iterations_inputs_are_labelled_with_it()
    {
        var d = Diagram();
        d.ShowRun(Run(), Client().Client);
        d.Selected = Card(d, "Inner");
        await Settle();

        d.SelectedIteration = d.SelectedIterations[1];
        d.ShowInputsCommand.Execute(null);
        await Settle();

        Assert.Equal("Inputs of iteration #2", d.DetailCodeTitle);
    }

    [Fact]
    public async Task The_error_of_the_selected_step_is_shown()
    {
        var d = Diagram();
        d.ShowRun(Run(), Client().Client);
        await Settle();

        d.Selected = Card(d, "Yes step");

        Assert.Equal("BadRequest: Row not found", d.SelectedError);
        Assert.StartsWith("Failed · 2 s", d.SelectedRunStatus);
    }

    [Fact]
    public async Task Back_to_design_clears_the_run()
    {
        var d = Diagram();
        var cleared = false;
        d.RunCleared += (_, _) => cleared = true;
        d.ShowRun(Run(), Client().Client);
        await Settle();

        d.ClearRunCommand.Execute(null);

        Assert.True(cleared);
        Assert.False(d.IsRunMode);
        Assert.All(d.Cards, c => Assert.Null(c.RunOutcome));
        Assert.Null(Card(d, "Loop").IterationLabel);
        Assert.False(d.ClearRunCommand.CanExecute(null));
    }

    [Fact]
    public void A_notice_shows_in_the_banner_until_a_run_arrives()
    {
        var d = Diagram();

        d.SetRunNotice("Power Automate has no record of this run.", isError: true);
        Assert.True(d.HasRunBanner);
        Assert.True(d.IsRunNoticeError);
        Assert.False(d.IsRunMode);

        d.ShowRun(Run(), Client().Client);
        Assert.False(d.HasRunNotice);
        Assert.True(d.HasRunBanner);
    }

    [Fact]
    public void A_step_the_flow_no_longer_has_is_named_not_hidden()
    {
        var d = Diagram();
        var run = Run();
        var actions = new Dictionary<string, FlowActionResult>(run.Actions) { ["Old_name"] = Result("Old_name", FlowStepOutcome.Succeeded) };

        d.ShowRun(run with { Actions = actions }, Client().Client);

        Assert.True(d.HasRunMismatch);
        Assert.Equal("1 step in this run is no longer in the flow (renamed or removed since), so the diagram cannot show it: Old name. " +
                     "The flow has changed since this run, so steps added since show as did not run.", d.RunMismatch);

        d.ClearRun();
        Assert.False(d.HasRunMismatch);
    }

    [Fact]
    public void A_run_that_matches_the_flow_has_no_mismatch()
    {
        var d = Diagram();

        d.ShowRun(Run(), Client().Client);

        Assert.Null(d.RunMismatch);
    }

    [Fact]
    public async Task Search_and_selection_still_work_in_a_run()
    {
        var d = Diagram();
        d.ShowRun(Run(), Client().Client);
        await Settle();

        d.SearchText = "notify";

        Assert.True(Card(d, "Notify").IsMatch);
    }

    [Theory]
    [InlineData("08584501234567890123456789CU01", "…6789CU01")]
    [InlineData("short", "short")]
    public void Run_names_are_shortened_to_their_telling_end(string name, string expected) =>
        Assert.Equal(expected, FlowDiagramViewModel.ShortRunName(name));
}
