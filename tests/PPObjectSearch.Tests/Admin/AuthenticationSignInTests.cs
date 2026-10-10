using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using PPObjectSearch.Auth;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Admin;

/// <summary>
/// The MSAL sign-in service after a sign-in has completed: which cached account a tab reuses,
/// silent tokens, guests, and signing out. The sign-in service's endpoints are faked, and the
/// "browser" only calls back to MSAL's own listener on this machine.
/// </summary>
[Collection(TokenCacheCollection.Name)]
public sealed class AuthenticationSignInTests : IDisposable
{
    private const string Resource = "https://contoso.crm11.dynamics.com/";
    private const string OtherResource = "https://graph.microsoft.com";
    private const string Home = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Guest = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string UserId = "cccccccc-0000-0000-0000-000000000003";

    private readonly FakeHttpHandler _identity = new();
    private int _browserOpens;

    public AuthenticationSignInTests()
    {
        TokenCacheHelper.Clear();

        // A cache file this machine cannot read: on Windows it is cleared and replaced by a real one;
        // elsewhere there is no DPAPI, and the unreadable file keeps MSAL's cache in memory only.
        File.WriteAllBytes(Path.Combine(TestDataDirectory.Path, "msal.cache"), [0x42, 0x41, 0x44]);
    }

    public void Dispose() => TokenCacheHelper.Clear();

    private sealed class Factory(HttpMessageHandler handler) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => new(handler, disposeHandler: false);
    }

    private AuthenticationService Service() => new(null, new Factory(_identity));

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The sign-in service's answer: tokens for <paramref name="tokenTenant"/> for an account whose home is <paramref name="homeTenant"/>.</summary>
    private static string Tokens(string homeTenant, string tokenTenant, string accessToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var idToken = Base64Url("""{"alg":"none","typ":"JWT"}""") + "." + Base64Url(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = AuthenticationService.DefaultClientId,
            ["iss"] = $"https://login.microsoftonline.com/{tokenTenant}/v2.0",
            ["iat"] = now, ["nbf"] = now, ["exp"] = now + 3600,
            ["name"] = "Ada Lovelace", ["oid"] = UserId, ["sub"] = "subject", ["tid"] = tokenTenant,
            ["preferred_username"] = "ada@contoso.com", ["ver"] = "2.0"
        })) + ".";

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["token_type"] = "Bearer",
            ["scope"] = "https://contoso.crm11.dynamics.com/.default",
            ["expires_in"] = 3600,
            ["ext_expires_in"] = 3600,
            ["access_token"] = accessToken,
            ["refresh_token"] = "refresh-" + accessToken,
            ["id_token"] = idToken,
            ["client_info"] = Base64Url($$"""{"uid":"{{UserId}}","utid":"{{homeTenant}}"}""")
        });
    }

    /// <summary>The token endpoint: codes are exchanged for tokens; refresh tokens are refused, as for another resource never consented to.</summary>
    private void Answer(string homeTenant, string tokenTenant, string accessToken = "access-1") =>
        _identity.On(HttpMethod.Post, "/oauth2/v2.0/token", r => r.Body!.Contains("grant_type=refresh_token")
            ? FakeHttpHandler.Json("""{"error":"invalid_grant","error_description":"AADSTS65001: consent required","error_codes":[65001],"suberror":"consent_required"}""", HttpStatusCode.BadRequest)
            : FakeHttpHandler.Json(Tokens(homeTenant, tokenTenant, accessToken)));

    /// <summary>
    /// Stands in for the user finishing the sign-in page: a code and the page's own state are posted
    /// back to the listener MSAL opened on this machine.
    /// </summary>
    private Task Browser(Uri page)
    {
        Interlocked.Increment(ref _browserOpens);
        var query = page.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
        var redirect = new Uri(query["redirect_uri"]);
        var form = new Dictionary<string, string> { ["code"] = "auth-code", ["state"] = query["state"] };

        _ = Task.Run(async () =>
        {
            using var http = new HttpClient();
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    // The sign-in page posts its answer back (response_mode=form_post).
                    using var content = new FormUrlEncodedContent(form);
                    using var response = await http.PostAsync(redirect, content);
                    return;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(50);
                }
            }
        });

        return Task.CompletedTask;
    }

    private static Task Refuse(Uri page) => throw new InvalidOperationException($"No sign-in page was expected, but {page} was opened.");

    [Fact]
    public async Task A_completed_sign_in_returns_the_token_and_the_account_and_the_next_request_is_silent()
    {
        Answer(Home, Home);
        var auth = Service();

        var first = await auth.AcquireTokenAsync(Resource, Home, null, openBrowser: Browser);
        var second = await auth.AcquireTokenAsync(Resource, Home, first.AccountId, openBrowser: Refuse);

        Assert.Equal(new AccountToken("access-1", $"{UserId}.{Home}", "ada@contoso.com", Home), first);
        Assert.Equal("access-1", second.AccessToken);
        Assert.Equal(1, _browserOpens);
        var exchange = Assert.Single(_identity.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains($"/{Home}/oauth2/v2.0/token", exchange.Url);
        Assert.Contains("grant_type=authorization_code", exchange.Body);
        Assert.Contains("code=auth-code", exchange.Body);
        Assert.Equal([new KnownAccount($"{UserId}.{Home}", "ada@contoso.com", Home)], await auth.GetKnownAccountsAsync());
    }

    [Fact]
    public async Task A_guest_signed_in_to_the_environments_tenant_is_reused_there_without_a_prompt()
    {
        Answer(Home, Guest);
        var auth = Service();

        var signedIn = await auth.AcquireTokenAsync(Resource, Guest, null, openBrowser: Browser);
        var reused = await auth.AcquireTokenAsync(Resource, Guest, null, openBrowser: Refuse);
        var silent = await auth.TryAcquireTokenSilentAsync(Resource, Guest, null);

        Assert.Equal(Guest, signedIn.TenantId);
        Assert.Equal($"{UserId}.{Home}", signedIn.AccountId);
        Assert.Equal("access-1", reused.AccessToken);
        Assert.Equal("access-1", silent!.AccessToken);
        Assert.Equal(Guest, silent.TenantId);
        Assert.Equal(1, _browserOpens);
    }

    [Fact]
    public async Task A_silent_token_for_a_resource_not_consented_to_is_none()
    {
        Answer(Home, Home);
        var auth = Service();
        var signedIn = await auth.AcquireTokenAsync(Resource, Home, null, openBrowser: Browser);

        var token = await auth.TryAcquireTokenSilentAsync(OtherResource, Home, signedIn.AccountId);

        Assert.Null(token);
        Assert.Equal(1, _browserOpens);
    }

    [Fact]
    public async Task A_resource_that_needs_consent_reopens_sign_in_for_the_remembered_account_without_the_picker()
    {
        Answer(Home, Home);
        var auth = Service();
        var signedIn = await auth.AcquireTokenAsync(Resource, Home, null, openBrowser: Browser);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Uri? page = null;

        await Assert.ThrowsAnyAsync<Exception>(() => auth.AcquireTokenAsync(OtherResource, Home, signedIn.AccountId, ct: cts.Token, openBrowser: uri =>
        {
            page = uri;
            cts.Cancel();
            return Task.CompletedTask;
        }));

        Assert.NotNull(page);
        Assert.Contains("login_hint=ada%40contoso.com", page.Query);
        Assert.DoesNotContain("prompt=select_account", page.Query);
    }

    [Fact]
    public async Task A_tab_remembers_the_account_it_signed_in_with_and_then_gets_tokens_silently()
    {
        Answer(Home, Home);
        var context = new EnvironmentAuthContext(Service(), Home) { OpenBrowser = Browser };

        var token = await context.GetTokenAsync(Resource);
        var silent = await context.TryGetTokenSilentAsync(Resource);

        Assert.Equal("access-1", token);
        Assert.Equal("access-1", silent);
        Assert.Equal($"{UserId}.{Home}", context.AccountId);
        Assert.Equal("ada@contoso.com", context.AccountName);
    }

    [Fact]
    public async Task Signing_out_everywhere_forgets_every_account()
    {
        Answer(Home, Home);
        var auth = Service();
        await auth.AcquireTokenAsync(Resource, Home, null, openBrowser: Browser);

        await auth.SignOutAllAsync();

        Assert.Empty(await auth.GetKnownAccountsAsync());
        Assert.Null(await auth.TryAcquireTokenSilentAsync(Resource, Home, null));
    }
}
