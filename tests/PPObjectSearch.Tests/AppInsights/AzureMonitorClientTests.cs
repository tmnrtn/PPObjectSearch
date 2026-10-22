using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.AzureMonitor;
using PPObjectSearch.Core;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.AppInsights;

public class AzureMonitorClientTests
{
    internal const string Arm = "https://management.azure.com";
    internal const string AppInsightsApi = "https://api.applicationinsights.io";
    internal const string AppId = "00000000-0000-0000-0000-0000000000a1";
    internal const string ComponentId =
        "/subscriptions/sub-1/resourceGroups/rg-telemetry/providers/microsoft.insights/components/contoso-dataverse";

    internal static AzureMonitorClient Client(FakeHttpHandler handler, EnvironmentAuthContext? auth = null, Cloud? cloud = null) =>
        new(auth ?? TestAuth.Tokens(), cloud ?? Clouds.Public, handler);

    /// <summary>One subscription with one App Insights resource in it.</summary>
    internal static FakeHttpHandler OnOneComponent(FakeHttpHandler handler) => handler
        .OnJson(HttpMethod.Get, "/subscriptions?", """{"value":[{"subscriptionId":"sub-1","displayName":"Contoso Prod","state":"Enabled"}]}""")
        .OnJson(HttpMethod.Get, "/subscriptions/sub-1/providers/Microsoft.Insights/components", JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    id = ComponentId,
                    name = "contoso-dataverse",
                    location = "uksouth",
                    properties = new { AppId, WorkspaceResourceId = "/subscriptions/sub-1/resourceGroups/rg-telemetry/providers/Microsoft.OperationalInsights/workspaces/law" }
                }
            }
        }));

    [Theory]
    [InlineData("AADSTS65002: Consent between first party application and first party resource must be configured", AzureFailure.ClientNotAllowed)]
    [InlineData("AADSTS650057: Invalid resource. The client has requested access to a resource which is not listed", AzureFailure.ClientNotAllowed)]
    [InlineData("AADSTS65001: The user or administrator has not consented", AzureFailure.ClientNotAllowed)]
    [InlineData("AADSTS53003: Access has been blocked by Conditional Access policies.", AzureFailure.SignIn)]
    [InlineData("User canceled authentication.", AzureFailure.SignIn)]
    public void A_refused_sign_in_says_whether_the_client_is_the_problem(string message, AzureFailure expected)
    {
        var failure = AzureMonitorClient.SignInFailure(AppInsightsApi, new InvalidOperationException(message));

        Assert.Equal(expected, failure.Failure);
        if (expected == AzureFailure.ClientNotAllowed) Assert.Contains("ClientId", failure.Message);
    }

    [Fact]
    public async Task No_token_is_a_sign_in_failure_not_a_crash()
    {
        using var client = Client(new FakeHttpHandler(), TestAuth.NoTokens());

        var ex = await Assert.ThrowsAsync<AzureMonitorException>(() => client.ListSubscriptionsAsync());

        Assert.Equal(AzureFailure.SignIn, ex.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, AzureFailure.Forbidden, "Reader")]
    [InlineData(HttpStatusCode.NotFound, AzureFailure.NotFound, "deleted or moved")]
    [InlineData(HttpStatusCode.BadRequest, AzureFailure.Other, "Bad query")]
    public void An_api_error_says_what_to_do(HttpStatusCode status, AzureFailure expected, string says)
    {
        var body = """{"error":{"code":"BadArgumentError","message":"Bad query","innererror":{"code":"SyntaxError","message":"Bad query"}}}""";

        var failure = AzureMonitorClient.HttpFailure("Application Insights", status, body);

        Assert.Equal(expected, failure.Failure);
        Assert.Contains(says, failure.Message);
        Assert.Equal("BadArgumentError", failure.ErrorCode);
    }

    [Fact]
    public void An_inner_error_adds_its_detail()
    {
        var body = """{"error":{"message":"The request had some invalid properties","innererror":{"innererror":{"message":"'where' operator: Failed to resolve column 'nope'"}}}}""";

        var failure = AzureMonitorClient.HttpFailure("Application Insights", HttpStatusCode.BadRequest, body);

        Assert.Contains("Failed to resolve column 'nope'", failure.Message);
    }

    [Fact]
    public async Task Subscriptions_are_read_page_by_page_from_resource_manager()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "skiptoken=2",
                """{"value":[{"subscriptionId":"sub-2","displayName":"Alpha"}]}""")
            .OnJson(HttpMethod.Get, "/subscriptions?",
                """{"value":[{"subscriptionId":"sub-1","displayName":"Zulu"}],"nextLink":"https://management.azure.com/subscriptions?api-version=2022-12-01&skiptoken=2"}""");
        using var client = Client(handler);

        var subscriptions = await client.ListSubscriptionsAsync();

        Assert.Equal(["Alpha", "Zulu"], subscriptions.Select(s => s.Name));
        Assert.All(handler.Requests, r => Assert.Equal("Bearer token:" + Arm, r.Authorization));
    }

    [Fact]
    public async Task Components_carry_their_app_id_and_resource_group()
    {
        using var client = Client(OnOneComponent(new FakeHttpHandler()));

        var (components, notes) = await client.ListAllComponentsAsync();

        var component = Assert.Single(components);
        Assert.Empty(notes);
        Assert.Equal(AppId, component.AppId);
        Assert.Equal("rg-telemetry", component.ResourceGroup);
        Assert.Equal("Contoso Prod", component.SubscriptionName);
        Assert.Equal("uksouth", component.Location);
    }

    [Fact]
    public async Task A_subscription_that_refuses_is_noted_and_the_rest_still_listed()
    {
        var handler = OnOneComponent(new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "/subscriptions?",
                """{"value":[{"subscriptionId":"sub-1","displayName":"Contoso Prod"},{"subscriptionId":"sub-x","displayName":"Locked"}]}""")
            .OnError(HttpMethod.Get, "/subscriptions/sub-x/", HttpStatusCode.Forbidden, "AuthorizationFailed"));
        using var client = Client(handler);

        var (components, notes) = await client.ListAllComponentsAsync();

        Assert.Single(components);
        Assert.Contains(notes, n => n.StartsWith("Locked could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_query_posts_its_kql_and_period_to_the_app()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Post, $"/v1/apps/{AppId}/query", """
            {"tables":[{"name":"PrimaryResult",
              "columns":[{"name":"timestamp","type":"datetime"},{"name":"name","type":"string"},{"name":"success","type":"bool"},
                         {"name":"duration","type":"real"},{"name":"customDimensions","type":"dynamic"}],
              "rows":[["2026-10-20T08:15:02.123Z","Contoso.Plugins.AccountPostUpdate",false,812.5,"{\"StepName\":\"Contoso.Plugins.AccountPostUpdate: Update of account\",\"EntityName\":\"account\"}"]]}]}
            """);
        using var client = Client(handler);
        var from = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        using var table = await client.QueryAsync(AppId, "dependencies | take 1", from, from.AddDays(7));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer token:" + AppInsightsApi, request.Authorization);
        using (var body = JsonDocument.Parse(request.Body!))
        {
            Assert.Equal("dependencies | take 1", body.RootElement.GetProperty("query").GetString());
            Assert.Equal("2026-10-13T00:00:00Z/2026-10-20T00:00:00Z", body.RootElement.GetProperty("timespan").GetString());
        }

        var row = Assert.Single(table.Rows);
        Assert.Equal("Contoso.Plugins.AccountPostUpdate", row.String("name"));
        Assert.False(row.Bool("success"));
        Assert.Equal(812.5, row.Number("duration"));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 15, 2, 123, TimeSpan.Zero), row.Date("timestamp"));
        Assert.Equal("account", row.Properties("customDimensions")["entityname"]);
        Assert.Null(row.String("no such column"));
        Assert.Null(table.PartialError);
    }

    [Fact]
    public async Task A_partial_result_keeps_its_rows_and_says_so()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Post, "/query", """
            {"tables":[{"name":"PrimaryResult","columns":[{"name":"n","type":"long"}],"rows":[[1]]}],
             "error":{"code":"PartialError","message":"There were some errors when processing your query.","innererror":{"message":"Query result set has exceeded the internal limit"}}}
            """);
        using var client = Client(handler);

        using var table = await client.QueryAsync(AppId, "x", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);

        Assert.Single(table.Rows);
        Assert.Contains("exceeded the internal limit", table.PartialError);
    }

    [Fact]
    public async Task Azure_is_asked_in_the_environments_cloud()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/subscriptions?", """{"value":[]}""");
        using var client = Client(handler, cloud: Clouds.UsGccHigh);

        await client.ListSubscriptionsAsync();

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://management.usgovcloudapi.net/subscriptions?", request.Url);
        Assert.Equal("Bearer token:https://management.usgovcloudapi.net", request.Authorization);
    }

    [Fact]
    public void A_resource_group_is_read_from_a_resource_id()
    {
        Assert.Equal("rg-telemetry", AzureMonitorClient.ResourceGroupOf(ComponentId));
        Assert.Null(AzureMonitorClient.ResourceGroupOf("/subscriptions/sub-1"));
    }
}
