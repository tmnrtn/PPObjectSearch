using System.Net;
using System.Net.Http;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Tests.Infrastructure;
using static PPObjectSearch.Tests.AppInsights.AzureMonitorClientTests;

namespace PPObjectSearch.Tests.AppInsights;

public class AzureAccessProbeTests
{
    [Fact]
    public async Task With_every_token_every_check_passes_and_a_query_is_tried()
    {
        var handler = OnOneComponent(new FakeHttpHandler())
            .OnJson(HttpMethod.Post, $"/v1/apps/{AppId}/query", """{"tables":[{"name":"PrimaryResult","columns":[{"name":"ok","type":"long"}],"rows":[[1]]}]}""");
        using var client = Client(handler);

        var results = await AzureAccessProbe.RunAsync(client, appId: null);

        Assert.All(results, r => Assert.True(r.Ok, r.Line));
        Assert.Contains(results, r => r is { Check: "Query", Detail: "ran against contoso-dataverse" });
        Assert.Contains(results, r => r.Check == AzureAccessProbe.LogAnalyticsCheck);
    }

    [Fact]
    public async Task A_refused_query_api_is_reported_without_hiding_what_worked()
    {
        var auth = TestAuth.TokensFor(Arm, "https://api.loganalytics.io");
        using var client = Client(OnOneComponent(new FakeHttpHandler()), auth);

        var results = await AzureAccessProbe.RunAsync(client, appId: null);

        Assert.True(results.Single(r => r.Check == AzureAccessProbe.ResourceManagerCheck).Ok);
        Assert.False(results.Single(r => r.Check == AzureAccessProbe.AppInsightsCheck).Ok);
        Assert.True(results.Single(r => r.Check == AzureAccessProbe.LogAnalyticsCheck).Ok);
        Assert.DoesNotContain(results, r => r.Check == "Query");
    }

    [Fact]
    public async Task No_role_on_the_resource_shows_as_a_failed_query()
    {
        var handler = OnOneComponent(new FakeHttpHandler())
            .OnError(HttpMethod.Post, "/query", HttpStatusCode.Forbidden, "The provided credentials have insufficient access");
        using var client = Client(handler);

        var results = await AzureAccessProbe.RunAsync(client, appId: null);

        var query = results.Single(r => r.Check == "Query");
        Assert.False(query.Ok);
        Assert.Contains("Reader", query.Detail);
    }

    [Fact]
    public void The_text_names_the_environment_and_the_sign_in_client()
    {
        var text = AzureAccessProbe.ToText("contoso.crm11.dynamics.com", "built-in (51f8...)",
            [new AzureProbeResult("Azure Resource Manager", true, "2 subscriptions visible")]);

        Assert.Contains("contoso.crm11.dynamics.com", text);
        Assert.Contains("built-in (51f8...)", text);
        Assert.Contains("OK - Azure Resource Manager: 2 subscriptions visible", text);
    }
}
