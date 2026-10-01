using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PPObjectSearch.Services;

/// <summary>The browsers whose profiles can be chosen. Both are Chromium, so one switch picks a profile.</summary>
public enum BrowserKind
{
    Edge,
    Chrome
}

/// <summary>A browser profile on this machine: "Edge · Work (tom@contoso.com)".</summary>
public sealed record BrowserProfile(BrowserKind Browser, string Directory, string Name, string? Account)
{
    public string BrowserName => Browser == BrowserKind.Edge ? "Edge" : "Chrome";

    public string Label => Account is { Length: > 0 } account && !string.Equals(account, Name, StringComparison.OrdinalIgnoreCase)
        ? $"{BrowserName} · {Name} ({account})"
        : $"{BrowserName} · {Name}";
}

/// <summary>The profile an environment's links and sign-ins open in, as saved in settings.</summary>
public sealed class BrowserProfileSetting
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BrowserKind Browser { get; set; }

    /// <summary>The profile's folder name: "Default", "Profile 2".</summary>
    public string ProfileDirectory { get; set; } = "Default";

    public bool Matches(BrowserProfile profile) =>
        profile.Browser == Browser && string.Equals(profile.Directory, ProfileDirectory, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Finds Edge and Chrome profiles, and where each browser is installed.</summary>
public static class BrowserProfiles
{
    /// <summary>Every profile of every installed browser, Edge first, in each browser's own order.</summary>
    public static IReadOnlyList<BrowserProfile> Discover()
    {
        var profiles = new List<BrowserProfile>();

        foreach (var browser in Enum.GetValues<BrowserKind>())
        {
            if (ExecutablePath(browser) is null) continue;

            try
            {
                var localState = Path.Combine(UserDataDirectory(browser), "Local State");
                if (File.Exists(localState)) profiles.AddRange(ParseLocalState(browser, File.ReadAllText(localState)));
            }
            catch
            {
                // A browser whose profile list cannot be read simply offers none.
            }
        }

        return profiles;
    }

    /// <summary>
    /// The profiles listed in a browser's "Local State" file - profile.info_cache, keyed by the
    /// profile's folder - with the name the browser shows and the signed-in account, if any.
    /// </summary>
    internal static IReadOnlyList<BrowserProfile> ParseLocalState(BrowserKind browser, string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("profile", out var profile) ||
            !profile.TryGetProperty("info_cache", out var cache) ||
            cache.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<BrowserProfile>();
        }

        return cache.EnumerateObject()
            .Select(entry => new BrowserProfile(
                browser,
                entry.Name,
                Text(entry.Value, "name") ?? Text(entry.Value, "shortcut_name") ?? entry.Name,
                Text(entry.Value, "user_name")))
            .OrderBy(p => p.Directory == "Default" ? 0 : 1)
            .ThenBy(p => p.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The browser's executable, from Windows' App Paths registration; null if not installed.</summary>
    public static string? ExecutablePath(BrowserKind browser)
    {
        var exe = browser == BrowserKind.Edge ? "msedge.exe" : "chrome.exe";

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (key?.GetValue(null) is string path && File.Exists(path)) return path;
            }
            catch
            {
                // Try the other hive.
            }
        }

        return null;
    }

    private static string UserDataDirectory(BrowserKind browser) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        browser == BrowserKind.Edge ? @"Microsoft\Edge\User Data" : @"Google\Chrome\User Data");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}

/// <summary>
/// Opens a web link - in an environment's chosen browser profile, or the system default.
/// </summary>
public static class LinkLauncher
{
    /// <summary>Opens <paramref name="url"/>; throws if it cannot be started.</summary>
    public static void Open(string url, BrowserProfileSetting? profile)
    {
        var exe = profile is null ? null : BrowserProfiles.ExecutablePath(profile.Browser);
        Process.Start(StartInfo(url, profile, exe))?.Dispose();
    }

    /// <summary>
    /// How a link is started. In a profile, the browser is run directly with the profile switch and
    /// the link as separate arguments; only an absolute http(s) link is passed, so nothing in a URL
    /// can be read as a browser switch. Without a profile - or its browser - the shell decides.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string url, BrowserProfileSetting? profile, string? browserPath)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"Only web links can be opened: '{url}'.", nameof(url));
        }

        if (profile is null || browserPath is null)
        {
            return new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
        }

        var info = new ProcessStartInfo(browserPath) { UseShellExecute = false };
        info.ArgumentList.Add($"--profile-directory={profile.ProfileDirectory}");
        info.ArgumentList.Add(uri.AbsoluteUri);
        return info;
    }
}
