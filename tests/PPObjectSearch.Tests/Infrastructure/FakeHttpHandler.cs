using System.Net;
using System.Net.Http;
using System.Text;

namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>A request as it was sent, captured before the message is disposed.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, string? Authorization, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The URL with percent-encoding undone, which is what routes and assertions match against.</summary>
    public string Url => Uri.UnescapeDataString(Uri.ToString());

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Stands in for the network. Routes are tried in the order they were added; the first whose
/// method matches and whose URL contains the given text answers. Anything unrouted gets a 404 with
/// an OData-style error naming the request, so a missing route fails loudly rather than hanging.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod? Method, string UrlContains, Func<RecordedRequest, Task<HttpResponseMessage>> Respond)> _routes = new();

    public List<RecordedRequest> Requests { get; } = new();

    public FakeHttpHandler On(HttpMethod? method, string urlContains, Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _routes.Add((method, urlContains, r => Task.FromResult(respond(r))));
        return this;
    }

    /// <summary>A route that answers when the test says so - for requests that must overlap.</summary>
    public FakeHttpHandler OnAsync(HttpMethod? method, string urlContains, Func<RecordedRequest, Task<HttpResponseMessage>> respond)
    {
        _routes.Add((method, urlContains, respond));
        return this;
    }

    public FakeHttpHandler OnJson(HttpMethod? method, string urlContains, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, urlContains, _ => Json(json, status));

    public FakeHttpHandler OnStatus(HttpMethod? method, string urlContains, HttpStatusCode status) =>
        On(method, urlContains, _ => new HttpResponseMessage(status));

    public FakeHttpHandler OnError(HttpMethod? method, string urlContains, HttpStatusCode status, string message) =>
        OnJson(method, urlContains, ErrorJson(message), status);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string ErrorJson(string message) =>
        System.Text.Json.JsonSerializer.Serialize(new { error = new { code = "0x0", message } });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString(), headers);
        lock (Requests) Requests.Add(recorded);

        foreach (var (method, contains, respond) in _routes)
        {
            if (method is not null && method != request.Method) continue;
            if (!recorded.Url.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;

            var response = await respond(recorded);
            response.RequestMessage ??= request;
            return response;
        }

        return Json(ErrorJson($"No fake route for {request.Method} {recorded.Url}"), HttpStatusCode.NotFound);
    }
}
