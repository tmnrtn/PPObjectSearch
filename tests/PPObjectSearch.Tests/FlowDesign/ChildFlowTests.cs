using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.FlowDesign;

/// <summary>"Run a child flow" steps are named by the flow they call.</summary>
public class ChildFlowTests
{
    private static readonly Guid Escalate = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Gone = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static readonly string Sample = $$"""
        { "triggers": { "manual": { "type": "Request" } },
          "actions": {
            "Escalate_it": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "{{Escalate}}" }, "body": {} } },
            "Call_missing": { "type": "Workflow", "runAfter": { "Escalate_it": ["Succeeded"] },
                              "inputs": { "host": { "workflowReferenceName": "{{Gone}}" } } },
            "Not_a_child": { "type": "Compose", "runAfter": { "Call_missing": ["Succeeded"] } }
          } }
        """;

    private static FlowCardViewModel Card(FlowDiagramViewModel d, string title) => d.Cards.Single(c => c.Title == title);

    [Fact]
    public void The_parser_reads_the_child_flows_workflow_id()
    {
        var design = FlowDesignParser.Parse(Sample);
        var nodes = design.Actions.Steps.OfType<Models.FlowActionStep>().Select(s => s.Node).ToList();

        Assert.Equal(Escalate, nodes.Single(n => n.Name == "Escalate_it").ChildFlowId);
        Assert.Null(nodes.Single(n => n.Name == "Not_a_child").ChildFlowId);
    }

    [Fact]
    public void A_reference_that_is_not_an_id_is_ignored()
    {
        var design = FlowDesignParser.Parse("""
            { "triggers": {}, "actions": { "Call": { "type": "Workflow", "inputs": { "host": { "workflowReferenceName": "not-a-guid" } } } } }
            """);

        Assert.Null(((Models.FlowActionStep)design.Actions.Steps[0]).Node.ChildFlowId);
    }

    [Fact]
    public void The_diagram_lists_the_child_flows_to_look_up()
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse(Sample));

        Assert.Equal([Escalate, Gone], d.ChildFlowIds.OrderBy(g => g.ToString()));
    }

    [Fact]
    public void Until_names_arrive_a_child_flow_shows_its_id()
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse(Sample));

        Assert.Equal(Escalate.ToString(), Card(d, "Escalate it").Detail);
    }

    [Fact]
    public void A_named_child_flow_shows_its_name_with_the_id_in_the_tooltip()
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse(Sample));
        var raised = new List<string?>();
        Card(d, "Escalate it").PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        d.SetChildFlowNames(new Dictionary<Guid, string> { [Escalate] = "Escalate case" });

        var card = Card(d, "Escalate it");
        Assert.Equal("Escalate case", card.Detail);
        Assert.Equal("Escalate case", card.ChildFlowName);
        Assert.Contains($"Child flow {Escalate}", card.DetailTooltip);
        Assert.Contains(nameof(FlowCardViewModel.Detail), raised);
    }

    [Fact]
    public void A_child_flow_the_environment_does_not_have_says_so()
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse(Sample));

        d.SetChildFlowNames(new Dictionary<Guid, string> { [Escalate] = "Escalate case" });

        Assert.Equal("Child flow not found in this environment", Card(d, "Call missing").Detail);
        Assert.Null(Card(d, "Not a child").ChildFlowName);
    }

    [Fact]
    public void Search_finds_a_step_by_its_child_flows_name()
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse(Sample));
        d.SetChildFlowNames(new Dictionary<Guid, string> { [Escalate] = "Escalate case" });

        d.SearchText = "escalate case";

        Assert.Equal(["Escalate it"], d.Cards.Where(c => c.IsMatch).Select(c => c.Title));
    }

    [Fact]
    public void Mermaid_names_the_child_flow()
    {
        var d = new FlowDiagramViewModel(FlowDesignParser.Parse(Sample), "Parent");
        d.SetChildFlowNames(new Dictionary<Guid, string> { [Escalate] = "Escalate case" });

        Assert.Contains("<small>Run a child flow: Escalate case</small>", d.Mermaid);
    }

    [Fact]
    public async Task Names_are_read_in_one_query_by_workflow_id()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "workflows?", JsonSerializer.Serialize(new
        {
            value = new[] { new { workflowid = Escalate.ToString(), name = "Escalate case" } }
        }));

        var names = await Fakes.Dataverse(handler).GetWorkflowNamesAsync([Escalate, Gone, Escalate]);

        Assert.Equal("Escalate case", names[Escalate]);
        Assert.False(names.ContainsKey(Gone));
        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains("$select=workflowid,name", url);
        Assert.Contains($"In(PropertyName='workflowid',PropertyValues=['{Escalate}','{Gone}'])", url);
    }
}
