using System.IO;
using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace PPObjectSearch.Auth;

/// <summary>
/// Persists the MSAL token cache to %LOCALAPPDATA%\PPObjectSearch, protected with DPAPI
/// (current user scope) so the app can re-connect silently without a browser prompt.
///
/// Every tenant has its own MSAL application and all of them share this one file, so a cache read
/// and the write that follows it are one locked unit: within the process by a semaphore (MSAL may
/// call back on different threads), and across processes by a lock file. Without that, two tabs
/// restoring at once could each read, then each write - the second overwriting the first's new
/// refresh token, and signing that tab out at random on the next start.
/// </summary>
internal static class TokenCacheHelper
{
    private static readonly string CacheFile =
        Path.Combine(AppPaths.DataDirectory, "msal.cache");

    private static readonly string LockFile = CacheFile + ".lock";

    /// <summary>Held from MSAL's before-access to its after-access notification.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>How long to wait for another instance of the app to finish with the cache.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private static FileStream? _crossProcessLock;

    /// <summary>The read under the current lock failed for a reason that may pass (the file was
    /// busy), so what MSAL holds is not the whole cache and must not be written over the file.</summary>
    private static bool _readIncomplete;

    public static void Bind(ITokenCache cache)
    {
        cache.SetBeforeAccess(OnBeforeAccess);
        cache.SetAfterAccess(OnAfterAccess);
    }

    private static void OnBeforeAccess(TokenCacheNotificationArgs args)
    {
        Gate.Wait();

        _readIncomplete = false;
        _crossProcessLock = AcquireFileLock();

        try
        {
            if (!File.Exists(CacheFile)) return;

            var protectedBytes = File.ReadAllBytes(CacheFile);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            args.TokenCache.DeserializeMsalV3(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or MsalClientException or System.Text.Json.JsonException)
        {
            // Genuinely unreadable - another user's DPAPI, or corrupt: start clean rather than block sign-in.
            TryDelete();
        }
        catch
        {
            // Busy, locked or otherwise unreadable this moment: the accounts are still in the file,
            // so nothing is deleted and nothing is written back over it. Never thrown on, either -
            // the lock taken above is only released in the after-access notification.
            _readIncomplete = true;
        }
    }

    private static void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        try
        {
            if (!args.HasStateChanged || _readIncomplete) return;

            Directory.CreateDirectory(AppPaths.DataDirectory);
            var bytes = args.TokenCache.SerializeMsalV3();
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);

            // Through a temporary file, so a crash mid-write cannot leave a torn cache behind.
            var temp = CacheFile + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);

            if (File.Exists(CacheFile)) File.Replace(temp, CacheFile, null, ignoreMetadataErrors: true);
            else File.Move(temp, CacheFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Persisting the cache is a convenience; failing to do so is not fatal.
        }
        finally
        {
            _crossProcessLock?.Dispose();
            _crossProcessLock = null;
            Gate.Release();
        }
    }

    /// <summary>
    /// An exclusively opened lock file, so a second instance of the app waits its turn rather than
    /// reading a cache this one is halfway through replacing. Null if it cannot be had in time - the
    /// cache is then used without it rather than sign-in hanging.
    /// </summary>
    private static FileStream? AcquireFileLock()
    {
        var deadline = DateTime.UtcNow + LockTimeout;

        while (true)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                return new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static void Clear()
    {
        Gate.Wait();
        try
        {
            TryDelete();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void TryDelete()
    {
        try
        {
            if (File.Exists(CacheFile)) File.Delete(CacheFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
