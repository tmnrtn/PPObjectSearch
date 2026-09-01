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
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    [GeneratedRegex("authorization_uri\\s*=\\s*\"?(?<uri>[^\",\\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationUriRegex();

    public static async Task<string?> GetTenantIdAsync(string environmentUrl, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, environmentUrl.TrimEnd('/') + "/api/data/v9.2/WhoAmI");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Unauthorized) return null;

            foreach (var header in response.Headers.WwwAuthenticate)
            {
                var raw = header.Parameter;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var match = AuthorizationUriRegex().Match(raw);
                if (!match.Success) continue;

                if (!Uri.TryCreate(match.Groups["uri"].Value, UriKind.Absolute, out var authorizationUri)) continue;

                var tenant = authorizationUri.Segments
                    .Select(s => s.Trim('/'))
                    .FirstOrDefault(s => s.Length > 0);

                if (!string.IsNullOrWhiteSpace(tenant) &&
                    !tenant.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                    !tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase))
                {
                    return tenant;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Discovery is best effort; the caller falls back to the "organizations" authority.
        }

        return null;
    }
}
