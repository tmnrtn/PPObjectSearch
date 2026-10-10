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
    /// <summary>The Power Platform API of the environment's cloud.</summary>
    private static string Bap(Auth.EnvironmentAuthContext auth, string environmentUrl) =>
        (Core.Clouds.ForEnvironment(environmentUrl) ?? auth.Cloud).BapApi;

    /// <summary>The environments the signed-in user can see. The admin-scoped list needs Power
    /// Platform administrator rights, so the per-user one is tried first.</summary>
    private static string[] Endpoints(string bap) =>
    [
        bap + "/providers/Microsoft.BusinessAppPlatform/environments?api-version=2020-10-01",
        bap + "/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01"
    ];

    public static async Task<EnvironmentTypeInfo> ProbeAsync(
        Auth.EnvironmentAuthContext auth,
        HttpClient http,
        string environmentUrl,
        string? environmentId,
        CancellationToken ct = default)
    {
        var token = await SilentTokenAsync(auth, environmentUrl, ct).ConfigureAwait(false);

        if (token is null)
        {
            return new EnvironmentTypeInfo(EnvironmentSku.Unknown, null,
                "No Power Platform API token for this account, so the environment type could not be read.");
        }

        var host = HostOf(environmentUrl);

        // What each endpoint said, so an Unknown can explain itself rather than just say "not found".
        var outcomes = new List<string>();

        foreach (var endpoint in Endpoints(Bap(auth, environmentUrl)))
        {
            try
            {
                var result = await SearchListAsync(http, token, endpoint, e => Matches(e, host, environmentId), ct)
                    .ConfigureAwait(false);

                if (result.Found is { } found) return found;
                outcomes.Add(result.Outcome);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Try the next endpoint; anything still unresolved stays Unknown.
                outcomes.Add($"{ScopeOf(endpoint)}: {ex.Message}");
            }
        }

        var looked = environmentId is null ? $"host {host}" : $"host {host} or id {environmentId}";
        return new EnvironmentTypeInfo(EnvironmentSku.Unknown, null,
            $"This environment was not found in the Power Platform API (looked for {looked}; " +
            $"{string.Join("; ", outcomes)}), so its type could not be read.");
    }

    private static async Task<string?> SilentTokenAsync(Auth.EnvironmentAuthContext auth, string environmentUrl, CancellationToken ct)
    {
        try
        {
            return await auth.TryGetTokenSilentAsync(Bap(auth, environmentUrl), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>The URL's host; null when it does not parse, and matching falls back to the environment id.</summary>
    private static string? HostOf(string environmentUrl)
    {
        try
        {
            return new Uri(environmentUrl).Host;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string ScopeOf(string endpoint) =>
        endpoint.Contains("/scopes/admin/", StringComparison.Ordinal) ? "admin list" : "user list";

    /// <summary>What one list endpoint said: the environment's type if it was listed, otherwise why not.</summary>
    private sealed record ListResult(EnvironmentTypeInfo? Found, string Outcome);

    private static async Task<ListResult> SearchListAsync(
        HttpClient http, string token, string endpoint, Func<JsonElement, bool> isTarget, CancellationToken ct)
    {
        var scope = ScopeOf(endpoint);
        var listed = 0;

        // The list is paged: an environment past the first page is only reachable through
        // nextLink, and missing it reads exactly like the environment not existing.
        for (string? url = endpoint; url is not null;)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ListResult(null, $"{scope}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                return new ListResult(null, $"{scope}: no environment list in the response");
            }

            foreach (var environment in value.EnumerateArray())
            {
                listed++;
                if (isTarget(environment)) return new ListResult(TypeOf(environment), string.Empty);
            }

            url = JsonHelper.GetString(doc.RootElement, "nextLink");
        }

        return new ListResult(null, $"{scope}: {listed} environment(s), none matching");
    }

    private static EnvironmentTypeInfo TypeOf(JsonElement environment)
    {
        var properties = environment.ValueKind == JsonValueKind.Object &&
                         environment.TryGetProperty("properties", out var p)
            ? p
            : default;

        var rawSku = JsonHelper.GetString(properties, "environmentSku");
        var sku = Parse(rawSku);

        return new EnvironmentTypeInfo(
            sku,
            JsonHelper.GetString(properties, "displayName"),
            sku != EnvironmentSku.Unknown ? null : UnknownSkuNote(rawSku));
    }

    /// <summary>
    /// The URL is what the user connected to, so it decides. The environment id is only a
    /// fallback for when either side carries no URL to compare: an id that names an environment
    /// at a different host (a stale or mistyped settings entry, or a bad discovery answer) must not
    /// lend that environment's type - a sandbox's - to the one actually being written to.
    /// </summary>
    internal static bool Matches(JsonElement environment, string? host, string? environmentId)
    {
        var hosts = InstanceHosts(environment);

        if (host is not null && hosts.Count > 0)
        {
            return hosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        }

        // "name" is the environment id, not a label.
        return !string.IsNullOrWhiteSpace(environmentId) &&
               string.Equals(JsonHelper.GetString(environment, "name"), environmentId, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> InstanceHosts(JsonElement environment)
    {
        var hosts = new List<string>();

        if (environment.ValueKind != JsonValueKind.Object) return hosts;
        if (!environment.TryGetProperty("properties", out var properties)) return hosts;
        if (properties.ValueKind != JsonValueKind.Object) return hosts;
        if (!properties.TryGetProperty("linkedEnvironmentMetadata", out var linked)) return hosts;
        if (linked.ValueKind != JsonValueKind.Object) return hosts;

        foreach (var name in new[] { "instanceApiUrl", "instanceUrl" })
        {
            if (Uri.TryCreate(JsonHelper.GetString(linked, name), UriKind.Absolute, out var parsed))
            {
                hosts.Add(parsed.Host);
            }
        }

        return hosts;
    }

    /// <summary>Why the type is Unknown: the API gave none, or one this app does not know.</summary>
    private static string UnknownSkuNote(string? rawSku) => string.IsNullOrWhiteSpace(rawSku)
        ? "The Power Platform API returned no environment type for this environment."
        : $"The Power Platform API reported an environment type this app does not know: '{rawSku}'.";

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
