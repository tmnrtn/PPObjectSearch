using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class DataverseClientRunsTests
{
    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";

    private static string Page(string rows) => "{\"value\":[" + rows + "]}";

    // ---- Cloud flow runs ----

    [Fact]
    public async Task Cloud_flow_runs_are_filtered_by_workflow_and_ordered_on_the_server()
    {
        var workflowId = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "flowruns?", Page(
            "{\"name\":\"run-1\",\"status\":\"Succeeded\",\"starttime\":\"2026-01-01T10:00:00Z\",\"endtime\":\"2026-01-01T10:00:02Z\"," +
            "\"duration\":2000,\"triggertype\":\"Automated\",\"workflowid\":\"flow-abc\"}"));
        using var client = Fakes.Dataverse(handler);

        var runs = await client.GetCloudFlowRunsAsync(workflowId);

        var run = Assert.Single(runs);
        Assert.Equal("run-1", run.Name);
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(RunOutcome.Succeeded, run.Outcome);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), run.StartTime);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 0, 2, TimeSpan.Zero), run.EndTime);
        Assert.Equal(2000, run.DurationMs);
        Assert.Equal("Automated", run.TriggerType);
        Assert.Equal("flow-abc", run.FlowId);

        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains($"$filter=_workflow_value eq {workflowId}", url);
        Assert.Contains("$top=100", url);
        Assert.EndsWith("&$orderby=starttime desc", url);
    }

    [Fact]
    public async Task Cloud_flow_runs_retry_without_orderby_when_it_is_refused_and_sort_locally()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$orderby=starttime desc", HttpStatusCode.BadRequest, "Elastic table does not support orderby")
            .OnJson(HttpMethod.Get, "flowruns?", Page(
                "{\"name\":\"old\",\"status\":\"Failed\",\"starttime\":\"2026-01-01T00:00:00Z\",\"errorcode\":\"E1\",\"errormessage\":\"bad\"}," +
                "{\"name\":\"new\",\"status\":\"Running\",\"starttime\":\"2026-01-03T00:00:00Z\"}," +
                "{\"name\":\"mid\",\"status\":\"Cancelled\",\"starttime\":\"2026-01-02T00:00:00Z\"}"));
        using var client = Fakes.Dataverse(handler);

        var runs = await client.GetCloudFlowRunsAsync(Guid.NewGuid());

        Assert.Equal(new[] { "new", "mid", "old" }, runs.Select(r => r.Name));
        Assert.Equal("E1", runs[2].ErrorCode);
        Assert.Equal("bad", runs[2].ErrorMessage);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("$orderby", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Cloud_flow_runs_other_errors_are_not_swallowed()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "flowruns?", HttpStatusCode.Forbidden, "no access");
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.GetCloudFlowRunsAsync(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("Succeeded", RunOutcome.Succeeded)]
    [InlineData("succeeded", RunOutcome.Succeeded)]
    [InlineData("Failed", RunOutcome.Failed)]
    [InlineData("TimedOut", RunOutcome.Failed)]
    [InlineData("Running", RunOutcome.Running)]
    [InlineData("Waiting", RunOutcome.Running)]
    [InlineData("Cancelled", RunOutcome.Cancelled)]
    [InlineData("Canceled", RunOutcome.Cancelled)]
    [InlineData("Skipped", RunOutcome.Other)]
    public async Task Cloud_flow_run_status_maps_to_an_outcome(string status, RunOutcome expected)
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "flowruns?", Page($"{{\"name\":\"r\",\"status\":\"{status}\"}}"));
        using var client = Fakes.Dataverse(handler);

        var run = Assert.Single(await client.GetCloudFlowRunsAsync(Guid.NewGuid()));

        Assert.Equal(expected, run.Outcome);
    }

    [Fact]
    public async Task Cloud_flow_run_without_status_reads_unknown()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "flowruns?", Page("{}"));
        using var client = Fakes.Dataverse(handler);

        var run = Assert.Single(await client.GetCloudFlowRunsAsync(Guid.NewGuid()));

        Assert.Equal("Unknown", run.Status);
        Assert.Equal(RunOutcome.Other, run.Outcome);
        Assert.Equal(string.Empty, run.Name);
        Assert.Null(run.DurationMs);
    }

    // ---- Classic workflow runs ----

    [Fact]
    public async Task Classic_workflow_runs_include_activation_records()
    {
        var workflowId = Guid.NewGuid();
        var activation = Guid.NewGuid();

        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid", Page($"{{\"workflowid\":\"{activation}\"}},{{\"workflowid\":\"junk\"}}"))
            .OnJson(HttpMethod.Get, "asyncoperations?", Page(
                "{\"name\":\"job\",\"statuscode\":31,\"statuscode" + Formatted + "\":\"Failed\"," +
                "\"startedon\":\"2026-01-01T00:00:00Z\",\"completedon\":\"2026-01-01T00:00:01.5Z\"," +
                "\"message\":\"raw message\",\"friendlymessage\":\"friendly message\",\"errorcode\":\"-2147\"," +
                "\"_regardingobjectid_value" + Formatted + "\":\"Account A\"}"));
        using var client = Fakes.Dataverse(handler);

        var runs = await client.GetClassicWorkflowRunsAsync(workflowId);

        Assert.Contains($"$filter=_parentworkflowid_value eq {workflowId}", handler.Requests[0].Url);
        var jobs = handler.Requests[1].Url;
        Assert.Contains($"_workflowactivationid_value eq {workflowId} or _workflowactivationid_value eq {activation}", jobs);
        Assert.Contains("$orderby=createdon desc", jobs);
        Assert.Contains("$top=100", jobs);

        var run = Assert.Single(runs);
        Assert.Equal("job", run.Name);
        Assert.Equal("Failed", run.Status);
        Assert.Equal(RunOutcome.Failed, run.Outcome);
        Assert.Equal(1500, run.DurationMs);
        Assert.Equal("System job", run.TriggerType);
        Assert.Equal("-2147", run.ErrorCode);
        Assert.Equal("friendly message", run.ErrorMessage);
        Assert.Equal("Account A", run.Regarding);
    }

    [Theory]
    [InlineData(30, RunOutcome.Succeeded)]
    [InlineData(31, RunOutcome.Failed)]
    [InlineData(32, RunOutcome.Cancelled)]
    [InlineData(0, RunOutcome.Running)]
    [InlineData(10, RunOutcome.Running)]
    [InlineData(20, RunOutcome.Running)]
    [InlineData(21, RunOutcome.Running)]
    [InlineData(22, RunOutcome.Running)]
    [InlineData(99, RunOutcome.Other)]
    public async Task Classic_workflow_status_code_maps_to_an_outcome(int code, RunOutcome expected)
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "workflows?", Page(""))
            .OnJson(HttpMethod.Get, "asyncoperations?", Page($"{{\"statuscode\":{code}}}"));
        using var client = Fakes.Dataverse(handler);

        var run = Assert.Single(await client.GetClassicWorkflowRunsAsync(Guid.NewGuid()));

        Assert.Equal(expected, run.Outcome);
        Assert.Equal(code.ToString(), run.Status);
    }

    [Fact]
    public async Task Classic_workflow_run_falls_back_to_created_on_and_raw_message()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "workflows?", Page(""))
            .OnJson(HttpMethod.Get, "asyncoperations?", Page(
                "{\"createdon\":\"2026-02-01T00:00:00Z\",\"friendlymessage\":\" \",\"message\":\"raw\"}"));
        using var client = Fakes.Dataverse(handler);

        var run = Assert.Single(await client.GetClassicWorkflowRunsAsync(Guid.NewGuid()));

        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), run.StartTime);
        Assert.Null(run.DurationMs);
        Assert.Equal("raw", run.ErrorMessage);
        Assert.Equal("Unknown", run.Status);
        Assert.Equal(RunOutcome.Other, run.Outcome);
    }

    // ---- Plug-in trace setting ----

    [Theory]
    [InlineData(0, PluginTraceSetting.Off)]
    [InlineData(1, PluginTraceSetting.Exception)]
    [InlineData(2, PluginTraceSetting.All)]
    public async Task Plugin_trace_setting_is_read_from_the_organization(int value, PluginTraceSetting expected)
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "organizations?$select=plugintracelogsetting",
            Page($"{{\"plugintracelogsetting\":{value}}}"));
        using var client = Fakes.Dataverse(handler);

        Assert.Equal(expected, await client.GetPluginTraceSettingAsync());
    }

    [Fact]
    public async Task Plugin_trace_setting_is_null_when_absent()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "organizations?", Page("{}"));
        using var client = Fakes.Dataverse(handler);

        Assert.Null(await client.GetPluginTraceSettingAsync());
    }

    // ---- Plug-in trace log ----

    private const string TraceRow =
        "{\"plugintracelogid\":\"{ID}\",\"createdon\":\"2026-01-01T00:00:00Z\",\"typename\":\"Contoso.Plugins.OnCreate\"," +
        "\"messagename\":\"Create\",\"primaryentity\":\"account\",\"mode\":0,\"mode" + Formatted + "\":\"Synchronous\"," +
        "\"depth\":1,\"performanceexecutionduration\":42,\"messageblock\":\"trace\",\"exceptiondetails\":\"boom\"," +
        "\"correlationid\":\"{CORR}\"}";

    [Fact]
    public async Task Trace_log_for_a_step_filters_on_the_step_id_and_reads_every_field()
    {
        var stepId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "plugintracelogs?",
            Page(TraceRow.Replace("{ID}", id.ToString()).Replace("{CORR}", correlation.ToString()) + ",{\"plugintracelogid\":\"bad\"}"));
        using var client = Fakes.Dataverse(handler);

        var entries = await client.GetPluginTraceLogAsync(stepId, 92);

        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains($"$filter=pluginstepid eq {stepId}", url);
        Assert.Contains("$orderby=createdon desc", url);

        var entry = Assert.Single(entries!);
        Assert.Equal(id, entry.Id);
        Assert.Equal("Contoso.Plugins.OnCreate", entry.TypeName);
        Assert.Equal("Create", entry.MessageName);
        Assert.Equal("account", entry.PrimaryEntity);
        Assert.Equal("Synchronous", entry.Mode);
        Assert.Equal(1, entry.Depth);
        Assert.Equal(42, entry.DurationMs);
        Assert.Equal("trace", entry.MessageBlock);
        Assert.Equal("boom", entry.ExceptionDetails);
        Assert.Equal(correlation, entry.CorrelationId);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), entry.CreatedOn);
    }

    [Fact]
    public async Task Trace_log_for_a_plugin_type_filters_on_its_escaped_class_name()
    {
        var typeId = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"plugintypes({typeId})", "{\"typename\":\"Contoso.O'Brien\"}")
            .OnJson(HttpMethod.Get, "plugintracelogs?", Page(""));
        using var client = Fakes.Dataverse(handler);

        var entries = await client.GetPluginTraceLogAsync(typeId, 90);

        Assert.Empty(entries!);
        Assert.Contains("$filter=typename eq 'Contoso.O''Brien'", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Trace_log_for_a_plugin_type_without_a_name_is_empty_without_querying_the_log()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "plugintypes(", "{\"typename\":\"\"}");
        using var client = Fakes.Dataverse(handler);

        var entries = await client.GetPluginTraceLogAsync(Guid.NewGuid(), 90);

        Assert.NotNull(entries);
        Assert.Empty(entries);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Trace_log_for_an_assembly_filters_on_every_distinct_type_name()
    {
        var assemblyId = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "plugintypes?", Page(
                "{\"typename\":\"A.One\"},{\"typename\":\"a.one\"},{\"typename\":\"A.Two\"},{\"typename\":null}"))
            .OnJson(HttpMethod.Get, "plugintracelogs?", Page(""));
        using var client = Fakes.Dataverse(handler);

        await client.GetPluginTraceLogAsync(assemblyId, 91);

        Assert.Contains($"$filter=_pluginassemblyid_value eq {assemblyId}", handler.Requests[0].Url);
        Assert.Contains("$filter=typename eq 'A.One' or typename eq 'A.Two'&", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Trace_log_for_an_assembly_without_types_is_empty_without_querying_the_log()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "plugintypes?", Page(""));
        using var client = Fakes.Dataverse(handler);

        var entries = await client.GetPluginTraceLogAsync(Guid.NewGuid(), 91);

        Assert.Empty(entries!);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(29)]
    [InlineData(93)]
    public async Task Trace_log_is_null_for_other_component_types(int componentType)
    {
        var handler = new FakeHttpHandler();
        using var client = Fakes.Dataverse(handler);

        Assert.Null(await client.GetPluginTraceLogAsync(Guid.NewGuid(), componentType));
        Assert.Empty(handler.Requests);
    }
}
