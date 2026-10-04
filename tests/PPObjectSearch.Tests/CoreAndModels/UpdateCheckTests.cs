using System.Net;
using System.Net.Http;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.CoreAndModels;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.13.0", "1.12.2", true)]
    [InlineData("v1.12.10", "1.12.9", true)]
    [InlineData("v1.12.2", "1.12.2", false)]
    [InlineData("v1.12.1", "1.12.2", false)]
    [InlineData("1.13.0", "1.12.2+abc1234", true)]
    [InlineData("v2.0.0-beta", "1.12.2", true)]
    [InlineData("nightly", "1.12.2", false)]
    [InlineData("v1.13.0", "unknown", false)]
    public void A_release_is_offered_only_when_it_is_newer(string tag, string running, bool expected)
    {
        Assert.Equal(expected, UpdateCheck.IsNewer(tag, running));
    }

    [Fact]
    public async Task A_newer_release_is_offered_with_its_page()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "releases/latest",
            """{"tag_name":"v1.13.0","html_url":"https://github.com/tmnrtn/PPObjectSearch/releases/tag/v1.13.0"}""");

        var update = await UpdateCheck.CheckAsync("1.12.2", new HttpClient(handler));

        Assert.Equal(new AvailableUpdate("1.13.0", "https://github.com/tmnrtn/PPObjectSearch/releases/tag/v1.13.0"), update);
        Assert.StartsWith("PPObjectSearch/1.12.2", Assert.Single(handler.Requests).Header("User-Agent"));
    }

    [Fact]
    public async Task A_failed_check_offers_nothing()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Get, "releases/latest", HttpStatusCode.Forbidden);

        Assert.Null(await UpdateCheck.CheckAsync("1.12.2", new HttpClient(handler)));
    }
}
