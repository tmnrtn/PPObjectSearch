using System.IO;
using System.Windows;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>
/// A main window's view model whose settings live in a temporary folder rather than the user's
/// profile, whose update check goes to a fake, and whose messages and themes are recorded.
/// </summary>
public sealed class TestShell : IDisposable
{
    /// <summary>Settings that keep the update check quiet; most tests want nothing else.</summary>
    public const string Quiet = """{ "CheckForUpdates": false }""";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ppos-shell-" + Guid.NewGuid().ToString("N"));

    public TestShell(string? settingsJson = Quiet, FakeHttpHandler? updates = null)
    {
        Directory.CreateDirectory(_folder);
        SettingsPath = Path.Combine(_folder, "settings.json");
        if (settingsJson is not null) File.WriteAllText(SettingsPath, settingsJson);

        Updates = updates ?? new FakeHttpHandler();
        Shell = new ShellViewModel(SettingsPath, Updates)
        {
            ShowMessage = (message, _, _) =>
            {
                Messages.Add(message);
                return Answer;
            },
            ApplyTheme = AppliedThemes.Add
        };
    }

    public ShellViewModel Shell { get; }
    public string SettingsPath { get; }
    public FakeHttpHandler Updates { get; }

    /// <summary>Every message box the shell showed, in order.</summary>
    public List<string> Messages { get; } = new();

    /// <summary>The button pressed on every message box.</summary>
    public MessageBoxResult Answer { get; set; } = MessageBoxResult.Cancel;

    public List<AppTheme> AppliedThemes { get; } = new();

    /// <summary>The settings as saved to the file.</summary>
    public AppSettings Saved() => AppSettings.Load(SettingsPath);

    public void Dispose()
    {
        foreach (var session in Shell.Sessions) session.Dispose();
        Directory.Delete(_folder, recursive: true);
    }
}
