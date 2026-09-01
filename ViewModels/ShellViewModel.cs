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
        CloseTabCommand = new RelayCommand(CloseTab, p => Sessions.Count > 1 || p is not null);
        SignOutAllCommand = new AsyncRelayCommand(_ => SignOutAllAsync());

        RestoreTabs();
    }

    public ObservableCollection<EnvironmentSessionViewModel> Sessions { get; } = new();

    public RelayCommand AddTabCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public AsyncRelayCommand SignOutAllCommand { get; }

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

    private async Task SignOutAllAsync()
    {
        var confirm = MessageBox.Show(
            "Sign out of every account and clear all tabs' data?",
            "PPObjectSearch", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        await _auth.SignOutAllAsync();

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
