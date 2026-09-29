using System.Windows;
using Microsoft.Win32;

namespace PPObjectSearch.Services;

public enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>
/// Swaps the colour dictionary merged into the application's resources. Every themed brush is
/// consumed through DynamicResource, so replacing the dictionary repaints every open window.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static bool _listening;

    public static AppTheme Theme { get; private set; } = AppTheme.System;

    public static event EventHandler? ThemeChanged;

    /// <summary>The theme actually on screen once <see cref="AppTheme.System"/> is resolved.</summary>
    public static bool IsDark => Resolve(Theme) == AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        Theme = theme;

        var resolved = Resolve(theme);
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PPObjectSearch;component/Themes/{resolved}.xaml", UriKind.Absolute)
        };

        var merged = Application.Current.Resources.MergedDictionaries;

        // App.xaml merges Light.xaml so the designer has brushes; that one goes too.
        foreach (var old in merged.Where(IsThemeDictionary).ToList()) merged.Remove(old);

        // First, so the control styles merged after it can resolve the brushes.
        merged.Insert(0, dictionary);

        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool IsThemeDictionary(ResourceDictionary d) =>
        d.Source?.OriginalString is { } source &&
        (source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
         source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase));

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Theme != AppTheme.System || e.Category != UserPreferenceCategory.General) return;

        Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
    }

    private static AppTheme Resolve(AppTheme theme) =>
        theme != AppTheme.System ? theme : SystemUsesLightTheme() ? AppTheme.Light : AppTheme.Dark;

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }
}
