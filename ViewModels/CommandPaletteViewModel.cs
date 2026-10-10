using System.Collections.ObjectModel;
using System.Windows.Input;
using PPObjectSearch.Core;
using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

/// <summary>One thing the palette can do.</summary>
public sealed class PaletteItem
{
    public required string Title { get; init; }
    public required string Group { get; init; }
    public string? Shortcut { get; init; }
    public string? Detail { get; init; }
    public required ICommand Command { get; init; }
    public object? Parameter { get; init; }

    /// <summary>Search text: the title, then the group, so "admin" finds every admin view.</summary>
    internal string Haystack => $"{Title} {Group} {Detail}";
}

/// <summary>
/// Ctrl+K: one keyboard route to every command, every connected environment, the objects in the
/// current list, and recent ones - each with its shortcut shown, so the shortcuts are learnt.
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    private const int MaxResults = 60;

    private readonly IReadOnlyList<PaletteItem> _fixed;
    private readonly EnvironmentSessionViewModel? _session;

    public CommandPaletteViewModel(ShellViewModel shell)
    {
        _session = shell.SelectedSession is { IsConnected: true } s ? s : null;
        _fixed = BuildItems(shell, _session);
        Apply();
    }

    public ObservableCollection<PaletteItem> Results { get; } = new();

    private string _query = string.Empty;
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value)) Apply();
        }
    }

    private PaletteItem? _selected;
    public PaletteItem? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>Runs the selected item. True when something ran and the palette should close.</summary>
    public bool Execute()
    {
        if (Selected is not { } item || !item.Command.CanExecute(item.Parameter)) return false;
        item.Command.Execute(item.Parameter);
        return true;
    }

    public void Move(int by)
    {
        if (Results.Count == 0) return;
        var index = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(index + by, 0, Results.Count - 1)];
    }

    private void Apply()
    {
        var query = Query.Trim();
        IEnumerable<PaletteItem> candidates = _fixed;

        // Objects only once something is typed: a list of thousands is no help on its own.
        if (query.Length >= 2 && _session is not null) candidates = candidates.Concat(Objects(_session));

        var ranked = candidates
            .Select(item => (Item: item, Score: FuzzyMatch.Score(query, item.Title) ?? FuzzyMatch.Score(query, item.Haystack) / 2))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            .Take(MaxResults)
            .Select(x => x.Item)
            .ToList();

        Results.Clear();
        foreach (var item in ranked) Results.Add(item);
        Selected = Results.FirstOrDefault();
    }

    private static IEnumerable<PaletteItem> Objects(EnvironmentSessionViewModel session) =>
        session.Items.Select(item => new PaletteItem
        {
            Title = item.PrimaryLabel,
            Group = "Open",
            Detail = item.ComponentTypeName,
            Command = session.ShowDetailsCommand,
            Parameter = item
        });

    internal static IReadOnlyList<PaletteItem> BuildItems(ShellViewModel shell, EnvironmentSessionViewModel? session)
    {
        const string across = "Across environments";
        const string settings = "Settings";

        var items = new List<PaletteItem>
        {
            new() { Title = "Search all environments", Group = across, Shortcut = "Ctrl+Shift+F", Command = shell.GlobalSearchCommand },
            new() { Title = "Compare objects", Group = across, Shortcut = "Ctrl+D", Command = shell.CompareCommand },
            new() { Title = "Compare data", Group = across, Shortcut = "Ctrl+Shift+D", Command = shell.CompareDataCommand },
            new() { Title = "Readiness check", Group = across, Shortcut = "Ctrl+Shift+R", Command = shell.ReadinessCommand },
            new() { Title = "Close all details windows", Group = "Window", Shortcut = "Ctrl+Shift+W", Command = shell.CloseAllDetailsCommand },
            new() { Title = "New tab", Group = "Window", Shortcut = "Ctrl+T", Command = shell.AddTabCommand },
            new() { Title = "Theme: light", Group = settings, Command = shell.SetThemeCommand, Parameter = "Light" },
            new() { Title = "Theme: dark", Group = settings, Command = shell.SetThemeCommand, Parameter = "Dark" },
            new() { Title = "Theme: follow Windows", Group = settings, Command = shell.SetThemeCommand, Parameter = "System" },
            new() { Title = "Open log folder", Group = settings, Command = shell.OpenLogFolderCommand }
        };

        foreach (var other in shell.Sessions.Where(s => !ReferenceEquals(s, shell.SelectedSession)))
        {
            items.Add(new PaletteItem
            {
                Title = $"Switch to {other.Title}", Group = "Environments", Detail = other.EnvironmentHost,
                Command = new RelayCommand(_ => shell.SelectedSession = other)
            });
        }

        if (session is null) return items;

        var env = session.Title;
        var admin = env + " admin";
        items.AddRange(
        [
            new() { Title = "Refresh", Group = env, Shortcut = "F5", Command = session.RefreshCommand },
            new() { Title = "Search inside definitions", Group = env, Shortcut = "Ctrl+Shift+U", Command = session.ContentSearchCommand },
            new() { Title = "Solution history", Group = env, Command = session.SolutionHistoryCommand },
            // An import log belongs to one import, so this opens the history to pick it from.
            new() { Title = "Import log…", Group = env, Detail = "choose an import in Solution history", Command = session.SolutionHistoryCommand },
            new() { Title = "Recent changes", Group = env, Command = session.RecentChangesCommand },
            new() { Title = "Failures", Group = env, Command = session.FailuresCommand },
            new() { Title = "Users", Group = admin, Command = session.EnvironmentAdminCommand, Parameter = "Users" },
            new() { Title = "Security roles", Group = admin, Command = session.EnvironmentAdminCommand, Parameter = "Roles" },
            new() { Title = "Mailboxes", Group = admin, Command = session.EnvironmentAdminCommand, Parameter = "Mailboxes" },
            new() { Title = "Queues", Group = admin, Command = session.EnvironmentAdminCommand, Parameter = "Queues" },
            new() { Title = "Security lookup", Group = admin, Command = session.SecurityLookupCommand },
            new() { Title = "Entra team sync", Group = env, Detail = "writes", Command = session.EntraTeamSyncCommand },
            new() { Title = "Queue membership sync", Group = env, Detail = "writes", Command = session.QueueSyncCommand },
            new() { Title = "Deployment settings…", Group = env, Detail = "settings file for pac solution import", Command = session.ExportDeploymentSettingsCommand },
            new() { Title = "Documentation…", Group = env, Detail = "document this solution as Markdown", Command = session.DocumentSolutionCommand },
            new() { Title = "Export CSV", Group = env, Command = session.ExportCsvCommand },
            new() { Title = "Check layers", Group = env, Command = session.CheckUnmanagedLayersCommand },
            new() { Title = "Turn on every flow listed that is off", Group = env, Detail = "writes", Command = session.TurnOnSolutionFlowsCommand },
            new() { Title = "Save this search", Group = env, Command = session.SaveSearchCommand },
            new() { Title = "Show or hide the detail pane", Group = env, Command = session.ToggleDetailPaneCommand },
            new() { Title = session.IsWriteAllowlisted ? "Stop allowing writes here" : "Allow writes here", Group = env, Detail = "production guard",
                    Command = session.IsWriteAllowlisted ? session.RevokeWritesCommand : session.AllowWritesCommand },
            new() { Title = "Switch account", Group = env, Command = session.SwitchAccountCommand },
            new() { Title = "Disconnect", Group = env, Command = session.DisconnectCommand }
        ]);

        if (session.SelectedItem is { } selected)
        {
            items.Add(new PaletteItem
            {
                Title = "Dependency explorer…", Group = env, Detail = selected.PrimaryLabel,
                Command = session.ExploreDependenciesCommand
            });
        }

        foreach (var recent in session.RecentObjects)
        {
            items.Add(new PaletteItem
            {
                Title = recent.Label, Group = "Recent", Detail = recent.TypeName,
                Command = session.OpenRecentCommand, Parameter = recent
            });
        }

        foreach (var search in session.SavedSearches)
        {
            items.Add(new PaletteItem
            {
                Title = $"Saved search: {search.Name}", Group = env, Command = session.ApplySavedSearchCommand, Parameter = search
            });
        }

        return items;
    }
}
