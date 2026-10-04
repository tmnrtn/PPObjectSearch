using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PPObjectSearch.Auth;

namespace PPObjectSearch.Services;

/// <summary>One restored tab: which environment, in which tenant, signed in as whom.</summary>
public sealed class TabState
{
    public string? EnvironmentUrl { get; set; }
    public string? TenantId { get; set; }
    public string? AccountId { get; set; }
    public string? SolutionUniqueName { get; set; }

    /// <summary>
    /// The environment type last read for this tab, so its colour and badge show before it
    /// connects. Display only: the write guard always asks the Power Platform API afresh.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Dataverse.EnvironmentSku? EnvironmentType { get; set; }
}

/// <summary>
/// User settings persisted to %LOCALAPPDATA%\PPObjectSearch\settings.json.
/// Everything is optional - the app works with an environment URL alone.
/// </summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // The file is documented as editable by hand, with comments in the example.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Files already copied aside this session, so a second read does not copy again.</summary>
    private static readonly Dictionary<string, string> BackedUp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why the settings file could not be read, when it could not. Settings loaded that way are
    /// defaults standing in for the user's file, so they are never saved over it.
    /// </summary>
    [JsonIgnore]
    public string? LoadProblem { get; private set; }

    /// <summary>Tabs to restore at startup, in order.</summary>
    public List<TabState>? Tabs { get; set; }

    /// <summary>Override the built-in public client id with your own app registration.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Unique name of the solution to select when a tab connects. Defaults to the environment's
    /// default solution.
    /// </summary>
    public string? DefaultSolutionUniqueName { get; set; }

    /// <summary>
    /// Power Platform environment ids used for maker portal links, keyed by environment host
    /// (e.g. "contoso.crm11.dynamics.com"). Normally discovered automatically; set an entry here
    /// if discovery is blocked in a tenant.
    /// </summary>
    public Dictionary<string, string>? EnvironmentIds { get; set; }

    /// <summary>
    /// Optional maker portal URL templates, keyed by component type number (as a string) or by
    /// component logical name. Placeholders: {envId} {envUrl} {solutionId} {objectId} {name}
    /// {logicalName} {primaryEntity} {primaryEntityId} {componentType}.
    /// </summary>
    public Dictionary<string, string>? MakerLinkTemplates { get; set; }

    /// <summary>
    /// Saved reference-data comparisons - which tables to check, keyed on what, with which columns
    /// left out. Named so a team can keep one per data set rather than rebuilding the list.
    /// </summary>
    public List<ReferenceDataConfig>? ReferenceDataConfigurations { get; set; }

    /// <summary>
    /// Environment URLs this app may write reference data to despite being production - or despite
    /// their type being unreadable, which is guarded the same way. Sandbox, developer and trial
    /// environments do not need an entry. Nothing here is a wildcard: each entry clears exactly
    /// the one environment it names. The app adds an entry only when someone confirms it for that
    /// one environment from the sidebar, and removes it the same way.
    /// </summary>
    public List<string>? AllowProductionWrites { get; set; }

    /// <summary>Light, Dark, or System to follow the Windows app theme.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Whether to look, once a day, for a newer release on GitHub. Nothing is downloaded.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last update check ran, so it runs at most once a day.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Whether the main window's object detail pane is showing.</summary>
    public bool IsDetailPaneOpen { get; set; } = true;

    /// <summary>
    /// The browser profile each environment's links and sign-ins open in, keyed by host. An
    /// environment without an entry uses the system default browser.
    /// </summary>
    public Dictionary<string, BrowserProfileSetting>? BrowserProfiles { get; set; }

    public BrowserProfileSetting? GetBrowserProfile(string environmentUrl)
    {
        if (BrowserProfiles is null || TryGetHost(environmentUrl) is not { } host) return null;
        return BrowserProfiles.TryGetValue(host, out var profile) ? profile : null;
    }

    /// <summary>Sets - or, with null, clears - an environment's browser profile. The caller saves.</summary>
    public void SetBrowserProfile(string environmentUrl, BrowserProfileSetting? profile)
    {
        if (TryGetHost(environmentUrl) is not { } host) return;

        if (profile is null)
        {
            BrowserProfiles?.Remove(host);
            return;
        }

        (BrowserProfiles ??= new Dictionary<string, BrowserProfileSetting>(StringComparer.OrdinalIgnoreCase))[host] = profile;
    }

    public string? GetEnvironmentId(string environmentUrl)
    {
        if (EnvironmentIds is null) return null;

        var host = TryGetHost(environmentUrl);
        return host is not null && EnvironmentIds.TryGetValue(host, out var id) ? id : null;
    }

    private static string? TryGetHost(string environmentUrl)
    {
        var value = (environmentUrl ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    public static AppSettings Load() => Load(FilePath);

    internal static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            settings.EnvironmentIds = Rekey(settings.EnvironmentIds);
            if (settings.BrowserProfiles is not null)
            {
                settings.BrowserProfiles = new Dictionary<string, BrowserProfileSetting>(
                    settings.BrowserProfiles, StringComparer.OrdinalIgnoreCase);
            }
            settings.MakerLinkTemplates = Rekey(settings.MakerLinkTemplates);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Startup goes ahead on defaults, but the file is the user's saved comparisons and
            // allowlist: it is copied aside and never overwritten with the defaults.
            Log.Error($"Settings file {path} could not be read", ex);
            var copy = BackUp(path);
            var where = copy is null ? string.Empty : $" A copy of it was saved as {Path.GetFileName(copy)}.";

            return new AppSettings
            {
                LoadProblem =
                    $"Your settings file ({path}) could not be read: {ex.Message}{where} " +
                    "The app is running on default settings and will not save over the file. " +
                    "Fix or remove it, then restart."
            };
        }
    }

    private static string? BackUp(string path)
    {
        lock (BackedUp)
        {
            if (BackedUp.TryGetValue(path, out var existing)) return existing;

            try
            {
                var copy = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(path, copy, overwrite: true);
                return BackedUp[path] = copy;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static Dictionary<string, string>? Rekey(Dictionary<string, string>? source) =>
        source is null ? null : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);

    public void Save() => Save(FilePath);

    /// <summary>
    /// Writes through a temporary file and swaps it in, so a crash mid-write leaves the previous
    /// file (and a .bak of it) rather than a truncated one that would read as no settings at all.
    /// </summary>
    internal void Save(string path)
    {
        // Defaults standing in for a file that could not be read must not replace it.
        if (LoadProblem is not null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));

            if (File.Exists(path))
            {
                File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save one is not worth interrupting work.
            Log.Warn($"Settings could not be saved to {path}", ex);
        }
    }
}
