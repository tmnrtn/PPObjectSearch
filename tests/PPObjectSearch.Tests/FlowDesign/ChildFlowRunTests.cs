using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.FlowDesign;

/// <summary>A "Run a child flow" step in a run opens the run it started, read from its outputs' headers.</summary>
public class ChildFlowRunTests
{
    private static readonly Guid Child = Guid.Parse("80db2afb-4514-ef11-9f8a-6045bdf21a10");
    private const string ChildRun = "08584078564804724058289653285CU19";
    private const string ChildFlowName = "dfa2d439-6870-3af2-8f23-cd66207fb747";
    private const string RunUrl = "https://api/run";

    private static readonly DateTimeOffset Start = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly string Sample = $$"""
        { "triggers": { "manual": { "type": "Request" } },
          "actions": {
            "Run_a_Child_Flow": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "{{Child}}" } } },
            "Not_a_child": { "type": "Compose", "runAfter": { "Run_a_Child_Flow": ["Succeeded"] } }
          } }
        """;

    private static readonly string LoopSample = $$"""
        { "triggers": { "manual": { "type": "Request" } },
          "actions": {
            "Loop": { "type": "Foreach", "foreach": "@x", "actions": {
              "Run_a_Child_Flow": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "{{Child}}" } } } } }
          } }
        """;

    // As Power Automate records it, trimmed.
    private static string Outputs(string runId = ChildRun) => $$"""
        {
          "statusCode": 200,
          "headers": {
            "x-ms-workflow-run-id": "{{runId}}",
            "x-ms-client-tracking-id": "08584078564850982032846484654CU09",
            "x-ms-workflow-id": "7a697be85cb14447b9cc775adcd52322",
            "x-ms-workflow-name": "{{ChildFlowName}}",
            "Content-Type": "application/json; charset=utf-8"
          },
          "body": { "result": "Yes" }
        }
        """;

    private static FlowActionResult Result(string name, FlowStepOutcome outcome = FlowStepOutcome.Succeeded, bool outputs = true) =>
        new(name, outcome.ToString(), outcome, Start, Start.AddSeconds(1), null, null,
            $"https://x/in/{name}", outputs ? $"https://x/out/{name}" : null);

    private static FlowRunDetail Run(params FlowActionResult[] actions) =>
        new("08584078564850982032846484654CU09", "Succeeded", FlowStepOutcome.Succeeded, Start, Start.AddMinutes(1),
            Result("manual"), actions.ToDictionary(a => a.Name), null, null, RunUrl, false);

    private static FlowDiagramViewModel Diagram(string definition, FakeHttpHandler handler, List<ChildFlowRun> opened,
        string? problem = null)
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse(definition), "Parent");
        d.OpenChildRun = run =>
        {
            opened.Add(run);
            return Task.FromResult(problem);
        };
        return d;
    }

    private static PowerAutomateClient Client(FakeHttpHandler handler) => new(TestAuth.Tokens(), handler);

    private static FlowCardViewModel Card(FlowDiagramViewModel d, string title) => d.Cards.Single(c => c.Title == title);

    // ---------------------------------------------------------------- reading the outputs

    [Fact]
    public void The_outputs_headers_name_the_child_run_and_flow()
    {
        var run = ChildFlowRun.From(Child, Outputs());

        Assert.Equal(new ChildFlowRun(Child, ChildFlowName, ChildRun), run);
    }

    [Fact]
    public void Header_names_are_matched_whatever_their_case()
    {
        var run = ChildFlowRun.From(Child, """{ "headers": { "X-MS-Workflow-Run-Id": "R1" } }""");

        Assert.Equal(new ChildFlowRun(Child, null, "R1"), run);
    }

    [Theory]
    [InlineData("""{ "statusCode": 200, "body": {} }""")]
    [InlineData("""{ "headers": { "x-ms-workflow-run-id": "" } }""")]
    [InlineData("""{ "headers": "none" }""")]
    [InlineData("""[1, 2]""")]
    [InlineData("not json")]
    public void Outputs_that_name_no_run_give_none(string outputs) =>
        Assert.Null(ChildFlowRun.From(Child, outputs));

    // ---------------------------------------------------------------- the step pane

    [Fact]
    public void Only_a_child_flow_step_in_a_run_offers_its_run()
    {
        var opened = new List<ChildFlowRun>();
        var handler = new FakeHttpHandler();
        var d = Diagram(Sample, handler, opened);

        d.Selected = Card(d, "Run a Child Flow");
        Assert.False(d.IsChildRunOffered);

        d.ShowRun(Run(Result("Run_a_Child_Flow"), Result("Not_a_child")), Client(handler));
        d.Selected = Card(d, "Run a Child Flow");
        Assert.True(d.IsChildRunOffered);
        Assert.True(d.OpenChildRunCommand.CanExecute(null));

        d.Selected = Card(d, "Not a child");
        Assert.False(d.IsChildRunOffered);
        Assert.False(d.OpenChildRunCommand.CanExecute(null));
    }

    [Fact]
    public void Nothing_is_offered_where_nothing_can_open_flows()
    {
        var handler = new FakeHttpHandler();
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse(Sample));
        d.ShowRun(Run(Result("Run_a_Child_Flow")), Client(handler));

        d.Selected = Card(d, "Run a Child Flow");

        Assert.False(d.IsChildRunOffered);
    }

    [Fact]
    public void A_step_with_no_outputs_cannot_open_a_run()
    {
        var opened = new List<ChildFlowRun>();
        var handler = new FakeHttpHandler();
        var d = Diagram(Sample, handler, opened);
        d.ShowRun(Run(Result("Run_a_Child_Flow", FlowStepOutcome.Skipped, outputs: false)), Client(handler));

        d.Selected = Card(d, "Run a Child Flow");

        Assert.True(d.IsChildRunOffered);
        Assert.False(d.OpenChildRunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Opening_reads_the_outputs_and_hands_on_the_run()
    {
        var opened = new List<ChildFlowRun>();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "https://x/out/Run_a_Child_Flow", Outputs());
        var d = Diagram(Sample, handler, opened);
        d.ShowRun(Run(Result("Run_a_Child_Flow")), Client(handler));
        d.Selected = Card(d, "Run a Child Flow");

        await d.OpenChildRunCommand.ExecuteAsync(null);

        Assert.Equal([new ChildFlowRun(Child, ChildFlowName, ChildRun)], opened);
        Assert.False(d.HasChildRunProblem);
    }

    [Fact]
    public async Task In_a_loop_the_iteration_picked_is_the_one_opened()
    {
        var opened = new List<ChildFlowRun>();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/actions/Run_a_Child_Flow/repetitions", JsonSerializer.Serialize(new
            {
                value = new[] { "first", "second" }.Select((name, i) => new
                {
                    properties = new
                    {
                        status = "Succeeded",
                        repetitionIndexes = new[] { new { scopeName = "Loop", itemIndex = i } },
                        outputsLink = new { uri = $"https://x/out/{name}" }
                    }
                })
            }))
            .OnJson(HttpMethod.Get, "https://x/out/first", Outputs("FIRST"))
            .OnJson(HttpMethod.Get, "https://x/out/second", Outputs("SECOND"));
        var d = Diagram(LoopSample, handler, opened);
        d.ShowRun(Run(Result("Loop"), Result("Run_a_Child_Flow")), Client(handler));
        d.Selected = Card(d, "Run a Child Flow");
        await d.Work.WhenIdleAsync();

        d.SelectedIteration = d.SelectedIterations[0];
        await d.OpenChildRunCommand.ExecuteAsync(null);

        Assert.Equal("FIRST", Assert.Single(opened).RunName);
    }

    [Fact]
    public async Task Outputs_that_name_no_run_say_so()
    {
        var opened = new List<ChildFlowRun>();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "https://x/out/Run_a_Child_Flow", """{"body":{}}""");
        var d = Diagram(Sample, handler, opened);
        d.ShowRun(Run(Result("Run_a_Child_Flow")), Client(handler));
        d.Selected = Card(d, "Run a Child Flow");

        await d.OpenChildRunCommand.ExecuteAsync(null);

        Assert.Empty(opened);
        Assert.Equal("The step's outputs do not name the run it started, so it cannot be opened.", d.ChildRunProblem);
    }

    [Fact]
    public async Task Outputs_that_cannot_be_read_say_why_until_another_step_is_picked()
    {
        var opened = new List<ChildFlowRun>();
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Get, "https://x/out/Run_a_Child_Flow", HttpStatusCode.Forbidden);
        var d = Diagram(Sample, handler, opened);
        d.ShowRun(Run(Result("Run_a_Child_Flow"), Result("Not_a_child")), Client(handler));
        d.Selected = Card(d, "Run a Child Flow");

        await d.OpenChildRunCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not read the step's outputs - The link to this content has expired", d.ChildRunProblem);

        d.Selected = Card(d, "Not a child");
        Assert.False(d.HasChildRunProblem);
    }

    [Fact]
    public async Task Why_the_child_flow_could_not_be_opened_is_shown()
    {
        var opened = new List<ChildFlowRun>();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "https://x/out/Run_a_Child_Flow", Outputs());
        var d = Diagram(Sample, handler, opened, problem: "The child flow is not in this environment.");
        d.ShowRun(Run(Result("Run_a_Child_Flow")), Client(handler));
        d.Selected = Card(d, "Run a Child Flow");

        await d.OpenChildRunCommand.ExecuteAsync(null);

        Assert.Single(opened);
        Assert.Equal("The child flow is not in this environment.", d.ChildRunProblem);
    }

    // ---------------------------------------------------------------- finding the child flow

    [Fact]
    public async Task A_child_flow_is_read_from_the_environment_by_its_workflow_id()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"workflows({Child})?$select=name,category", JsonSerializer.Serialize(new
        {
            name = "Child - Check",
            category = 5,
            workflowidunique = ChildFlowName
        }));

        var flow = await Fakes.Dataverse(handler).FindProcessAsync(Child);

        Assert.NotNull(flow);
        Assert.Equal(Child, flow.ObjectId);
        Assert.Equal("Child - Check", flow.Name);
        Assert.Equal(Guid.Parse(ChildFlowName), flow.WorkflowIdUnique);
    }
}
