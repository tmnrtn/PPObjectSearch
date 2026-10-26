using System.IO;

namespace PPObjectSearch.Tests.Xaml;

/// <summary>
/// Screen views are shown in a tab of the main window as often as in a window of their own, so
/// nothing in them may reach for the window around them: in a tab that is the main window, whose
/// data context is the shell rather than the screen's view model.
/// </summary>
public class ScreenViewXamlTests
{
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(here)!); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PPObjectSearch.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("The repository root (PPObjectSearch.slnx) was not found.");
    }

    private static IEnumerable<string> ScreenViews() =>
        Directory.EnumerateFiles(Path.Combine(FindRoot(), "Views"), "*.xaml")
            .Where(f => File.ReadAllText(f).TrimStart('﻿', ' ', '\r', '\n').StartsWith("<local:ScreenView", StringComparison.Ordinal));

    [Fact]
    public void The_secondary_screens_are_screen_views()
    {
        var names = ScreenViews().Select(Path.GetFileNameWithoutExtension).ToList();

        Assert.Contains("ObjectDetailsView", names);
        Assert.Contains("FailuresView", names);
        Assert.Equal(20, names.Count);
    }

    [Fact]
    public void No_screen_view_looks_for_its_window()
    {
        var offending = ScreenViews()
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("AncestorType=Window", StringComparison.Ordinal) ||
                       text.Contains("{x:Type Window}", StringComparison.Ordinal) ||
                       text.Contains("<Window", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offending);
    }
}
