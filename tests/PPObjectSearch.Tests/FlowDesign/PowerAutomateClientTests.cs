using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.FlowDesign;

public class PowerAutomateClientTests
{
    private const string Env = "env-1";
    private const string Flow = "flow-1";
    private const string RunName = "08584501234567890123456789CU01";
    private const string MakerRun = $"https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/environments/{Env}/flows/{Flow}/runs/{RunName}";
    private const string AdminRun = $"https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/scopes/admin/environments/{Env}/flows/{Flow}/runs/{RunName}";

    private static PowerAutomateClient Client(FakeHttpHandler handler) => new(TestAuth.Tokens(), handler);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static object RunBody(string status = "Failed") => new
    {
        name = RunName,
        properties = new
        {
            status,
            startTime = "2026-07-01T08:15:00Z",
            endTime = "2026-07-01T08:15:12Z",
            error = new { code = "ActionFailed", message = "An action failed. No dependent actions succeeded." },
            trigger = new { name = "manual", status = "Succeeded", startTime = "2026-07-01T08:15:00Z", endTime = "2026-07-01T08:15:00Z" }
        }
    };

    private static object Action(string name, string status, string? errorCode = null, string? errorMessage = null) => new
    {
        name,
        properties = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["code"] = status == "Failed" ? "BadRequest" : "OK",
            ["startTime"] = "2026-07-01T08:15:01Z",
            ["endTime"] = "2026-07-01T08:15:02.5Z",
            ["error"] = errorCode is null ? null : new { code = errorCode, message = errorMessage },
            ["inputsLink"] = new { uri = $"https://prod-01.logic.azure.com/in/{name}?sig=abc" },
            ["outputsLink"] = new { uri = $"https://prod-01.logic.azure.com/out/{name}?sig=abc" },
        }
    };

    [Fact]
    public async Task A_run_is_read_with_every_steps_result()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, MakerRun + "/actions", Json(new { value = new[] { Action("List_rows", "Succeeded"), Action("Update_row", "Failed", "BadRequest", "Row not found") } }))
            .OnJson(HttpMethod.Get, MakerRun, Json(RunBody()));

        var run = await Client(handler).GetRunAsync(Env, Flow, RunName);

        Assert.Equal(FlowStepOutcome.Failed, run.Outcome);
        Assert.Equal("ActionFailed", run.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(12), run.Duration);
        Assert.False(run.ViaAdminScope);
        Assert.Equal(FlowStepOutcome.Succeeded, run.Trigger!.Outcome);

        Assert.Equal(2, run.Actions.Count);
        var failed = run.Actions["Update_row"];
        Assert.Equal(FlowStepOutcome.Failed, failed.Outcome);
        Assert.Equal("BadRequest", failed.ErrorCode);
        Assert.Equal("Row not found", failed.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(1.5), failed.Duration);
        Assert.Equal("https://prod-01.logic.azure.com/in/Update_row?sig=abc", failed.InputsLink);
        Assert.Equal("Bearer token:https://service.flow.microsoft.com/", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task Every_page_of_actions_is_read()
    {
        const string page2 = "https://api.flow.microsoft.com/next-page-2";
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, page2, Json(new { value = new[] { Action("C", "Skipped") } }))
            .OnJson(HttpMethod.Get, MakerRun + "/actions", Json(new Dictionary<string, object>
            {
                ["value"] = new[] { Action("A", "Succeeded"), Action("B", "Succeeded") },
                ["nextLink"] = page2
            }))
            .OnJson(HttpMethod.Get, MakerRun, Json(RunBody("Succeeded")));

        var run = await Client(handler).GetRunAsync(Env, Flow, RunName);

        Assert.Equal(["A", "B", "C"], run.Actions.Keys.OrderBy(k => k));
        Assert.Equal(FlowStepOutcome.Skipped, run.Actions["C"].Outcome);
    }

    [Fact]
    public async Task A_non_owner_is_served_through_the_admin_scope()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, AdminRun + "/actions", Json(new { value = new[] { Action("A", "Succeeded") } }))
            .OnJson(HttpMethod.Get, AdminRun, Json(RunBody("Succeeded")))
            .OnError(HttpMethod.Get, MakerRun, HttpStatusCode.Forbidden, "The caller does not have permission.");

        var run = await Client(handler).GetRunAsync(Env, Flow, RunName);

        Assert.True(run.ViaAdminScope);
        Assert.Single(run.Actions);
        Assert.StartsWith(AdminRun, run.RunUrl);
    }

    [Fact]
    public async Task Refused_both_ways_says_who_can_see_it()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "/runs/", HttpStatusCode.Forbidden, "nope");

        var ex = await Assert.ThrowsAsync<PowerAutomateException>(() => Client(handler).GetRunAsync(Env, Flow, RunName));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("own or co-own the flow", ex.Message);
        Assert.Contains("admin of the environment", ex.Message);
    }

    [Fact]
    public async Task A_run_too_old_to_have_details_says_so()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, MakerRun, HttpStatusCode.NotFound, "WorkflowRunNotFound");

        var ex = await Assert.ThrowsAsync<PowerAutomateException>(() => Client(handler).GetRunAsync(Env, Flow, RunName));

        Assert.Contains("about 28 days", ex.Message);
    }

    [Fact]
    public async Task A_sign_in_failure_is_reported_as_such()
    {
        var client = new PowerAutomateClient(TestAuth.NoTokens(), new FakeHttpHandler());

        var ex = await Assert.ThrowsAsync<PowerAutomateException>(() => client.GetRunAsync(Env, Flow, RunName));

        Assert.StartsWith("Could not sign in to Power Automate", ex.Message);
    }

    [Fact]
    public async Task Iterations_are_read_from_a_step_inside_the_loop()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/actions/Inner_step/repetitions", Json(new
            {
                value = new object[]
                {
                    new { name = "000000", properties = new { status = "Succeeded", repetitionIndexes = new[] { new { scopeName = "Loop", itemIndex = 0 } },
                        startTime = "2026-07-01T08:15:01Z", endTime = "2026-07-01T08:15:02Z", inputsLink = new { uri = "https://x/in0" } } },
                    new { name = "000001", properties = new { status = "Failed", repetitionIndexes = new[] { new { scopeName = "Loop", itemIndex = 1 } },
                        error = new { code = "BadRequest", message = "Bad item" } } },
                }
            }))
            .OnJson(HttpMethod.Get, MakerRun + "/actions", Json(new { value = Array.Empty<object>() }))
            .OnJson(HttpMethod.Get, MakerRun, Json(RunBody()));

        var client = Client(handler);
        var run = await client.GetRunAsync(Env, Flow, RunName);
        var repetitions = await client.GetRepetitionsAsync(run, "Inner_step");

        Assert.Equal(["#1", "#2"], repetitions.Select(r => r.Label));
        Assert.Equal(FlowStepOutcome.Failed, repetitions[1].Outcome);
        Assert.Equal("Bad item", repetitions[1].ErrorMessage);
        Assert.Equal("https://x/in0", repetitions[0].InputsLink);
        Assert.Equal("#1 · Succeeded · 1 s", repetitions[0].Summary);
    }

    [Fact]
    public async Task Nested_loop_iterations_name_every_level()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/repetitions", Json(new
            {
                value = new[] { new { properties = new { status = "Succeeded",
                    repetitionIndexes = new[] { new { scopeName = "Outer", itemIndex = 2 }, new { scopeName = "Inner", itemIndex = 0 } } } } }
            }))
            .OnJson(HttpMethod.Get, MakerRun + "/actions", Json(new { value = Array.Empty<object>() }))
            .OnJson(HttpMethod.Get, MakerRun, Json(RunBody()));

        var client = Client(handler);
        var repetitions = await client.GetRepetitionsAsync(await client.GetRunAsync(Env, Flow, RunName), "Step");

        Assert.Equal("#3 › #1", Assert.Single(repetitions).Label);
    }

    [Fact]
    public async Task Inputs_and_outputs_are_fetched_without_the_users_token_and_tidied()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "logic.azure.com/in/", """{"body":{"name":"x"}}""");

        var content = await Client(handler).GetContentAsync("https://prod-01.logic.azure.com/in/A?sig=abc");

        Assert.Null(Assert.Single(handler.Requests).Authorization);
        Assert.Contains("\n", content);            // prettified
        Assert.Contains("\"name\": \"x\"", content);
    }

    [Fact]
    public async Task An_expired_content_link_says_so()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Get, "logic.azure.com", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<PowerAutomateException>(() => Client(handler).GetContentAsync("https://prod-01.logic.azure.com/in/A"));

        Assert.Contains("expired", ex.Message);
    }

    [Theory]
    [InlineData("Succeeded", FlowStepOutcome.Succeeded)]
    [InlineData("Failed", FlowStepOutcome.Failed)]
    [InlineData("Faulted", FlowStepOutcome.Failed)]
    [InlineData("Skipped", FlowStepOutcome.Skipped)]
    [InlineData("TimedOut", FlowStepOutcome.TimedOut)]
    [InlineData("Cancelled", FlowStepOutcome.Cancelled)]
    [InlineData("Aborted", FlowStepOutcome.Cancelled)]
    [InlineData("Running", FlowStepOutcome.Running)]
    [InlineData("Waiting", FlowStepOutcome.Waiting)]
    [InlineData("Something", FlowStepOutcome.Other)]
    public void Statuses_map_to_outcomes(string status, FlowStepOutcome expected) =>
        Assert.Equal(expected, PowerAutomateClient.Outcome(status));

    [Theory]
    [InlineData(340, "340 ms")]
    [InlineData(1200, "1.2 s")]
    [InlineData(185_000, "3m 05s")]
    [InlineData(7_800_000, "2h 10m")]
    public void Durations_read_naturally(int milliseconds, string expected) =>
        Assert.Equal(expected, FlowRunFormat.Duration(TimeSpan.FromMilliseconds(milliseconds)));
}
