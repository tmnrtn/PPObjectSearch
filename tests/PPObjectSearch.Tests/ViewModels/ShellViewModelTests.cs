using System.IO;
using System.Net.Http;
using System.Windows;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The main window's tabs - restored, added, closed and moved, and saved each time - with its theme, update check and app-wide commands.</summary>
public sealed class ShellViewModelTests : IDisposable
{
    private const string Contoso = "https://contoso.crm11.dynamics.com";
    private const string Fabrikam = "https://fabrikam.crm4.dynamics.com";
    private const string Northwind = "https://northwind.crm.dynamics.com";

    private static readonly string ThreeTabs = $$"""
        { "CheckForUpdates": false, "Theme": "Light",
          "Tabs": [ { "EnvironmentUrl": "{{Contoso}}" }, { "EnvironmentUrl": "" }, { "EnvironmentUrl": "{{Fabrikam}}" },
                    { "EnvironmentUrl": "{{Northwind}}" } ] }
        """;

    private readonly List<TestShell> _shells = new();

    public void Dispose()
    {
        foreach (var shell in _shells) shell.Dispose();
    }

    private TestShell Start(string? settings = TestShell.Quiet, FakeHttpHandler? updates = null)
    {
        var shell = new TestShell(settings, updates);
        _shells.Add(shell);
        return shell;
    }

    private static string[] Urls(ShellViewModel shell) => shell.Sessions.Select(s => s.EnvironmentUrl).ToArray();

    private static string[] SavedUrls(TestShell test) => test.Saved().Tabs!.Select(t => t.EnvironmentUrl ?? string.Empty).ToArray();

    // ---------------------------------------------------------------- tabs

    [Fact]
    public void Saved_tabs_are_restored_in_order_and_the_first_is_selected()
    {
        var shell = Start(ThreeTabs).Shell;

        Assert.Equal([Contoso, Fabrikam, Northwind], Urls(shell));
        Assert.Same(shell.Sessions[0], shell.SelectedSession);
        Assert.Null(shell.SettingsProblem);
    }

    [Fact]
    public void Without_saved_tabs_there_is_one_new_one()
    {
        var shell = Start(settings: null).Shell;

        var session = Assert.Single(shell.Sessions);
        Assert.Equal("New environment", session.Title);
        Assert.Same(session, shell.SelectedSession);
        Assert.False(shell.CloseTabCommand.CanExecute(null));
        Assert.True(shell.CloseTabCommand.CanExecute(session));
    }

    [Fact]
    public void A_new_tab_is_selected_and_saving_leaves_out_tabs_without_an_environment()
    {
        var test = Start(ThreeTabs);

        test.Shell.AddTabCommand.Execute(null);

        Assert.Equal(4, test.Shell.Sessions.Count);
        Assert.Same(test.Shell.Sessions[3], test.Shell.SelectedSession);
        Assert.Equal([Contoso, Fabrikam, Northwind], SavedUrls(test));
    }

    [Fact]
    public void Closing_a_tab_selects_the_one_that_takes_its_place()
    {
        var test = Start(ThreeTabs);
        var shell = test.Shell;
        var fabrikam = shell.Sessions[1];

        shell.CloseTabCommand.Execute(fabrikam);

        Assert.Equal([Contoso, Northwind], Urls(shell));
        Assert.Same(shell.Sessions[1], shell.SelectedSession);
        Assert.Equal([Contoso, Northwind], SavedUrls(test));

        shell.CloseTabCommand.Execute(shell.Sessions[1]);

        Assert.Same(shell.Sessions[0], shell.SelectedSession);
    }

    [Fact]
    public void Closing_the_last_tab_leaves_a_fresh_one()
    {
        var test = Start(ThreeTabs);
        var shell = test.Shell;

        foreach (var session in shell.Sessions.ToList()) shell.CloseTabCommand.Execute(session);

        var fresh = Assert.Single(shell.Sessions);
        Assert.Equal(string.Empty, fresh.EnvironmentUrl);
        Assert.Same(fresh, shell.SelectedSession);
        Assert.Empty(test.Saved().Tabs!);
    }

    [Fact]
    public void Closing_something_that_is_not_a_tab_here_does_nothing()
    {
        var shell = Start(ThreeTabs).Shell;
        using var stranger = TestSessions.Disconnected();

        shell.CloseTabCommand.Execute(null);
        shell.CloseTabCommand.Execute(stranger);

        Assert.Equal(3, shell.Sessions.Count);
    }

    [Fact]
    public void Moving_tabs_keeps_the_selection_and_saves_the_new_order()
    {
        var test = Start(ThreeTabs);
        var shell = test.Shell;
        var contoso = shell.Sessions[0];

        shell.MoveTabRightCommand.Execute(null);

        Assert.Equal([Fabrikam, Contoso, Northwind], Urls(shell));
        Assert.Same(contoso, shell.SelectedSession);
        Assert.Equal([Fabrikam, Contoso, Northwind], SavedUrls(test));

        shell.MoveTab(contoso, 99);
        Assert.Equal([Fabrikam, Northwind, Contoso], Urls(shell));

        shell.MoveTabLeftCommand.Execute(null);
        Assert.Equal([Fabrikam, Contoso, Northwind], Urls(shell));
    }

    [Fact]
    public void A_move_that_goes_nowhere_saves_nothing()
    {
        var test = Start(ThreeTabs);
        var shell = test.Shell;
        var before = File.ReadAllText(test.SettingsPath);
        using var stranger = TestSessions.Disconnected();

        shell.MoveTabLeftCommand.Execute(null);
        shell.MoveTab(stranger, 1);
        shell.SelectedSession = null;
        shell.MoveTabRightCommand.Execute(null);

        Assert.Equal([Contoso, Fabrikam, Northwind], Urls(shell));
        Assert.Equal(before, File.ReadAllText(test.SettingsPath));
    }

    [Fact]
    public void A_tab_choosing_a_solution_is_saved_with_it()
    {
        var test = Start(ThreeTabs);

        test.Shell.Sessions[1].SelectedSolution = TestSessions.Solution("Fabrikam Sales", "fab_sales");

        Assert.Equal("fab_sales", test.Saved().Tabs![1].SolutionUniqueName);
    }

    [Fact]
    public void A_change_to_the_write_allowlist_in_one_tab_is_shown_by_every_tab()
    {
        var shell = Start(ThreeTabs).Shell;
        var told = new List<string>();
        shell.Sessions[2].PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EnvironmentSessionViewModel.IsWriteAllowlisted)) told.Add(e.PropertyName);
        };

        // Nothing is on the allowlist, so revoking changes no setting - but still tells the other tabs.
        shell.Sessions[0].RevokeWritesCommand.Execute(null);

        Assert.Single(told);
    }

    [Fact]
    public void Shutting_down_saves_the_tabs()
    {
        var test = Start(ThreeTabs);
        test.Shell.MoveTab(test.Shell.Sessions[2], 0);
        File.Delete(test.SettingsPath);

        test.Shell.Shutdown();

        Assert.Equal([Northwind, Contoso, Fabrikam], SavedUrls(test));
    }

    // ---------------------------------------------------------------- theme

    [Fact]
    public void The_theme_comes_from_settings()
    {
        var shell = Start(ThreeTabs).Shell;

        Assert.Equal(AppTheme.Light, shell.Theme);
        Assert.True(shell.IsLightTheme);
        Assert.False(shell.IsDarkTheme);
        Assert.False(shell.IsSystemTheme);
    }

    [Fact]
    public void Choosing_a_theme_applies_and_remembers_it()
    {
        var test = Start(ThreeTabs);
        var changed = new List<string?>();
        test.Shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        test.Shell.SetThemeCommand.Execute("Dark");

        Assert.Equal(AppTheme.Dark, test.Shell.Theme);
        Assert.True(test.Shell.IsDarkTheme);
        Assert.Equal([AppTheme.Dark], test.AppliedThemes);
        Assert.Equal(AppTheme.Dark, test.Saved().Theme);
        Assert.Equal(
            [nameof(ShellViewModel.Theme), nameof(ShellViewModel.IsSystemTheme), nameof(ShellViewModel.IsLightTheme), nameof(ShellViewModel.IsDarkTheme)],
            changed);
    }

    [Fact]
    public void The_same_theme_or_an_unknown_one_changes_nothing()
    {
        var test = Start(ThreeTabs);

        test.Shell.SetThemeCommand.Execute("Light");
        test.Shell.SetThemeCommand.Execute("Purple");
        test.Shell.SetThemeCommand.Execute(null);

        Assert.Equal(AppTheme.Light, test.Shell.Theme);
        Assert.Empty(test.AppliedThemes);
    }

    // ---------------------------------------------------------------- app-wide commands

    [Theory]
    [InlineData("GlobalSearch", "Connect at least one environment first.")]
    [InlineData("Compare", "Connect at least two environments to compare them.")]
    [InlineData("CompareData", "Connect at least two environments to compare their data.")]
    [InlineData("Readiness", "Connect the environment the solution comes from and the one it is going to.")]
    public void Cross_environment_windows_need_enough_connected_tabs(string command, string message)
    {
        var test = Start(ThreeTabs);
        var shell = test.Shell;
        if (command != "GlobalSearch")
        {
            TestSessions.Connect(shell.Sessions[0], new FakeHttpHandler(), []);
        }

        var run = command switch
        {
            "GlobalSearch" => shell.GlobalSearchCommand,
            "Compare" => shell.CompareCommand,
            "CompareData" => shell.CompareDataCommand,
            _ => shell.ReadinessCommand
        };
        run.Execute(shell.Sessions[0]);

        Assert.Equal([message], test.Messages);
    }

    [Fact]
    public void Signing_out_of_everything_asks_first_and_cancelling_keeps_every_tab()
    {
        var test = Start(ThreeTabs);
        test.Answer = MessageBoxResult.Cancel;
        var statuses = test.Shell.Sessions.Select(s => s.Status).ToList();

        test.Shell.SignOutAllCommand.Execute(null);

        Assert.Equal(["Sign out of every account and clear all tabs' data?"], test.Messages);
        Assert.Equal(statuses, test.Shell.Sessions.Select(s => s.Status));
        Assert.Equal(3, test.Shell.Sessions.Count);
    }

    [Fact]
    public void Close_all_details_is_off_while_none_are_open()
    {
        var shell = Start(ThreeTabs).Shell;

        Assert.Equal(0, ShellViewModel.OpenDetailsCount);
        Assert.False(ShellViewModel.HasOpenDetails);
        Assert.Equal("Close all details (0)", ShellViewModel.CloseAllDetailsLabel);
        Assert.False(shell.CloseAllDetailsCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- update check

    private static FakeHttpHandler Release(string tag) => new FakeHttpHandler().OnJson(HttpMethod.Get, "releases/latest",
        $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/tmnrtn/PPObjectSearch/releases/tag/{{tag}}"}""");

    /// <summary>The check runs in the background; this waits until it has saved when it ran.</summary>
    private static async Task WaitForCheck(TestShell test, string settings)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Read(test.SettingsPath) is not { } now || now == settings)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The update check did not finish.");
            await Task.Delay(10);
        }
    }

    /// <summary>The file's text, or null while it is being swapped for a new one.</summary>
    private static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    [Fact]
    public async Task A_newer_release_is_offered()
    {
        const string settings = """{ "CheckForUpdates": true }""";
        var test = Start(settings, Release("v999.0.0"));

        await WaitForCheck(test, settings);

        Assert.True(test.Shell.HasUpdate);
        Assert.Equal("999.0.0", test.Shell.Update!.Version);
        Assert.Equal("Version 999.0.0 is available", test.Shell.UpdateLabel);
        Assert.NotNull(test.Saved().LastUpdateCheck);
        Assert.Contains("api.github.com", Assert.Single(test.Updates.Requests).Url);
    }

    [Fact]
    public async Task An_older_release_is_not_offered()
    {
        var settings = $$"""{ "CheckForUpdates": true, "LastUpdateCheck": "{{DateTimeOffset.Now.AddDays(-2):O}}" }""";
        var test = Start(settings, Release("v0.0.1"));

        await WaitForCheck(test, settings);

        Assert.False(test.Shell.HasUpdate);
        Assert.Equal(string.Empty, test.Shell.UpdateLabel);
        Assert.Single(test.Updates.Requests);

        // With nothing to open, opening the update does nothing.
        test.Shell.OpenUpdateCommand.Execute(null);
    }

    [Theory]
    [InlineData(false, -2)]
    [InlineData(true, 0)]
    public void The_check_is_skipped_when_turned_off_or_run_within_the_day(bool enabled, int daysAgo)
    {
        var lastCheck = DateTimeOffset.Now.AddDays(daysAgo).ToString("O");
        var test = Start($$"""{ "CheckForUpdates": {{(enabled ? "true" : "false")}}, "LastUpdateCheck": "{{lastCheck}}" }""",
            Release("v999.0.0"));

        Assert.Empty(test.Updates.Requests);
        Assert.False(test.Shell.HasUpdate);
    }
}
