using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using PPObjectSearch.Core;

namespace PPObjectSearch.Tests.Infrastructure;

public class RetryHandlerTests
{
    private readonly List<TimeSpan> _waits = new();

    private HttpClient Client(FakeHttpHandler inner, int maxAttempts = RetryHandler.DefaultMaxAttempts) =>
        new(new RetryHandler(inner, maxAttempts, (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        }));

    /// <summary>Answers with each status in turn, then the last one for ever.</summary>
    private static FakeHttpHandler Sequence(params HttpResponseMessage[] responses)
    {
        var next = 0;
        return new FakeHttpHandler().On(null, "", _ => responses[Math.Min(next++, responses.Length - 1)]);
    }

    private static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } after) response.Headers.RetryAfter = new RetryConditionHeaderValue(after);
        return response;
    }

    [Fact]
    public async Task A_throttled_read_is_retried_after_the_wait_the_server_asks_for()
    {
        var inner = Sequence(Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)), Status(HttpStatusCode.OK));

        using var response = await Client(inner).GetAsync("https://x.test/a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, _waits);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Transient_failures_on_a_read_back_off_and_retry(HttpStatusCode status)
    {
        var inner = Sequence(Status(status), Status(status), Status(HttpStatusCode.OK));

        using var response = await Client(inner).GetAsync("https://x.test/a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _waits.Count);
        Assert.True(_waits[1] > _waits[0]);
    }

    [Fact]
    public async Task It_gives_up_after_the_last_attempt_and_returns_the_failure()
    {
        var inner = Sequence(Status(HttpStatusCode.TooManyRequests));

        using var response = await Client(inner, maxAttempts: 3).GetAsync("https://x.test/a");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, inner.Requests.Count);
    }

    private static readonly string[] SameBodyBothTimes = ["{\"a\":1}", "{\"a\":1}"];

    [Fact]
    public async Task A_throttled_write_is_retried_with_its_body()
    {
        var inner = Sequence(Status(HttpStatusCode.TooManyRequests), Status(HttpStatusCode.NoContent));

        using var response = await Client(inner).PostAsync("https://x.test/rows", new StringContent("{\"a\":1}"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(SameBodyBothTimes, inner.Requests.Select(r => r.Body));
    }

    [Fact]
    public async Task A_write_that_timed_out_at_the_gateway_is_not_repeated()
    {
        // The write may have happened; repeating it is not the transport's decision.
        var inner = Sequence(Status(HttpStatusCode.GatewayTimeout), Status(HttpStatusCode.NoContent));

        using var response = await Client(inner).PostAsync("https://x.test/rows", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Other_failures_are_returned_at_once(HttpStatusCode status)
    {
        var inner = Sequence(Status(status), Status(HttpStatusCode.OK));

        using var response = await Client(inner).GetAsync("https://x.test/a");

        Assert.Equal(status, response.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Headers_travel_with_a_retried_request()
    {
        var inner = Sequence(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://x.test/a");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "t");
        request.Headers.Add("Prefer", "odata.maxpagesize=500");

        using var response = await Client(inner).SendAsync(request);

        Assert.All(inner.Requests, r => Assert.Equal("Bearer t", r.Authorization));
        Assert.All(inner.Requests, r => Assert.Equal("odata.maxpagesize=500", r.Header("Prefer")));
    }

    [Fact]
    public async Task A_very_long_retry_after_is_capped()
    {
        var inner = Sequence(Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(30)), Status(HttpStatusCode.OK));

        using var response = await Client(inner).GetAsync("https://x.test/a");

        Assert.Equal(RetryHandler.MaxDelay, Assert.Single(_waits));
    }
}
