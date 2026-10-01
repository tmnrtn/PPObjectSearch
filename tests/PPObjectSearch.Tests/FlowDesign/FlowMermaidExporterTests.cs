using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.FlowDesign;

public class FlowMermaidExporterTests
{
    private const string Sample = """
        { "properties": { "definition": {
          "triggers": { "manual": { "type": "Request", "kind": "Button" } },
          "actions": {
            "Init": { "type": "InitializeVariable", "inputs": { "variables": [ { "name": "n", "type": "integer" } ] } },
            "Try": { "type": "Scope", "runAfter": { "Init": ["Succeeded"] }, "actions": {
              "List_rows": { "type": "OpenApiConnection", "inputs": { "host": { "apiId": "/x/shared_commondataserviceforapps", "operationId": "ListRecords" } } },
              "Check": { "type": "If", "expression": {}, "runAfter": { "List_rows": ["Succeeded"] },
                         "actions": { "Close_task": { "type": "Compose", "inputs": 1 } },
                         "else": { "actions": { "Skip_it": { "type": "Compose", "inputs": 2 } } } } } },
            "Email": { "type": "OpenApiConnection", "runAfter": { "Try": ["Succeeded"] },
                       "inputs": { "host": { "apiId": "/x/shared_office365", "operationId": "SendEmailV2" } } },
            "Catch": { "type": "Terminate", "runAfter": { "Try": ["Failed", "TimedOut"] }, "inputs": { "runStatus": "Failed" } },
            "Route": { "type": "Switch", "expression": "@x", "runAfter": { "Email": ["Succeeded"] },
                       "cases": { "Case": { "case": 1, "actions": { "One": { "type": "Compose" } } } },
                       "default": { "actions": {} } }
          } } } }
        """;

    private static string Mermaid(string json = Sample, string? name = null) =>
        FlowMermaidExporter.ToMermaid(FlowDesignParser.Parse(json), name);

    private static string[] Lines(string mermaid) =>
        mermaid.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

    /// <summary>The id the exporter gave a step, read back from its node line.</summary>
    private static string IdOf(string mermaid, string displayName)
    {
        var line = Lines(mermaid).First(l => !l.StartsWith("subgraph") && l.Contains($"\"{displayName}"));
        return new string(line.TakeWhile(char.IsLetterOrDigit).ToArray());
    }

    [Fact]
    public void It_is_a_top_down_flowchart()
    {
        Assert.Equal("flowchart TD", Lines(Mermaid())[0]);
    }

    [Fact]
    public void The_flows_name_heads_it_as_a_comment()
    {
        var lines = Lines(Mermaid(name: "Close resolved cases"));

        Assert.Equal("%% Close resolved cases - drawn by PPObjectSearch", lines[0]);
        Assert.Equal("flowchart TD", lines[1]);
    }

    [Fact]
    public void The_trigger_is_a_stadium_leading_to_the_first_step()
    {
        var m = Mermaid();
        var trigger = IdOf(m, "manual");
        var init = IdOf(m, "Init");

        Assert.Contains($"{trigger}([\"manual<br/><small>Manually trigger a flow</small>\"])", m);
        Assert.Contains($"{trigger} --> {init}", m);
    }

    [Fact]
    public void Each_step_shows_its_name_and_what_it_does()
    {
        var m = Mermaid();

        Assert.Contains("[\"List rows<br/><small>Microsoft Dataverse · List rows</small>\"]", m);
        Assert.Contains("[\"Email<br/><small>Office 365 Outlook · Send email V2</small>\"]", m);
    }

    [Fact]
    public void Containers_are_subgraphs_entered_and_left_as_a_whole()
    {
        var m = Mermaid();
        var init = IdOf(m, "Init");
        var tryHeader = IdOf(m, "Try");
        var email = IdOf(m, "Email");

        Assert.Contains($"subgraph {tryHeader}_box [\"Try\"]", m);
        Assert.Contains($"{init} --> {tryHeader}_box", m);
        Assert.Contains($"{tryHeader}_box --> {email}", m);
        Assert.Equal(Lines(m).Count(l => l.StartsWith("subgraph")), Lines(m).Count(l => l == "end"));
    }

    [Fact]
    public void A_containers_first_steps_hang_from_its_header()
    {
        var m = Mermaid();

        Assert.Contains($"{IdOf(m, "Try")} --> {IdOf(m, "List rows")}", m);
    }

    [Fact]
    public void A_condition_is_a_diamond_with_labelled_yes_and_no_edges()
    {
        var m = Mermaid();
        var check = IdOf(m, "Check");

        Assert.Contains($"{check}{{\"Check<br/><small>Condition</small>\"}}", m);
        Assert.Contains($"{check} -->|\"Yes\"| {IdOf(m, "Close task")}", m);
        Assert.Contains($"{check} -->|\"No\"| {IdOf(m, "Skip it")}", m);
    }

    [Fact]
    public void A_switch_is_a_hexagon_with_an_edge_per_case()
    {
        var m = Mermaid();
        var route = IdOf(m, "Route");

        Assert.Contains($"{route}{{{{\"Route<br/><small>Switch</small>\"}}}}", m);
        Assert.Contains($"{route} -->|\"Case: 1\"| {IdOf(m, "One")}", m);
    }

    [Fact]
    public void Error_paths_are_dotted_and_say_when_they_run()
    {
        var m = Mermaid();

        Assert.Contains($"{IdOf(m, "Try")}_box -.->|\"failed or timed out\"| {IdOf(m, "Catch")}", m);
    }

    [Fact]
    public void Parallel_branches_come_out_as_edges_from_the_same_step()
    {
        var m = Mermaid();
        var tryBox = IdOf(m, "Try") + "_box";

        Assert.Contains($"{tryBox} --> {IdOf(m, "Email")}", m);
        Assert.Contains($"{tryBox} -.->", m);
    }

    [Fact]
    public void A_loop_is_a_subroutine_shape()
    {
        var m = Mermaid("""{ "triggers": {}, "actions": { "Each": { "type": "Foreach", "foreach": "@x", "actions": { "A": { "type": "Compose" } } } } }""");

        Assert.Contains($"{IdOf(m, "Each")}[[\"Each<br/><small>Apply to each</small>\"]]", m);
    }

    [Fact]
    public void Steps_are_coloured_by_kind()
    {
        var m = Mermaid();

        Assert.Contains("classDef trigger ", m);
        Assert.Contains("classDef connector ", m);
        Assert.Contains($"class {IdOf(m, "List rows")},{IdOf(m, "Email")} connector", m);
        Assert.DoesNotContain("classDef http ", m); // no HTTP steps, no unused class
    }

    [Fact]
    public void A_step_whose_name_is_its_type_shows_it_once()
    {
        var m = Mermaid("""{ "triggers": {}, "actions": { "Compose": { "type": "Compose", "inputs": 1 } } }""");

        Assert.Contains("[\"Compose\"]", m);
        Assert.DoesNotContain("<small>Compose</small>", m);
    }

    [Fact]
    public void Ids_are_safe_whatever_the_step_is_called()
    {
        var m = Mermaid("""{ "triggers": {}, "actions": { "Get_\"odd\"_<name>|#1": { "type": "Compose", "inputs": 1 } } }""");

        Assert.Contains("n1[\"Get #quot;odd#quot; #lt;name#gt;#124;#35;1", m);
        Assert.DoesNotContain("<name>", m);
    }

    [Theory]
    [InlineData("say \"hi\"", "say #quot;hi#quot;")]
    [InlineData("a < b > c", "a #lt; b #gt; c")]
    [InlineData("x | y", "x #124; y")]
    [InlineData("#1", "#35;1")]
    [InlineData("two\nlines", "two lines")]
    public void Labels_are_escaped(string text, string expected) =>
        Assert.Equal(expected, FlowMermaidExporter.Escape(text));

    [Fact]
    public void An_empty_flow_is_still_a_valid_chart()
    {
        var m = Mermaid("""{ "triggers": {}, "actions": {} }""");

        Assert.Equal(["flowchart TD"], Lines(m));
    }

    [Fact]
    public void The_diagram_offers_the_same_text_it_copies()
    {
        var design = FlowDesignParser.Parse(Sample);

        Assert.Equal(FlowMermaidExporter.ToMermaid(design, "My flow"), new FlowDiagramViewModel(design, "My flow").Mermaid);
    }
}
