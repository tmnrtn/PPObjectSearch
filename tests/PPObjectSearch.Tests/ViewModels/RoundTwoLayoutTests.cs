using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// What the second layout round added behind the views: the detail pane's last-run card, the
/// flow diagram's run strip, code switch and status line, and the Entra sync's account flags and
/// button counts.
/// </summary>
public class RoundTwoLayoutTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 22, 14, 0, TimeSpan.Zero);

    private static ProcessRun Run(string name, RunOutcome outcome, int minutesAgo, string? error = null) => new()
    {
        Name = name, Status = outcome.ToString(), Outcome = outcome, StartTime = T0.AddMinutes(-minutesAgo), ErrorMessage = error
    };

    // ---------------------------------------------------------------- the last-run card

    [Fact]
    public void The_card_shows_the_newest_run_and_how_many_recent_runs_failed()
    {
        var summary = LatestRunSummary.From(
        [
            Run("older", RunOutcome.Succeeded, 30),
            Run("newest", RunOutcome.Failed, 1, "Send_an_email: The mailbox could not be found.\nat line 2"),
            Run("middle", RunOutcome.Failed, 10)
        ])!;

        Assert.Equal("newest", summary.Run.Name);
        Assert.Equal(RunOutcome.Failed, summary.Outcome);
        Assert.Equal("2 of last 3 failed", summary.FailedLabel);
        Assert.Equal("Send_an_email: The mailbox could not be found.", summary.ErrorLine);
    }

    [Fact]
    public void A_flow_that_never_ran_has_no_card()
    {
        Assert.Null(LatestRunSummary.From([]));
    }

    [Fact]
    public void A_single_run_is_described_as_the_only_one()
    {
        Assert.Equal("the only run", LatestRunSummary.From([Run("a", RunOutcome.Succeeded, 1)])!.FailedLabel);
        Assert.Equal("the only run failed", LatestRunSummary.From([Run("a", RunOutcome.Failed, 1)])!.FailedLabel);
        Assert.Null(LatestRunSummary.From([Run("a", RunOutcome.Succeeded, 1)])!.ErrorLine);
    }

    [Fact]
    public async Task The_card_reads_only_the_latest_twenty_runs()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "flowruns", """{"value":[]}""");

        await Fakes.Dataverse(handler).GetCloudFlowRunsAsync(Guid.NewGuid(), top: EnvironmentSessionViewModel.RecentRunCount);

        Assert.Contains("$top=20", handler.Requests[0].Url);
    }

    // ---------------------------------------------------------------- the details window

    private const string Definition = """
        { "properties": { "definition": {
          "triggers": { "When_a_case_is_modified": { "type": "OpenApiConnectionWebhook" } },
          "actions": {
            "Get_owner": { "type": "Compose" },
            "Notify": { "type": "Compose", "runAfter": { "Get_owner": ["Succeeded"] } }
          } } } }
        """;

    private static SolutionComponentItem CloudFlow() => new()
    {
        Name = "Case escalation notifier", ComponentType = 29, ComponentTypeName = "Process", ProcessCategory = 5,
        ObjectId = Guid.NewGuid()
    };

    private static FakeHttpHandler FlowHandler() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "workflows(", JsonSerializer.Serialize(new { clientdata = Definition, statecode = 1 }))
        .OnJson(HttpMethod.Get, "flowruns", JsonSerializer.Serialize(new
        {
            value = new[] { new { name = "08584501234567890123456789CU01", status = "Failed", starttime = "2026-09-30T22:14:00Z" } }
        }))
        .OnJson(HttpMethod.Get, "", """{"value":[]}""");

    [Fact]
    public async Task Showing_a_run_on_the_diagram_moves_to_the_design_tab()
    {
        // No environment id: the run cannot be read, but the window still moves to the diagram to say so.
        var details = new ObjectDetailsViewModel(Fakes.Dataverse(FlowHandler()), CloudFlow(),
            new Dictionary<Guid, SolutionComponentItem>(), openOn: new DetailsShortcut("Run history", DetailsTab.Runs));
        await details.LoadAsync();
        Assert.Equal(DetailsTab.Runs, details.SelectedTab);

        await details.ShowRunOnDiagramCommand.ExecuteAsync(details.Runs[0]);
        await Settle(details.FlowDiagram!);

        Assert.Equal(DetailsTab.Design, details.SelectedTab);
        Assert.True(details.FlowDiagram!.HasRunNotice);
    }

    [Fact]
    public async Task A_loaded_flow_says_whether_it_is_on()
    {
        var details = new ObjectDetailsViewModel(Fakes.Dataverse(FlowHandler()), CloudFlow(), new Dictionary<Guid, SolutionComponentItem>());

        await details.LoadAsync();

        Assert.True(details.IsFlowOn);
        Assert.Equal("On", details.FlowStateLabel);
    }

    [Fact]
    public async Task On_the_design_tab_the_status_bar_is_the_diagrams()
    {
        var details = new ObjectDetailsViewModel(Fakes.Dataverse(FlowHandler()), CloudFlow(), new Dictionary<Guid, SolutionComponentItem>());
        await details.LoadAsync();

        Assert.Equal(details.FlowDiagram!.StatusLine, details.StatusText);

        details.SelectedTab = DetailsTab.Layers;

        Assert.Equal(details.Status, details.StatusText);
    }

    [Fact]
    public void A_row_count_splits_into_the_number_and_its_unit()
    {
        var details = new ObjectDetailsViewModel(Fakes.Dataverse(new FakeHttpHandler()),
            new SolutionComponentItem { Name = "account", ComponentType = 1, ComponentTypeName = "Table" },
            new Dictionary<Guid, SolutionComponentItem>());

        Assert.Equal(string.Empty, details.RowCountValue);
        Assert.Equal(string.Empty, details.RowCountUnit);
    }

    // ---------------------------------------------------------------- the diagram's run strip and code switch

    private static FlowActionResult Result(string name, FlowStepOutcome outcome) =>
        new(name, outcome.ToString(), outcome, T0, T0.AddSeconds(1.8), null,
            outcome == FlowStepOutcome.Failed ? "The mailbox could not be found." : null,
            $"https://x/in/{name}", $"https://x/out/{name}");

    private static FlowRunDetail FailedRun() => new(
        "08584501234567890123456789CU01", "Failed", FlowStepOutcome.Failed, T0, T0.AddSeconds(4.2),
        Result("When_a_case_is_modified", FlowStepOutcome.Succeeded),
        new Dictionary<string, FlowActionResult>
        {
            ["Get_owner"] = Result("Get_owner", FlowStepOutcome.Succeeded),
            ["Notify"] = Result("Notify", FlowStepOutcome.Failed)
        },
        "ActionFailed", "An action failed.", "https://api/run", ViaAdminScope: false);

    private static (FlowDiagramViewModel Diagram, FakeHttpHandler Handler) RunDiagram()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "https://x/in/", """{"to":"someone"}""");
        var diagram = new FlowDiagramViewModel(FlowDesignParser.Parse(Definition), "Case escalation notifier");
        diagram.ShowRun(FailedRun(), new PowerAutomateClient(TestAuth.Tokens(), handler));
        return (diagram, handler);
    }

    private static Task Settle(FlowDiagramViewModel d) => d.Work.WhenIdleAsync();

    [Fact]
    public void The_run_strip_leads_with_the_outcome_then_when_how_long_and_the_trigger()
    {
        var (d, _) = RunDiagram();

        Assert.Equal("Run failed", d.RunOutcomeText);
        Assert.Equal($"{T0:yyyy-MM-dd HH:mm} · 4.2 s · trigger When a case is modified", d.RunStripText);
        Assert.Contains("Run …6789CU01 · Failed", d.RunToolTip);
        Assert.Contains("ActionFailed: An action failed.", d.RunToolTip);
        Assert.True(d.IsRunFailed);
        Assert.False(d.IsRunSucceeded);
    }

    [Fact]
    public void The_status_line_counts_the_steps_of_the_run_on_show()
    {
        var (d, _) = RunDiagram();

        Assert.Equal("Showing run …6789CU01 · 3 step(s), 1 failed, 0 skipped · run details are kept about 28 days", d.StatusLine);

        d.ClearRun();

        Assert.Equal(d.Summary, d.StatusLine);
    }

    [Fact]
    public async Task The_code_switch_shows_inputs_only_when_asked_and_goes_back_to_the_definition()
    {
        var (d, handler) = RunDiagram();
        d.Selected = d.Cards.Single(c => c.Node.Name == "Get_owner");

        Assert.True(d.IsDefinitionShown);
        Assert.True(d.CanShowInputs);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/in/"));

        d.IsInputsShown = true;
        await Settle(d);

        Assert.True(d.IsInputsShown);
        Assert.False(d.IsDefinitionShown);
        Assert.Equal("Inputs", d.DetailCodeTitle);

        d.IsDefinitionShown = true;

        Assert.True(d.IsDefinitionShown);
        Assert.Equal("Definition", d.DetailCodeTitle);
    }

    [Fact]
    public void Without_a_run_there_are_no_inputs_to_show()
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse(Definition), "x");
        d.Selected = d.Cards.Single(c => c.Node.Name == "Get_owner");

        Assert.False(d.CanShowInputs);
        Assert.False(d.CanShowOutputs);

        d.IsInputsShown = true;

        Assert.True(d.IsDefinitionShown);
    }

    [Fact]
    public void The_selected_steps_outcome_is_known_only_in_a_run()
    {
        var (d, _) = RunDiagram();
        d.Selected = d.Cards.Single(c => c.Node.Name == "Notify");

        Assert.Equal(FlowStepOutcome.Failed, d.SelectedRunOutcome);

        d.ClearRun();

        Assert.Null(d.SelectedRunOutcome);
    }

    // ---------------------------------------------------------------- Entra sync

    private static MemberUser DvUser(bool disabled = false) =>
        new(Guid.NewGuid(), "Daniel Okafor", "daniel@contoso.com", null, Guid.NewGuid(), disabled, null, null);

    private static EntraUser Entra(bool enabled = true) => new(Guid.NewGuid().ToString(), "Priya", "priya@contoso.com", null, enabled);

    [Fact]
    public void A_healthy_account_has_no_flags()
    {
        var row = new EntraMatchRowViewModel(new EntraMatchRow(EntraMatchStatus.Both, "object id", DvUser(), Entra()));

        Assert.Empty(row.AccountFlags);
    }

    [Fact]
    public void Disabled_accounts_are_flagged_on_either_side()
    {
        var row = new EntraMatchRowViewModel(new EntraMatchRow(EntraMatchStatus.Both, "object id", DvUser(disabled: true), Entra(enabled: false)));

        Assert.Equal(["Entra disabled", "DV disabled"], row.AccountFlags);
    }

    [Fact]
    public void A_team_member_entra_no_longer_has_is_flagged_once_diagnosed()
    {
        var row = new EntraMatchRowViewModel(new EntraMatchRow(EntraMatchStatus.DataverseOnly, null, DvUser(), null));
        Assert.Empty(row.AccountFlags);

        row.Diagnosis = new Diagnosis(DiagnosisCategory.EntraNotFound, "The account no longer exists in Entra", "object id", null, null);

        Assert.Equal(["Not in Entra"], row.AccountFlags);
    }

    [Fact]
    public void The_row_tooltip_carries_what_the_columns_left_out()
    {
        var entra = Entra();
        var row = new EntraMatchRowViewModel(new EntraMatchRow(EntraMatchStatus.Both, "object id", DvUser(), entra));

        Assert.Contains("Matched on object id", row.RowToolTip);
        Assert.Contains("Entra object id " + entra.Id, row.RowToolTip);
    }

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(150, "2 min ago")]
    [InlineData(7300, "2 h ago")]
    [InlineData(200000, "over a day ago")]
    public void The_comparisons_age_reads_naturally(int seconds, string expected)
    {
        Assert.Equal(expected, EntraTeamSyncViewModel.Ago(TimeSpan.FromSeconds(seconds)));
    }
}
