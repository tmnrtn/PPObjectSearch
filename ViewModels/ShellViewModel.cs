using System.Collections.ObjectModel;
using System.Windows;
using PPObjectSearch.Auth;
using PPObjectSearch.Core;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// Owns the environment tabs. Tabs are independent connections - they may target different
/// environments in different tenants, signed in as different accounts - but they share one
/// MSAL token cache so an account already used elsewhere is reused without another prompt.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AuthenticationService _auth;

    public ShellViewModel()
    {
        _settings = AppSettings.Load();
        _auth = new AuthenticationService(_settings.ClientId);

        AddTabCommand = new RelayCommand(_ => AddTab());
        GlobalSearchCommand = new RelayCommand(_ => OpenGlobalSearch());
        CompareCommand = new RelayCommand(_ => OpenCompare());
        CompareDataCommand = new RelayCommand(_ => OpenDataCompare());
        ReadinessCommand = new RelayCommand(p => OpenReadiness(p as EnvironmentSessionViewModel));
        CommandPaletteCommand = new RelayCommand(_ => OpenCommandPalette());
        CloseTabCommand = new RelayCommand(CloseTab, p => Sessions.Count > 1 || p is not null);
        MoveTabLeftCommand = new RelayCommand(_ => MoveSelectedTab(-1));
        MoveTabRightCommand = new RelayCommand(_ => MoveSelectedTab(+1));
        SignOutAllCommand = new AsyncRelayCommand(_ => SignOutAllAsync());
        CloseAllDetailsCommand = new RelayCommand(_ => DetailsWindows.CloseAll(), _ => OpenDetailsCount > 0);
        DetailsWindows.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(OpenDetailsCount));
            OnPropertyChanged(nameof(HasOpenDetails));
            OnPropertyChanged(nameof(CloseAllDetailsLabel));
            CloseAllDetailsCommand.RaiseCanExecuteChanged();
        };
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        SetThemeCommand = new RelayCommand(p =>
        {
            if (Enum.TryParse<AppTheme>(p as string, out var theme)) Theme = theme;
        });

        RestoreTabs();
        _ = CheckForUpdateAsync();
    }

    private Services.AvailableUpdate? _update;
    /// <summary>A newer release, when the daily check found one.</summary>
    public Services.AvailableUpdate? Update
    {
        get => _update;
        private set
        {
            if (!SetProperty(ref _update, value)) return;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateLabel));
        }
    }

    public bool HasUpdate => Update is not null;
    public string UpdateLabel => Update is null ? string.Empty : $"Version {Update.Version} is available";

    public RelayCommand OpenUpdateCommand => _openUpdateCommand ??= new RelayCommand(_ =>
    {
        if (Update is { } update) LinkLauncher.Open(update.Url, null);
    });
    private RelayCommand? _openUpdateCommand;

    /// <summary>
    /// Once a day at most, in the background: is there a newer release? Off with CheckForUpdates
    /// set to false in settings.json, for machines that should not reach out to GitHub.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        if (!_settings.CheckForUpdates) return;
        if (_settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < UpdateCheck.Interval) return;

        using var http = new System.Net.Http.HttpClient(Core.RetryHandler.Shared, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        Update = await UpdateCheck.CheckAsync(Core.AppVersion.Version, http);

        _settings.LastUpdateCheck = DateTimeOffset.Now;
        _settings.Save();
    }

    public ObservableCollection<EnvironmentSessionViewModel> Sessions { get; } = new();

    /// <summary>Why settings.json could not be read at startup, if it could not.</summary>
    public string? SettingsProblem => _settings.LoadProblem;

    public RelayCommand AddTabCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand MoveTabLeftCommand { get; }
    public RelayCommand MoveTabRightCommand { get; }
    public AsyncRelayCommand SignOutAllCommand { get; }
    public RelayCommand CloseAllDetailsCommand { get; }

    /// <summary>Object details windows open across every tab.</summary>
    public int OpenDetailsCount => DetailsWindows.Count;
    public bool HasOpenDetails => OpenDetailsCount > 0;
    public string CloseAllDetailsLabel => $"Close all details ({OpenDetailsCount})";
    public RelayCommand GlobalSearchCommand { get; }
    public RelayCommand CompareCommand { get; }
    public RelayCommand CompareDataCommand { get; }
    public RelayCommand ReadinessCommand { get; }

    /// <summary>Ctrl+K: every command, environment and object, by typing.</summary>
    public RelayCommand CommandPaletteCommand { get; }

    private void OpenCommandPalette()
    {
        var palette = new Views.CommandPaletteWindow { DataContext = new CommandPaletteViewModel(this) };
        palette.PlaceOver(Application.Current.MainWindow);
        palette.Show();
        palette.Activate();
    }
    public RelayCommand SetThemeCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }

    private static void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Log.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Log.Folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The log folder could not be opened: {ex.Message}\n\n{Log.Folder}",
                "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Light, Dark or System; applied at once and remembered in settings.</summary>
    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;

            _settings.Theme = value;
            _settings.Save();
            ThemeManager.Apply(value);

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSystemTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
        }
    }

    public bool IsSystemTheme => Theme == AppTheme.System;
    public bool IsLightTheme => Theme == AppTheme.Light;
    public bool IsDarkTheme => Theme == AppTheme.Dark;

    private void OpenGlobalSearch()
    {
        if (!RequireConnectedTabs(1, "Connect at least one environment first.")) return;

        new Views.GlobalSearchWindow
        {
            DataContext = new GlobalSearchViewModel(Sessions),
            Owner = Application.Current.MainWindow
        }.Show();
    }

    private void OpenCompare()
    {
        if (!RequireConnectedTabs(2, "Connect at least two environments to compare them.")) return;

        new Views.CompareWindow
        {
            DataContext = new CompareViewModel(Sessions),
            Owner = Application.Current.MainWindow
        }.Show();
    }

    /// <summary>
    /// Reference data is rows rather than components, so it has its own window: nothing it needs
    /// is in memory already, and which tables to read is a saved configuration rather than a
    /// property of the tabs.
    /// </summary>
    /// <param name="source">The tab whose solution to check, when opened from its Solution tools.</param>
    private void OpenReadiness(EnvironmentSessionViewModel? source)
    {
        if (!RequireConnectedTabs(2, "Connect the environment the solution comes from and the one it is going to.")) return;

        new Views.ReadinessWindow
        {
            DataContext = new ReadinessViewModel(Sessions, source),
            Owner = Application.Current.MainWindow
        }.Show();
    }

    private void OpenDataCompare()
    {
        if (!RequireConnectedTabs(2, "Connect at least two environments to compare their data.")) return;

        new Views.ReferenceDataCompareWindow
        {
            DataContext = new ReferenceDataCompareViewModel(Sessions, _settings),
            Owner = Application.Current.MainWindow
        }.Show();
    }

    private bool RequireConnectedTabs(int minimum, string message)
    {
        if (Sessions.Count(s => s.IsConnected) >= minimum) return true;

        MessageBox.Show(message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private EnvironmentSessionViewModel? _selectedSession;
    public EnvironmentSessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set => SetProperty(ref _selectedSession, value);
    }

    private void RestoreTabs()
    {
        var tabs = _settings.Tabs?.Where(t => !string.IsNullOrWhiteSpace(t.EnvironmentUrl)).ToList();

        if (tabs is { Count: > 0 })
        {
            foreach (var tab in tabs) Sessions.Add(CreateSession(tab));
        }
        else
        {
            Sessions.Add(CreateSession(null));
        }

        SelectedSession = Sessions[0];
    }

    private EnvironmentSessionViewModel CreateSession(TabState? state)
    {
        var session = new EnvironmentSessionViewModel(_auth, _settings, state);
        session.StateChanged += (_, _) => SaveTabs();

        // Tabs share one settings object, so another tab on the same environment is already
        // allowed or blocked - it only needs telling to show it.
        session.WriteAllowlistChanged += (_, _) =>
        {
            foreach (var other in Sessions) other.RaiseWriteAllowlist();
        };
        return session;
    }

    private void AddTab()
    {
        var session = CreateSession(null);
        Sessions.Add(session);
        SelectedSession = session;
        SaveTabs();
    }

    private void CloseTab(object? parameter)
    {
        if (parameter is not EnvironmentSessionViewModel session) return;

        var index = Sessions.IndexOf(session);
        if (index < 0) return;

        Sessions.Remove(session);
        session.Dispose();

        // Never leave the window empty - a closed last tab becomes a fresh one.
        if (Sessions.Count == 0) Sessions.Add(CreateSession(null));

        SelectedSession = Sessions[Math.Clamp(index, 0, Sessions.Count - 1)];
        SaveTabs();
        CloseTabCommand.RaiseCanExecuteChanged();
    }

    private void MoveSelectedTab(int offset)
    {
        if (SelectedSession is null) return;
        MoveTab(SelectedSession, Sessions.IndexOf(SelectedSession) + offset);
    }

    /// <summary>
    /// Tab order is the order saved to settings, so a moved tab stays where it was put.
    /// </summary>
    public void MoveTab(EnvironmentSessionViewModel session, int newIndex)
    {
        var oldIndex = Sessions.IndexOf(session);
        if (oldIndex < 0) return;

        newIndex = Math.Clamp(newIndex, 0, Sessions.Count - 1);
        if (newIndex == oldIndex) return;

        var selected = SelectedSession;
        Sessions.Move(oldIndex, newIndex);

        // The TabControl can drop its selection while the item moves - put it back.
        SelectedSession = selected;
        OnPropertyChanged(nameof(SelectedSession));

        SaveTabs();
    }

    private async Task SignOutAllAsync()
    {
        var confirm = MessageBox.Show(
            "Sign out of every account and clear all tabs' data?",
            "PPObjectSearch", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        await _auth.SignOutAllAsync();

        // Cached component lists are environment data - they go with the accounts.
        ComponentCache.Clear();

        foreach (var session in Sessions) session.Reset("Signed out.");
        SaveTabs();
    }

    public void SaveTabs()
    {
        _settings.Tabs = Sessions
            .Select(s => s.ToState())
            .Where(s => !string.IsNullOrWhiteSpace(s.EnvironmentUrl))
            .ToList();

        _settings.Save();
    }

    public void Shutdown()
    {
        SaveTabs();
        foreach (var session in Sessions) session.Dispose();
    }
}
