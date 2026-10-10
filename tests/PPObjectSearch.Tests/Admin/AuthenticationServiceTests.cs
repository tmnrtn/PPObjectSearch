using System.IO;
using PPObjectSearch.Auth;
using PPObjectSearch.Core;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Admin;

/// <summary>
/// The MSAL sign-in service and the per-tab auth context, as far as they go without a real
/// sign-in: account lookups against an empty cache, and the sign-in page the browser would open.
/// </summary>
[Collection(TokenCacheCollection.Name)]
public sealed class AuthenticationServiceTests : IDisposable
{
    private const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string Resource = "https://contoso.crm11.dynamics.com/";

    private static string CacheFile => Path.Combine(TestDataDirectory.Path, "msal.cache");

    public AuthenticationServiceTests() => TokenCacheHelper.Clear();

    public void Dispose() => TokenCacheHelper.Clear();

    /// <summary>
    /// Starts an interactive sign-in and stops it at the point the browser would open, returning
    /// the page it would have shown. Nothing is opened and no request leaves the machine.
    /// </summary>
    private static async Task<Uri> SignInPageAsync(Func<Func<Uri, Task>, CancellationToken, Task> signIn)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Uri? opened = null;

        await Assert.ThrowsAnyAsync<Exception>(() => signIn(uri =>
        {
            opened = uri;
            cts.Cancel();
            return Task.CompletedTask;
        }, cts.Token));

        return opened ?? throw new InvalidOperationException("The sign-in page was never opened.");
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : string.Empty), StringComparer.Ordinal);

    [Fact]
    public async Task A_new_service_knows_no_accounts()
    {
        var auth = new AuthenticationService();

        Assert.Empty(await auth.GetKnownAccountsAsync());
    }

    [Theory]
    [InlineData(Tenant, "https://login.microsoftonline.com")]
    [InlineData(null, "https://login.microsoftonline.us")]
    public async Task A_silent_token_with_nothing_cached_is_none_rather_than_a_prompt(string? tenant, string authority)
    {
        var auth = new AuthenticationService("  " + AuthenticationService.DefaultClientId + "  ");

        var token = await auth.TryAcquireTokenSilentAsync(Resource, tenant, preferredAccountId: "someone", authority: authority);

        Assert.Null(token);
        Assert.Empty(await auth.GetKnownAccountsAsync());
    }

    [Fact]
    public async Task Signing_out_everywhere_removes_the_token_cache_file()
    {
        var auth = new AuthenticationService();
        await auth.TryAcquireTokenSilentAsync(Resource, Tenant, null);
        await File.WriteAllBytesAsync(CacheFile, [1, 2, 3]);

        await auth.SignOutAllAsync();

        Assert.False(File.Exists(CacheFile));
        Assert.Empty(await auth.GetKnownAccountsAsync());
    }

    [Fact]
    public async Task A_first_sign_in_opens_the_account_picker_at_the_environments_tenant_for_its_resource()
    {
        var auth = new AuthenticationService("11111111-2222-3333-4444-555555555555");

        var page = await SignInPageAsync((open, ct) =>
            auth.AcquireTokenAsync(Resource, Tenant, preferredAccountId: null, ct: ct, openBrowser: open, authority: Clouds.UsGccHigh.Authority));

        var query = Query(page);
        Assert.Equal("login.microsoftonline.us", page.Host);
        Assert.StartsWith($"/{Tenant}/", page.AbsolutePath);
        Assert.Equal("11111111-2222-3333-4444-555555555555", query["client_id"]);
        Assert.Contains("https://contoso.crm11.dynamics.com/.default", query["scope"]);
        Assert.Equal("select_account", query["prompt"]);
        Assert.StartsWith("http://localhost", query["redirect_uri"]);
    }

    [Fact]
    public async Task Switching_account_always_shows_the_picker_and_without_a_tenant_signs_in_to_organizations()
    {
        var auth = new AuthenticationService();

        var page = await SignInPageAsync((open, ct) =>
            auth.AcquireTokenAsync(Resource, null, "remembered-account", forceAccountPicker: true, openBrowser: open, ct: ct));

        Assert.Equal("login.microsoftonline.com", page.Host);
        Assert.StartsWith("/organizations/", page.AbsolutePath);
        Assert.Equal(AuthenticationService.DefaultClientId, Query(page)["client_id"]);
        Assert.Equal("select_account", Query(page)["prompt"]);
    }

    [Fact]
    public async Task A_tab_asks_for_its_token_at_its_clouds_sign_in_and_the_picker_is_forced_only_once()
    {
        var context = new EnvironmentAuthContext(new AuthenticationService(), Tenant, accountId: null)
        {
            ForceAccountPicker = true
        };
        context.UseCloud(Clouds.China);

        var page = await SignInPageAsync((open, ct) =>
        {
            context.OpenBrowser = open;
            return context.GetTokenAsync(Resource, ct);
        });

        // MSAL sends China sign-ins to that cloud's preferred alias, login.partner.microsoftonline.cn.
        Assert.EndsWith(".cn", page.Host, StringComparison.Ordinal);
        Assert.False(context.ForceAccountPicker);
        Assert.Null(context.AccountName);
    }

    [Fact]
    public async Task A_tab_with_nothing_cached_has_no_silent_token()
    {
        var context = new EnvironmentAuthContext(new AuthenticationService(), Tenant);

        Assert.Null(await context.TryGetTokenSilentAsync(Resource));
        Assert.Equal(Tenant, context.TenantId);
        Assert.Null(context.AccountId);
        Assert.Same(Clouds.Public, context.Cloud);
    }

    [Theory]
    [InlineData("https://contoso.crm.microsoftdynamics.us", "US Government (GCC High)")]
    [InlineData("https://contoso.crm9.dynamics.com", "US Government (GCC)")]
    [InlineData("https://contoso.crm.dynamics.cn", "China (21Vianet)")]
    public async Task A_tab_whose_tenant_is_known_takes_its_cloud_from_the_environments_host(string url, string cloud)
    {
        var context = new EnvironmentAuthContext(new AuthenticationService(), Tenant);

        await context.EnsureTenantAsync(url, CancellationToken.None);

        Assert.Equal(cloud, context.Cloud.Name);
        Assert.Equal(Tenant, context.TenantId);
    }

    [Fact]
    public async Task Tokens_from_a_token_source_never_reach_msal()
    {
        var context = EnvironmentAuthContext.FromTokenSource((resource, _) =>
            Task.FromResult(resource.Contains("graph", StringComparison.Ordinal) ? null : "token:" + resource));
        context.UseCloud(Clouds.UsDod);

        Assert.Equal("token:" + Resource, await context.GetTokenAsync(Resource));
        Assert.Equal("token:" + Resource, await context.TryGetTokenSilentAsync(Resource));
        Assert.Null(await context.TryGetTokenSilentAsync("https://graph.microsoft.com"));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => context.GetTokenAsync("https://graph.microsoft.com"));
        Assert.Equal("No token for https://graph.microsoft.com.", missing.Message);
        Assert.Same(Clouds.UsDod, context.Cloud);
    }

    [Fact]
    public void A_known_account_reads_as_its_user_name()
    {
        Assert.Equal("ada@contoso.com", new KnownAccount("id", "ada@contoso.com", Tenant).ToString());
    }
}
