using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PPObjectSearch.Tests.Xaml;

/// <summary>
/// Checks over the XAML files themselves, read as XML: a resource used but defined nowhere, or a
/// colour defined in one theme and not the other, otherwise only shows up as a runtime error or a
/// control drawn in the wrong colour after a theme switch.
/// </summary>
public partial class ResourceKeyTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [GeneratedRegex(@"\{(?:Dynamic|Static)Resource\s+(?<key>[A-Za-z_][\w.]*)\s*\}")]
    private static partial Regex ResourceUse();

    private static string Root => FindRoot();

    /// <summary>Up from this source file, which is in the repository wherever the tests are built.</summary>
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(here)!); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PPObjectSearch.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("The repository root (PPObjectSearch.slnx) was not found.");
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(Root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}"));

    private static HashSet<string> KeysIn(string file) =>
        XDocument.Load(file).Descendants()
            .Select(e => (string?)e.Attribute(X + "Key"))
            .Where(k => k is not null && !k.StartsWith('{'))
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Light_and_dark_themes_define_the_same_keys()
    {
        var light = KeysIn(Path.Combine(Root, "Themes", "Light.xaml"));
        var dark = KeysIn(Path.Combine(Root, "Themes", "Dark.xaml"));

        Assert.Empty(light.Except(dark).Select(k => "only in Light: " + k)
            .Concat(dark.Except(light).Select(k => "only in Dark: " + k)));
    }

    [Fact]
    public void Every_resource_used_in_xaml_is_defined()
    {
        // Shared keys: the themes, the control styles and App.xaml are merged into every window.
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "App.xaml", Path.Combine("Themes", "Light.xaml"), Path.Combine("Themes", "Controls.xaml") })
        {
            shared.UnionWith(KeysIn(Path.Combine(Root, file)));
        }

        var missing = new List<string>();

        foreach (var file in XamlFiles())
        {
            var local = KeysIn(file);
            var text = File.ReadAllText(file);

            foreach (Match match in ResourceUse().Matches(text))
            {
                var key = match.Groups["key"].Value;
                if (!shared.Contains(key) && !local.Contains(key))
                {
                    missing.Add($"{Path.GetRelativePath(Root, file)}: {key}");
                }
            }
        }

        Assert.Empty(missing.Distinct());
    }
}
