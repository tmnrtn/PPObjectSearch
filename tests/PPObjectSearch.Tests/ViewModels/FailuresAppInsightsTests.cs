using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.AppInsights;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.AppInsights.AzureMonitorClientTests;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The failures window with an App Insights resource chosen for its environment.</summary>
public class FailuresAppInsightsTests
{
    private const string PluginType = "Contoso.Plugins.AccountPostUpdate";

    /// <summary>Dataverse with one trace log entry - the same failure App Insights holds - and nothing else failing.</summary>
    private static FakeHttpHandler Handler(bool tracesFail = false)
    {
        var handler = new FakeHttpHandler();
        if (tracesFail) handler.OnError(HttpMethod.Get, "plugintracelogs?", System.Net.HttpStatusCode.Forbidden, "no read on traces");

        return handler
            .OnJson(HttpMethod.Get, "flowruns?", """{"value":[]}""")
            .OnJson(HttpMethod.Get, "asyncoperations?", """{"value":[]}""")
            .OnJson(HttpMethod.Get, "organizations?", """{"value":[{"plugintracelogsetting":1}]}""")
            .OnJson(HttpMethod.Get, "plugintracelogs?", $$"""
                {"value":[{"plugintracelogid":"t1","createdon":"2026-10-20T08:15:03Z","typename":"{{PluginType}}","messagename":"Update",
                  "primaryentity":"account","exceptiondetails":"Message: Credit limit cannot be negative."}]}
                """)
            .OnDataverseExport();
    }

    private static AppSettings Settings(bool withResource)
    {
        var settings = new AppSettings();
        if (withResource)
        {
            settings.SetTelemetryBinding(Fakes.EnvironmentUrl, TelemetryKind.DataverseExport,
                new TelemetryBinding { AppId = AppId, ResourceId = ComponentId, Name = "contoso-dataverse", SubscriptionName = "Contoso Prod" });
        }

        return settings;
    }

    private static async Task<(FailuresViewModel Vm, FakeHttpHandler Handler, EnvironmentSessionViewModel Session)> Loaded(
        bool withResource = true, bool tracesFail = false, params SolutionComponentItem[] items)
    {
        InlineSynchronizationContext.Install();
        var handler = Handler(tracesFail);
        var session = TestSessions.Connected(handler, items, settings: Settings(withResource));
        var vm = new FailuresViewModel(session);
        await vm.LoadAsync();
        return (vm, handler, session);
    }

    [Fact]
    public async Task Without_a_resource_App_Insights_is_offered_not_read()
    {
        var (vm, handler, _) = await Loaded(withResource: false);

        Assert.False(vm.HasAppInsights);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("applicationinsights"));
        Assert.Equal(0, vm.AppInsightsFailureCount);
        Assert.DoesNotContain("App Insights", vm.StatusLine);
    }

    [Fact]
    public async Task With_a_resource_its_failures_join_the_window()
    {
        var (vm, _, _) = await Loaded();

        Assert.True(vm.HasAppInsights);
        Assert.Equal(3, vm.AppInsightsFailureCount);
        Assert.Equal(1, vm.PluginFailureCount);
        Assert.Contains(vm.Components, c => c.Source == FailureSource.AppInsights && c.ComponentName == "POST contacts");
        Assert.Contains("and 6 App Insights rows in", vm.StatusLine);
        Assert.Equal("Failed plug-ins, failed requests and exceptions from contoso-dataverse (Contoso Prod)", vm.AppInsightsToolTip);
    }

    [Fact]
    public async Task A_failure_in_both_the_trace_log_and_App_Insights_is_counted_once()
    {
        var (vm, _, _) = await Loaded();

        Assert.Equal(3, vm.TotalFailures);
        Assert.Contains("also in the trace log", vm.Notes);

        vm.IncludePlugins = false;

        Assert.Equal(3, vm.TotalFailures);
        Assert.Equal(3, vm.Components.Count);
    }

    [Fact]
    public async Task The_App_Insights_pill_takes_its_failures_out_and_back()
    {
        var (vm, _, _) = await Loaded();

        vm.IncludeAppInsights = false;
        Assert.All(vm.Components, c => Assert.NotEqual(FailureSource.AppInsights, c.Source));

        vm.IncludeAppInsights = true;
        Assert.Contains(vm.Components, c => c.Source == FailureSource.AppInsights);
    }

    [Fact]
    public async Task A_plug_in_failure_from_App_Insights_opens_the_loaded_plug_in()
    {
        var type = new SolutionComponentItem { Name = PluginType, ComponentTypeName = "Plug-in Type", ComponentType = 90, ObjectId = Guid.NewGuid() };
        var (vm, _, session) = await Loaded(items: type);
        TestSessions.DropClient(session);

        vm.SelectedComponent = vm.Components.Single(c => c is { Source: FailureSource.AppInsights, ComponentKey: PluginType });
        vm.OpenLatestFailedRunCommand.Execute(null);

        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public async Task Removing_the_resource_reads_again_without_it()
    {
        var (vm, _, session) = await Loaded();

        session.SetAppInsights(null);
        await TestSessions.Until(() => !vm.IsBusy && vm.AppInsightsFailureCount == 0);

        Assert.False(vm.HasAppInsights);
        Assert.DoesNotContain(vm.Components, c => c.Source == FailureSource.AppInsights);
    }
}
