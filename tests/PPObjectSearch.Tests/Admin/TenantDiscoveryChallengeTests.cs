using System.Net;
using System.Net.Http;
using PPObjectSearch.Auth;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Admin;

/// <summary>Which sign-in host a Dataverse challenge names, and discovery when the challenge is missing or the request fails.</summary>
public class TenantDiscoveryChallengeTests
{
    private const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    private static FakeHttpHandler Challenge(string? header) => new FakeHttpHandler().On(HttpMethod.Get, "WhoAmI", _ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (header is not null) response.Headers.TryAddWithoutValidation("WWW-Authenticate", header);
        return response;
    });

    [Theory]
    [InlineData($"authorization_uri=https://login.microsoftonline.us/{Tenant}/oauth2/authorize", "login.microsoftonline.us")]
    [InlineData($"Bearer authorization_uri=\"https://login.chinacloudapi.cn/{Tenant}/oauth2/authorize\"", "login.chinacloudapi.cn")]
    [InlineData("authorization_uri=\"not a uri\"", null)]
    [InlineData("realm=\"\"", null)]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void The_sign_in_host_is_read_from_the_challenge(string? challenge, string? expected)
    {
        Assert.Equal(expected, TenantDiscovery.AuthorityHostFromChallenge(challenge));
    }

    [Fact]
    public async Task Discovery_returns_the_tenant_and_the_clouds_sign_in_host()
    {
        var handler = Challenge($"Bearer authorization_uri=https://login.microsoftonline.us/{Tenant}/oauth2/authorize, resource_id=https://contoso.crm.microsoftdynamics.us/");

        var (tenant, host) = await TenantDiscovery.DiscoverAsync("https://contoso.crm.microsoftdynamics.us/", new HttpClient(handler), default);

        Assert.Equal(Tenant, tenant);
        Assert.Equal("login.microsoftonline.us", host);
        Assert.Equal("https://contoso.crm.microsoftdynamics.us/api/data/v9.2/WhoAmI", Assert.Single(handler.Requests).Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer authorization_uri=https://login.microsoftonline.com/common/oauth2/authorize")]
    public async Task A_challenge_that_names_no_tenant_says_nothing(string? header)
    {
        var (tenant, host) = await TenantDiscovery.DiscoverAsync("https://contoso.crm.dynamics.com", new HttpClient(Challenge(header)), default);

        Assert.Null(tenant);
        Assert.Null(host);
    }

    [Fact]
    public async Task A_request_that_fails_says_nothing_rather_than_stopping_sign_in()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "WhoAmI", _ => throw new HttpRequestException("No such host is known."));

        Assert.Null(await TenantDiscovery.GetTenantIdAsync("https://contoso.crm.dynamics.com", new HttpClient(handler), default));
    }

    [Fact]
    public async Task A_cancelled_discovery_is_not_swallowed()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "WhoAmI", _ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TenantDiscovery.DiscoverAsync("https://contoso.crm.dynamics.com", new HttpClient(handler), default));
    }
}
