using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>The views of the table settings window.</summary>
public enum TableSettingsTab
{
    Tables,
    Columns,
    Gaps,
    Links,
    Search,
    Environments
}

/// <summary>A connected environment, and whether the review reads it.</summary>
public sealed class EnvironmentChoice : ObservableObject
{
    public required EnvironmentSessionViewModel Session { get; init; }
    public string Title => Session.Title;
    public Dataverse.EnvironmentSku EnvironmentSku => Session.EnvironmentSku;

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>A solution offered for the review's scope.</summary>
public sealed class SolutionChoice : ObservableObject
{
    public required SolutionInfo Solution { get; init; }
    public string Label => Solution.DisplayLabel;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>A data lake folder links write to, and whether a link to it counts as "linked".</summary>
public sealed class FolderChoice : ObservableObject
{
    public required string Name { get; init; }

    private bool _isCounted = true;
    public bool IsCounted
    {
        get => _isCounted;
        set => SetProperty(ref _isCounted, value);
    }
}

/// <summary>
/// The table settings window: how tables are set up for audit, change tracking, Synapse Link and
/// Dataverse search in every chosen environment, side by side, with what differs called out. It
/// only reads. Every view is cut from snapshots taken by one run, so switching setting, filter
/// or ignored link folders re-draws without asking any environment again.
/// </summary>
public sealed class TableSettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _save;
    private CancellationTokenSource? _cts;

    private IReadOnlyList<TableSettingsSnapshot> _snapshots = [];
    private IReadOnlyList<string> _scopeTables = [];

    private List<SettingRow> _allTables = new();
    private List<SettingRow> _allColumns = new();
    private List<AuditGap> _allGaps = new();
    private List<LinkRow> _allLinks = new();
    private List<SearchIndexRow> _allSearch = new();
    private List<SettingRow> _allEnvironments = new();

    /// <param name="current">The tab it was opened from, whose solution is the starting scope.</param>
    /// <param name="save">Saves the remembered scope; the settings file by default, replaced in tests.</param>
    public TableSettingsViewModel(
        IEnumerable<EnvironmentSessionViewModel> sessions,
        AppSettings settings,
        EnvironmentSessionViewModel? current = null,
        Action<AppSettings>? save = null)
    {
        _settings = settings;
        _save = save ?? (s => s.Save());

        var connected = sessions.Where(s => s.IsConnected).ToList();
        foreach (var session in connected)
        {
            var choice = new EnvironmentChoice { Session = session };
            choice.PropertyChanged += (_, _) =>
            {
                RunCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(EmptyText));
            };
            Environments.Add(choice);
        }

        ScopeSessions = new ObservableCollection<EnvironmentSessionViewModel>(connected);

        RunCommand = new AsyncRelayCommand(_ => RunAsync(), _ => CanRun);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);
        ExportCommand = new RelayCommand(_ => Export(), _ => HasResults);
        ClearFilterCommand = new RelayCommand(_ => FilterText = string.Empty);

        var preferences = settings.TableSettings ?? new TableSettingsPreferences();
        _scope = preferences.Scope;

        _scopeSession = (current is not null && connected.Contains(current) ? current : null)
                        ?? connected.FirstOrDefault(s => s.SelectedSolution is { IsDefaultSolution: false })
                        ?? connected.FirstOrDefault();
        LoadSolutionChoices(preferences.Solutions);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand ClearFilterCommand { get; }

    // ---------------------------------------------------------------- what to read

    public ObservableCollection<EnvironmentChoice> Environments { get; } = new();

    /// <summary>Where solution membership is read from - the environment that masters the list.</summary>
    public ObservableCollection<EnvironmentSessionViewModel> ScopeSessions { get; }

    private EnvironmentSessionViewModel? _scopeSession;
    public EnvironmentSessionViewModel? ScopeSession
    {
        get => _scopeSession;
        set
        {
            if (!SetProperty(ref _scopeSession, value)) return;
            LoadSolutionChoices(SolutionChoices.Where(c => c.IsSelected).Select(c => c.Solution.UniqueName).ToList());
        }
    }

    private TableScope _scope;
    public TableScope Scope
    {
        get => _scope;
        set
        {
            if (!SetProperty(ref _scope, value)) return;
            OnPropertyChanged(nameof(IsSolutionScope));
            OnPropertyChanged(nameof(EmptyText));
            RunCommand.RaiseCanExecuteChanged();
            Remember();
        }
    }

    public bool IsSolutionScope => Scope == TableScope.Solutions;

    public static IReadOnlyList<KeyValuePair<TableScope, string>> Scopes { get; } =
    [
        new(TableScope.Solutions, "In solutions"),
        new(TableScope.CustomTables, "Every custom table"),
        new(TableScope.AllTables, "Every table")
    ];

    public ObservableCollection<SolutionChoice> SolutionChoices { get; } = new();

    /// <summary>"Contoso Core + 1" - what the solutions button says.</summary>
    public string SolutionsLabel
    {
        get
        {
            var chosen = SolutionChoices.Where(c => c.IsSelected).ToList();
            return chosen.Count switch
            {
                0 => "Choose solutions",
                1 => chosen[0].Solution.FriendlyName,
                _ => $"{chosen[0].Solution.FriendlyName} + {chosen.Count - 1}"
            };
        }
    }

    private void LoadSolutionChoices(IReadOnlyCollection<string>? selected)
    {
        SolutionChoices.Clear();

        var source = ScopeSession;
        if (source is not null)
        {
            var wanted = new HashSet<string>(selected ?? [], StringComparer.OrdinalIgnoreCase);

            // Nothing remembered: start from the solution the tab has open.
            if (wanted.Count == 0 && source.SelectedSolution is { IsDefaultSolution: false } open) wanted.Add(open.UniqueName);

            foreach (var solution in source.Solutions.Where(s => !s.IsDefaultSolution)
                         .OrderBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
            {
                var choice = new SolutionChoice { Solution = solution, IsSelected = wanted.Contains(solution.UniqueName) };
                choice.PropertyChanged += (_, _) =>
                {
                    OnPropertyChanged(nameof(SolutionsLabel));
                    OnPropertyChanged(nameof(EmptyText));
                    RunCommand.RaiseCanExecuteChanged();
                    Remember();
                };
                SolutionChoices.Add(choice);
            }
        }

        OnPropertyChanged(nameof(SolutionsLabel));
        OnPropertyChanged(nameof(EmptyText));
        RunCommand?.RaiseCanExecuteChanged();
    }

    private bool _includeColumns = true;
    /// <summary>Column audit costs a read per audited table per environment, so it can be left out.</summary>
    public bool IncludeColumns
    {
        get => _includeColumns;
        set => SetProperty(ref _includeColumns, value);
    }

    // ---------------------------------------------------------------- how to show it

    private TableSettingsTab _selectedTab;
    public TableSettingsTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value)) return;
            OnPropertyChanged(nameof(ShowsSetting));
            OnPropertyChanged(nameof(CountLabel));
        }
    }

    /// <summary>The setting picker only means something on the Tables view.</summary>
    public bool ShowsSetting => SelectedTab == TableSettingsTab.Tables;

    private TableSettingKind _kind = TableSettingKind.All;
    public TableSettingKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            RebuildTables();
        }
    }

    public static IReadOnlyList<KeyValuePair<TableSettingKind, string>> Kinds { get; } =
    [
        new(TableSettingKind.All, "All settings"),
        new(TableSettingKind.Audit, "Audit"),
        new(TableSettingKind.ChangeTracking, "Change tracking"),
        new(TableSettingKind.SynapseLink, "Synapse Link"),
        new(TableSettingKind.Search, "Dataverse search")
    ];

    private bool _findingsOnly;
    /// <summary>Only rows that differ between environments, or that something is wrong with.</summary>
    public bool FindingsOnly
    {
        get => _findingsOnly;
        set
        {
            if (SetProperty(ref _findingsOnly, value)) Refill();
        }
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? string.Empty)) Refill();
        }
    }

    /// <summary>Link folders found by the last run; one left unticked no longer counts as linked.</summary>
    public ObservableCollection<FolderChoice> Folders { get; } = new();

    public bool HasFolders => Folders.Count > 0;

    private HashSet<string> IgnoredFolders() =>
        Folders.Where(f => !f.IsCounted).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- results

    /// <summary>The environments of the last run, in column order. The window builds a column for each.</summary>
    private IReadOnlyList<string> _resultEnvironments = [];
    public IReadOnlyList<string> ResultEnvironments
    {
        get => _resultEnvironments;
        private set => SetProperty(ref _resultEnvironments, value);
    }

    public ObservableCollection<SettingRow> TableRows { get; } = new();
    public ObservableCollection<SettingRow> ColumnRows { get; } = new();
    public ObservableCollection<AuditGap> GapRows { get; } = new();
    public ObservableCollection<LinkRow> LinkRows { get; } = new();
    public ObservableCollection<SearchIndexRow> SearchRows { get; } = new();
    public ObservableCollection<SettingRow> EnvironmentRows { get; } = new();

    public bool HasResults => _snapshots.Count > 0;

    public int TableFindings => _allTables.Count(IsFinding);
    public int ColumnFindings => _allColumns.Count(IsFinding);
    public int GapCount => _allGaps.Count;
    public int LinkProblems => _allLinks.Count(l => l.HasProblem);
    public int SearchProblems => _allSearch.Count(s => s.HasProblem);
    public int EnvironmentFindings => _allEnvironments.Count(IsFinding);

    /// <summary>The status bar's right side: what the current view lists.</summary>
    public string CountLabel => !HasResults ? string.Empty : SelectedTab switch
    {
        TableSettingsTab.Tables => Count(TableRows.Count, "table"),
        TableSettingsTab.Columns => Count(ColumnRows.Count, "column"),
        TableSettingsTab.Gaps => Count(GapRows.Count, "gap"),
        TableSettingsTab.Links => Count(LinkRows.Count, "linked table"),
        TableSettingsTab.Search => Count(SearchRows.Count, "indexed table"),
        _ => Count(EnvironmentRows.Count, "setting")
    };

    private static string Count(int n, string word) => $"{n:N0} {word}{(n == 1 ? string.Empty : "s")}";

    private static bool IsFinding(SettingRow row) => !row.IsConsistent || row.HasProblem;

    private string _problems = string.Empty;
    /// <summary>What could not be read, environment by environment - so silence is not taken for a pass.</summary>
    public string Problems
    {
        get => _problems;
        private set
        {
            if (SetProperty(ref _problems, value)) OnPropertyChanged(nameof(HasProblems));
        }
    }

    public bool HasProblems => Problems.Length > 0;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    private string _readSummary = string.Empty;
    public string ReadSummary
    {
        get => _readSummary;
        private set
        {
            if (SetProperty(ref _readSummary, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    public string StatusLine => string.IsNullOrEmpty(Status) ? ReadSummary : Status;

    /// <summary>Nothing read yet and nothing running - the empty state shows in place of the views.</summary>
    public bool IsEmpty => !IsRunning && !HasResults;

    public string EmptyText
    {
        get
        {
            if (!string.IsNullOrEmpty(Status)) return Status;
            if (!Environments.Any(e => e.IsSelected)) return "Choose at least one environment to read.";
            if (IsSolutionScope && !SolutionChoices.Any(c => c.IsSelected))
            {
                return ScopeSession is null
                    ? "Choose the environment whose solutions decide which tables are covered."
                    : $"Choose the solutions in {ScopeSession.Title} whose tables to cover, or cover every custom table instead.";
            }

            return "Check reads each environment's tables, links and search index - nothing is written.";
        }
    }

    // ---------------------------------------------------------------- run

    private bool CanRun =>
        !IsRunning &&
        Environments.Any(e => e.IsSelected && e.Session.Client is not null) &&
        (!IsSolutionScope || (ScopeSession?.Client is not null && SolutionChoices.Any(c => c.IsSelected)));

    private async Task RunAsync()
    {
        if (!CanRun) return;

        var chosen = Environments.Where(e => e.IsSelected && e.Session.Client is not null).Select(e => e.Session).ToList();
        var solutions = SolutionChoices.Where(c => c.IsSelected).Select(c => c.Solution).ToList();
        var scopeSession = ScopeSession;
        var scope = Scope;
        var columns = IncludeColumns;

        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;

        try
        {
            var clock = Stopwatch.StartNew();
            Status = $"Reading {Count(chosen.Count, "environment")}...";

            var snapshots = await Task.WhenAll(chosen.Select(s =>
                TableSettingsReader.ReadAsync(s.Title, s.Client!, cts.Token)));

            Status = "Working out which tables are covered...";
            var tables = await ResolveScopeAsync(snapshots, chosen, scope, scopeSession, solutions, cts.Token);

            if (columns)
            {
                var audited = TableSettingsReader.AuditedTables(snapshots, tables);
                if (audited.Count > 0)
                {
                    Status = $"Reading the columns of {Count(audited.Count, "audited table")} in each environment...";
                    await Task.WhenAll(snapshots.Select((snapshot, i) =>
                        TableSettingsReader.ReadColumnsAsync(snapshot, chosen[i].Client!, audited, cts.Token)));
                }
            }

            _snapshots = snapshots;
            _scopeTables = tables;
            ResultEnvironments = snapshots.Select(s => s.Environment).ToList();

            LoadFolders();
            Rebuild();

            Problems = string.Join("\n", snapshots.SelectMany(s => s.Problems.Select(p => $"{s.Environment}: {p}")));
            ReadSummary = $"Read {Count(tables.Count, "table")} in {Count(snapshots.Length, "environment")} in " +
                          $"{clock.Elapsed.TotalSeconds:0.0} s - {Count(TableFindings, "table")} differ" +
                          (TableFindings == 1 ? "s" : string.Empty) +
                          $", {Count(GapCount, "unaudited column")}, {Count(LinkProblems + SearchProblems, "sync problem")}";
            Status = string.Empty;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Log.Error("Table settings review could not run", ex);
            Status = "The review could not run - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            IsRunning = false;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(IsEmpty));
            ExportCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// The tables the review covers. Solution membership is mastered by one environment rather than
    /// taken from each - a solution is not deployed everywhere its tables are, and scoping each
    /// environment to its own membership would quietly drop a table that is missing somewhere,
    /// which is exactly the finding worth seeing.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveScopeAsync(
        IReadOnlyList<TableSettingsSnapshot> snapshots,
        List<EnvironmentSessionViewModel> chosen,
        TableScope scope,
        EnvironmentSessionViewModel? scopeSession,
        IReadOnlyList<SolutionInfo> solutions,
        CancellationToken ct)
    {
        if (scope == TableScope.AllTables)
        {
            return snapshots.SelectMany(s => s.Tables.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (scope == TableScope.CustomTables)
        {
            return snapshots.SelectMany(s => s.Tables.Values).Where(t => t.IsCustom)
                .Select(t => t.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var client = scopeSession!.Client!;

        // A solution lists its tables by metadata id; the scope environment's metadata names them.
        var index = chosen.IndexOf(scopeSession);
        var tables = index >= 0 && snapshots[index].TablesRead
            ? snapshots[index].Tables.Values
            : await client.GetTableSettingsAsync(ct);
        var byId = tables.Where(t => t.MetadataId != Guid.Empty)
            .GroupBy(t => t.MetadataId).ToDictionary(g => g.Key, g => g.First().LogicalName);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var solution in solutions)
        {
            var components = await client.GetSolutionComponentsAsync(solution.SolutionId, ct: ct);
            foreach (var table in components.Where(c => c.ComponentType == 1))
            {
                if (byId.TryGetValue(table.ObjectId, out var name)) names.Add(name);
            }
        }

        return names.ToList();
    }

    private void LoadFolders()
    {
        var ignored = (_settings.TableSettings?.IgnoredLinkFolders ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Folders.Clear();
        foreach (var name in TableSettingsReview.Folders(_snapshots))
        {
            var folder = new FolderChoice { Name = name, IsCounted = !ignored.Contains(name) };
            folder.PropertyChanged += (_, _) =>
            {
                Remember();
                RebuildTables();
                RebuildLinks();
            };
            Folders.Add(folder);
        }

        OnPropertyChanged(nameof(HasFolders));
    }

    private void Rebuild()
    {
        _allColumns = TableSettingsReview.Columns(_snapshots, _scopeTables).ToList();
        _allGaps = TableSettingsReview.Gaps(_snapshots, _scopeTables).ToList();
        _allSearch = TableSettingsReview.SearchIndex(_snapshots).ToList();
        _allEnvironments = TableSettingsReview.Environments(_snapshots).ToList();
        RebuildLinks();
        RebuildTables();
    }

    private void RebuildTables()
    {
        _allTables = TableSettingsReview.Tables(_snapshots, _scopeTables, Kind, IgnoredFolders()).ToList();
        Refill();
    }

    private void RebuildLinks()
    {
        _allLinks = TableSettingsReview.Links(_snapshots, IgnoredFolders()).ToList();
        Refill();
    }

    /// <summary>Every view again, through the filter and the findings toggle.</summary>
    private void Refill()
    {
        var words = FilterText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        bool Matches(params string?[] fields) =>
            words.All(w => fields.Any(f => f?.Contains(w, StringComparison.CurrentCultureIgnoreCase) == true));

        Fill(TableRows, _allTables.Where(r => (!FindingsOnly || IsFinding(r)) && Matches(r.Name, r.Detail)));
        Fill(ColumnRows, _allColumns.Where(r => (!FindingsOnly || IsFinding(r)) && Matches(r.Name, r.Detail)));
        Fill(GapRows, _allGaps.Where(g => Matches(g.Environment, g.Table, g.TableLabel, g.Column, g.ColumnLabel)));
        Fill(LinkRows, _allLinks.Where(l => (!FindingsOnly || l.HasProblem) &&
                                            Matches(l.Environment, l.Table.Table, l.Profile.Name, l.Profile.FolderLabel)));
        Fill(SearchRows, _allSearch.Where(s => (!FindingsOnly || s.HasProblem) && Matches(s.Environment, s.Table, s.TableLabel)));
        Fill(EnvironmentRows, _allEnvironments.Where(r => (!FindingsOnly || IsFinding(r)) && Matches(r.Name, r.Detail)));

        OnPropertyChanged(nameof(TableFindings));
        OnPropertyChanged(nameof(ColumnFindings));
        OnPropertyChanged(nameof(GapCount));
        OnPropertyChanged(nameof(LinkProblems));
        OnPropertyChanged(nameof(SearchProblems));
        OnPropertyChanged(nameof(EnvironmentFindings));
        OnPropertyChanged(nameof(CountLabel));
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    private void Remember()
    {
        _settings.TableSettings = new TableSettingsPreferences
        {
            Scope = Scope,
            Solutions = SolutionChoices.Where(c => c.IsSelected).Select(c => c.Solution.UniqueName).ToList(),
            // Folders not seen this run are kept: they may be in an environment not read this time.
            IgnoredLinkFolders = (_settings.TableSettings?.IgnoredLinkFolders ?? [])
                .Where(name => !Folders.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                .Concat(Folders.Where(f => !f.IsCounted).Select(f => f.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        try
        {
            _save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Table settings preferences could not be saved", ex);
        }
    }

    // ---------------------------------------------------------------- export

    /// <summary>The current view as CSV lines - what Export writes.</summary>
    internal IReadOnlyList<string> ExportLines()
    {
        var environments = ResultEnvironments;

        IEnumerable<string> Matrix(IEnumerable<SettingRow> rows, string name, string detail)
        {
            string?[] header = [name, detail, .. environments, "Consistent", "Difference"];
            yield return CsvExporter.Line(header);

            foreach (var r in rows)
            {
                string?[] values = [r.Name, r.Detail, .. r.Cells.Select(c => c.Text), r.IsConsistent ? "Yes" : "No", r.Difference];
                yield return CsvExporter.Line(values);
            }
        }

        return SelectedTab switch
        {
            TableSettingsTab.Tables => Matrix(TableRows, "Table", "Display name").ToList(),
            TableSettingsTab.Columns => Matrix(ColumnRows, "Column", "Table").ToList(),
            TableSettingsTab.Environments => Matrix(EnvironmentRows, "Setting", "Meaning").ToList(),
            TableSettingsTab.Gaps =>
            [
                CsvExporter.Line("Environment", "Table", "Table display name", "Column", "Column display name", "Managed"),
                .. GapRows.Select(g => CsvExporter.Line(g.Environment, g.Table, g.TableLabel, g.Column, g.ColumnLabel, g.IsManaged ? "Yes" : "No"))
            ],
            TableSettingsTab.Links =>
            [
                CsvExporter.Line("Environment", "Profile", "Folder", "Profile state", "Table", "Sync state", "Initial sync",
                    "Lake rows", "Dataverse rows", "Last synced", "Counted", "Problem"),
                .. LinkRows.Select(l => CsvExporter.Line(l.Environment, l.Profile.Name, l.Profile.FolderLabel, l.Profile.State,
                    l.Table.Table, l.Table.SyncState, l.Table.InitialSyncState, l.Table.LakeRecordCount?.ToString(),
                    l.Table.SourceRecordCount?.ToString(), l.Table.LastSynced?.ToString("u"), l.IsCounted ? "Yes" : "No", l.Problem))
            ],
            _ =>
            [
                CsvExporter.Line("Environment", "Table", "Display name", "Search on", "Index status", "Last synced",
                    "Indexed columns", "Problem"),
                .. SearchRows.Select(s => CsvExporter.Line(s.Environment, s.Table, s.TableLabel,
                    s.IsEnabled switch { true => "Yes", false => "No", null => "" }, s.StatusLabel,
                    s.LastSynced?.ToString("u"), s.ColumnsLabel, s.Problem))
            ]
        };
    }

    private void Export()
    {
        if (!HasResults) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"table-settings-{SelectedTab.ToString().ToLowerInvariant()}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            CsvExporter.WriteLines(dialog.FileName, ExportLines());
            Status = string.Empty;
            ReadSummary = $"Exported to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not export - " + ex.Message;
        }
    }
}
