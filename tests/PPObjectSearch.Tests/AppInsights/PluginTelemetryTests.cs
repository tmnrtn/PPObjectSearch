using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.AppInsights.AzureMonitorClientTests;

namespace PPObjectSearch.Tests.AppInsights;

/// <summary>A plug-in's App Insights tab in its details: which tabs it has, what it reads and how it says so.</summary>
public class PluginTelemetryTests
{
    private const string PluginType = "Contoso.Plugins.AccountPostUpdate";
    private static readonly TelemetryScope Scope = new(Guid.Parse(TelemetryFixtures.OrganizationId), null, "contoso.crm11.dynamics.com");
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly TelemetryBinding Binding = new()
    {
        AppId = AppId, ResourceId = ComponentId, Name = "contoso-dataverse", SubscriptionName = "Contoso Prod"
    };

    private const string Summary = """
        {"tables":[{"name":"PrimaryResult",
          "columns":[{"name":"Executions","type":"long"},{"name":"Failures","type":"long"},{"name":"AverageMs","type":"real"},
                     {"name":"P95Ms","type":"real"},{"name":"Last","type":"datetime"}],
          "rows":[[1250,25,180.4,1520.0,"2026-10-22T11:58:00Z"]]}]}
        """;

    /// <summary>The summary query and the failures queries, told apart by what they ask.</summary>
    private static FakeHttpHandler Handler(string summary = Summary) => new FakeHttpHandler()
        .On(HttpMethod.Post, "/query", r =>
        {
            var query = JsonDocument.Parse(r.Body!).RootElement.GetProperty("query").GetString()!;
            return FakeHttpHandler.Json(query switch
            {
                _ when query.Contains("summarize Executions", StringComparison.Ordinal) => summary,
                _ when query.StartsWith("dependencies", StringComparison.Ordinal) => TelemetryFixtures.FailedPlugins,
                _ when query.StartsWith("exceptions", StringComparison.Ordinal) => TelemetryFixtures.Exceptions,
                _ => """{"tables":[]}"""
            });
        });

    private static PluginTelemetryViewModel Tab(FakeHttpHandler handler, IReadOnlyList<string>? names = null, List<string?>? opened = null) =>
        new(() => Client(handler), Binding, Scope, _ => Task.FromResult(names ?? (IReadOnlyList<string>)[PluginType]),
            url => opened?.Add(url), Clouds.Public.Azure, "tenant-1", () => Now);

    // ---------------------------------------------------------------- tabs

    [Fact]
    public void A_plug_in_has_an_App_Insights_tab_only_where_a_resource_is_chosen()
    {
        Assert.Equal([DetailsTab.TraceLog, DetailsTab.Layers, DetailsTab.Dependencies], DetailsTabs.TabsFor(ObjectKind.Plugin));
        Assert.Equal([DetailsTab.TraceLog, DetailsTab.Telemetry, DetailsTab.Layers, DetailsTab.Dependencies],
            DetailsTabs.TabsFor(ObjectKind.Plugin, hasTelemetry: true));

        Assert.Equal(DetailsTab.TraceLog, DetailsTabs.Resolve(ObjectKind.Plugin, DetailsTab.Telemetry));
        Assert.Equal(DetailsTab.Telemetry, DetailsTabs.Resolve(ObjectKind.Plugin, DetailsTab.Telemetry, hasTelemetry: true));
        Assert.Equal(DetailsTab.Components, DetailsTabs.Resolve(ObjectKind.Table, DetailsTab.Telemetry, hasTelemetry: true));
    }

    [Fact]
    public void The_detail_pane_offers_App_Insights_for_a_plug_in_with_a_resource()
    {
        var item = new SolutionComponentItem { Name = PluginType, ComponentTypeName = "Plug-in Type", ComponentType = 90, ObjectId = Guid.NewGuid() };

        Assert.Equal(["Trace log", "Layers"], DetailsTabs.ShortcutsFor(item).Select(s => s.Label));
        Assert.Equal(["Trace log", "App Insights", "Layers"], DetailsTabs.ShortcutsFor(item, hasTelemetry: true).Select(s => s.Label));
    }

    // ---------------------------------------------------------------- plug-in type names

    [Fact]
    public async Task A_step_is_read_as_the_plug_in_type_it_runs()
    {
        var stepId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"sdkmessageprocessingsteps({stepId})", $$"""{"_eventhandler_value":"{{typeId}}"}""")
            .OnJson(HttpMethod.Get, $"plugintypes({typeId})", $$"""{"typename":"{{PluginType}}"}""");
        using var client = Fakes.Dataverse(handler);

        Assert.Equal([PluginType], await client.GetPluginTypeNamesAsync(stepId, 92));
    }

    [Fact]
    public async Task An_assembly_is_read_as_every_type_in_it_and_anything_else_as_none()
    {
        var assemblyId = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "plugintypes?$select=typename&$filter=_pluginassemblyid_value",
                """{"value":[{"typename":"Contoso.A"},{"typename":"Contoso.B"},{"typename":"contoso.a"}]}""");
        using var client = Fakes.Dataverse(handler);

        Assert.Equal(["Contoso.A", "Contoso.B"], await client.GetPluginTypeNamesAsync(assemblyId, 91));
        Assert.Empty(await client.GetPluginTypeNamesAsync(Guid.NewGuid(), 1));
    }

    [Fact]
    public async Task A_step_on_a_service_endpoint_runs_no_plug_in_type()
    {
        var stepId = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, $"sdkmessageprocessingsteps({stepId})", $$"""{"_eventhandler_value":"{{Guid.NewGuid()}}"}""")
            .OnError(HttpMethod.Get, "plugintypes(", HttpStatusCode.NotFound, "Does Not Exist");
        using var client = Fakes.Dataverse(handler);

        Assert.Empty(await client.GetPluginTypeNamesAsync(stepId, 92));
    }

    // ---------------------------------------------------------------- the tab

    [Fact]
    public async Task The_tab_reads_the_plug_ins_executions_and_latest_failures()
    {
        var handler = Handler();
        var tab = Tab(handler);

        await tab.LoadAsync();

        Assert.True(tab.HasLoaded);
        Assert.Equal("1,250", tab.ExecutionsLabel);
        Assert.Equal("25", tab.FailuresLabel);
        Assert.Equal("2%", tab.FailureRateLabel);
        Assert.Equal("180 ms", tab.AverageLabel);
        Assert.Equal("1.5 s", tab.P95Label);

        var failure = Assert.Single(tab.Failures);
        Assert.Same(failure, tab.SelectedFailure);
        Assert.Contains("InvalidPluginExecutionException", tab.SelectedDetails);
        Assert.Equal("1 failure in the last 7 days.", tab.Status);
        Assert.Equal("From contoso-dataverse (Contoso Prod)", tab.SourceLabel);

        Assert.DoesNotContain(handler.Requests, r => r.Body!.Contains("\"query\":\"requests", StringComparison.Ordinal));
        Assert.All(handler.Requests, r => Assert.Contains("2026-10-15T12:00:00Z/2026-10-22T12:00:00Z", r.Body));
    }

    [Fact]
    public async Task No_executions_at_all_asks_whether_the_export_goes_here()
    {
        var empty = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"Executions","type":"long"}],"rows":[]}]}""";
        var tab = Tab(new FakeHttpHandler()
            .OnJson(HttpMethod.Post, "/query", empty));

        await tab.LoadAsync();

        Assert.Empty(tab.Failures);
        Assert.Contains("Is the Dataverse export pointed at this resource?", tab.Status);
        Assert.Equal("—", tab.LastLabel);
    }

    [Fact]
    public async Task An_object_without_a_plug_in_type_reads_nothing()
    {
        var handler = Handler();
        var tab = Tab(handler, names: []);

        await tab.LoadAsync();

        Assert.Empty(handler.Requests);
        Assert.StartsWith("This object runs no plug-in type", tab.Status);
    }

    [Fact]
    public async Task A_refused_read_says_why()
    {
        var tab = Tab(new FakeHttpHandler().OnError(HttpMethod.Post, "/query", HttpStatusCode.Forbidden, "InsufficientAccessError"));

        await tab.LoadAsync();

        Assert.StartsWith("App Insights (contoso-dataverse) could not be read - Application Insights: 403", tab.Status);
        Assert.False(tab.IsBusy);
    }

    [Fact]
    public async Task The_portal_opens_on_the_selected_failures_operation()
    {
        var opened = new List<string?>();
        var tab = Tab(Handler(), opened: opened);
        await tab.LoadAsync();

        tab.OpenInPortalCommand.Execute(null);

        var link = Assert.Single(opened)!;
        Assert.StartsWith("https://portal.azure.com/#@tenant-1/blade/Microsoft_Azure_Monitoring_Logs/LogsBlade/", link);
        Assert.EndsWith("/timespan/P7D", link);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(12.4, "12 ms")]
    [InlineData(2500.0, "2.5 s")]
    public void Durations_read_in_the_unit_that_suits_them(double? ms, string label) =>
        Assert.Equal(label, PluginTelemetryViewModel.Duration(ms));
}
