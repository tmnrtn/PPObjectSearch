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

    private IPublicClientApplication GetApp(string? tenantId)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "organizations" : tenantId.Trim();

        return _apps.GetOrAdd(tenant, t =>
        {
            var app = PublicClientApplicationBuilder
                .Create(_clientId)
                .WithAuthority($"https://login.microsoftonline.com/{t}", validateAuthority: false)
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
        CancellationToken ct = default)
    {
        var app = GetApp(tenantId);
        var scopes = new[] { $"{resource.TrimEnd('/')}/.default" };
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);

        IAccount? account = null;
        if (!forceAccountPicker)
        {
            account = accounts.FirstOrDefault(a =>
                          preferredAccountId is not null &&
                          string.Equals(a.HomeAccountId?.Identifier, preferredAccountId, StringComparison.OrdinalIgnoreCase))
                      // No remembered account: reuse one already signed into this tenant.
                      ?? accounts.FirstOrDefault(a =>
                          tenantId is not null &&
                          string.Equals(a.HomeAccountId?.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                      // Single account overall - unambiguous, so no need to interrupt the user.
                      ?? (accounts.Count() == 1 ? accounts.First() : null);
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

            result = await builder.ExecuteAsync(ct).ConfigureAwait(false);
        }

        return new AccountToken(
            result.AccessToken,
            result.Account?.HomeAccountId?.Identifier ?? string.Empty,
            result.Account?.Username ?? "(unknown account)",
            result.Account?.HomeAccountId?.TenantId ?? tenantId);
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

    public EnvironmentAuthContext(AuthenticationService auth, string? tenantId = null, string? accountId = null)
    {
        _auth = auth;
        TenantId = tenantId;
        AccountId = accountId;
    }

    public string? TenantId { get; private set; }
    public string? AccountId { get; private set; }
    public string? AccountName { get; private set; }

    /// <summary>Set once by the caller before the first token request, to force the account chooser.</summary>
    public bool ForceAccountPicker { get; set; }

    public async Task EnsureTenantAsync(string environmentUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(TenantId)) return;
        TenantId = await TenantDiscovery.GetTenantIdAsync(environmentUrl, ct).ConfigureAwait(false);
    }

    public async Task<string> GetTokenAsync(string resource, CancellationToken ct = default)
    {
        var force = ForceAccountPicker;
        ForceAccountPicker = false;

        var token = await _auth.AcquireTokenAsync(resource, TenantId, AccountId, force, ct).ConfigureAwait(false);

        AccountId = string.IsNullOrEmpty(token.AccountId) ? AccountId : token.AccountId;
        AccountName = token.AccountName;
        TenantId ??= token.TenantId;

        return token.AccessToken;
    }
}
