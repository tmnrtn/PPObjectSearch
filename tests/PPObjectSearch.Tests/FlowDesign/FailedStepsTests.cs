using PPObjectSearch.Models;

namespace PPObjectSearch.Tests.FlowDesign;

/// <summary>The steps behind a run's "An action failed": which ones are the cause, and how they read.</summary>
public class FailedStepsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static FlowActionResult Step(string name, FlowStepOutcome outcome, int second, string? code = null, string? message = null) =>
        new(name, outcome.ToString(), outcome, T0.AddSeconds(second), null, code, message, null, null);

    private static FlowRunDetail Run(params FlowActionResult[] steps) =>
        new("run", "Failed", FlowStepOutcome.Failed, T0, null, null, steps.ToDictionary(s => s.Name), "ActionFailed", null, "https://x/run", false);

    [Fact]
    public void Scopes_that_only_say_a_step_inside_failed_are_left_out()
    {
        var run = Run(
            Step("Try", FlowStepOutcome.Failed, 1, "ActionFailed", "An action failed. No dependent actions succeeded."),
            Step("Get_file", FlowStepOutcome.Failed, 3, "NotFound", "The file was not found."),
            Step("Compose", FlowStepOutcome.Succeeded, 0),
            Step("Update_row", FlowStepOutcome.TimedOut, 2, "ActionTimedOut", "The action timed out."));

        Assert.Equal(["Update_row", "Get_file"], run.FailedSteps.Select(s => s.Name));
    }

    [Fact]
    public void When_every_failed_step_only_says_that_they_are_all_shown()
    {
        var run = Run(
            Step("Outer", FlowStepOutcome.Failed, 1, "ActionFailed"),
            Step("Inner", FlowStepOutcome.Failed, 2, "actionfailed"),
            Step("Skipped", FlowStepOutcome.Skipped, 3));

        Assert.Equal(["Outer", "Inner"], run.FailedSteps.Select(s => s.Name));
        Assert.Empty(Run(Step("Compose", FlowStepOutcome.Succeeded, 0)).FailedSteps);
    }

    [Theory]
    [InlineData("The file was not found.\nRequest id: 1", "NotFound", "The file was not found.")]
    [InlineData("{\"status\":404,\"message\":\"Item not found\"}", "NotFound", "Item not found")]
    [InlineData(null, "BadRequest", "BadRequest")]
    [InlineData(null, null, "No error text")]
    public void A_step_reads_as_its_name_and_first_line_of_error(string? message, string? code, string expected)
    {
        var step = Step("Send_an_email_(V2)", FlowStepOutcome.Failed, 0, code, message);

        Assert.Equal("Send an email (V2)", step.Label);
        Assert.Equal(expected, step.ErrorLine);
    }

    [Theory]
    [InlineData("""{"statusCode":404,"body":{"odata.error":{"code":"-2130575338","message":{"lang":"en-US","value":"File Not Found."}}}}""",
        "HTTP 404: File Not Found.")]
    [InlineData("""{"statusCode":400,"body":{"error":{"code":"BadRequest","message":"Invalid filter clause\nat position 3"}}}""",
        "HTTP 400: Invalid filter clause")]
    [InlineData("""{"statusCode":401,"body":{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret."}}""",
        "HTTP 401: AADSTS7000215: Invalid client secret.")]
    [InlineData("""{"statusCode":500,"body":"Upstream timed out"}""", "HTTP 500: Upstream timed out")]
    [InlineData("""{"statusCode":502,"headers":{"x":"y"}}""", "HTTP 502")]
    [InlineData("""{"body":{"message":"Row not found"}}""", "Row not found")]
    [InlineData("""{"headers":{}}""", null)]
    [InlineData("[1,2]", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void A_failed_step_s_outputs_give_its_status_and_the_message_in_its_body(string? outputs, string? expected)
    {
        Assert.Equal(expected, FlowStepError.FromOutputs(outputs));
    }

    [Theory]
    [InlineData("{\n  \"code\": \"ActionFailed\",\n  \"message\": \"An action failed.\"\n}", "ActionFailed", "An action failed.")]
    [InlineData(null, "ActionFailed", "ActionFailed")]
    [InlineData(null, null, null)]
    public void A_run_s_error_line_reads_the_message_out_of_json_or_falls_back_to_its_code(string? message, string? code, string? expected)
    {
        Assert.Equal(expected, new ProcessRun { Name = "run", Status = "Failed", ErrorCode = code, ErrorMessage = message }.ErrorLine);
    }
}
