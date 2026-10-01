using System.Reflection;

namespace PPObjectSearch.Core;

/// <summary>
/// The running build's version, for the sidebar. Read from the executable itself, so it is always
/// the build being run: a release is stamped from its tag, a local build from the project file.
/// </summary>
public static class AppVersion
{
    private static readonly (string Version, string? Commit) Current =
        Parse(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
              ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3));

    /// <summary>"1.9.0".</summary>
    public static string Version => Current.Version;

    /// <summary>"Version 1.9.0".</summary>
    public static string Label => $"Version {Current.Version}";

    /// <summary>"Version 1.9.0, built from commit e8da9ee" - the commit when the build recorded one.</summary>
    public static string Description => Current.Commit is { } commit
        ? $"{Label}, built from commit {commit}"
        : Label;

    /// <summary>
    /// Splits an informational version - "1.9.0+e8da9ee5c1..." - into the version and a short
    /// commit. The SDK appends the commit after a '+'; a build without one has no commit to show.
    /// </summary>
    internal static (string Version, string? Commit) Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return ("unknown", null);

        var plus = informational.IndexOf('+');
        if (plus < 0) return (informational.Trim(), null);

        var version = informational[..plus].Trim();
        var metadata = informational[(plus + 1)..].Trim();

        // Only a hex commit id is worth showing; other build metadata is left out.
        var commit = metadata.Length >= 7 && metadata.All(Uri.IsHexDigit) ? metadata[..7] : null;

        return (version.Length > 0 ? version : "unknown", commit);
    }
}
