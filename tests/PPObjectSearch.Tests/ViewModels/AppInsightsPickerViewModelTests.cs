using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.AppInsights;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.AppInsights.AzureMonitorClientTests;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>Choosing an environment's App Insights resource, and the check that it is receiving telemetry.</summary>
public class AppInsightsPickerViewModelTests
{
    private static readonly TelemetryScope Scope = new(Guid.Parse(TelemetryFixtures.OrganizationId), "env-1", "contoso.crm11.dynamics.com");

    private static FakeHttpHandler Handler(long total = 900, long matching = 40) =>
        OnOneComponent(new FakeHttpHandler())
            .OnJson(HttpMethod.Post, "/query", JsonSerializer.Serialize(new
            {
                tables = new[]
                {
                    new
                    {
                        name = "PrimaryResult",
                        columns = new[] { new { name = "Total", type = "long" }, new { name = "Matching", type = "long" } },
                        rows = new[] { new object[] { total, matching } }
                    }
                }
            }));

    private static AppInsightsPickerViewModel Picker(FakeHttpHandler handler, TelemetryBinding? current = null)
    {
        InlineSynchronizationContext.Install();
        return new AppInsightsPickerViewModel(Client(handler), Scope, current);
    }

    [Fact]
    public async Task Every_resource_is_listed_and_none_is_picked_at_first()
    {
        var vm = Picker(Handler());

        await vm.LoadAsync();

        var choice = Assert.Single(vm.Items);
        Assert.Equal("contoso-dataverse", choice.Name);
        Assert.Equal("Contoso Prod · rg-telemetry · uksouth", choice.Where);
        Assert.Null(vm.Selected);
        Assert.False(vm.CanSave);
        Assert.Equal("1 resource found.", vm.Status);
        Assert.Equal("None chosen", vm.CurrentLabel);
    }

    [Fact]
    public async Task Picking_a_resource_checks_it_and_offers_it_to_save()
    {
        var vm = Picker(Handler());
        await vm.LoadAsync();

        vm.Selected = vm.Items[0];
        await vm.CheckAsync();

        Assert.True(vm.CanSave);
        Assert.True(vm.CheckFoundTelemetry);
        Assert.Contains("40 of 900 rows", vm.CheckText);

        var binding = vm.Result()!;
        Assert.Equal(AppId, binding.AppId);
        Assert.Equal(ComponentId, binding.ResourceId);
        Assert.Equal(TelemetryKind.DataverseExport, binding.KnownKind);
        Assert.True(binding.OnlyThisEnvironment);
    }

    [Fact]
    public async Task A_resource_with_none_of_this_environments_rows_says_so()
    {
        var vm = Picker(Handler(matching: 0));
        await vm.LoadAsync();

        vm.Selected = vm.Items[0];
        await vm.CheckAsync();

        Assert.False(vm.CheckFoundTelemetry);
        Assert.Contains("none mention this environment", vm.CheckText);
    }

    [Fact]
    public async Task The_current_resource_is_selected_and_its_setting_kept()
    {
        var current = new TelemetryBinding { ResourceId = ComponentId.ToUpperInvariant(), AppId = AppId, Name = "contoso-dataverse", OnlyThisEnvironment = false };
        var vm = Picker(Handler(), current);

        await vm.LoadAsync();

        Assert.True(vm.Items[0].IsCurrent);
        Assert.Same(vm.Items[0], vm.Selected);
        Assert.False(vm.OnlyThisEnvironment);
        Assert.True(vm.HasCurrent);
        Assert.Equal("Now: contoso-dataverse", vm.CurrentLabel);
        Assert.False(vm.Result()!.OnlyThisEnvironment);
    }

    [Fact]
    public async Task Resources_are_filtered_by_name_or_where_they_are()
    {
        var vm = Picker(Handler());
        await vm.LoadAsync();

        vm.SearchText = "uksouth";
        Assert.Single(vm.ItemsView.Cast<AppInsightsChoice>());

        vm.SearchText = "nothing like it";
        Assert.Empty(vm.ItemsView.Cast<AppInsightsChoice>());
    }

    [Fact]
    public async Task A_listing_that_fails_says_why()
    {
        var vm = Picker(new FakeHttpHandler().OnError(HttpMethod.Get, "/subscriptions?", HttpStatusCode.Forbidden, "AuthorizationFailed"));

        await vm.LoadAsync();

        Assert.Empty(vm.Items);
        Assert.StartsWith("Could not list resources - Azure Resource Manager: 403", vm.Status);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void The_session_saves_and_removes_the_resource_for_its_environment()
    {
        var settings = new AppSettings();
        var session = TestSessions.Disconnected(settings: settings);
        var changed = 0;
        session.AppInsightsChanged += (_, _) => changed++;

        session.SetAppInsights(new TelemetryBinding { ResourceId = ComponentId, AppId = AppId, Name = "contoso-dataverse", SubscriptionName = "Contoso Prod" });

        Assert.True(session.HasAppInsights);
        Assert.Equal("App Insights: contoso-dataverse (Contoso Prod)", session.AppInsightsDescription);
        Assert.Equal(AppId, settings.GetTelemetryBinding(session.EnvironmentUrl, TelemetryKind.DataverseExport)?.AppId);

        session.SetAppInsights(null);

        Assert.False(session.HasAppInsights);
        Assert.Equal(string.Empty, session.AppInsightsDescription);
        Assert.Equal(2, changed);
    }
}
