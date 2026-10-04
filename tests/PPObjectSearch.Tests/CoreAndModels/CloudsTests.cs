using System.Net.Http;
using PPObjectSearch.Auth;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.CoreAndModels;

public class CloudsTests
{
    [Theory]
    [InlineData("https://contoso.crm11.dynamics.com", "Public")]
    [InlineData("contoso.crm.dynamics.com", "Public")]
    [InlineData("https://contoso.crm9.dynamics.com", "US Government (GCC)")]
    [InlineData("https://contoso.crm.microsoftdynamics.us", "US Government (GCC High)")]
    [InlineData("https://contoso.crm.appsplatform.us", "US Government (DoD)")]
    [InlineData("https://contoso.crm.dynamics.cn", "China (21Vianet)")]
    public void An_environments_host_names_its_cloud(string url, string cloud)
    {
        Assert.Equal(cloud, Clouds.ForEnvironment(url)!.Name);
    }

    [Fact]
    public void An_unknown_host_names_no_cloud()
    {
        Assert.Null(Clouds.ForEnvironment("https://crm.contoso.example"));
        Assert.Null(Clouds.ForEnvironment(null));
        Assert.Null(Clouds.ForEnvironment("not a url at all"));
    }

    [Theory]
    [InlineData("login.microsoftonline.com", "Public")]
    [InlineData("login.microsoftonline.us", "US Government (GCC High)")]
    [InlineData("login.chinacloudapi.cn", "China (21Vianet)")]
    public void A_known_authority_names_its_cloud(string host, string cloud)
    {
        Assert.Equal(cloud, Clouds.ForAuthorityHost(host)!.Name);
    }

    [Fact]
    public void Any_other_authority_is_not_trusted()
    {
        Assert.Null(Clouds.ForAuthorityHost("login.evil.example"));
    }

    [Fact]
    public void The_challenge_authority_host_is_read()
    {
        Assert.Equal("login.microsoftonline.us",
            TenantDiscovery.AuthorityHostFromChallenge("Bearer authorization_uri=https://login.microsoftonline.us/abc/oauth2/authorize, resource_id=x"));
        Assert.Null(TenantDiscovery.AuthorityHostFromChallenge("Bearer realm=x"));
    }

    [Fact]
    public void Links_go_to_the_clouds_own_portals()
    {
        var item = new PPObjectSearch.Models.SolutionComponentItem
        {
            Name = "account", ComponentTypeName = "Table", ComponentType = 1, ObjectId = Guid.NewGuid()
        };

        var high = new MakerPortalLinkBuilder("env-1", "https://contoso.crm.microsoftdynamics.us", null).Build(item, Guid.NewGuid());
        Assert.StartsWith("https://make.high.powerapps.us/environments/env-1/entities/", high);

        var flow = MakerPortalLinkBuilder.BuildFlowUrl("env-1", "flow-1", Clouds.China);
        Assert.Equal("https://make.powerautomate.cn/environments/env-1/flows/flow-1/details", flow);
        Assert.StartsWith("https://make.powerautomate.com/", MakerPortalLinkBuilder.BuildFlowUrl("env-1", "flow-1"));
    }

    [Fact]
    public async Task Power_Automate_is_asked_in_the_environments_cloud()
    {
        var auth = EnvironmentAuthContext.FromTokenSource((resource, _) => Task.FromResult<string?>("token:" + resource));
        auth.UseCloud(Clouds.UsGccHigh);
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/connections?", """{"value":[]}""");

        await new PowerAutomateClient(auth, handler).GetConnectionsAsync("env-1");

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://high.api.flow.microsoft.us/providers/Microsoft.ProcessSimple/", request.Url);
        Assert.Equal("Bearer token:https://high.service.flow.microsoft.us/", request.Authorization);
    }

    [Fact]
    public void A_dataverse_client_knows_its_cloud_from_its_host()
    {
        Assert.Same(Clouds.UsDod, Fakes.Dataverse(new FakeHttpHandler(), "https://contoso.crm.appsplatform.us").Cloud);
        Assert.Same(Clouds.Public, Fakes.Dataverse(new FakeHttpHandler()).Cloud);
    }
}
