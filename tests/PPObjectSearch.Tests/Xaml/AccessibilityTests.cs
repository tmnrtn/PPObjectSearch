using System.IO;
using System.Text.RegularExpressions;

namespace PPObjectSearch.Tests.Xaml;

public partial class AccessibilityTests
{
    [GeneratedRegex(@"<Button\b[^>]*?Content=""&#x[0-9A-Fa-f]+;""[^>]*?/?>", RegexOptions.Singleline)]
    private static partial Regex GlyphButton();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(here)!); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PPObjectSearch.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("The repository root (PPObjectSearch.slnx) was not found.");
    }

    /// <summary>
    /// A button whose content is an icon-font glyph reads to a screen reader as a private-use
    /// character - nothing, or nonsense - and a tooltip is not its accessible name. Each needs one.
    /// </summary>
    [Fact]
    public void Every_icon_only_button_has_an_accessible_name()
    {
        var root = FindRoot();
        var unnamed = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj") && !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}"))
            .SelectMany(f => GlyphButton().Matches(File.ReadAllText(f))
                .Where(m => !m.Value.Contains("AutomationProperties.Name"))
                .Select(m => $"{Path.GetRelativePath(root, f)}: {m.Value[..Math.Min(80, m.Value.Length)]}"))
            .ToList();

        Assert.Empty(unnamed);
    }
}
