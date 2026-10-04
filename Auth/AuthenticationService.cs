using System.Collections.Concurrent;
using Microsoft.Identity.Client;

namespace PPObjectSearch.Auth;

public sealed record AccountToken(string AccessToken, string AccountId, string AccountName, string? TenantId);

public sealed record KnownAccount(string AccountId, string Username, string? TenantId)
{
    public override string ToString() => Username;
}

/// <summary>
/// Interactive OAuth 2.0 (authorization code + PKCE) against Entra ID via MSAL.
///
/// One <see cref="IPublicClientApplication"/> is kept per authority so environments in different
/// tenants can be signed in side by side; they all share a single encrypted token cache, so an
/// account signed in for one tab is reused silently by any other tab in the same tenant.
///
/// The default client id is Microsoft's pre-consented public client used by the Dataverse
/// developer tooling (PAC CLI, XrmToolBox), so no app registration is required.
/// </summary>
public sealed class AuthenticationService
{
    public const string DefaultClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    private readonly string _clientId;
    private readonly ConcurrentDictionary<string, IPublicClientApplication> _apps = new(StringComparer.OrdinalIgnoreCase);

    public AuthenticationService(string? clientId = null)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();
    }

    private IPublicClientApplication GetApp(string? tenantId, string? authority = null)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "organizations" : tenantId.Trim();

        // The sign-in host follows the environment's cloud: login.microsoftonline.us for the US
        // government clouds, login.chinacloudapi.cn for China.
        var host = (authority ?? Core.Clouds.Public.Authority).TrimEnd('/');

        return _apps.GetOrAdd($"{host}/{tenant}", key =>
        {
            var app = PublicClientApplicationBuilder
                .Create(_clientId)
                .WithAuthority(key, validateAuthority: false)
                // Loopback redirect: MSAL runs a temporary listener and uses the system browser,
                // so existing SSO / MFA sessions are reused.
                .WithRedirectUri("http://localhost")
                .Build();

            TokenCacheHelper.Bind(app.UserTokenCache);
            return app;
        });
    }

    /// <summary>
    /// Acquires a token for <paramref name="resource"/> (e.g. https://contoso.crm11.dynamics.com).
    /// Silent when a suitable cached account exists, otherwise the system browser is opened.
    /// </summary>
    /// <param name="preferredAccountId">MSAL home account id remembered for this tab, if any.</param>
    /// <param name="forceAccountPicker">Force the account chooser, for "switch account".</param>
    public async Task<AccountToken> AcquireTokenAsync(
        string resource,
        string? tenantId,
        string? preferredAccountId,
        bool forceAccountPicker = false,
        CancellationToken ct = default,
        Func<Uri, Task>? openBrowser = null,
        string? authority = null)
    {
        var app = GetApp(tenantId, authority);
        var scopes = new[] { $"{resource.TrimEnd('/')}/.default" };
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);

        IAccount? account = null;
        if (!forceAccountPicker)
        {
            account = accounts.FirstOrDefault(a =>
                          preferredAccountId is not null &&
                          string.Equals(a.HomeAccountId?.Identifier, preferredAccountId, StringComparison.OrdinalIgnoreCase))
                      // No remembered account: reuse one already signed into this tenant.
                      ?? accounts.FirstOrDefault(a => tenantId is not null && SignedInto(a, tenantId));
        }

        AuthenticationResult result;
        try
        {
            if (forceAccountPicker || account is null)
            {
                throw new MsalUiRequiredException("account_selection_required", "Interactive sign-in required.");
            }

            result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            var builder = app.AcquireTokenInteractive(scopes)
                .WithPrompt(forceAccountPicker || account is null ? Prompt.SelectAccount : Prompt.NoPrompt);

            if (!forceAccountPicker && account is not null) builder = builder.WithAccount(account);

            // The sign-in page opens where the environment's links do - the profile already
            // signed in to that tenant - rather than whichever browser is the default.
            if (openBrowser is not null)
            {
                builder = builder.WithSystemWebViewOptions(new SystemWebViewOptions { OpenBrowserAsync = openBrowser });
            }

            result = await builder.ExecuteAsync(ct).ConfigureAwait(false);
        }

        return Token(result, tenantId);
    }

    /// <summary>
    /// The tenant that issued the token, not the account's home tenant: for a guest the two
    /// differ, and every later token on the tab - Graph, the Power Platform API - has to come from
    /// the tenant the environment is in.
    /// </summary>
    private static AccountToken Token(AuthenticationResult result, string? tenantId) => new(
        result.AccessToken,
        result.Account?.HomeAccountId?.Identifier ?? string.Empty,
        result.Account?.Username ?? "(unknown account)",
        string.IsNullOrWhiteSpace(result.TenantId) ? tenantId : result.TenantId);

    /// <summary>
    /// Whether an account has signed in to a tenant - its home tenant, or one it is a guest in.
    /// Matching on the home tenant alone never found a guest account, so every new tab in a
    /// tenant where the user is a guest prompted again.
    /// </summary>
    private static bool SignedInto(IAccount account, string tenantId) =>
        string.Equals(account.HomeAccountId?.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) ||
        (account.GetTenantProfiles()?.Any(p => string.Equals(p.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>
    /// A token for another resource on an account already signed in, or null if getting one would
    /// mean prompting. Used for side questions - what type of environment is this - where opening
    /// a browser window the user did not ask for would be worse than not knowing the answer.
    /// </summary>
    public async Task<AccountToken?> TryAcquireTokenSilentAsync(
        string resource,
        string? tenantId,
        string? preferredAccountId,
        CancellationToken ct = default,
        string? authority = null)
    {
        try
        {
            var app = GetApp(tenantId, authority);
            var accounts = await app.GetAccountsAsync().ConfigureAwait(false);

            var account = accounts.FirstOrDefault(a =>
                              preferredAccountId is not null &&
                              string.Equals(a.HomeAccountId?.Identifier, preferredAccountId, StringComparison.OrdinalIgnoreCase))
                          ?? accounts.FirstOrDefault(a => tenantId is not null && SignedInto(a, tenantId));

            if (account is null) return null;

            var result = await app
                .AcquireTokenSilent(new[] { $"{resource.TrimEnd('/')}/.default" }, account)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);

            return Token(result, tenantId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Services.Log.Info($"No silent token for {resource}: {ex.Message}");
            // No consent for this resource, or nothing cached - the caller treats that as unknown.
            return null;
        }
    }

    public async Task<IReadOnlyList<KnownAccount>> GetKnownAccountsAsync()
    {
        var known = new Dictionary<string, KnownAccount>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in _apps.Values)
        {
            foreach (var account in await app.GetAccountsAsync().ConfigureAwait(false))
            {
                var id = account.HomeAccountId?.Identifier;
                if (id is null || known.ContainsKey(id)) continue;
                known[id] = new KnownAccount(id, account.Username, account.HomeAccountId?.TenantId);
            }
        }

        return known.Values.OrderBy(a => a.Username, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task SignOutAllAsync()
    {
        foreach (var app in _apps.Values)
        {
            foreach (var account in await app.GetAccountsAsync().ConfigureAwait(false))
            {
                await app.RemoveAsync(account).ConfigureAwait(false);
            }
        }

        _apps.Clear();
        TokenCacheHelper.Clear();
    }
}

/// <summary>
/// Per-environment view of <see cref="AuthenticationService"/>: remembers which tenant the
/// environment lives in and which account this tab signed in with, so each tab can hold a
/// different identity.
/// </summary>
public sealed class EnvironmentAuthContext
{
    private readonly AuthenticationService _auth;
    private readonly Func<string, CancellationToken, Task<string?>>? _tokenSource;

    public EnvironmentAuthContext(AuthenticationService auth, string? tenantId = null, string? accountId = null)
    {
        _auth = auth;
        TenantId = tenantId;
        AccountId = accountId;
    }

    /// <summary>
    /// For tests: tokens come from <paramref name="tokenSource"/>, keyed by resource, instead of
    /// MSAL. A null token means none is to be had - silently it reads as "no token", and an
    /// interactive request fails.
    /// </summary>
    internal EnvironmentAuthContext(Func<string, CancellationToken, Task<string?>> tokenSource)
    {
        _auth = new AuthenticationService();
        _tokenSource = tokenSource;
    }

    /// <summary>
    /// Tokens from somewhere other than an interactive sign-in - a service principal, for the
    /// command line. Keyed by resource; null means none is to be had.
    /// </summary>
    public static EnvironmentAuthContext FromTokenSource(Func<string, CancellationToken, Task<string?>> tokenSource) =>
        new(tokenSource);

    public string? TenantId { get; private set; }
    public string? AccountId { get; private set; }

    /// <summary>The cloud the environment is in, and so where sign-in and every other service live.</summary>
    public Core.Cloud Cloud { get; private set; } = Core.Clouds.Public;

    /// <summary>Sets the cloud outright - for a service principal, which has no sign-in challenge to read.</summary>
    public void UseCloud(Core.Cloud cloud) => Cloud = cloud;
    public string? AccountName { get; private set; }

    /// <summary>Set once by the caller before the first token request, to force the account chooser.</summary>
    public bool ForceAccountPicker { get; set; }

    /// <summary>Opens the sign-in page, when a sign-in is needed. Null uses the default browser.</summary>
    public Func<Uri, Task>? OpenBrowser { get; set; }

    public async Task EnsureTenantAsync(string environmentUrl, CancellationToken ct)
    {
        // The host says which cloud first; a host none of the clouds claims - a custom domain -
        // is settled by the authority Dataverse's own challenge names.
        var byHost = Core.Clouds.ForEnvironment(environmentUrl);
        if (byHost is not null) Cloud = byHost;

        if (!string.IsNullOrWhiteSpace(TenantId) && byHost is not null) return;

        var (tenant, authorityHost) = await TenantDiscovery.DiscoverAsync(environmentUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(TenantId)) TenantId = tenant;
        if (byHost is null && Core.Clouds.ForAuthorityHost(authorityHost) is { } byAuthority) Cloud = byAuthority;
    }

    /// <summary>A token for another resource without ever prompting - null when none is to be had.</summary>
    public async Task<string?> TryGetTokenSilentAsync(string resource, CancellationToken ct = default)
    {
        if (_tokenSource is not null) return await _tokenSource(resource, ct).ConfigureAwait(false);

        var token = await _auth.TryAcquireTokenSilentAsync(resource, TenantId, AccountId, ct, Cloud.Authority).ConfigureAwait(false);
        return token?.AccessToken;
    }

    public async Task<string> GetTokenAsync(string resource, CancellationToken ct = default)
    {
        if (_tokenSource is not null)
        {
            return await _tokenSource(resource, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"No token for {resource}.");
        }

        var force = ForceAccountPicker;
        ForceAccountPicker = false;

        var token = await _auth.AcquireTokenAsync(resource, TenantId, AccountId, force, ct, OpenBrowser, Cloud.Authority).ConfigureAwait(false);

        AccountId = string.IsNullOrEmpty(token.AccountId) ? AccountId : token.AccountId;
        AccountName = token.AccountName;

        // A tenant is only adopted from a token asked of that tenant. Without one the request went
        // to "organizations", which answers from the account's home tenant - for a guest, the wrong
        // directory for every Graph and Power Platform request that would follow.

        return token.AccessToken;
    }
}
