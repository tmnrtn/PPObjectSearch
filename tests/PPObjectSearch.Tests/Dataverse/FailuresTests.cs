using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class FailuresTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid FlowA = Guid.Parse("c0000000-0000-0000-0000-00000000000a");
    private static readonly Guid FlowB = Guid.Parse("c0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Activation = Guid.Parse("c0000000-0000-0000-0000-0000000000ac");
    private static readonly Guid Definition = Guid.Parse("c0000000-0000-0000-0000-0000000000de");
    private static readonly Guid Step = Guid.Parse("c0000000-0000-0000-0000-0000000000ff");

    private static FakeHttpHandler Handler(bool tracesFail = false) => (tracesFail
        ? new FakeHttpHandler().OnError(HttpMethod.Get, "plugintracelogs?", System.Net.HttpStatusCode.Forbidden, "no read on traces")
        : new FakeHttpHandler())
        .OnJson(HttpMethod.Get, "flowruns?fetchXml", $$"""
            {"value":[{"cnt":3,"g0":"{{FlowA}}"},{"cnt":1,"g0":"{{FlowB}}"}]}
            """)
        .OnJson(HttpMethod.Get, "flowruns?", $$"""
            {"value":[
              {"name":"run1","status":"Failed","starttime":"2026-10-01T10:00:00Z","errorcode":"ActionFailed","errormessage":"Record 1a2b3c4d-0000-0000-0000-000000000001 not found","_workflow_value":"{{FlowA}}"},
              {"name":"run2","status":"Succeeded","starttime":"2026-10-01T11:00:00Z","_workflow_value":"{{FlowA}}"},
              {"name":"run3","status":"Failed","starttime":"2026-10-01T12:00:00Z","errormessage":"Record 1a2b3c4d-0000-0000-0000-000000000002 not found","_workflow_value":"{{FlowA}}"},
              {"name":"run4","status":"Succeeded","starttime":"2026-10-01T12:30:00Z","_workflow_value":"{{FlowB}}"}
            ]}
            """)
        .OnJson(HttpMethod.Get, "workflows?$select=workflowid,name&", $$"""
            {"value":[{"workflowid":"{{FlowA}}","name":"Sync orders"},{"workflowid":"{{FlowB}}","name":"Notify"}]}
            """)
        .OnJson(HttpMethod.Get, "asyncoperations?", $$"""
            {"value":[{"name":"Escalate case","completedon":"2026-10-01T09:00:00Z","friendlymessage":"Owner is disabled","errorcode":-2147220891,
              "_workflowactivationid_value":"{{Activation}}","_regardingobjectid_value":"x","_regardingobjectid_value@OData.Community.Display.V1.FormattedValue":"CAS-001"}]}
            """)
        .OnJson(HttpMethod.Get, "workflows?$select=workflowid,name,_parentworkflowid_value", $$"""
            {"value":[{"workflowid":"{{Activation}}","name":"Escalate case","_parentworkflowid_value":"{{Definition}}"}]}
            """)
        .OnJson(HttpMethod.Get, "organizations?", """{"value":[{"plugintracelogsetting":1}]}""")
        .OnJson(HttpMethod.Get, "plugintracelogs?", $$"""
            {"value":[{"plugintracelogid":"t1","createdon":"2026-10-01T08:00:00Z","typename":"Contoso.Plugins.Validate","messagename":"Update",
              "primaryentity":"account","pluginstepid":"{{Step}}",
              "exceptiondetails":"Unhandled exception: \r\nException type: System.ServiceModel.FaultException\r\nMessage: Name is required\r\nDetail: ..."}]}
            """);

    [Fact]
    public async Task Each_source_is_read_for_the_period_and_mapped_to_its_component()
    {
        var handler = Handler();
        var data = await Fakes.Dataverse(handler).GetFailuresAsync(From, To, new FailureQuery());

        Assert.Empty(data.Notes);
        Assert.Equal(4, data.Events.Count);

        var flow = data.Events.Where(e => e.Source == FailureSource.CloudFlow).ToList();
        Assert.Equal(2, flow.Count);
        Assert.All(flow, e => Assert.Equal("Sync orders", e.ComponentName));
        Assert.Equal(3, data.RunCounts[FlowA.ToString()]);
        Assert.Equal(1, data.RunCounts[FlowB.ToString()]);

        var job = Assert.Single(data.Events, e => e.Source == FailureSource.ClassicWorkflow);
        Assert.Equal(Definition, job.WorkflowId);
        Assert.Equal("Owner is disabled", job.ErrorMessage);
        Assert.Equal("CAS-001", job.Regarding);

        var plugin = Assert.Single(data.Events, e => e.Source == FailureSource.Plugin);
        Assert.Equal("Name is required", plugin.ErrorMessage);
        Assert.Equal("Update of account", plugin.Context);
        Assert.Equal(Step, plugin.StepId);

        var flowUrl = Uri.UnescapeDataString(handler.Requests.First(r => r.Url.Contains("flowruns?")).Url);
        Assert.Contains("starttime ge 2026-10-01T00:00:00Z and starttime le 2026-10-02T00:00:00Z", flowUrl);
        Assert.Contains("operationtype eq 10 and statuscode eq 31", Uri.UnescapeDataString(handler.Requests.First(r => r.Url.Contains("asyncoperations?")).Url));
    }

    [Fact]
    public async Task A_trace_with_an_empty_exception_is_read_but_is_not_a_failure()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "organizations?", """{"value":[{"plugintracelogsetting":2}]}""")
            .OnJson(HttpMethod.Get, "plugintracelogs?", """
                {"value":[
                  {"plugintracelogid":"t1","createdon":"2026-10-01T08:00:00Z","typename":"Contoso.Plugins.Audit","messagename":"Retrieve","exceptiondetails":""},
                  {"plugintracelogid":"t2","createdon":"2026-10-01T09:00:00Z","typename":"Contoso.Plugins.Validate","messagename":"Update","exceptiondetails":"Message: Name is required"}
                ]}
                """);

        var data = await Fakes.Dataverse(handler).GetFailuresAsync(From, To, new FailureQuery { CloudFlows = false, ClassicWorkflows = false });

        Assert.Equal((2, 1), (data.TraceLogsRead, data.TraceExceptionsRead));
        Assert.Equal("Contoso.Plugins.Validate", Assert.Single(data.Events).ComponentName);
    }

    // ---- the cap ----

    private static readonly FailureQuery PluginsOnly = new() { CloudFlows = false, ClassicWorkflows = false };

    /// <summary>One page of trace log entries: some with an empty exception, then some with a real one.</summary>
    private static FakeHttpHandler Traces(int clean, int failing)
    {
        var rows = Enumerable.Range(0, clean)
            .Select(i => $$"""{"plugintracelogid":"c{{i}}","createdon":"2026-10-01T12:00:00Z","typename":"Contoso.Plugins.Audit","exceptiondetails":""}""")
            .Concat(Enumerable.Range(0, failing)
                .Select(i => $$"""{"plugintracelogid":"f{{i}}","createdon":"2026-10-01T08:00:00Z","typename":"Contoso.Plugins.Validate","exceptiondetails":"Message: Boom"}"""));

        return new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "organizations?", """{"value":[{"plugintracelogsetting":2}]}""")
            .OnJson(HttpMethod.Get, "plugintracelogs?", "{\"value\":[" + string.Join(",", rows) + "]}");
    }

    [Fact]
    public async Task Flow_runs_are_read_newest_first_so_a_period_cut_short_loses_its_oldest()
    {
        var handler = Handler();
        await Fakes.Dataverse(handler).GetFailuresAsync(From, To, new FailureQuery { ClassicWorkflows = false, Plugins = false });

        Assert.Contains("$orderby=starttime desc", Uri.UnescapeDataString(handler.Requests.First(r => r.Url.Contains("flowruns?")).Url));
    }

    [Fact]
    public async Task Runs_of_flows_outside_a_long_scope_do_not_count_toward_the_cap()
    {
        // More than 40 flows in scope, so runs are filtered here rather than asked for by id.
        var scope = Enumerable.Range(0, 41).Select(i => Guid.Parse($"d0000000-0000-0000-0000-{i:D12}")).ToHashSet();
        var inScope = scope.First();

        var rows = Enumerable.Range(0, DataverseClient.MaxFailureRows)
            .Select(i => $$"""{"name":"other{{i}}","status":"Failed","starttime":"2026-10-01T12:00:00Z","_workflow_value":"{{FlowA}}"}""")
            .Append($$"""{"name":"mine","status":"Failed","starttime":"2026-10-01T08:00:00Z","errormessage":"Boom","_workflow_value":"{{inScope}}"}""");

        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "flowruns?fetchXml", $$"""{"value":[{"cnt":5000,"g0":"{{FlowA}}"},{"cnt":1,"g0":"{{inScope}}"}]}""")
            .OnJson(HttpMethod.Get, "flowruns?", "{\"value\":[" + string.Join(",", rows) + "]}")
            .OnJson(HttpMethod.Get, "workflows?", """{"value":[]}""");

        var data = await Fakes.Dataverse(handler).GetFailuresAsync(From, To,
            new FailureQuery { ClassicWorkflows = false, Plugins = false, WorkflowIds = scope });

        Assert.Equal("mine", Assert.Single(data.Events).RunName);
        Assert.Empty(data.Notes);
        Assert.Equal(1, Assert.Single(data.RunCounts).Value);
    }

    [Fact]
    public async Task Only_failed_runs_are_read_and_every_run_is_counted_by_an_aggregate()
    {
        var handler = Handler();
        var data = await Fakes.Dataverse(handler).GetFailuresAsync(From, To, new FailureQuery { ClassicWorkflows = false, Plugins = false });

        var read = handler.Requests.Single(r => r.Url.Contains("flowruns?$select=name")).Url;
        Assert.Contains("and (status eq 'Failed' or status eq 'TimedOut')", read);

        var count = handler.Requests.Single(r => r.Url.Contains("flowruns?fetchXml")).Url;
        Assert.Contains("<attribute name='workflow' alias='g0' groupby='true'/>", count);
        Assert.Contains("<condition attribute='starttime' operator='ge' value='2026-10-01T00:00:00Z'/>", count);
        Assert.DoesNotContain("operator='in'", count);
        Assert.Equal(4, data.FlowRunsRead);
    }

    [Fact]
    public async Task Past_the_cap_of_failed_runs_the_read_says_it_was_cut_short()
    {
        var rows = Enumerable.Range(0, DataverseClient.MaxFailureRows + 1)
            .Select(i => $$"""{"name":"run{{i}}","status":"Failed","starttime":"2026-10-01T12:00:00Z","_workflow_value":"{{FlowA}}"}""");

        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "flowruns?fetchXml", $$"""{"value":[{"cnt":9000,"g0":"{{FlowA}}"}]}""")
            .OnJson(HttpMethod.Get, "flowruns?", "{\"value\":[" + string.Join(",", rows) + "]}")
            .OnJson(HttpMethod.Get, "workflows?", """{"value":[]}""");

        var data = await Fakes.Dataverse(handler).GetFailuresAsync(From, To, new FailureQuery { ClassicWorkflows = false, Plugins = false });

        Assert.Equal(DataverseClient.MaxFailureRows, data.Events.Count);
        Assert.Equal(9000, data.RunCounts[FlowA.ToString()]);
        Assert.Equal($"Cloud flows: only the newest {DataverseClient.MaxFailureRows:N0} failed runs were read.", Assert.Single(data.Notes));
    }

    /// <summary>Flow runs where the flowrun table will not aggregate, so they are counted row by row.</summary>
    private static FakeHttpHandler Unaggregated(int runs) => new FakeHttpHandler()
        .OnError(HttpMethod.Get, "flowruns?fetchXml", System.Net.HttpStatusCode.BadRequest, "Aggregate queries are not supported on elastic tables")
        .OnJson(HttpMethod.Get, "flowruns?$select=_workflow_value", "{\"value\":[" +
            string.Join(",", Enumerable.Range(0, runs).Select(_ => $$"""{"_workflow_value":"{{FlowA}}"}""")) + "]}")
        .OnJson(HttpMethod.Get, "flowruns?", $$"""
            {"value":[{"name":"run1","status":"Failed","starttime":"2026-10-01T10:00:00Z","_workflow_value":"{{FlowA}}"}]}
            """)
        .OnJson(HttpMethod.Get, "workflows?", """{"value":[]}""");

    [Fact]
    public async Task Runs_are_counted_row_by_row_when_the_table_will_not_aggregate()
    {
        var data = await Fakes.Dataverse(Unaggregated(runs: 7)).GetFailuresAsync(From, To, new FailureQuery { ClassicWorkflows = false, Plugins = false });

        Assert.Single(data.Events);
        Assert.Equal(7, data.RunCounts[FlowA.ToString()]);
        Assert.Empty(data.Notes);
    }

    [Fact]
    public async Task Too_many_runs_to_count_row_by_row_leaves_the_rate_out_and_says_so()
    {
        var data = await Fakes.Dataverse(Unaggregated(runs: DataverseClient.MaxScannedRows + 1))
            .GetFailuresAsync(From, To, new FailureQuery { ClassicWorkflows = false, Plugins = false });

        Assert.Single(data.Events);
        Assert.Empty(data.RunCounts);
        Assert.Equal(DataverseClient.MaxScannedRows, data.FlowRunsRead);
        Assert.Equal($"Cloud flows: over {DataverseClient.MaxScannedRows:N0} runs in the period, too many to count here, so failure rates are not shown.",
            Assert.Single(data.Notes));
    }

    [Fact]
    public async Task Traces_with_an_empty_exception_do_not_crowd_out_real_ones()
    {
        var data = await Fakes.Dataverse(Traces(clean: DataverseClient.MaxFailureRows, failing: 1)).GetFailuresAsync(From, To, PluginsOnly);

        Assert.Equal("Contoso.Plugins.Validate", Assert.Single(data.Events).ComponentName);
        Assert.Empty(data.Notes);
    }

    [Fact]
    public async Task Past_the_cap_of_real_exceptions_the_read_says_it_was_cut_short()
    {
        var data = await Fakes.Dataverse(Traces(clean: 0, failing: DataverseClient.MaxFailureRows + 1)).GetFailuresAsync(From, To, PluginsOnly);

        Assert.Equal(DataverseClient.MaxFailureRows, data.Events.Count);
        Assert.Equal($"Plug-ins: only the newest {DataverseClient.MaxFailureRows:N0} traced exceptions were read.", Assert.Single(data.Notes));
    }

    [Fact]
    public async Task A_read_of_mostly_clean_traces_stops_at_the_scan_limit_and_says_so()
    {
        var data = await Fakes.Dataverse(Traces(clean: DataverseClient.MaxScannedRows, failing: 1)).GetFailuresAsync(From, To, PluginsOnly);

        Assert.Empty(data.Events);
        Assert.Equal(DataverseClient.MaxScannedRows, data.TraceLogsRead);
        Assert.Equal($"Plug-ins: only the newest {DataverseClient.MaxScannedRows:N0} trace log entries were read, so older exceptions are missing.",
            Assert.Single(data.Notes));
    }

    [Fact]
    public async Task A_solution_scope_keeps_only_its_components_and_a_failing_source_is_noted()
    {
        var handler = Handler(tracesFail: true);
        var query = new FailureQuery
        {
            WorkflowIds = new HashSet<Guid> { FlowB },
            PluginStepIds = new HashSet<Guid> { Step }
        };

        var data = await Fakes.Dataverse(handler).GetFailuresAsync(From, To, query);

        Assert.Empty(data.Events);
        Assert.Contains($"_workflow_value eq {FlowB}", Uri.UnescapeDataString(handler.Requests.First(r => r.Url.Contains("flowruns?")).Url));
        Assert.Contains($"<condition attribute='workflow' operator='in'><value>{FlowB}</value></condition>",
            handler.Requests.Single(r => r.Url.Contains("flowruns?fetchXml")).Url);
        Assert.Contains(data.Notes, n => n.StartsWith("the plug-in trace log could not be read"));
    }

    [Fact]
    public async Task Summaries_rank_components_group_one_cause_and_bucket_by_hour()
    {
        var data = await Fakes.Dataverse(Handler()).GetFailuresAsync(From, To, new FailureQuery());

        var top = FailureOverview.ByComponent(data)[0];
        Assert.Equal("Sync orders", top.ComponentName);
        Assert.Equal(2, top.Failures);
        Assert.Equal(3, top.Runs);
        Assert.Equal("67%", top.RateLabel);
        Assert.Equal(2, top.TopErrorCount);

        // Two "not found" messages differing only in the record id are one cause.
        var group = FailureOverview.ByError(data)[0];
        Assert.Equal(2, group.Failures);
        Assert.Equal(1, group.Components);

        var trend = FailureOverview.Trend(data, From, To);
        Assert.Equal(4, trend.Sum(b => b.Failures));
        Assert.True(trend.Count >= 24);
        Assert.Equal(1.0, trend.Max(b => b.Share));

        var markdown = FailureOverview.ToMarkdown(data, "UAT", From, To);
        Assert.Contains("4 failure(s) across 3 component(s)", markdown);
        Assert.Contains("## By error", markdown);
    }

    [Theory]
    [InlineData("Record 1a2b3c4d-0000-0000-0000-000000000001 not found", "record {id} not found")]
    [InlineData("Value 'abc' is longer than 100", "value '…' is longer than #")]
    public void Messages_lose_what_varies_between_occurrences(string message, string normalised)
    {
        Assert.Equal(normalised, FailureOverview.Normalise(message));
    }

    [Fact]
    public void Flow_runs_with_json_errors_group_by_their_message_not_the_opening_brace()
    {
        static FailureEvent Failed(string message) => new()
        {
            Source = FailureSource.CloudFlow, ComponentKey = "flow", ComponentName = "Flow", When = From,
            ErrorCode = "ActionFailed", ErrorMessage = message
        };
        var data = new FailureData();
        data.Events.Add(Failed("{\r\n  \"code\": \"ActionFailed\",\r\n  \"message\": \"An action failed.\"\r\n}"));
        data.Events.Add(Failed("{\r\n  \"code\": \"ActionFailed\",\r\n  \"message\": \"An action failed.\"\r\n}"));
        data.Events.Add(Failed("{\r\n  \"code\": \"ActionFailed\",\r\n  \"message\": \"The flow timed out.\"\r\n}"));

        var groups = FailureOverview.ByError(data);

        Assert.Equal(["An action failed.", "The flow timed out."], groups.Select(g => g.Error));
        Assert.Equal("An action failed.", FailureOverview.ByComponent(data)[0].TopError);
    }

    [Theory]
    [InlineData("Unhandled exception: \nException type: X\nMessage: Real reason\n", "Real reason")]
    [InlineData("System.InvalidOperationException: Boom\n   at Foo", "System.InvalidOperationException: Boom")]
    public void A_traces_readable_message_is_picked_out(string details, string expected)
    {
        Assert.Equal(expected, DataverseClient.ExceptionMessage(details));
    }
}
