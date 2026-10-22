using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using static PPObjectSearch.Tests.AppInsights.AzureMonitorClientTests;

namespace PPObjectSearch.Tests.AppInsights;

/// <summary>App Insights as a source of the Failures window: reading it, counting it beside the trace log, and linking to it.</summary>
public class AppInsightsFailuresTests
{
    private static readonly TelemetryScope Scope = new(Guid.Parse(TelemetryFixtures.OrganizationId), null, "contoso.crm11.dynamics.com");
    private static readonly DateTimeOffset From = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset When = new(2026, 10, 20, 8, 15, 2, TimeSpan.Zero);

    private static TelemetryBinding Binding(string kind = nameof(TelemetryKind.DataverseExport), bool onlyThis = true) =>
        new() { Kind = kind, AppId = AppId, ResourceId = ComponentId, Name = "contoso-dataverse", OnlyThisEnvironment = onlyThis };

    private static FailureEvent Trace(string type, DateTimeOffset when) =>
        new() { Source = FailureSource.Plugin, ComponentKey = type, ComponentName = type, When = when };

    private static FailureEvent Telemetry(string type, DateTimeOffset when) =>
        new() { Source = FailureSource.AppInsights, IsPlugin = true, ComponentKey = type, ComponentName = type, When = when };

    // ---------------------------------------------------------------- reading

    [Fact]
    public async Task The_resources_failures_join_the_read()
    {
        using var client = Client(new FakeHttpHandler().OnDataverseExport());
        var data = new FailureData();

        await AppInsightsFailures.ReadAsync(client, Binding(), Scope, From, From.AddDays(7), new FailureQuery(), data);

        Assert.Equal(3, data.Events.Count(e => e.Source == FailureSource.AppInsights));
        Assert.Empty(data.Notes);
    }

    [Fact]
    public async Task A_resource_that_refuses_becomes_a_note()
    {
        using var client = Client(new FakeHttpHandler().OnError(HttpMethod.Post, "/query", HttpStatusCode.Forbidden, "InsufficientAccessError"));
        var data = new FailureData();

        await AppInsightsFailures.ReadAsync(client, Binding(), Scope, From, From.AddDays(7), new FailureQuery(), data);

        Assert.Empty(data.Events);
        var note = Assert.Single(data.Notes);
        Assert.StartsWith("App Insights (contoso-dataverse) could not be read: Application Insights: 403", note);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Choose the resource again")]
    [InlineData(HttpStatusCode.Forbidden, "Check Azure access")]
    public async Task A_note_says_what_to_do_next(HttpStatusCode status, string advice)
    {
        using var client = Client(new FakeHttpHandler().OnError(HttpMethod.Post, "/query", status, "PathNotFoundError"));
        var data = new FailureData();

        await AppInsightsFailures.ReadAsync(client, Binding(), Scope, From, From.AddDays(7), new FailureQuery(), data);

        Assert.Contains(advice, Assert.Single(data.Notes));
    }

    [Fact]
    public async Task A_kind_this_version_cannot_read_is_said_and_not_queried()
    {
        var handler = new FakeHttpHandler();
        using var client = Client(handler);
        var data = new FailureData();

        await AppInsightsFailures.ReadAsync(client, Binding("CopilotStudioExport"), Scope, From, From.AddDays(7), new FailureQuery(), data);

        Assert.Empty(handler.Requests);
        Assert.Contains("cannot read (CopilotStudioExport)", Assert.Single(data.Notes));
    }

    [Fact]
    public async Task The_binding_decides_whether_rows_are_narrowed_to_the_environment()
    {
        var handler = new FakeHttpHandler().OnDataverseExport();
        using var client = Client(handler);

        await AppInsightsFailures.ReadAsync(client, Binding(onlyThis: false), Scope, From, From.AddDays(7), new FailureQuery(), new FailureData());

        Assert.All(handler.Requests, r => Assert.DoesNotContain("cloud_RoleInstance", r.Body));
    }

    // ---------------------------------------------------------------- the trace log and App Insights together

    [Fact]
    public void A_plug_in_failure_in_both_sources_is_counted_once()
    {
        var data = new FailureData();
        data.Events.Add(Trace("Contoso.A", When));
        data.Events.Add(Telemetry("Contoso.A", When.AddSeconds(1)));
        data.Events.Add(Telemetry("Contoso.A", When.AddMinutes(5)));
        data.Events.Add(Telemetry("Contoso.B", When));

        Assert.Equal(1, FailureOverview.MarkTraceLogOverlaps(data));
        Assert.Equal(3, FailureOverview.Counted(data));
        Assert.True(data.Events[1].AlsoInTraceLog);
        Assert.False(data.Events[2].AlsoInTraceLog);
    }

    [Fact]
    public void Without_the_trace_log_shown_every_App_Insights_failure_counts()
    {
        var data = new FailureData();
        data.Events.Add(Trace("Contoso.A", When));
        data.Events.Add(Telemetry("Contoso.A", When));
        FailureOverview.MarkTraceLogOverlaps(data);

        var appInsightsOnly = FailureOverview.Only(data, new HashSet<FailureSource> { FailureSource.AppInsights });

        Assert.Equal(1, FailureOverview.Counted(appInsightsOnly));
        Assert.Equal(1, FailureOverview.Counted(data));
    }

    [Fact]
    public void One_trace_entry_pairs_with_one_failure()
    {
        var data = new FailureData();
        data.Events.Add(Trace("Contoso.A", When));
        data.Events.Add(Telemetry("Contoso.A", When));
        data.Events.Add(Telemetry("Contoso.A", When.AddMilliseconds(500)));

        Assert.Equal(1, FailureOverview.MarkTraceLogOverlaps(data));
    }

    [Fact]
    public void The_markdown_names_the_source_and_counts_once()
    {
        var data = new FailureData();
        data.Events.Add(Trace("Contoso.A", When));
        data.Events.Add(Telemetry("Contoso.A", When));
        FailureOverview.MarkTraceLogOverlaps(data);

        var md = FailureOverview.ToMarkdown(data, "Contoso", From, From.AddDays(7));

        Assert.Contains("1 failure(s) across 2 component(s)", md);
        Assert.Contains("| App Insights |", md);
    }

    // ---------------------------------------------------------------- links

    [Fact]
    public void A_logs_link_carries_the_query_compressed_and_the_resource()
    {
        var query = AppInsightsLinks.OperationQuery("op-1");

        var link = AppInsightsLinks.Logs(Clouds.Public.Azure, "tenant-1", ComponentId, query, TimeSpan.FromDays(30));

        Assert.StartsWith("https://portal.azure.com/#@tenant-1/blade/Microsoft_Azure_Monitoring_Logs/LogsBlade/resourceId/%2Fsubscriptions%2Fsub-1", link);
        Assert.EndsWith("/timespan/P30D", link);

        var encoded = link.Split("/q/")[1].Split("/timespan/")[0];
        Assert.Equal(query, Decompress(Uri.UnescapeDataString(encoded)));
        Assert.Contains("where operation_Id == 'op-1'", query);
    }

    [Fact]
    public void A_resource_link_goes_to_its_overview_in_the_clouds_portal()
    {
        Assert.Equal("https://portal.azure.us/#resource/subscriptions/x/overview",
            AppInsightsLinks.Resource(Clouds.UsGccHigh.Azure, null, "/subscriptions/x"));
    }

    private static string Decompress(string base64)
    {
        using var input = new MemoryStream(Convert.FromBase64String(base64));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
