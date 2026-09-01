using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

public sealed class CachedComponents
{
    public DateTimeOffset LoadedAt { get; set; }
    public string? EnvironmentUrl { get; set; }
    public Guid SolutionId { get; set; }
    public List<SolutionComponentItem> Items { get; set; } = new();
}

/// <summary>
/// On-disk copy of the last successful load, per environment and solution.
///
/// It is shown immediately on reconnect and then replaced by a fresh read running in the
/// background - stale-while-revalidate. Deliberately not a delta cache: Dataverse gives no
/// reliable way to detect components *removed* from a solution, so an incremental refresh would
/// quietly accumulate objects that no longer exist. A full background refresh costs nothing the
/// user waits for and cannot go stale.
/// </summary>
public static class ComponentCache
{
    private static readonly string CacheDirectory = Path.Combine(AppPaths.DataDirectory, "cache");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string PathFor(string environmentUrl, Guid solutionId)
    {
        // The URL can contain characters that are illegal in file names; a short hash keeps the
        // name safe and stable without needing to sanitise.
        var key = Encoding.UTF8.GetBytes(environmentUrl.ToLowerInvariant());
        var hash = Convert.ToHexString(SHA256.HashData(key))[..16];
        return Path.Combine(CacheDirectory, $"{hash}_{solutionId:N}.json");
    }

    public static CachedComponents? TryLoad(string environmentUrl, Guid solutionId)
    {
        try
        {
            var path = PathFor(environmentUrl, solutionId);
            if (!File.Exists(path)) return null;

            var cached = JsonSerializer.Deserialize<CachedComponents>(File.ReadAllText(path), JsonOptions);
            if (cached is null || cached.Items.Count == 0) return null;

            // SearchIndex is derived, not stored.
            foreach (var item in cached.Items) item.BuildSearchIndex();

            return cached;
        }
        catch
        {
            // A cache that cannot be read is simply a cache miss.
            return null;
        }
    }

    public static void Save(string environmentUrl, Guid solutionId, IEnumerable<SolutionComponentItem> items)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            var payload = new CachedComponents
            {
                LoadedAt = DateTimeOffset.Now,
                EnvironmentUrl = environmentUrl,
                SolutionId = solutionId,
                Items = items.ToList()
            };

            File.WriteAllText(PathFor(environmentUrl, solutionId), JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch
        {
            // Caching is an optimisation - never fail a load because it could not be written.
        }
    }

    public static void Clear()
    {
        try
        {
            if (Directory.Exists(CacheDirectory)) Directory.Delete(CacheDirectory, recursive: true);
        }
        catch
        {
            // ignored
        }
    }
}
