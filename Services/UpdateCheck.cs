using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PPObjectSearch.Services;

/// <summary>A release newer than the running build.</summary>
public sealed record AvailableUpdate(string Version, string Url);

/// <summary>
/// Asks GitHub for the latest release and says whether it is newer than the running build. Never
/// downloads or installs anything: it only offers the release page.
/// </summary>
public static class UpdateCheck
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1075:URIs should not be hardcoded",
        Justification = "The app's own release feed: fixed by design, and named here once.")]
    public const string LatestReleaseUrl = "https://api.github.com/repos/tmnrtn/PPObjectSearch/releases/latest";

    /// <summary>At most this often, so starting the app many times a day costs one request.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>The newer release, or null when there is none, the check is off, or it failed.</summary>
    public static async Task<AvailableUpdate?> CheckAsync(
        string runningVersion, HttpMessageInvoker http, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PPObjectSearch", runningVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;

            return tag is not null && url is not null && IsNewer(tag, runningVersion)
                ? new AvailableUpdate(tag.TrimStart('v', 'V'), url)
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Offline, behind a proxy that blocks GitHub, rate limited: it simply says nothing.
            Log.Info($"Update check skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>"v1.13.0" against "1.12.2". A version that does not parse is never newer.</summary>
    internal static bool IsNewer(string tag, string running) =>
        System.Version.TryParse(tag.TrimStart('v', 'V').Split(SuffixSeparators)[0], out var latest) &&
        System.Version.TryParse(running.Split(SuffixSeparators)[0], out var current) &&
        latest > current;

    /// <summary>Where a pre-release ("-beta.1") or build ("+abc123") suffix starts.</summary>
    private static readonly char[] SuffixSeparators = ['-', '+'];
}
