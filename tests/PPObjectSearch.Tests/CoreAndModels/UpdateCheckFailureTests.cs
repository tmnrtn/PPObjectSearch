using System.Net.Http;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>The update check when GitHub cannot be reached or answers with something unexpected.</summary>
public class UpdateCheckFailureTests
{
    [Fact]
    public async Task An_unreachable_github_offers_nothing()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "releases/latest", _ => throw new HttpRequestException("No such host is known."));

        Assert.Null(await UpdateCheck.CheckAsync("1.12.2", new HttpClient(handler)));
    }

    [Fact]
    public async Task A_cancelled_check_is_not_swallowed()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "releases/latest", _ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateCheck.CheckAsync("1.12.2", new HttpClient(handler)));
    }

    [Theory]
    [InlineData("""{"html_url":"https://github.com/x"}""")]
    [InlineData("""{"tag_name":"v9.0.0"}""")]
    [InlineData("""{"tag_name":"v1.0.0","html_url":"https://github.com/x"}""")]
    [InlineData("""not json""")]
    public async Task A_release_without_a_newer_tag_and_a_page_offers_nothing(string json)
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "releases/latest", json);

        Assert.Null(await UpdateCheck.CheckAsync("1.12.2", new HttpClient(handler)));
    }

    [Fact]
    public void The_check_runs_at_most_once_a_day()
    {
        Assert.Equal(TimeSpan.FromDays(1), UpdateCheck.Interval);
    }
}
