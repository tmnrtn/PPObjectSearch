using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class EnvironmentTypeProbeTests
{
    private const string Bap = "https://api.bap.microsoft.com";
    private const string UserList = "BusinessAppPlatform/environments?";
    private const string AdminList = "BusinessAppPlatform/scopes/admin/environments?";
    private const string EnvUrl = "https://contoso.crm11.dynamics.com";
    private const string EnvId = "11111111-2222-3333-4444-555555555555";

    private static string Environment(string name, string? sku, string? instanceApiUrl = null, string? instanceUrl = null,
        string displayName = "Contoso")
    {
        var linked = new Dictionary<string, string?>();
        if (instanceApiUrl is not null) linked["instanceApiUrl"] = instanceApiUrl;
        if (instanceUrl is not null) linked["instanceUrl"] = instanceUrl;

        var properties = new Dictionary<string, object?> { ["displayName"] = displayName, ["linkedEnvironmentMetadata"] = linked };
        if (sku is not null) properties["environmentSku"] = sku;

        return JsonSerializer.Serialize(new { name, properties });
    }

    private static string List(string? nextLink, params string[] environments) =>
        "{\"value\":[" + string.Join(",", environments) + "]" +
        (nextLink is null ? "" : ",\"nextLink\":" + JsonSerializer.Serialize(nextLink)) + "}";

    private static Task<EnvironmentTypeInfo> Probe(FakeHttpHandler handler, string? environmentId = EnvId,
        EnvironmentAuthContext? auth = null, string environmentUrl = EnvUrl) =>
        EnvironmentTypeProbe.ProbeAsync(auth ?? TestAuth.TokensFor(Bap), new HttpClient(handler), environmentUrl, environmentId);

    [Fact]
    public async Task No_power_platform_token_reads_as_unknown_without_calling_the_api()
    {
        var handler = new FakeHttpHandler();

        var result = await Probe(handler, auth: TestAuth.TokensFor(EnvUrl));

        Assert.Equal(EnvironmentSku.Unknown, result.Sku);
        Assert.True(result.IsProtected);
        Assert.Contains("No Power Platform API token", result.Detail);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Token_source_throwing_reads_as_unknown()
    {
        var handler = new FakeHttpHandler();
        var auth = new EnvironmentAuthContext((_, _) => throw new InvalidOperationException("MSAL blew up"));

        var result = await Probe(handler, auth: auth);

        Assert.Equal(EnvironmentSku.Unknown, result.Sku);
        Assert.Contains("No Power Platform API token", result.Detail);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var handler = new FakeHttpHandler();
        var auth = new EnvironmentAuthContext((_, _) => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe(handler, auth: auth));
    }

    [Fact]
    public async Task Matches_by_environment_id_in_name_and_sends_the_bap_token()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, List(null,
                Environment("other", "Sandbox", "https://other.crm.dynamics.com"),
                Environment(EnvId.ToUpperInvariant(), "Production", displayName: "Contoso Prod")));

        var result = await Probe(handler);

        Assert.Equal(EnvironmentSku.Production, result.Sku);
        Assert.Equal("Contoso Prod", result.DisplayName);
        Assert.Null(result.Detail);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer token:" + Bap, request.Authorization);
        Assert.StartsWith(Bap + "/providers/Microsoft.BusinessAppPlatform/environments?api-version=", request.Url);
    }

    [Fact]
    public async Task Matches_by_host_in_instance_api_url()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, List(null,
                Environment("other", "Production", "https://other.crm11.dynamics.com/"),
                Environment("env-x", "Sandbox", "https://CONTOSO.crm11.dynamics.com/")));

        var result = await Probe(handler, environmentId: null);

        Assert.Equal(EnvironmentSku.Sandbox, result.Sku);
        Assert.False(result.IsProtected);
    }

    [Fact]
    public async Task Matches_by_host_in_instance_url_when_api_url_is_missing()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, List(null,
                Environment("env-x", "Developer", instanceUrl: "https://contoso.crm11.dynamics.com/")));

        var result = await Probe(handler, environmentId: "not-this-one");

        Assert.Equal(EnvironmentSku.Developer, result.Sku);
    }

    [Fact]
    public async Task Different_host_does_not_match()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, List(null, Environment("x", "Sandbox", "https://contoso2.crm11.dynamics.com/")))
            .OnJson(HttpMethod.Get, AdminList, List(null));

        var result = await Probe(handler, environmentId: null);

        Assert.Equal(EnvironmentSku.Unknown, result.Sku);
    }

    [Fact]
    public async Task Follows_next_link_to_find_an_environment_past_the_first_page()
    {
        var page2 = Bap + "/providers/Microsoft.BusinessAppPlatform/environments?api-version=2020-10-01&$skiptoken=page2";
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "skiptoken=page2", List(null, Environment(EnvId, "Trial")))
            .OnJson(HttpMethod.Get, UserList, List(page2, Environment("a", "Production"), Environment("b", "Production")));

        var result = await Probe(handler);

        Assert.Equal(EnvironmentSku.Trial, result.Sku);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(page2, handler.Requests[1].Url);
        Assert.Equal("Bearer token:" + Bap, handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task User_list_forbidden_falls_through_to_admin_list()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Get, UserList, HttpStatusCode.Forbidden)
            .OnJson(HttpMethod.Get, AdminList, List(null, Environment(EnvId, "Sandbox")));

        var result = await Probe(handler);

        Assert.Equal(EnvironmentSku.Sandbox, result.Sku);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/scopes/admin/", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Environment_only_on_admin_list_is_found_there()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, AdminList, List(null, Environment(EnvId, "Default")))
            .OnJson(HttpMethod.Get, UserList, List(null, Environment("other", "Sandbox")));

        var result = await Probe(handler);

        Assert.Equal(EnvironmentSku.Default, result.Sku);
        Assert.True(result.IsProtected);
    }

    [Theory]
    [InlineData("Production", EnvironmentSku.Production)]
    [InlineData("production", EnvironmentSku.Production)]
    [InlineData(" Sandbox ", EnvironmentSku.Sandbox)]
    [InlineData("Trial", EnvironmentSku.Trial)]
    [InlineData("SubscriptionBasedTrial", EnvironmentSku.Trial)]
    [InlineData("Developer", EnvironmentSku.Developer)]
    [InlineData("Dev", EnvironmentSku.Developer)]
    [InlineData("Teams", EnvironmentSku.Teams)]
    [InlineData("Default", EnvironmentSku.Default)]
    public async Task Maps_every_known_sku(string raw, EnvironmentSku expected)
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, List(null, Environment(EnvId, raw)));

        var result = await Probe(handler);

        Assert.Equal(expected, result.Sku);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Unknown_sku_stays_unknown_and_names_the_raw_value()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, List(null, Environment(EnvId, "SuperSandbox")));

        var result = await Probe(handler);

        Assert.Equal(EnvironmentSku.Unknown, result.Sku);
        Assert.True(result.IsProtected);
        Assert.Equal("Contoso", result.DisplayName);
        Assert.Contains("'SuperSandbox'", result.Detail);
        Assert.Contains("does not know", result.Detail);
    }

    [Fact]
    public async Task Missing_sku_stays_unknown_and_says_none_was_returned()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, List(null, Environment(EnvId, sku: null)));

        var result = await Probe(handler);

        Assert.Equal(EnvironmentSku.Unknown, result.Sku);
        Assert.Contains("returned no environment type", result.Detail);
    }

    [Fact]
    public async Task Not_found_anywhere_is_unknown_and_explains_both_outcomes()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Get, UserList, HttpStatusCode.Forbidden)
            .OnJson(HttpMethod.Get, AdminList, List(null, Environment("a", "Sandbox"), Environment("b", "Production")));

        var result = await Probe(handler);

        Assert.Equal(EnvironmentSku.Unknown, result.Sku);
        Assert.True(result.IsProtected);
        Assert.Contains("not found in the Power Platform API", result.Detail);
        Assert.Contains("host contoso.crm11.dynamics.com or id " + EnvId, result.Detail);
        Assert.Contains("user list: HTTP 403", result.Detail);
        Assert.Contains("admin list: 2 environment(s), none matching", result.Detail);
    }

    [Fact]
    public async Task Not_found_without_an_id_describes_only_the_host()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, List(null))
            .OnJson(HttpMethod.Get, AdminList, List(null));

        var result = await Probe(handler, environmentId: null);

        Assert.Contains("looked for host contoso.crm11.dynamics.com;", result.Detail);
        Assert.Contains("user list: 0 environment(s), none matching", result.Detail);
        Assert.Contains("admin list: 0 environment(s), none matching", result.Detail);
    }

    [Fact]
    public async Task Response_without_a_value_array_is_reported_and_stays_unknown()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, "{\"something\":1}")
            .OnJson(HttpMethod.Get, AdminList, "not json at all");

        var result = await Probe(handler);

        Assert.Equal(EnvironmentSku.Unknown, result.Sku);
        Assert.Contains("user list: no environment list in the response", result.Detail);
        Assert.Contains("admin list:", result.Detail);
    }

    [Fact]
    public async Task Unparseable_environment_url_still_matches_on_id()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, UserList, List(null, Environment(EnvId, "Sandbox")));

        var result = await Probe(handler, environmentUrl: "not a url");

        Assert.Equal(EnvironmentSku.Sandbox, result.Sku);
    }

    [Theory]
    [InlineData(EnvironmentSku.Unknown, true, "Type unknown")]
    [InlineData(EnvironmentSku.Production, true, "Production")]
    [InlineData(EnvironmentSku.Default, true, "Default environment")]
    [InlineData(EnvironmentSku.Sandbox, false, "Sandbox")]
    [InlineData(EnvironmentSku.Trial, false, "Trial")]
    [InlineData(EnvironmentSku.Developer, false, "Developer")]
    [InlineData(EnvironmentSku.Teams, false, "Teams")]
    public void Protection_and_label_for_every_sku(EnvironmentSku sku, bool isProtected, string label)
    {
        var info = new EnvironmentTypeInfo(sku, null, null);

        Assert.Equal(isProtected, info.IsProtected);
        Assert.Equal(label, info.SkuLabel);
    }

    [Fact]
    public void Every_sku_value_is_covered_by_the_theory()
    {
        Assert.Equal(7, Enum.GetValues<EnvironmentSku>().Length);
    }
}
