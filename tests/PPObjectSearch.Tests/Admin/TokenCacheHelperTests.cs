using System.IO;
using Microsoft.Identity.Client;
using PPObjectSearch.Auth;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Admin;

/// <summary>Tests that share the one token cache file, so they never run at the same time.</summary>
[CollectionDefinition(Name)]
public sealed class TokenCacheCollection
{
    public const string Name = "Token cache";

    private TokenCacheCollection()
    {
    }
}

/// <summary>The token cache's file handling: an unreadable file never blocks sign-in, and clearing removes it.</summary>
[Collection(TokenCacheCollection.Name)]
public sealed class TokenCacheHelperTests : IDisposable
{
    private static string CacheFile => Path.Combine(TestDataDirectory.Path, "msal.cache");

    public TokenCacheHelperTests() => TokenCacheHelper.Clear();

    public void Dispose() => TokenCacheHelper.Clear();

    private static IPublicClientApplication BoundApp()
    {
        var app = PublicClientApplicationBuilder.Create(AuthenticationService.DefaultClientId)
            .WithAuthority("https://login.microsoftonline.com/organizations", validateAuthority: false)
            .WithRedirectUri("http://localhost")
            .Build();
        TokenCacheHelper.Bind(app.UserTokenCache);
        return app;
    }

    [Fact]
    public async Task The_cache_lives_in_the_apps_data_directory_and_starts_empty()
    {
        var accounts = await BoundApp().GetAccountsAsync();

        Assert.Empty(accounts);
        Assert.False(File.Exists(CacheFile));
        Assert.False(File.Exists(CacheFile + ".lock"));
    }

    [Fact]
    public async Task An_unreadable_cache_file_reads_as_no_accounts_and_leaves_the_cache_usable()
    {
        await File.WriteAllBytesAsync(CacheFile, [0x42, 0x41, 0x44]);
        var app = BoundApp();

        Assert.Empty(await app.GetAccountsAsync());
        // The lock taken for the first read was released, or this would wait for ever.
        Assert.Empty(await app.GetAccountsAsync());
        Assert.False(File.Exists(CacheFile + ".lock"));
    }

    [Fact]
    public async Task A_cache_held_by_another_instance_is_read_once_that_instance_lets_go()
    {
        var app = BoundApp();
        var held = new FileStream(CacheFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var reading = Task.Run(() => app.GetAccountsAsync());
        await Task.Delay(150);
        await held.DisposeAsync();
        var accounts = await reading.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(accounts);
        Assert.False(File.Exists(CacheFile + ".lock"));
    }

    [Fact]
    public async Task Clearing_removes_the_cache_file_and_clearing_again_is_harmless()
    {
        await File.WriteAllBytesAsync(CacheFile, [1, 2, 3]);

        TokenCacheHelper.Clear();
        TokenCacheHelper.Clear();

        Assert.False(File.Exists(CacheFile));
    }
}
