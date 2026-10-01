using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One choice in an environment's "Open links in" menu.</summary>
public sealed class BrowserProfileOption
{
    private BrowserProfileOption(string label, BrowserProfileSetting? setting, bool isSelected)
    {
        Label = label;
        Setting = setting;
        IsSelected = isSelected;
    }

    public string Label { get; }

    /// <summary>What choosing it saves; null for the system default browser.</summary>
    public BrowserProfileSetting? Setting { get; }

    public bool IsSelected { get; }

    /// <summary>
    /// The default browser, then every profile found. A saved profile that is no longer on this
    /// machine is listed too, ticked and marked, so the choice is visible and can be changed.
    /// </summary>
    public static IReadOnlyList<BrowserProfileOption> Build(IReadOnlyList<BrowserProfile> found, BrowserProfileSetting? current)
    {
        var options = new List<BrowserProfileOption> { new("Default browser", null, current is null) };

        options.AddRange(found.Select(p => new BrowserProfileOption(
            p.Label,
            new BrowserProfileSetting { Browser = p.Browser, ProfileDirectory = p.Directory },
            current?.Matches(p) == true)));

        if (current is not null && !found.Any(current.Matches))
        {
            options.Add(new BrowserProfileOption(
                $"{current.Browser} · {current.ProfileDirectory} (not found - using the default browser)", current, true));
        }

        return options;
    }
}
