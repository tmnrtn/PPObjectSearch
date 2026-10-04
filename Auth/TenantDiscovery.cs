using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace PPObjectSearch.Auth;

/// <summary>
/// Finds which Entra ID tenant an environment belongs to, so tabs can point at environments in
/// different tenants at the same time.
///
/// Dataverse answers an unauthenticated request with
/// <c>WWW-Authenticate: Bearer authorization_uri=https://login.microsoftonline.com/{tenantId}/oauth2/authorize</c>,
/// which is the authoritative answer even when the signed-in user is only a guest.
/// </summary>
public static partial class TenantDiscovery
{
    private static readonly HttpClient Http = new(new Core.RetryHandler(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }))
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    [GeneratedRegex("authorization_uri\\s*=\\s*\"?(?<uri>[^\",\\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationUriRegex();

    public static async Task<string?> GetTenantIdAsync(string environmentUrl, CancellationToken ct = default) =>
        (await DiscoverAsync(environmentUrl, Http, ct).ConfigureAwait(false)).TenantId;

    /// <summary>The tenant, and the host of the sign-in authority the challenge names.</summary>
    public static Task<(string? TenantId, string? AuthorityHost)> DiscoverAsync(string environmentUrl, CancellationToken ct = default) =>
        DiscoverAsync(environmentUrl, Http, ct);

    /// <summary>For tests: the challenge comes from <paramref name="http"/> rather than the network.</summary>
    internal static async Task<string?> GetTenantIdAsync(string environmentUrl, HttpMessageInvoker http, CancellationToken ct) =>
        (await DiscoverAsync(environmentUrl, http, ct).ConfigureAwait(false)).TenantId;

    internal static async Task<(string? TenantId, string? AuthorityHost)> DiscoverAsync(
        string environmentUrl, HttpMessageInvoker http, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, environmentUrl.TrimEnd('/') + "/api/data/v9.2/WhoAmI");
            request.Headers.ConnectionClose = true;
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Unauthorized) return (null, null);

            // Read raw: Dataverse writes the challenge's URLs unquoted, which the typed
            // WwwAuthenticate collection treats as malformed and leaves out entirely.
            if (response.Headers.NonValidated.TryGetValues("WWW-Authenticate", out var challenges))
            {
                foreach (var challenge in challenges)
                {
                    if (TenantFromChallenge(challenge) is { } tenant) return (tenant, AuthorityHostFromChallenge(challenge));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"Tenant discovery failed for {environmentUrl}", ex);
            // Discovery is best effort; the caller falls back to the "organizations" authority.
        }

        return (null, null);
    }

    /// <summary>The host of the challenge's authorization_uri - which cloud's sign-in it names.</summary>
    internal static string? AuthorityHostFromChallenge(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return null;

        var match = AuthorizationUriRegex().Match(parameter);
        return match.Success && Uri.TryCreate(match.Groups["uri"].Value, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>
    /// The tenant named in a Bearer challenge's authorization_uri - or null for none, a malformed
    /// one, or one of the multi-tenant names, which say nothing about where the environment is.
    /// </summary>
    internal static string? TenantFromChallenge(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return null;

        var match = AuthorizationUriRegex().Match(parameter);
        if (!match.Success || !Uri.TryCreate(match.Groups["uri"].Value, UriKind.Absolute, out var authorizationUri)) return null;

        var tenant = authorizationUri.Segments
            .Select(s => s.Trim('/'))
            .FirstOrDefault(s => s.Length > 0);

        return string.IsNullOrWhiteSpace(tenant) ||
               tenant.Equals("common", StringComparison.OrdinalIgnoreCase) ||
               tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase)
            ? null
            : tenant;
    }
}
