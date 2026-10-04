using System.IO;
using System.Text.Json;
using PPObjectSearch.Auth;

namespace PPObjectSearch.Services;

/// <summary>A search worth keeping: the words and every filter, under a name.</summary>
public sealed class SavedSearch
{
    public required string Name { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? SubType { get; set; }
    public string? State { get; set; }
    public string? Layer { get; set; }
    public bool FavouritesOnly { get; set; }
}

/// <summary>An object whose details were opened, most recent first.</summary>
public sealed class RecentObject
{
    public required Guid ObjectId { get; set; }
    public required string Label { get; set; }
    public string? TypeName { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
}

/// <summary>One environment's favourites, saved searches and recent objects.</summary>
public sealed class EnvironmentLibrary
{
    public HashSet<Guid> Favourites { get; set; } = new();
    public List<SavedSearch> Searches { get; set; } = new();
    public List<RecentObject> Recent { get; set; } = new();
}

/// <summary>
/// What a person has marked or kept in each environment - favourite objects, named searches, the
/// objects they opened lately - in %LOCALAPPDATA%\PPObjectSearch\library.json.
///
/// Kept apart from settings.json: it changes far more often, and a settings file that cannot be
/// read (and so is not written) should not stop a favourite being kept, or the other way round.
/// </summary>
public sealed class UserLibrary
{
    public const int MaxRecent = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Lazy<UserLibrary> SharedInstance =
        new(() => Load(Path.Combine(AppPaths.DataDirectory, "library.json")));

    /// <summary>The one library the app uses; tests make their own.</summary>
    public static UserLibrary Shared => SharedInstance.Value;

    private readonly object _sync = new();
    private string _path = string.Empty;

    /// <summary>Keyed by environment host, so a URL written with or without a slash is the same place.</summary>
    public Dictionary<string, EnvironmentLibrary> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static UserLibrary Load(string path)
    {
        UserLibrary library;

        try
        {
            library = File.Exists(path)
                ? JsonSerializer.Deserialize<UserLibrary>(File.ReadAllText(path), JsonOptions) ?? new UserLibrary()
                : new UserLibrary();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unlike settings, losing these is an inconvenience rather than a hazard: start empty,
            // but keep the file that would not read beside the new one.
            Log.Warn($"The library at {path} could not be read; starting empty", ex);
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
            library = new UserLibrary();
        }

        library.Environments = new Dictionary<string, EnvironmentLibrary>(library.Environments, StringComparer.OrdinalIgnoreCase);
        library._path = path;
        return library;
    }

    /// <summary>The environment's library, created empty if it has none yet.</summary>
    public EnvironmentLibrary For(string environmentUrl)
    {
        var host = Host(environmentUrl);

        lock (_sync)
        {
            if (!Environments.TryGetValue(host, out var library)) Environments[host] = library = new EnvironmentLibrary();
            return library;
        }
    }

    public bool IsFavourite(string environmentUrl, Guid objectId) => For(environmentUrl).Favourites.Contains(objectId);

    /// <summary>Marks or unmarks the objects, and saves.</summary>
    public void SetFavourite(string environmentUrl, IEnumerable<Guid> objectIds, bool favourite)
    {
        var library = For(environmentUrl);

        lock (_sync)
        {
            foreach (var id in objectIds)
            {
                if (favourite) library.Favourites.Add(id);
                else library.Favourites.Remove(id);
            }
        }

        Save();
    }

    /// <summary>Puts the object at the top of the recent list, once, and saves.</summary>
    public void AddRecent(string environmentUrl, RecentObject opened)
    {
        var library = For(environmentUrl);

        lock (_sync)
        {
            library.Recent.RemoveAll(r => r.ObjectId == opened.ObjectId);
            library.Recent.Insert(0, opened);
            if (library.Recent.Count > MaxRecent) library.Recent.RemoveRange(MaxRecent, library.Recent.Count - MaxRecent);
        }

        Save();
    }

    /// <summary>Adds a search, or replaces the one already called that, and saves.</summary>
    public void SaveSearch(string environmentUrl, SavedSearch search)
    {
        var library = For(environmentUrl);

        lock (_sync)
        {
            library.Searches.RemoveAll(s => string.Equals(s.Name, search.Name, StringComparison.CurrentCultureIgnoreCase));
            library.Searches.Add(search);
            library.Searches.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        Save();
    }

    public void DeleteSearch(string environmentUrl, string name)
    {
        var library = For(environmentUrl);
        lock (_sync) library.Searches.RemoveAll(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase));
        Save();
    }

    /// <summary>Through a temporary file, like settings, so a crash cannot leave half a library.</summary>
    public void Save()
    {
        if (string.IsNullOrEmpty(_path)) return;

        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(this, JsonOptions);

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The library could not be saved to {_path}", ex);
        }
    }

    private static string Host(string environmentUrl)
    {
        var value = (environmentUrl ?? string.Empty).Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : value;
    }
}
