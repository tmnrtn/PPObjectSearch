using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.FlowDesign;

/// <summary>
/// Run history merges Dataverse's copy with Power Automate's live list, so runs in progress and
/// ones just cancelled show - which Dataverse alone missed.
/// </summary>
public class LiveRunsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static ProcessRun Run(string name, string status, int minutesAgo, bool live = false, string? error = null) => new()
    {
        Name = name,
        Status = status,
        Outcome = PPObjectSearch.Dataverse.DataverseClient.FlowOutcome(status),
        StartTime = T0.AddMinutes(-minutesAgo),
        EndTime = status is "Running" or "Waiting" ? null : T0.AddMinutes(-minutesAgo + 1),
        ErrorMessage = error,
        TriggerType = live ? null : "Automated",
        FlowId = "flow-1",
        IsLiveOnly = live
    };

    // ---------------------------------------------------------------- merging

    [Fact]
    public void A_run_in_progress_that_dataverse_lacks_is_added_and_marked_live()
    {
        var dataverse = new[] { Run("old", "Succeeded", 60) };
        var live = new[] { Run("now", "Running", 1, live: true), Run("old", "Succeeded", 60, live: true) };

        var result = RunHistoryMerge.Merge(dataverse, live);

        Assert.Equal(["now", "old"], result.Runs.Select(r => r.Name));
        Assert.True(result.Runs[0].IsLiveOnly);
        Assert.Equal(RunOutcome.Running, result.Runs[0].Outcome);
        Assert.False(result.Runs[1].IsLiveOnly);
        Assert.Equal(1, result.LiveOnly);
    }

    [Fact]
    public void A_run_dataverse_still_shows_as_running_takes_its_live_cancelled_status()
    {
        var dataverse = new[] { Run("r1", "Running", 5) };
        var live = new[] { Run("r1", "Cancelled", 5, live: true) };

        var result = RunHistoryMerge.Merge(dataverse, live);

        var run = Assert.Single(result.Runs);
        Assert.Equal("Cancelled", run.Status);
        Assert.Equal(RunOutcome.Cancelled, run.Outcome);
        Assert.False(run.IsLiveOnly);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public void Dataverse_fills_in_what_the_live_list_lacks()
    {
        var dataverse = new[] { Run("r1", "Failed", 5, error: "Stored error") };
        var live = new[] { new ProcessRun { Name = "r1", Status = "Failed", Outcome = RunOutcome.Failed, IsLiveOnly = true } };

        var run = Assert.Single(RunHistoryMerge.Merge(dataverse, live).Runs);

        Assert.Equal("Stored error", run.ErrorMessage);
        Assert.Equal("Automated", run.TriggerType);
        Assert.Equal(T0.AddMinutes(-5), run.StartTime);
    }

    [Fact]
    public void Older_dataverse_runs_beyond_the_live_list_are_kept()
    {
        var dataverse = new[] { Run("recent", "Succeeded", 10), Run("ancient", "Succeeded", 5000) };
        var live = new[] { Run("recent", "Succeeded", 10, live: true) };

        var result = RunHistoryMerge.Merge(dataverse, live);

        Assert.Equal(["recent", "ancient"], result.Runs.Select(r => r.Name));
        Assert.Equal(0, result.LiveOnly);
        Assert.Equal(0, result.Updated);
    }

    [Fact]
    public void Run_ids_match_whatever_their_case()
    {
        var result = RunHistoryMerge.Merge([Run("ABC", "Running", 3)], [Run("abc", "Succeeded", 3, live: true)]);

        Assert.Equal("Succeeded", Assert.Single(result.Runs).Status);
    }

    [Fact]
    public void With_no_live_list_dataverse_stands_alone()
    {
        var dataverse = new[] { Run("a", "Succeeded", 1), Run("b", "Failed", 2) };

        var result = RunHistoryMerge.Merge(dataverse, []);

        Assert.Equal(["a", "b"], result.Runs.Select(r => r.Name));
        Assert.All(result.Runs, r => Assert.False(r.IsLiveOnly));
    }

    [Fact]
    public void With_no_dataverse_history_the_live_list_stands_alone()
    {
        var result = RunHistoryMerge.Merge([], [Run("a", "Running", 1, live: true), Run("b", "Succeeded", 9, live: true)]);

        Assert.Equal(2, result.LiveOnly);
        Assert.All(result.Runs, r => Assert.True(r.IsLiveOnly));
    }

    [Fact]
    public void A_live_run_notes_where_it_came_from()
    {
        Assert.Contains("Power Automate", Run("x", "Running", 1, live: true).LiveNote);
        Assert.Null(Run("x", "Succeeded", 1).LiveNote);
    }

    // ---------------------------------------------------------------- the live list

    private const string RunsUrl = "https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/environments/env-1/flows/flow-1/runs";

    private static string RunsBody => JsonSerializer.Serialize(new
    {
        value = new object[]
        {
            new { name = "run-running", properties = new { status = "Running", startTime = "2026-10-01T08:59:00Z", trigger = new { name = "When_a_row_is_added" } } },
            new { name = "run-cancelled", properties = new { status = "Cancelled", startTime = "2026-10-01T08:50:00Z", endTime = "2026-10-01T08:52:30Z" } },
            new { name = "run-failed", properties = new { status = "Failed", startTime = "2026-10-01T08:40:00Z", endTime = "2026-10-01T08:40:02Z",
                                                         error = new { code = "ActionFailed", message = "An action failed." } } },
        }
    });

    [Fact]
    public async Task The_live_list_reads_every_status_including_runs_in_progress()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, RunsUrl, RunsBody);

        var runs = await new PowerAutomateClient(TestAuth.Tokens(), handler).GetRunsAsync("env-1", "flow-1");

        Assert.Equal([RunOutcome.Running, RunOutcome.Cancelled, RunOutcome.Failed], runs.Select(r => r.Outcome));
        Assert.All(runs, r => Assert.True(r.IsLiveOnly));
        Assert.Null(runs[0].EndTime);
        Assert.Null(runs[0].DurationMs);
        Assert.Equal("When_a_row_is_added", runs[0].TriggerType);
        Assert.Equal(150_000, runs[1].DurationMs);
        Assert.Equal("ActionFailed", runs[2].ErrorCode);
        Assert.Contains("$top=50", handler.Requests[0].Url);
    }

    [Fact]
    public async Task A_non_owner_gets_the_list_through_the_admin_scope()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/scopes/admin/", RunsBody)
            .OnError(HttpMethod.Get, RunsUrl, HttpStatusCode.Forbidden, "no");

        var runs = await new PowerAutomateClient(TestAuth.Tokens(), handler).GetRunsAsync("env-1", "flow-1");

        Assert.Equal(3, runs.Count);
    }

    [Fact]
    public async Task Refused_both_ways_says_who_can_list_runs()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "/runs", HttpStatusCode.Forbidden, "no");

        var ex = await Assert.ThrowsAsync<PowerAutomateException>(() =>
            new PowerAutomateClient(TestAuth.Tokens(), handler).GetRunsAsync("env-1", "flow-1"));

        Assert.Contains("own or co-own the flow", ex.Message);
    }
}
