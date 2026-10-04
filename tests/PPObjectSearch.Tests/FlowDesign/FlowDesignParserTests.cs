using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.FlowDesign;

public class FlowDesignParserTests
{
    // ---------------------------------------------------------------- building definitions

    /// <summary>A clientdata document in the shape Dataverse stores it.</summary>
    private static string Flow(string actions, string? triggers = null, string? connectionReferences = null) => $$"""
        {
          "properties": {
            "connectionReferences": {{connectionReferences ?? "{}"}},
            "definition": {
              "$schema": "https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#",
              "triggers": {{triggers ?? """{ "manual": { "type": "Request", "kind": "Button", "inputs": {} } }"""}},
              "actions": {{actions}}
            }
          },
          "schemaVersion": "1.0.0.0"
        }
        """;

    /// <summary>A Compose action running after the given steps.</summary>
    private static string Compose(params string[] runAfter) =>
        $$"""{ "type": "Compose", "inputs": "x", "runAfter": { {{string.Join(",", runAfter.Select(r => $"\"{r}\": [\"Succeeded\"]"))}} } }""";

    private static string Actions(params (string Name, string Json)[] actions) =>
        "{" + string.Join(",", actions.Select(a => $"\"{a.Name}\": {a.Json}")) + "}";

    /// <summary>The shape of a sequence as text: "A, [B | C], D" - parallel branches in brackets.</summary>
    private static string Shape(FlowSequence sequence) =>
        string.Join(", ", sequence.Steps.Select(step => step switch
        {
            FlowActionStep a => a.Node.Name,
            FlowParallelStep p => "[" + string.Join(" | ", p.Branches.Select(Shape)) + "]",
            _ => "?"
        }));

    private static FlowNode Node(Models.FlowDesign design, string name) => All(design.Actions).Single(n => n.Name == name);

    private static IEnumerable<FlowNode> All(FlowSequence sequence) =>
        sequence.Steps.SelectMany(step => step switch
        {
            FlowActionStep a => new[] { a.Node }.Concat(a.Node.Branches.SelectMany(b => All(b.Steps))),
            FlowParallelStep p => p.Branches.SelectMany(All),
            _ => Enumerable.Empty<FlowNode>()
        });

    // ---------------------------------------------------------------- ordering

    [Fact]
    public void Actions_follow_run_after_not_definition_order()
    {
        var design = FlowDesignParser.Parse(Flow(Actions(("C", Compose("B")), ("A", Compose()), ("B", Compose("A")))));

        Assert.Equal("A, B, C", Shape(design.Actions));
    }

    [Fact]
    public void Steps_after_the_same_step_run_in_parallel_and_rejoin()
    {
        var design = FlowDesignParser.Parse(Flow(Actions(
            ("A", Compose()), ("B", Compose("A")), ("C", Compose("A")), ("D", Compose("B", "C")))));

        Assert.Equal("A, [B | C], D", Shape(design.Actions));
    }

    [Fact]
    public void A_parallel_branch_can_hold_several_steps()
    {
        var design = FlowDesignParser.Parse(Flow(Actions(
            ("A", Compose()), ("B", Compose("A")), ("B2", Compose("B")), ("C", Compose("A")), ("D", Compose("B2", "C")))));

        Assert.Equal("A, [B, B2 | C], D", Shape(design.Actions));
    }

    [Fact]
    public void Parallel_branches_nest_and_each_join_lands_at_its_own_level()
    {
        var design = FlowDesignParser.Parse(Flow(Actions(
            ("A", Compose()),
            ("B", Compose("A")), ("C", Compose("A")),
            ("B1", Compose("B")), ("B2", Compose("B")),
            ("J", Compose("B1", "B2")),
            ("Z", Compose("J", "C")))));

        Assert.Equal("A, [B, [B1 | B2], J | C], Z", Shape(design.Actions));
    }

    [Fact]
    public void Several_starting_steps_run_in_parallel()
    {
        var design = FlowDesignParser.Parse(Flow(Actions(("A", Compose()), ("B", Compose()))));

        Assert.Equal("[A | B]", Shape(design.Actions));
    }

    [Fact]
    public void A_step_after_both_a_step_and_its_successor_follows_in_sequence()
    {
        // X waits on A and on B, and B waits on A: there is nothing parallel about it.
        var design = FlowDesignParser.Parse(Flow(Actions(("A", Compose()), ("B", Compose("A")), ("X", Compose("A", "B")))));

        Assert.Equal("A, B, X", Shape(design.Actions));
    }

    [Fact]
    public void Parallel_branches_keep_definition_order()
    {
        var design = FlowDesignParser.Parse(Flow(Actions(("A", Compose()), ("Second", Compose("A")), ("First", Compose("A")))));

        Assert.Equal("A, [Second | First]", Shape(design.Actions));
    }

    [Fact]
    public void A_cycle_is_shown_at_the_end_with_a_warning_rather_than_dropped()
    {
        var design = FlowDesignParser.Parse(Flow(Actions(("A", Compose()), ("X", Compose("Y")), ("Y", Compose("X")))));

        Assert.Equal("A, X, Y", Shape(design.Actions));
        Assert.Contains(design.Warnings, w => w.Contains("could not be ordered") && w.Contains("X") && w.Contains("Y"));
    }

    [Fact]
    public void A_run_after_naming_a_missing_step_is_warned_about_and_the_step_still_shown()
    {
        var design = FlowDesignParser.Parse(Flow(Actions(("A", Compose("Gone")))));

        Assert.Equal("A", Shape(design.Actions));
        Assert.Contains(design.Warnings, w => w.Contains("Gone"));
    }

    [Fact]
    public void An_empty_flow_has_no_steps()
    {
        var design = FlowDesignParser.Parse(Flow("{}"));

        Assert.Empty(design.Actions.Steps);
        Assert.Equal(0, design.ActionCount);
    }

    // ---------------------------------------------------------------- containers

    [Fact]
    public void A_condition_has_yes_and_no_branches()
    {
        var design = FlowDesignParser.Parse(Flow("""
            {
              "Check": {
                "type": "If",
                "expression": { "equals": [ "@true", true ] },
                "actions": { "Yes1": { "type": "Compose", "inputs": 1 }, "Yes2": { "type": "Compose", "inputs": 2, "runAfter": { "Yes1": ["Succeeded"] } } },
                "else": { "actions": { "No1": { "type": "Compose", "inputs": 3 } } },
                "runAfter": {}
              }
            }
            """));

        var check = Node(design, "Check");
        Assert.Equal(FlowNodeKind.Condition, check.Kind);
        Assert.Equal(FlowNodeCategory.Control, check.Category);
        Assert.True(check.IsContainer);
        Assert.Equal(["Yes", "No"], check.Branches.Select(b => b.Label));
        Assert.Equal("Yes1, Yes2", Shape(check.Branches[0].Steps));
        Assert.Equal("No1", Shape(check.Branches[1].Steps));
        Assert.Equal(4, design.ActionCount);
    }

    [Fact]
    public void A_condition_without_an_else_has_an_empty_no_branch()
    {
        var design = FlowDesignParser.Parse(Flow("""{ "Check": { "type": "If", "expression": {}, "actions": {} } }"""));

        Assert.Empty(Node(design, "Check").Branches[1].Steps.Steps);
    }

    [Fact]
    public void A_switch_has_a_branch_per_case_and_a_default()
    {
        var design = FlowDesignParser.Parse(Flow("""
            {
              "Route": {
                "type": "Switch",
                "expression": "@variables('x')",
                "cases": {
                  "Case": { "case": 1, "actions": { "One": { "type": "Compose", "inputs": 1 } } },
                  "Case_2": { "case": "two", "actions": {} }
                },
                "default": { "actions": { "Other": { "type": "Compose", "inputs": 0 } } }
              }
            }
            """));

        var route = Node(design, "Route");
        Assert.Equal(FlowNodeKind.Switch, route.Kind);
        Assert.Equal(["Case: 1", "Case: two", "Default"], route.Branches.Select(b => b.Label));
        Assert.Equal("One", Shape(route.Branches[0].Steps));
        Assert.Equal("Other", Shape(route.Branches[2].Steps));
    }

    [Theory]
    [InlineData("Foreach", FlowNodeKind.ForEach, "Apply to each")]
    [InlineData("Until", FlowNodeKind.Until, "Do until")]
    [InlineData("Scope", FlowNodeKind.Scope, "Scope")]
    public void Loops_and_scopes_hold_one_unlabelled_branch(string type, FlowNodeKind kind, string label)
    {
        var design = FlowDesignParser.Parse(Flow($$"""
            { "Box": { "type": "{{type}}", "foreach": "@body('x')", "expression": "@equals(1,1)",
                       "actions": { "Inner": { "type": "Compose", "inputs": 1 } } } }
            """));

        var box = Node(design, "Box");
        Assert.Equal(kind, box.Kind);
        Assert.Equal(label, box.TypeLabel);
        var branch = Assert.Single(box.Branches);
        Assert.Equal(string.Empty, branch.Label);
        Assert.Equal("Inner", Shape(branch.Steps));
    }

    [Fact]
    public void A_loop_shows_what_it_loops_over()
    {
        var design = FlowDesignParser.Parse(Flow("""{ "Each": { "type": "Foreach", "foreach": "@outputs('List_rows')?['body/value']", "actions": {} } }"""));

        Assert.Equal("@outputs('List_rows')?['body/value']", Node(design, "Each").Detail);
    }

    [Fact]
    public void Containers_nest()
    {
        var design = FlowDesignParser.Parse(Flow("""
            { "Outer": { "type": "Scope", "actions": {
                "Loop": { "type": "Foreach", "foreach": "@x", "actions": {
                  "Inner": { "type": "Compose", "inputs": 1 } } } } } }
            """));

        Assert.Equal("Inner", Node(design, "Inner").Name);
        Assert.Equal(3, design.ActionCount);
    }

    [Fact]
    public void A_containers_json_leaves_out_the_steps_inside_it()
    {
        var design = FlowDesignParser.Parse(Flow("""
            { "Check": { "type": "If", "expression": { "equals": [1, 1] },
                         "actions": { "Hidden_inside": { "type": "Compose", "inputs": 1 } },
                         "else": { "actions": { "Also_hidden": { "type": "Compose", "inputs": 2 } } } } }
            """));

        var json = Node(design, "Check").Json;

        Assert.Contains("\"expression\"", json);
        Assert.Contains("shown in the diagram", json);
        Assert.DoesNotContain("Hidden_inside", json);
        Assert.DoesNotContain("Also_hidden", json);
        Assert.NotNull(JsonDocument.Parse(json));
    }

    // ---------------------------------------------------------------- run after

    [Fact]
    public void Run_after_conditions_other_than_succeeded_are_kept()
    {
        var design = FlowDesignParser.Parse(Flow("""
            { "Try": { "type": "Scope", "actions": {} },
              "Catch": { "type": "Compose", "inputs": 1, "runAfter": { "Try": [ "Failed", "TimedOut" ] } } }
            """));

        var runAfter = Assert.Single(Node(design, "Catch").RunAfter);
        Assert.Equal("Try", runAfter.Action);
        Assert.Equal(["Failed", "TimedOut"], runAfter.Statuses);
        Assert.False(runAfter.IsDefault);
        Assert.Equal("Try, Catch", Shape(design.Actions));
    }

    [Fact]
    public void Plain_run_after_is_the_default()
    {
        var design = FlowDesignParser.Parse(Flow(Actions(("A", Compose()), ("B", Compose("A")))));

        Assert.True(Assert.Single(Node(design, "B").RunAfter).IsDefault);
    }

    // ---------------------------------------------------------------- connectors

    [Fact]
    public void A_connector_step_is_named_from_its_api_id()
    {
        var design = FlowDesignParser.Parse(Flow("""
            { "List_rows": { "type": "OpenApiConnection",
                "inputs": { "host": { "apiId": "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps",
                                      "connectionName": "shared_commondataserviceforapps", "operationId": "ListRecords" },
                            "parameters": { "entityName": "accounts" } } } }
            """));

        var node = Node(design, "List_rows");
        Assert.Equal(FlowNodeCategory.Connector, node.Category);
        Assert.Equal("Microsoft Dataverse", node.Connector);
        Assert.Equal("List rows", node.Operation);
        Assert.Equal("Microsoft Dataverse · List rows", node.Summary);
        Assert.Equal("List rows", node.DisplayName);
    }

    [Fact]
    public void A_connector_step_is_named_through_its_connection_reference()
    {
        var design = FlowDesignParser.Parse(Flow("""
            { "Send": { "type": "OpenApiConnection",
                "inputs": { "host": { "connectionName": "shared_office365-1", "operationId": "SendEmailV2" } } } }
            """, connectionReferences: """{ "shared_office365-1": { "api": { "name": "shared_office365" } } }"""));

        var node = Node(design, "Send");
        Assert.Equal("Office 365 Outlook", node.Connector);
        Assert.Equal("Send email V2", node.Operation);
    }

    [Fact]
    public void The_older_api_connection_shape_is_understood()
    {
        var design = FlowDesignParser.Parse(Flow("""
            { "Post": { "type": "ApiConnection",
                "inputs": { "host": { "connection": { "name": "@parameters('$connections')['shared_teams']['connectionId']" } },
                            "method": "post", "path": "/v3/beta/teams/x" } } }
            """));

        var node = Node(design, "Post");
        Assert.Equal("Microsoft Teams", node.Connector);
        Assert.Null(node.Operation);
        Assert.Equal("Microsoft Teams", node.Summary);
    }

    [Theory]
    [InlineData("shared_commondataserviceforapps", "Microsoft Dataverse")]
    [InlineData("shared_SHAREPOINTONLINE", "SharePoint")]
    [InlineData("shared_somecustomapi", "somecustomapi")]
    [InlineData("contoso_custom", "contoso_custom")]
    public void Connector_names_are_friendly_where_known(string api, string expected) =>
        Assert.Equal(expected, FlowDesignParser.ConnectorName(api));

    [Theory]
    [InlineData("GetItem", "Get item")]
    [InlineData("SendEmailV2", "Send email V2")]
    [InlineData("PostMessageToConversation", "Post message to conversation")]
    [InlineData("HTTPRequest", "HTTP request")]
    [InlineData("Get_file_content", "Get file content")]
    public void Identifiers_become_words(string identifier, string expected) =>
        Assert.Equal(expected, FlowDesignParser.SplitWords(identifier));

    // ---------------------------------------------------------------- built-in steps

    [Theory]
    [InlineData("Compose", FlowNodeCategory.Data, "Compose")]
    [InlineData("ParseJson", FlowNodeCategory.Data, "Parse JSON")]
    [InlineData("Query", FlowNodeCategory.Data, "Filter array")]
    [InlineData("SetVariable", FlowNodeCategory.Variable, "Set variable")]
    [InlineData("Http", FlowNodeCategory.Http, "HTTP")]
    [InlineData("Response", FlowNodeCategory.Http, "Response")]
    [InlineData("Workflow", FlowNodeCategory.ChildFlow, "Run a child flow")]
    [InlineData("Terminate", FlowNodeCategory.Control, "Terminate")]
    [InlineData("SomethingNew", FlowNodeCategory.Other, "Something new")]
    public void Built_in_steps_get_their_designer_names(string type, FlowNodeCategory category, string label)
    {
        var design = FlowDesignParser.Parse(Flow($$"""{ "Step": { "type": "{{type}}", "inputs": {} } }"""));

        var node = Node(design, "Step");
        Assert.Equal(category, node.Category);
        Assert.Equal(label, node.TypeLabel);
        Assert.Equal(label, node.Summary);
    }

    [Fact]
    public void Variables_show_their_name_and_type()
    {
        var design = FlowDesignParser.Parse(Flow("""
            { "Init": { "type": "InitializeVariable", "inputs": { "variables": [ { "name": "Count", "type": "integer", "value": 0 } ] } },
              "Set": { "type": "SetVariable", "inputs": { "name": "Count", "value": 1 }, "runAfter": { "Init": ["Succeeded"] } } }
            """));

        Assert.Equal("Count (integer)", Node(design, "Init").Detail);
        Assert.Equal("Count", Node(design, "Set").Detail);
    }

    [Fact]
    public void Terminate_shows_its_status_and_notes_are_kept()
    {
        var design = FlowDesignParser.Parse(Flow("""
            { "Stop": { "type": "Terminate", "description": "Bail out if nothing to do", "inputs": { "runStatus": "Cancelled" } } }
            """));

        var stop = Node(design, "Stop");
        Assert.Equal("Cancelled", stop.Detail);
        Assert.Equal("Bail out if nothing to do", stop.Description);
    }

    // ---------------------------------------------------------------- triggers

    [Fact]
    public void A_manual_trigger_is_named_as_the_designer_names_it()
    {
        var trigger = Assert.Single(FlowDesignParser.Parse(Flow("{}")).Triggers);

        Assert.Equal(FlowNodeKind.Trigger, trigger.Kind);
        Assert.Equal(FlowNodeCategory.Trigger, trigger.Category);
        Assert.Equal("Manually trigger a flow", trigger.TypeLabel);
    }

    [Fact]
    public void A_recurrence_trigger_shows_its_schedule()
    {
        var trigger = Assert.Single(FlowDesignParser.Parse(Flow("{}",
            triggers: """{ "Recurrence": { "type": "Recurrence", "recurrence": { "frequency": "Day", "interval": 1 } } }""")).Triggers);

        Assert.Equal("Recurrence", trigger.TypeLabel);
        Assert.Equal("Every 1 day", trigger.Detail);
    }

    [Fact]
    public void A_dataverse_trigger_is_named_by_its_operation()
    {
        var trigger = Assert.Single(FlowDesignParser.Parse(Flow("{}", triggers: """
            { "When_a_row_is_added": { "type": "OpenApiConnectionWebhook",
                "inputs": { "host": { "apiId": "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps",
                                      "operationId": "SubscribeWebhookTrigger" } } } }
            """)).Triggers);

        Assert.Equal(FlowNodeCategory.Trigger, trigger.Category);
        Assert.Equal("Microsoft Dataverse", trigger.Connector);
        Assert.Equal("When a row is added, modified or deleted", trigger.TypeLabel);
    }

    // ---------------------------------------------------------------- documents

    [Fact]
    public void A_definition_at_the_root_is_read_too()
    {
        var design = FlowDesignParser.Parse("""{ "triggers": {}, "actions": { "A": { "type": "Compose", "inputs": 1 } } }""");

        Assert.Equal("A", Shape(design.Actions));
        Assert.Empty(design.Triggers);
    }

    [Theory]
    [InlineData("""{ "properties": { "displayName": "No definition" } }""")]
    [InlineData("""{ "something": "else" }""")]
    public void A_document_without_a_definition_is_refused(string json) =>
        Assert.Throws<FormatException>(() => FlowDesignParser.Parse(json));

    [Fact]
    public void Malformed_json_is_refused() =>
        Assert.ThrowsAny<JsonException>(() => FlowDesignParser.Parse("{ not json"));

    [Fact]
    public void A_deeply_nested_flow_still_parses()
    {
        // Twenty scopes, one inside another, holding a step with a deep inline body: well past
        // the 64 levels JsonDocument allows by default.
        var body = "{\"a\":" + string.Concat(Enumerable.Repeat("{\"b\":", 20)) + "1" + new string('}', 21);
        var inner = $"{{ \"Deep\": {{ \"type\": \"Compose\", \"inputs\": {body} }} }}";
        for (var i = 0; i < 20; i++) inner = $"{{ \"Scope_{i}\": {{ \"type\": \"Scope\", \"actions\": {inner} }} }}";

        var json = $"{{ \"properties\": {{ \"definition\": {{ \"triggers\": {{}}, \"actions\": {inner} }} }} }}";

        var design = FlowDesignParser.Parse(json);

        Assert.Equal(21, design.ActionCount);
    }
}
