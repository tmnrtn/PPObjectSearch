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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
    /// component logical name. Placeholders:
    /// {envId} {envUrl} {solutionId} {objectId} {name} {logicalName} {primaryEntity} {componentType}.
    /// </summary>
    public Dictionary<string, string>? MakerLinkTemplates { get; set; }

    public string? GetEnvironmentId(string environmentUrl)
    {
        if (EnvironmentIds is null) return null;

        var host = TryGetHost(environmentUrl);
        return host is not null && EnvironmentIds.TryGetValue(host, out var id) ? id : null;
    }

    private static string? TryGetHost(string environmentUrl)
    {
        return Uri.TryCreate(environmentUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    settings.EnvironmentIds = Rekey(settings.EnvironmentIds);
                    settings.MakerLinkTemplates = Rekey(settings.MakerLinkTemplates);
                    return settings;
                }
            }
        }
        catch
        {
            // Fall through to defaults rather than failing startup on a bad settings file.
        }

        return new AppSettings();
    }

    private static Dictionary<string, string>? Rekey(Dictionary<string, string>? source) =>
        source is null ? null : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // ignored - settings are a convenience
        }
    }
}
