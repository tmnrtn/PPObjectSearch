using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace PPObjectSearch.Core;

/// <summary>
/// Retries requests that failed for reasons that pass: service-protection throttling (429), a
/// service briefly unavailable (503) or a gateway timeout (504), and a connection that dropped.
///
/// Dataverse, Graph and Power Automate all throttle as a matter of routine, and this app reads in
/// parallel - a burst of layer or row-count requests is exactly what trips the limits. Without a
/// retry, a throttled read looked like "no data" and a throttled write failed a row for good.
///
/// Reads are retried on any of those. Writes are retried only on 429 and 503, which mean the server
/// did not act on the request; a 504 or a dropped connection after a write leaves it unknown whether
/// the write happened, and repeating it is not this layer's call.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    public const int DefaultMaxAttempts = 4;

    /// <summary>The longest a single wait may be, whatever Retry-After asks for.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RetryHandler(
        HttpMessageHandler inner,
        int maxAttempts = DefaultMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(inner)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// One connection pool for the whole app, recycled every few minutes so DNS changes are
    /// picked up, wrapped in the retry policy. Clients built on it must not dispose it.
    /// </summary>
    public static HttpMessageHandler Shared { get; } = new RetryHandler(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A request can only be sent once, so a retried one is a copy. Its body is read up front
        // so every copy can carry it.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var attempt = 0;
        while (true)
        {
            attempt++;
            var current = attempt == 1 ? request : Clone(request, body);
            HttpResponseMessage response;

            try
            {
                if (attempt == 1 && body is not null) request.Content = Copy(request.Content!, body);
                response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _maxAttempts && IsSafeToRepeat(request.Method) && !cancellationToken.IsCancellationRequested)
            {
                await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (attempt >= _maxAttempts || !ShouldRetry(response.StatusCode, request.Method)) return response;

            var wait = RetryAfter(response) ?? Backoff(attempt);
            response.Dispose();

            await _delay(wait > MaxDelay ? MaxDelay : wait, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool ShouldRetry(HttpStatusCode status, HttpMethod method) => status switch
    {
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.GatewayTimeout => IsSafeToRepeat(method),
        _ => false
    };

    /// <summary>Methods that change nothing, so repeating one after an unknown outcome is harmless.</summary>
    private static bool IsSafeToRepeat(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options;

    /// <summary>Retry-After as seconds or as a date; Dataverse sends seconds.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;

        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;

        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>1, 2, 4... seconds, with up to a quarter more at random so parallel readers spread out.</summary>
    internal static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Pow(2, attempt - 1);
        return TimeSpan.FromSeconds(seconds * (1 + Random.Shared.NextDouble() / 4));
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;

        if (body is not null && request.Content is not null) clone.Content = Copy(request.Content, body);

        return clone;
    }

    private static ByteArrayContent Copy(HttpContent original, byte[] body)
    {
        var content = new ByteArrayContent(body);
        foreach (var header in original.Headers) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return content;
    }
}
