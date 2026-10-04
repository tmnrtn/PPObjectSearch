using System.Net;
using System.Net.Http;
using PPObjectSearch.Auth;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Admin;

public class TenantDiscoveryTests
{
    private const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    [Theory]
    [InlineData($"authorization_uri=\"https://login.microsoftonline.com/{Tenant}/oauth2/authorize\", resource_id=\"https://x\"", Tenant)]
    [InlineData($"authorization_uri=https://login.microsoftonline.com/{Tenant}/oauth2/authorize", Tenant)]
    [InlineData($"realm=\"\", AUTHORIZATION_URI = \"https://login.microsoftonline.us/{Tenant}/oauth2/authorize\"", Tenant)]
    [InlineData("authorization_uri=\"https://login.microsoftonline.com/common/oauth2/authorize\"", null)]
    [InlineData("authorization_uri=\"https://login.microsoftonline.com/organizations/oauth2/authorize\"", null)]
    [InlineData("authorization_uri=\"not a uri\"", null)]
    [InlineData("resource_id=\"https://x\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_tenant_is_read_from_the_bearer_challenge(string? parameter, string? expected)
    {
        Assert.Equal(expected, TenantDiscovery.TenantFromChallenge(parameter));
    }

    [Fact]
    public async Task An_unauthenticated_WhoAmI_names_the_environments_tenant()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/api/data/v9.2/WhoAmI", _ =>
        {
            // As Dataverse sends it: the URLs unquoted, which .NET's typed header parser rejects.
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.TryAddWithoutValidation("WWW-Authenticate",
                $"Bearer authorization_uri=https://login.microsoftonline.com/{Tenant}/oauth2/authorize, resource_id=https://contoso.crm.dynamics.com/");
            return response;
        });

        var tenant = await TenantDiscovery.GetTenantIdAsync("https://contoso.crm.dynamics.com/", new HttpClient(handler), default);

        Assert.Equal(Tenant, tenant);
    }

    [Fact]
    public async Task Anything_but_a_401_says_nothing_about_the_tenant()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Get, "WhoAmI", HttpStatusCode.NotFound);

        Assert.Null(await TenantDiscovery.GetTenantIdAsync("https://contoso.crm.dynamics.com", new HttpClient(handler), default));
    }
}
