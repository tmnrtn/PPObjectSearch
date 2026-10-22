using PPObjectSearch.Auth;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.AppInsights.AzureMonitorClientTests;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The details window's App Insights tab: offered for a plug-in where a resource is chosen, and read only when shown.</summary>
public class ObjectDetailsTelemetryTests
{
    private static EnvironmentSessionViewModel Session(bool withResource)
    {
        var settings = new AppSettings();
        if (withResource)
        {
            settings.SetTelemetryBinding(Fakes.EnvironmentUrl, TelemetryKind.DataverseExport,
                new TelemetryBinding { AppId = AppId, ResourceId = ComponentId, Name = "contoso-dataverse" });
        }

        return new EnvironmentSessionViewModel(new AuthenticationService(), settings, new TabState { EnvironmentUrl = Fakes.EnvironmentUrl });
    }

    [Fact]
    public void A_plug_in_in_an_environment_with_a_resource_opens_on_its_App_Insights_tab()
    {
        var details = DetailsHarness.Details(new FakeHttpHandler().Quiet(), DetailsHarness.Item(90), session: Session(withResource: true),
            openOn: new DetailsShortcut("App Insights", DetailsTab.Telemetry));

        Assert.True(details.HasTelemetry);
        Assert.Equal(DetailsTab.Telemetry, details.SelectedTab);
    }

    [Fact]
    public void Without_a_resource_the_tab_is_not_there_and_asking_for_it_lands_on_the_trace_log()
    {
        var details = DetailsHarness.Details(new FakeHttpHandler().Quiet(), DetailsHarness.Item(90), session: Session(withResource: false),
            openOn: new DetailsShortcut("App Insights", DetailsTab.Telemetry));

        Assert.False(details.HasTelemetry);
        Assert.Null(details.Telemetry);
        Assert.Equal(DetailsTab.TraceLog, details.SelectedTab);
    }

    [Fact]
    public void Anything_but_a_plug_in_has_no_App_Insights_tab()
    {
        var details = DetailsHarness.Details(new FakeHttpHandler().Quiet(), DetailsHarness.CloudFlow(), session: Session(withResource: true));

        Assert.False(details.HasTelemetry);
    }

    [Fact]
    public async Task The_telemetry_is_read_when_the_tab_is_shown_not_before()
    {
        var handler = new FakeHttpHandler().Quiet()
            .OnJson(System.Net.Http.HttpMethod.Get, "plugintypes(", """{"typename":"Contoso.Plugins.AccountPostUpdate"}""")
            .OnJson(System.Net.Http.HttpMethod.Post, "/query", """{"tables":[]}""");
        var details = DetailsHarness.Details(handler, DetailsHarness.Item(90), session: Session(withResource: true));

        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/query"));

        details.SelectedTab = DetailsTab.Telemetry;
        await TestSessions.Until(() => details.Telemetry!.HasLoaded);

        Assert.Contains(handler.Requests, r => r.Url.Contains("/query"));
    }
}
