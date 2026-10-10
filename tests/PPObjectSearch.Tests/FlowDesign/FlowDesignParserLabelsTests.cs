using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.FlowDesign;

/// <summary>How the flow design parser labels, categorises and describes each kind of step.</summary>
public class FlowDesignParserLabelsTests
{
    /// <summary>A bare definition with one action named A, found at the document's root.</summary>
    private static FlowNode Single(string actionJson)
    {
        var design = FlowDesignParser.Parse($$"""{ "actions": { "A": {{actionJson}} } }""");
        return Assert.IsType<FlowActionStep>(Assert.Single(design.Actions.Steps)).Node;
    }

    private static FlowNode Trigger(string triggerJson) =>
        Assert.Single(FlowDesignParser.Parse($$"""{ "definition": { "triggers": { "T": {{triggerJson}} } } }""").Triggers);

    [Theory]
    [InlineData("Select", "Select", FlowNodeCategory.Data)]
    [InlineData("Join", "Join", FlowNodeCategory.Data)]
    [InlineData("Query", "Filter array", FlowNodeCategory.Data)]
    [InlineData("JavaScriptCode", "Execute JavaScript code", FlowNodeCategory.Data)]
    [InlineData("SetVariable", "Set variable", FlowNodeCategory.Variable)]
    [InlineData("IncrementVariable", "Increment variable", FlowNodeCategory.Variable)]
    [InlineData("DecrementVariable", "Decrement variable", FlowNodeCategory.Variable)]
    [InlineData("AppendToArrayVariable", "Append to array variable", FlowNodeCategory.Variable)]
    [InlineData("AppendToStringVariable", "Append to string variable", FlowNodeCategory.Variable)]
    [InlineData("Http", "HTTP", FlowNodeCategory.Http)]
    [InlineData("HttpWebhook", "HTTP webhook", FlowNodeCategory.Http)]
    [InlineData("Response", "Response", FlowNodeCategory.Http)]
    [InlineData("Terminate", "Terminate", FlowNodeCategory.Control)]
    [InlineData("ApiConnection", "Api connection", FlowNodeCategory.Connector)]
    [InlineData("SendToQueue", "Send to queue", FlowNodeCategory.Other)]
    public void Each_built_in_action_type_gets_its_designer_label_and_category(string type, string label, FlowNodeCategory category)
    {
        var node = Single($$"""{ "type": "{{type}}", "inputs": {} }""");

        Assert.Equal(label, node.TypeLabel);
        Assert.Equal(category, node.Category);
    }

    [Fact]
    public void A_table_names_its_format_when_it_has_one()
    {
        Assert.Equal("Create CSV table", Single("""{ "type": "Table", "inputs": { "format": "csv" } }""").TypeLabel);
        Assert.Equal("Create table", Single("""{ "type": "Table", "inputs": {} }""").TypeLabel);
    }

    [Fact]
    public void A_delay_shows_how_long_and_a_delay_until_is_named_as_such()
    {
        var delay = Single("""{ "type": "Wait", "inputs": { "interval": { "count": 5, "unit": "Minute" } } }""");
        var noCount = Single("""{ "type": "Wait", "inputs": { "interval": { "unit": "Hour" } } }""");
        var until = Single("""{ "type": "Wait", "inputs": { "until": { "timestamp": "2024-01-01" } } }""");

        Assert.Equal(("Delay", "5 minute"), (delay.TypeLabel, delay.Detail));
        Assert.Equal("? hour", noCount.Detail);
        Assert.Equal(("Delay until", null), (until.TypeLabel, until.Detail));
        Assert.Equal(FlowNodeCategory.Control, until.Category);
    }

    [Theory]
    [InlineData("SetVariable", "{ \"name\": \"total\", \"value\": 1 }", "total")]
    [InlineData("Terminate", "{ \"runStatus\": \"Failed\" }", "Failed")]
    [InlineData("InitializeVariable", "{ \"variables\": [ { \"name\": \"count\" } ] }", "count")]
    [InlineData("InitializeVariable", "{ \"variables\": [] }", null)]
    [InlineData("Workflow", "{ \"host\": { \"workflowReferenceName\": \"not-a-guid\" } }", "not-a-guid")]
    [InlineData("Workflow", "{ \"host\": {} }", null)]
    public void A_steps_detail_is_its_most_telling_input(string type, string inputs, string? detail)
    {
        var node = Single($$"""{ "type": "{{type}}", "inputs": {{inputs}} }""");

        Assert.Equal(detail, node.Detail);
        Assert.Null(node.ChildFlowId);
    }

    [Fact]
    public void Loops_without_their_input_or_condition_have_no_detail()
    {
        Assert.Null(Single("""{ "type": "Foreach", "actions": {} }""").Detail);
        Assert.Null(Single("""{ "type": "Until", "actions": {} }""").Detail);
    }

    [Fact]
    public void A_long_loop_input_is_shortened()
    {
        var input = new string('x', 130);

        var node = Single($$"""{ "type": "Foreach", "foreach": "{{input}}", "actions": {} }""");

        Assert.Equal(new string('x', 117) + "...", node.Detail);
    }

    [Theory]
    [InlineData("Http", "When an HTTP request is received")]
    [InlineData("PowerAppV2", "When Power Apps calls a flow")]
    [InlineData("Skills", "When a flow is run from Copilot")]
    [InlineData("Other", "When a request is received")]
    public void A_request_trigger_is_named_by_its_kind(string kind, string label)
    {
        Assert.Equal(label, Trigger($$"""{ "type": "Request", "kind": "{{kind}}" }""").TypeLabel);
    }

    [Fact]
    public void Other_triggers_are_named_from_their_type_or_their_connector()
    {
        Assert.Equal("When a request is received", Trigger("""{ "type": "Request" }""").TypeLabel);
        Assert.Equal("Api connection webhook", Trigger("""{ "type": "ApiConnectionWebhook" }""").TypeLabel);
        Assert.Equal("Connector trigger", Trigger("""{ "type": "OpenApiConnectionWebhook", "inputs": { "host": { "apiId": "/providers/Microsoft.PowerApps/apis/shared_teams" } } }""").TypeLabel);
    }

    [Fact]
    public void A_recurrence_without_a_frequency_or_settings_has_no_detail()
    {
        Assert.Null(Trigger("""{ "type": "Recurrence", "recurrence": { "interval": 2 } }""").Detail);
        Assert.Null(Trigger("""{ "type": "Recurrence" }""").Detail);
        Assert.Equal("Every 1 day", Trigger("""{ "type": "Recurrence", "recurrence": { "frequency": "Day" } }""").Detail);
    }

    [Fact]
    public void A_connector_step_named_only_by_a_legacy_connection_expression_finds_its_connector()
    {
        var design = FlowDesignParser.Parse("""
            {
              "properties": {
                "connectionReferences": { "shared_sql_1": { "id": "/providers/Microsoft.PowerApps/apis/shared_sql" } },
                "definition": { "actions": {
                  "Legacy": { "type": "ApiConnection", "inputs": { "host": { "connection": { "name": "@parameters('$connections')['shared_sql_1']['connectionId']" } } } },
                  "Unmapped": { "type": "ApiConnection", "runAfter": { "Legacy": "Succeeded" },
                                "inputs": { "host": { "connection": { "name": "@parameters('$connections')['shared_custom']['connectionId']" }, "operationId": "DoIt" } } }
                } }
              }
            }
            """);

        var nodes = design.Actions.Steps.Cast<FlowActionStep>().Select(s => s.Node).ToList();
        Assert.Equal(("SQL Server", null), (nodes[0].Connector, nodes[0].Operation));
        Assert.Equal(("custom", "Do it"), (nodes[1].Connector, nodes[1].Operation));
        Assert.Equal(["Succeeded"], nodes[1].RunAfter.Single().Statuses);
    }

    [Fact]
    public void A_connection_reference_named_by_api_name_maps_a_connector_step()
    {
        var design = FlowDesignParser.Parse("""
            {
              "connectionReferences": { "ref": { "apiName": "shared_planner" }, "none": {} },
              "triggers": {},
              "actions": { "A": { "type": "OpenApiConnection", "inputs": { "host": { "connectionName": "ref", "operationId": "CreateTask_V3" } } } }
            }
            """);

        var node = Assert.IsType<FlowActionStep>(Assert.Single(design.Actions.Steps)).Node;
        Assert.Equal("Planner", node.Connector);
        Assert.Equal("Create task V3", node.Operation);
        Assert.Equal(FlowNodeCategory.Connector, node.Category);
    }

    [Fact]
    public void Steps_that_are_not_objects_are_skipped_and_actions_that_are_not_an_object_are_empty()
    {
        var skipped = FlowDesignParser.Parse("""{ "actions": { "A": 1, "B": { "type": "Compose" } } }""");
        var empty = FlowDesignParser.Parse("""{ "triggers": {}, "actions": [] }""");

        Assert.Equal(1, skipped.ActionCount);
        Assert.Empty(empty.Actions.Steps);
    }

    [Fact]
    public void A_switch_case_without_a_value_is_named_by_its_key()
    {
        var node = Single("""{ "type": "Switch", "expression": "@x", "cases": { "Case_2": { "actions": {} } } }""");

        Assert.Equal(["Case: Case_2", "Default"], node.Branches.Select(b => b.Label));
    }

    [Fact]
    public void A_steps_json_keeps_non_object_values_inside_its_branches()
    {
        var withNote = Single("""{ "type": "If", "else": { "actions": {}, "note": [1, 2] }, "actions": {} }""");
        var plainElse = Single("""{ "type": "If", "else": "none", "actions": {} }""");

        Assert.Contains("\"note\": [", withNote.Json);
        Assert.Contains("shown in the diagram", withNote.Json);
        Assert.Contains("\"else\": \"none\"", plainElse.Json);
        Assert.Empty(plainElse.Branches[1].Steps.Steps);
    }

    [Fact]
    public void Splitting_an_empty_identifier_leaves_it_as_it_is()
    {
        Assert.Equal("__", FlowDesignParser.SplitWords("__"));
    }
}
