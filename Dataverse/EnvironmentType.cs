using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PPObjectSearch.Dataverse;

/// <summary>
/// The Power Platform environment SKU, as the admin centre names it.
/// <see cref="Unknown"/> is not a failure to be smoothed over - it is the answer whenever the
/// question could not be asked, and it is treated exactly like Production everywhere it matters.
/// </summary>
public enum EnvironmentSku
{
    Unknown,
    Production,

    /// <summary>The tenant's default environment. Not labelled Production, but everyone in the
    /// tenant has access to it, so it is guarded like one.</summary>
    Default,

    Sandbox,
    Trial,
    Developer,
    Teams
}

public sealed record EnvironmentTypeInfo(EnvironmentSku Sku, string? DisplayName, string? Detail)
{
    /// <summary>
    /// Whether writing here needs the environment to be named in the settings allowlist. An
    /// environment whose type could not be read counts as protected: not knowing is not the same
    /// as knowing it is safe.
    /// </summary>
    public bool IsProtected => Sku is EnvironmentSku.Production or EnvironmentSku.Default or EnvironmentSku.Unknown;

    public string SkuLabel => Sku switch
    {
        EnvironmentSku.Unknown => "Type unknown",
        EnvironmentSku.Default => "Default environment",
        _ => Sku.ToString()
    };
}

/// <summary>
/// Asks the Power Platform API what type of environment a Dataverse URL belongs to.
///
/// Dataverse itself does not carry its own SKU - the organization table has no such column - so
/// the only answer comes from the Business Application Platform API, on a different audience. The
/// token for it is taken silently or not at all: a background question about environment type is
/// no reason to throw a sign-in window at someone. Every failure resolves to
/// <see cref="EnvironmentSku.Unknown"/>, which the write guard refuses by default.
/// </summary>
public static class EnvironmentTypeProbe
{
    private const string BapResource = "https://api.bap.microsoft.com";

    /// <summary>The environments the signed-in user can see. The admin-scoped list needs Power
    /// Platform administrator rights, so the per-user one is tried first.</summary>
    private static readonly string[] Endpoints =
    {
        BapResource + "/providers/Microsoft.BusinessAppPlatform/environments?api-version=2020-10-01",
        BapResource + "/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01"
    };

    public static async Task<EnvironmentTypeInfo> ProbeAsync(
        Auth.EnvironmentAuthContext auth,
        HttpClient http,
        string environmentUrl,
        string? environmentId,
        CancellationToken ct = default)
    {
        string? token;

        try
        {
            token = await auth.TryGetTokenSilentAsync(BapResource, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            token = null;
        }

        if (token is null)
        {
            return new EnvironmentTypeInfo(EnvironmentSku.Unknown, null,
                "No Power Platform API token for this account, so the environment type could not be read.");
        }

        string? host = null;
        try
        {
            host = new Uri(environmentUrl).Host;
        }
        catch (UriFormatException)
        {
            // Matching falls back to the environment id below.
        }

        // What each endpoint said, so an Unknown can explain itself rather than just say "not found".
        var outcomes = new List<string>();

        foreach (var endpoint in Endpoints)
        {
            var scope = endpoint.Contains("/scopes/admin/", StringComparison.Ordinal) ? "admin list" : "user list";
            var listed = 0;

            try
            {
                // The list is paged: an environment past the first page is only reachable through
                // nextLink, and missing it reads exactly like the environment not existing.
                for (string? url = endpoint; url is not null;)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        outcomes.Add($"{scope}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                        break;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                    if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                    {
                        outcomes.Add($"{scope}: no environment list in the response");
                        break;
                    }

                    foreach (var environment in value.EnumerateArray())
                    {
                        listed++;
                        if (!Matches(environment, host, environmentId)) continue;

                        var properties = environment.ValueKind == JsonValueKind.Object &&
                                         environment.TryGetProperty("properties", out var p)
                            ? p
                            : default;

                        var rawSku = JsonHelper.GetString(properties, "environmentSku");
                        var sku = Parse(rawSku);

                        return new EnvironmentTypeInfo(
                            sku,
                            JsonHelper.GetString(properties, "displayName"),
                            sku != EnvironmentSku.Unknown ? null
                            : string.IsNullOrWhiteSpace(rawSku)
                                ? "The Power Platform API returned no environment type for this environment."
                                : $"The Power Platform API reported an environment type this app does not know: '{rawSku}'.");
                    }

                    url = JsonHelper.GetString(doc.RootElement, "nextLink");
                }

                if (outcomes.Count == 0 || !outcomes[^1].StartsWith(scope, StringComparison.Ordinal))
                {
                    outcomes.Add($"{scope}: {listed} environment(s), none matching");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Try the next endpoint; anything still unresolved stays Unknown.
                outcomes.Add($"{scope}: {ex.Message}");
            }
        }

        var looked = environmentId is null ? $"host {host}" : $"host {host} or id {environmentId}";
        return new EnvironmentTypeInfo(EnvironmentSku.Unknown, null,
            $"This environment was not found in the Power Platform API (looked for {looked}; " +
            $"{string.Join("; ", outcomes)}), so its type could not be read.");
    }

    private static bool Matches(JsonElement environment, string? host, string? environmentId)
    {
        // The environment id is exact where it is known; "name" is the id, not a label.
        if (!string.IsNullOrWhiteSpace(environmentId) &&
            string.Equals(JsonHelper.GetString(environment, "name"), environmentId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (host is null || environment.ValueKind != JsonValueKind.Object) return false;
        if (!environment.TryGetProperty("properties", out var properties)) return false;
        if (properties.ValueKind != JsonValueKind.Object) return false;
        if (!properties.TryGetProperty("linkedEnvironmentMetadata", out var linked)) return false;
        if (linked.ValueKind != JsonValueKind.Object) return false;

        foreach (var name in new[] { "instanceApiUrl", "instanceUrl" })
        {
            var url = JsonHelper.GetString(linked, name);
            if (string.IsNullOrWhiteSpace(url)) continue;

            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                string.Equals(parsed.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Anything unrecognised stays Unknown rather than being guessed at, so a SKU this app has
    /// never heard of is guarded rather than waved through.
    /// </summary>
    private static EnvironmentSku Parse(string? sku) => sku?.Trim().ToLowerInvariant() switch
    {
        "production" => EnvironmentSku.Production,
        "sandbox" => EnvironmentSku.Sandbox,
        "trial" or "subscriptionbasedtrial" => EnvironmentSku.Trial,
        "developer" or "dev" => EnvironmentSku.Developer,
        "teams" => EnvironmentSku.Teams,
        "default" => EnvironmentSku.Default,
        _ => EnvironmentSku.Unknown
    };
}
