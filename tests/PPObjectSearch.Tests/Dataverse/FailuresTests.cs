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

    [Theory]
    [InlineData("Unhandled exception: \nException type: X\nMessage: Real reason\n", "Real reason")]
    [InlineData("System.InvalidOperationException: Boom\n   at Foo", "System.InvalidOperationException: Boom")]
    public void A_traces_readable_message_is_picked_out(string details, string expected)
    {
        Assert.Equal(expected, DataverseClient.ExceptionMessage(details));
    }
}
