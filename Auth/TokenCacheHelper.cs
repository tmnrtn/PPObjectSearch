using System.IO;
using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace PPObjectSearch.Auth;

/// <summary>
/// Persists the MSAL token cache to %LOCALAPPDATA%\PPObjectSearch, protected with DPAPI
/// (current user scope) so the app can re-connect silently without a browser prompt.
/// </summary>
internal static class TokenCacheHelper
{
    private static readonly string CacheFile =
        Path.Combine(AppPaths.DataDirectory, "msal.cache");

    private static readonly object Sync = new();

    public static void Bind(ITokenCache cache)
    {
        cache.SetBeforeAccess(OnBeforeAccess);
        cache.SetAfterAccess(OnAfterAccess);
    }

    private static void OnBeforeAccess(TokenCacheNotificationArgs args)
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(CacheFile)) return;
                var protectedBytes = File.ReadAllBytes(CacheFile);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                args.TokenCache.DeserializeMsalV3(bytes);
            }
            catch
            {
                // A corrupt or unreadable cache must never block sign-in - just start clean.
                TryDelete();
            }
        }
    }

    private static void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged) return;

        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var bytes = args.TokenCache.SerializeMsalV3();
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(CacheFile, protectedBytes);
            }
            catch
            {
                // Persisting the cache is a convenience; failing to do so is not fatal.
            }
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            TryDelete();
        }
    }

    private static void TryDelete()
    {
        try
        {
            if (File.Exists(CacheFile)) File.Delete(CacheFile);
        }
        catch
        {
            // ignored
        }
    }
}

internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PPObjectSearch");
}
