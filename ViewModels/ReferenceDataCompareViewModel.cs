using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One configured table in the left-hand list.</summary>
public sealed class ReferenceEntityViewModel : ObservableObject
{
    public ReferenceEntityViewModel(ReferenceEntityConfig config, EntitySummary? entity = null)
    {
        Config = config;
        Entity = entity;
    }

    public ReferenceEntityConfig Config { get; }

    /// <summary>Known once the source environment's table list has been read; a restored
    /// configuration starts without it.</summary>
    public EntitySummary? Entity { get; set; }

    public string LogicalName => Config.LogicalName ?? "(unnamed)";

    public string Label => Entity?.Label
                           ?? (string.IsNullOrWhiteSpace(Config.DisplayName)
                               ? LogicalName
                               : $"{Config.DisplayName} ({LogicalName})");

    public bool IsEnabled
    {
        get => Config.IsEnabled;
        set
        {
            if (Config.IsEnabled == value) return;

            Config.IsEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResultLabel));
            OnPropertyChanged(nameof(Result));
        }
    }

    private int? _differenceCount;
    /// <summary>Rows needing a change in the last comparison; null until this table has been compared.</summary>
    public int? DifferenceCount
    {
        get => _differenceCount;
        set
        {
            if (!SetProperty(ref _differenceCount, value)) return;
            OnPropertyChanged(nameof(ResultLabel));
            OnPropertyChanged(nameof(Result));
        }
    }

    /// <summary>Drives the colour of <see cref="ResultLabel"/>: "Differences", "Match", "Off" or "None".</summary>
    public string Result => (IsEnabled, DifferenceCount) switch
    {
        (false, _) => "Off",
        (_, null) => "None",
        (_, 0) => "Match",
        _ => "Differences"
    };

    /// <summary>"8 diff", "Match" or "Off", at the right of the table list.</summary>
    public string ResultLabel => Result switch
    {
        "Off" => "Off",
        "Match" => "Match",
        "Differences" => $"{DifferenceCount:N0} diff",
        _ => string.Empty
    };

    public string KeyDescription => Config.KeySource switch
    {
        RecordKeySource.AlternateKey => "Key: " + (Config.AlternateKeyName ?? "alternate key"),
        RecordKeySource.Columns => "Key: " + string.Join(" + ", Config.KeyColumns ?? new List<string>()),
        _ => "Key: primary id"
    };

    public string Detail
    {
        get
        {
            var parts = new List<string> { KeyDescription };

            if (!string.IsNullOrWhiteSpace(Config.Filter)) parts.Add("filter: " + Config.Filter);

            if (Config.ExcludedColumns is { Count: > 0 } excluded)
            {
                parts.Add($"{excluded.Count} column(s) excluded");
            }
            else if (Config.ExcludedColumns is null)
            {
                parts.Add("default columns");
            }

            return string.Join(" · ", parts);
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(KeyDescription));
        OnPropertyChanged(nameof(Detail));
    }
}

/// <summary>The segmented filter above the results. Matches replaces the old "Show matching rows".</summary>
public enum RecordStatusFilter
{
    Differences,
    OnlySource,
    OnlyTarget,
    Different,
    Matches
}

/// <summary>
/// Compares the rows of chosen tables between two environments - what is in one and not the other,
/// and what carries different values in each.
///
/// Unlike the solution component comparison, nothing here is already in memory: the tables, their
/// metadata and their rows are all read when Compare runs. Which tables to read, keyed on what,
/// is a configuration that can be saved and picked again.
/// </summary>
public sealed class ReferenceDataCompareViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly List<RecordComparison> _all = new();

    /// <summary>What the last run read, kept so the comparison can be re-judged without asking
    /// both environments for the same rows again.</summary>
    private readonly List<FetchedEntity> _fetched = new();
    private readonly List<string> _fetchWarnings = new();

    private IReadOnlyList<EntitySummary>? _sourceEntities;
    private DataverseClient? _sourceEntitiesFrom;
    private CancellationTokenSource? _cts;

    /// <summary>The target's tables, needed to write to it - to name its entity sets, and to
    /// resolve a lookup label to a row that actually exists there.</summary>
    private IReadOnlyDictionary<string, EntitySummary>? _targetEntities;
    private DataverseClient? _targetEntitiesFrom;

    /// <summary>Whether the target may be written to, re-asked whenever the target changes.</summary>
    private WritePermission? _permission;

    public ReferenceDataCompareViewModel(IEnumerable<EnvironmentSessionViewModel> sessions, AppSettings settings)
    {
        _settings = settings;

        Sessions = new ObservableCollection<EnvironmentSessionViewModel>(sessions.Where(s => s.IsConnected));

        Rows = new ObservableCollection<RecordComparison>();
        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;

        Configurations = new ObservableCollection<ReferenceDataConfig>(
            (settings.ReferenceDataConfigurations ?? new List<ReferenceDataConfig>())
            .Select(c => c.Clone()));

        CompareCommand = new AsyncRelayCommand(_ => CompareAsync(), _ => CanCompare);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        AddEntitiesCommand = new AsyncRelayCommand(_ => AddEntitiesAsync(), _ => Source is not null);
        EditEntityCommand = new AsyncRelayCommand(p => EditEntityAsync(p as ReferenceEntityViewModel ?? SelectedEntity),
            p => Source is not null && (p as ReferenceEntityViewModel ?? SelectedEntity) is not null);
        RemoveEntityCommand = new RelayCommand(p => RemoveEntity(p as ReferenceEntityViewModel ?? SelectedEntity),
            p => (p as ReferenceEntityViewModel ?? SelectedEntity) is not null);

        NewConfigurationCommand = new RelayCommand(_ => NewConfiguration());
        SaveConfigurationCommand = new RelayCommand(_ => SaveConfiguration());
        RenameConfigurationCommand = new RelayCommand(_ => RenameConfiguration(), _ => SelectedConfiguration is not null);
        DeleteConfigurationCommand = new RelayCommand(_ => DeleteConfiguration(), _ => SelectedConfiguration is not null);
        ToggleWarningsCommand = new RelayCommand(_ => ShowAllWarnings = !ShowAllWarnings);

        Warnings.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WarningsTitle));
            OnPropertyChanged(nameof(WarningsPreview));
        };
        Entities.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TablesHeading));

        ExportCommand = new RelayCommand(_ => Export(), _ => _all.Count > 0);
        ReconcileCommand = new AsyncRelayCommand(_ => ReconcileAsync(), _ => SelectedRows.Count > 0);

        _source = Sessions.FirstOrDefault();
        _target = Sessions.Skip(1).FirstOrDefault();

        if (Configurations.Count > 0) SelectedConfiguration = Configurations[0];
    }

    public ObservableCollection<EnvironmentSessionViewModel> Sessions { get; }
    public ObservableCollection<ReferenceEntityViewModel> Entities { get; } = new();
    public ObservableCollection<RecordComparison> Rows { get; }
    public ListCollectionView RowsView { get; }
    public ObservableCollection<ColumnComparison> DetailColumns { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<ReferenceDataConfig> Configurations { get; }
    public ObservableCollection<string> EntityFilters { get; } = new();

    public AsyncRelayCommand CompareCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand AddEntitiesCommand { get; }
    public AsyncRelayCommand EditEntityCommand { get; }
    public RelayCommand RemoveEntityCommand { get; }
    public RelayCommand NewConfigurationCommand { get; }
    public RelayCommand SaveConfigurationCommand { get; }
    public RelayCommand RenameConfigurationCommand { get; }
    public RelayCommand DeleteConfigurationCommand { get; }
    public RelayCommand ToggleWarningsCommand { get; }
    public RelayCommand ExportCommand { get; }
    public AsyncRelayCommand ReconcileCommand { get; }

    /// <summary>"Tables · 4 of 5 checked".</summary>
    public string TablesHeading => Entities.Count == 0
        ? "Tables"
        : $"Tables · {Entities.Count(e => e.IsEnabled)} of {Entities.Count} checked";

    private bool _isDirty;
    /// <summary>The configuration on screen has changes that Save would keep.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    private bool _showAllWarnings;
    public bool ShowAllWarnings
    {
        get => _showAllWarnings;
        set => SetProperty(ref _showAllWarnings, value);
    }

    public string WarningsTitle => Warnings.Count == 1
        ? "1 warning — the result may be partial"
        : $"{Warnings.Count} warnings — the result may be partial";

    /// <summary>The first couple of warnings, inline in the banner.</summary>
    public string WarningsPreview => string.Join(" · ", Warnings.Take(2));

    private bool CanCompare =>
        Source is not null && Target is not null && Source != Target &&
        Entities.Any(e => e.IsEnabled);

    private EnvironmentSessionViewModel? _source;
    public EnvironmentSessionViewModel? Source
    {
        get => _source;
        set
        {
            if (!SetProperty(ref _source, value)) return;

            // The table list belongs to an environment, not to the window.
            _sourceEntities = null;
            _sourceEntitiesFrom = null;

            OnPropertyChanged(nameof(SourceHeader));
            RaiseCommandStates();
        }
    }

    private EnvironmentSessionViewModel? _target;
    public EnvironmentSessionViewModel? Target
    {
        get => _target;
        set
        {
            if (!SetProperty(ref _target, value)) return;

            // Permission and table list belong to an environment, not to the window.
            _targetEntities = null;
            _targetEntitiesFrom = null;
            _permission = null;

            OnPropertyChanged(nameof(TargetHeader));
            OnPropertyChanged(nameof(WriteStatus));
            RaiseCommandStates();
        }
    }

    public string SourceHeader => Source?.Title ?? "Source";
    public string TargetHeader => Target?.Title ?? "Target";

    private ReferenceEntityViewModel? _selectedEntity;
    public ReferenceEntityViewModel? SelectedEntity
    {
        get => _selectedEntity;
        set
        {
            if (SetProperty(ref _selectedEntity, value)) RaiseCommandStates();
        }
    }

    private RecordComparison? _selectedRow;
    public RecordComparison? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;

            _detailAll = value?.AllColumns() ?? Array.Empty<ColumnComparison>();
            RebuildDetail();

            OnPropertyChanged(nameof(DetailHeading));
            OnPropertyChanged(nameof(DetailKey));
        }
    }

    private IReadOnlyList<ColumnComparison> _detailAll = Array.Empty<ColumnComparison>();

    private bool _differencesOnly = true;
    /// <summary>Hides the matching columns of the selected row, which are most of them.</summary>
    public bool DifferencesOnly
    {
        get => _differencesOnly;
        set
        {
            if (SetProperty(ref _differencesOnly, value)) RebuildDetail();
        }
    }

    private void RebuildDetail()
    {
        DetailColumns.Clear();

        foreach (var column in _detailAll.Where(c => !DifferencesOnly || c.IsDifferent))
        {
            DetailColumns.Add(column);
        }

        OnPropertyChanged(nameof(DetailCounts));
    }

    /// <summary>"3 of 14 columns differ".</summary>
    public string DetailCounts => SelectedRow is null
        ? string.Empty
        : $"{_detailAll.Count(c => c.IsDifferent):N0} of {_detailAll.Count:N0} columns differ";

    /// <summary>"contoso_category · CAT-017" beside the row name.</summary>
    public string DetailKey => SelectedRow is null
        ? string.Empty
        : $"{SelectedRow.EntityLogicalName} · {SelectedRow.Key}";

    /// <summary>
    /// Every row highlighted in the grid. WPF will not bind SelectedItems, so the view pushes it
    /// here; reconciling acts on all of them, not just the one the detail pane is showing.
    /// </summary>
    public IReadOnlyList<RecordComparison> SelectedRows { get; private set; } = Array.Empty<RecordComparison>();

    public void SetSelectedRows(IEnumerable<RecordComparison> rows)
    {
        SelectedRows = rows.ToList();

        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectionBreakdown));
        OnPropertyChanged(nameof(ReconcileLabel));
        ReconcileCommand.RaiseCanExecuteChanged();
    }

    /// <summary>"1 create, 2 update" - what reconciling the selection would do.</summary>
    public string SelectionBreakdown
    {
        get
        {
            var parts = new List<string>();
            var create = SelectedRows.Count(r => r.Status == RecordCompareStatus.OnlyInSource);
            var update = SelectedRows.Count(r => r.Status == RecordCompareStatus.Different);
            var delete = SelectedRows.Count(r => r.Status == RecordCompareStatus.OnlyInTarget);

            if (create > 0) parts.Add($"{create:N0} create");
            if (update > 0) parts.Add($"{update:N0} update");
            if (delete > 0) parts.Add($"{delete:N0} delete");

            return parts.Count == 0 ? string.Empty : " · " + string.Join(", ", parts);
        }
    }

    public string ReconcileLabel => SelectedRows.Count switch
    {
        0 => "Reconcile...",
        1 => "Reconcile 1 row...",
        var n => $"Reconcile {n:N0} rows..."
    };

    public string SelectionSummary
    {
        get
        {
            var actionable = SelectedRows.Count(r => r.Status != RecordCompareStatus.Same);

            if (SelectedRows.Count == 0) return "No rows selected";

            var rows = SelectedRows.Count == 1 ? "1 row selected" : $"{SelectedRows.Count:N0} rows selected";

            return actionable == SelectedRows.Count
                ? rows
                : $"{rows}, {actionable:N0} need a change";
        }
    }

    /// <summary>What the status bar says about writing to the target, once it is known.</summary>
    public string WriteStatus => _permission is null
        ? string.Empty
        : _permission.Reason;

    public string DetailHeading => SelectedRow is null
        ? "Select a row to see its columns."
        : $"{SelectedRow.EntityLogicalName}  |  {SelectedRow.KeyLabel} = {SelectedRow.Key}";

    private ReferenceDataConfig? _selectedConfiguration;
    public ReferenceDataConfig? SelectedConfiguration
    {
        get => _selectedConfiguration;
        set
        {
            if (!SetProperty(ref _selectedConfiguration, value)) return;

            if (value is not null) LoadConfiguration(value);
            DeleteConfigurationCommand.RaiseCanExecuteChanged();
        }
    }

    private string _configurationName = string.Empty;
    public string ConfigurationName
    {
        get => _configurationName;
        set
        {
            if (SetProperty(ref _configurationName, value)) SaveConfigurationCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _matchLookupsByName = true;
    /// <summary>Re-runs the comparison in memory - the rows are already loaded, only the verdict
    /// on lookup columns changes.</summary>
    public bool MatchLookupsByName
    {
        get => _matchLookupsByName;
        set
        {
            if (!SetProperty(ref _matchLookupsByName, value)) return;

            IsDirty = true;
            if (_fetched.Count > 0) Recompare();
        }
    }

    private int _maxRowsPerEntity = DataverseClient.DefaultMaxRecordsPerEntity;
    public int MaxRowsPerEntity
    {
        get => _maxRowsPerEntity;
        set
        {
            if (SetProperty(ref _maxRowsPerEntity, Math.Clamp(value, 1, 100_000))) IsDirty = true;
        }
    }

    private RecordStatusFilter _statusFilter = RecordStatusFilter.Differences;
    public RecordStatusFilter StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetProperty(ref _statusFilter, value)) RefreshView();
        }
    }

    // Counts for the segmented filter, after the table and search filters.
    private int _countDifferences, _countOnlySource, _countOnlyTarget, _countDifferent, _countMatches;
    public int CountDifferences { get => _countDifferences; private set => SetProperty(ref _countDifferences, value); }
    public int CountOnlySource { get => _countOnlySource; private set => SetProperty(ref _countOnlySource, value); }
    public int CountOnlyTarget { get => _countOnlyTarget; private set => SetProperty(ref _countOnlyTarget, value); }
    public int CountDifferent { get => _countDifferent; private set => SetProperty(ref _countDifferent, value); }
    public int CountMatches { get => _countMatches; private set => SetProperty(ref _countMatches, value); }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) RefreshView();
        }
    }

    private string _selectedEntityFilter = AllEntities;
    private const string AllEntities = "All tables";
    public string SelectedEntityFilter
    {
        get => _selectedEntityFilter;
        set
        {
            if (SetProperty(ref _selectedEntityFilter, value)) RefreshView();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            CancelCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsBusy;

    private string _status = "Pick a source and a target, add the tables to check, then Compare.";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private string _summary = string.Empty;
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    // --- comparing -------------------------------------------------------------------------

    /// <summary>One table as both environments returned it, with the plan it was read under.</summary>
    private sealed record FetchedEntity(
        EntityComparePlan Plan,
        IReadOnlyList<DataRecord> Source,
        IReadOnlyList<DataRecord> Target,
        bool SourceTruncated,
        bool TargetTruncated);

    private async Task CompareAsync()
    {
        if (Source?.Client is not { } sourceClient || Target?.Client is not { } targetClient) return;

        var superseded = _cts;
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        if (superseded is not null) await superseded.CancelAsync();

        IsBusy = true;
        _all.Clear();
        _fetched.Clear();
        _fetchWarnings.Clear();
        Warnings.Clear();
        Rows.Clear();
        SelectedRow = null;
        Summary = string.Empty;

        var dispatcher = Application.Current?.Dispatcher;
        var planner = new ReferenceDataPlanner(sourceClient, targetClient);
        var configured = Entities.Where(e => e.IsEnabled).ToList();
        var done = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            foreach (var entity in configured)
            {
                ct.ThrowIfCancellationRequested();

                done++;
                Status = $"({done}/{configured.Count}) {entity.LogicalName}: reading metadata...";

                var plan = await planner.BuildAsync(entity.Config, MatchLookupsByName, ct);

                _fetchWarnings.AddRange(plan.Warnings);

                if (plan.Failure is not null)
                {
                    _fetchWarnings.Add($"{plan.Failure.EntityLogicalName}: skipped - {plan.Failure.Reason}");
                    continue;
                }

                if (plan.Plan is null) continue;

                Status = $"({done}/{configured.Count}) {entity.LogicalName}: reading rows...";

                void Progress(string side, int count) => dispatcher?.InvokeAsync(() =>
                    Status = $"({done}/{configured.Count}) {entity.LogicalName}: {count:N0} {side} row(s)...");

                // Neither side depends on the other, so the two reads overlap.
                var sourceRows = sourceClient.GetRecordsAsync(
                    plan.Plan.Entity, plan.Plan.SelectNames, plan.Plan.Filter, MaxRowsPerEntity,
                    c => Progress("source", c), ct);

                var targetRows = targetClient.GetRecordsAsync(
                    plan.Plan.Entity, plan.Plan.SelectNames, plan.Plan.Filter, MaxRowsPerEntity,
                    c => Progress("target", c), ct);

                await Task.WhenAll(sourceRows, targetRows);

                var source = await sourceRows;
                var target = await targetRows;

                // A table that came back exactly at the cap was almost certainly truncated, and a
                // truncated side reports every unread row as missing from it - so those rows are
                // marked, and reconciling will not create or delete on the strength of them.
                var sourceTruncated = source.Count >= MaxRowsPerEntity;
                var targetTruncated = target.Count >= MaxRowsPerEntity;

                _fetched.Add(new FetchedEntity(plan.Plan, source, target, sourceTruncated, targetTruncated));

                if (sourceTruncated || targetTruncated)
                {
                    _fetchWarnings.Add($"{entity.LogicalName}: hit the {MaxRowsPerEntity:N0} row cap, so the result " +
                                       "is partial and rows missing from the capped side will not be created or " +
                                       "deleted. Raise the cap or add a filter.");
                }
            }

            Recompare();
            Status = BuildStatus(_fetched.Count, timer.Elapsed);
        }
        catch (OperationCanceledException)
        {
            Recompare();
            Status = "Cancelled - showing what had been read.";
        }
        catch (Exception ex)
        {
            Status = "Comparison failed - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ExportCommand.RaiseCanExecuteChanged();
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Rebuilds the result from rows already in hand. Everything except the row data itself - how
    /// lookups are judged, and so what counts as a difference - can change without another request.
    /// </summary>
    private void Recompare()
    {
        _all.Clear();

        Warnings.Clear();
        foreach (var warning in _fetchWarnings) Warnings.Add(warning);

        foreach (var fetched in _fetched)
        {
            fetched.Plan.MatchLookupsByName = MatchLookupsByName;

            var result = ReferenceDataComparer.Compare(
                fetched.Plan, fetched.Source, fetched.Target, fetched.SourceTruncated, fetched.TargetTruncated);

            foreach (var warning in result.Warnings) Warnings.Add(warning);
            _all.AddRange(result.Rows);
        }

        foreach (var entity in Entities)
        {
            var compared = _fetched.Any(f => string.Equals(
                f.Plan.Entity.LogicalName, entity.LogicalName, StringComparison.OrdinalIgnoreCase));

            entity.DifferenceCount = compared
                ? _all.Count(r => r.Status != RecordCompareStatus.Same &&
                                  string.Equals(r.EntityLogicalName, entity.LogicalName, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        SelectedRow = null;
        BuildEntityFilters();
        RefreshView();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private string BuildStatus(int tableCount, TimeSpan elapsed)
    {
        var noun = tableCount == 1 ? "table" : "tables";
        var took = $"Compared {tableCount} {noun} in {elapsed.TotalSeconds:0.0} s";

        if (_all.Count == 0) return $"{took} - no rows read.";

        var sourceRows = _fetched.Sum(f => f.Source.Count);
        var targetRows = _fetched.Sum(f => f.Target.Count);
        var read = sourceRows == targetRows
            ? $"{sourceRows:N0} rows read from each side"
            : $"{sourceRows:N0} source and {targetRows:N0} target rows read";

        var differing = _all.Count(r => r.Status != RecordCompareStatus.Same);

        return differing == 0
            ? $"{took} · {read} · every row matches."
            : $"{took} · {read}";
    }

    private void BuildEntityFilters()
    {
        var current = SelectedEntityFilter;

        EntityFilters.Clear();
        EntityFilters.Add(AllEntities);

        foreach (var name in _all.Select(r => r.EntityLogicalName)
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            EntityFilters.Add(name);
        }

        SelectedEntityFilter = EntityFilters.Contains(current) ? current : AllEntities;
    }

    private void RefreshView()
    {
        Rows.Clear();
        foreach (var row in _all) Rows.Add(row);

        RowsView.Refresh();

        var onlySource = _all.Count(r => r.Status == RecordCompareStatus.OnlyInSource);
        var onlyTarget = _all.Count(r => r.Status == RecordCompareStatus.OnlyInTarget);
        var different = _all.Count(r => r.Status == RecordCompareStatus.Different);
        var same = _all.Count(r => r.Status == RecordCompareStatus.Same);

        Summary = $"{onlySource:N0} only in {SourceHeader}  |  {onlyTarget:N0} only in {TargetHeader}  |  " +
                  $"{different:N0} with different values  |  {same:N0} matching";

        var narrowed = _all.Where(MatchesTableAndSearch).ToList();
        CountOnlySource = narrowed.Count(r => r.Status == RecordCompareStatus.OnlyInSource);
        CountOnlyTarget = narrowed.Count(r => r.Status == RecordCompareStatus.OnlyInTarget);
        CountDifferent = narrowed.Count(r => r.Status == RecordCompareStatus.Different);
        CountMatches = narrowed.Count(r => r.Status == RecordCompareStatus.Same);
        CountDifferences = CountOnlySource + CountOnlyTarget + CountDifferent;
    }

    private bool FilterRow(object obj)
    {
        if (obj is not RecordComparison row) return false;

        var statusMatches = StatusFilter switch
        {
            RecordStatusFilter.OnlySource => row.Status == RecordCompareStatus.OnlyInSource,
            RecordStatusFilter.OnlyTarget => row.Status == RecordCompareStatus.OnlyInTarget,
            RecordStatusFilter.Different => row.Status == RecordCompareStatus.Different,
            RecordStatusFilter.Matches => row.Status == RecordCompareStatus.Same,
            _ => row.Status != RecordCompareStatus.Same
        };

        return statusMatches && MatchesTableAndSearch(row);
    }

    private bool MatchesTableAndSearch(RecordComparison row)
    {
        if (!string.Equals(SelectedEntityFilter, AllEntities, StringComparison.Ordinal) &&
            !string.Equals(row.EntityLogicalName, SelectedEntityFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        return row.Key.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               (row.Name?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
               row.DifferenceSummary.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    // --- reconciling -----------------------------------------------------------------------

    /// <summary>
    /// Opens the reconcile window for the selected rows. The write guard is consulted here, before
    /// the window is shown, so a blocked environment is visible in the confirmation rather than
    /// only at the moment a write fails.
    /// </summary>
    private async Task ReconcileAsync()
    {
        if (Target?.Client is not { } targetClient) return;
        if (SelectedRows.Count == 0) return;

        var actionable = SelectedRows.Where(r => r.Status != RecordCompareStatus.Same).ToList();

        if (actionable.Count == 0)
        {
            Status = "Every selected row already matches, so there is nothing to reconcile.";
            return;
        }

        WritePermission permission;
        IReadOnlyDictionary<string, EntitySummary> targetEntities;

        try
        {
            IsBusy = true;
            Status = $"Checking what kind of environment {TargetHeader} is...";

            permission = await EnsurePermissionAsync(targetClient);
            targetEntities = await EnsureTargetEntitiesAsync(targetClient);
        }
        catch (Exception ex)
        {
            Status = "Could not prepare the write - " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        OnPropertyChanged(nameof(WriteStatus));

        var viewModel = new ReconcileViewModel(
            actionable, SourceHeader, TargetHeader, targetClient, targetEntities, permission,
            Source?.EnvironmentSku ?? EnvironmentSku.Unknown, Target?.AccountName);

        var window = new Views.ReconcileWindow { DataContext = viewModel, Owner = OwnerWindow() };
        // Read from the view model rather than the dialog result: closing with the title bar's X
        // after a run reports no result, yet the rows on screen are just as out of date.
        window.ShowDialog();
        var wrote = viewModel.AnyWritesSucceeded;

        Status = permission.Allowed
            ? viewModel.Status
            : permission.Reason;

        // Anything written makes the rows on screen a description of how things used to be, so the
        // comparison is run again rather than left showing differences that have just been fixed.
        if (wrote) await CompareAsync();
    }

    /// <summary>Asked afresh for every reconcile, so allowing or blocking writes from the sidebar
    /// applies without reopening this window.</summary>
    private async Task<WritePermission> EnsurePermissionAsync(DataverseClient targetClient)
    {
        var environmentId = _settings.GetEnvironmentId(targetClient.EnvironmentUrl);
        var type = await targetClient.GetEnvironmentTypeAsync(environmentId, CancellationToken.None);

        _permission = WriteGuard.Evaluate(_settings, targetClient.EnvironmentUrl, type);

        return _permission;
    }

    private async Task<IReadOnlyDictionary<string, EntitySummary>> EnsureTargetEntitiesAsync(
        DataverseClient targetClient)
    {
        if (_targetEntities is not null && ReferenceEquals(_targetEntitiesFrom, targetClient))
        {
            return _targetEntities;
        }

        var entities = await targetClient.GetEntitiesAsync(CancellationToken.None);

        _targetEntities = entities.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);
        _targetEntitiesFrom = targetClient;

        return _targetEntities;
    }

    // --- configuration ---------------------------------------------------------------------

    private void LoadConfiguration(ReferenceDataConfig config)
    {
        ConfigurationName = config.Name ?? string.Empty;
        MatchLookupsByName = config.MatchLookupsByName;
        MaxRowsPerEntity = config.MaxRowsPerEntity;

        Entities.Clear();

        foreach (var entity in config.Entities ?? new List<ReferenceEntityConfig>())
        {
            Add(new ReferenceEntityViewModel(entity.Clone()));
        }

        IsDirty = false;
        RaiseCommandStates();
    }

    private void NewConfiguration()
    {
        SelectedConfiguration = null;
        ConfigurationName = string.Empty;
        Entities.Clear();
        IsDirty = false;
        RaiseCommandStates();
    }

    /// <summary>
    /// A table whose columns were never chosen by hand still has a set it is compared on - the one
    /// its last comparison used. Saving records it, so a column the table gains afterwards is
    /// recognised as new rather than compared because it was never excluded.
    /// </summary>
    private ReferenceEntityConfig WithComparedColumns(ReferenceEntityConfig config)
    {
        if (config.ComparedColumns is not null) return config;

        var plan = _fetched.LastOrDefault(f => string.Equals(
            f.Plan.Entity.LogicalName, config.LogicalName, StringComparison.OrdinalIgnoreCase))?.Plan;

        if (plan is not null)
        {
            config.ComparedColumns = plan.ValueColumns.Select(c => c.LogicalName).ToList();
        }

        return config;
    }

    /// <summary>A configuration that has never been saved is asked for its name first.</summary>
    private void SaveConfiguration()
    {
        if (string.IsNullOrWhiteSpace(ConfigurationName))
        {
            var asked = Views.NamePromptWindow.Ask(OwnerWindow(), "Save configuration",
                "Name this set of tables so it can be picked again.", string.Empty);
            if (asked is null) return;

            ConfigurationName = asked;
        }

        var name = ConfigurationName.Trim();
        if (name.Length == 0) return;

        var saved = new ReferenceDataConfig
        {
            Name = name,
            Entities = Entities.Select(e => WithComparedColumns(e.Config.Clone())).ToList(),
            MatchLookupsByName = MatchLookupsByName,
            MaxRowsPerEntity = MaxRowsPerEntity
        };

        var existing = Configurations.FirstOrDefault(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            Configurations[Configurations.IndexOf(existing)] = saved;
        }
        else
        {
            Configurations.Add(saved);
        }

        // Set through the field so reloading the configuration does not undo unsaved edits the
        // user has just saved anyway.
        _selectedConfiguration = saved;
        OnPropertyChanged(nameof(SelectedConfiguration));
        DeleteConfigurationCommand.RaiseCanExecuteChanged();

        Persist();
        IsDirty = false;
        Status = $"Saved configuration '{name}'.";
    }

    private void RenameConfiguration()
    {
        if (SelectedConfiguration is not { } config) return;

        var name = Views.NamePromptWindow.Ask(OwnerWindow(), "Rename configuration",
            "The tables and settings stay as they are.", config.Name ?? string.Empty);

        if (name is null || string.Equals(name, config.Name, StringComparison.Ordinal)) return;

        if (Configurations.Any(c => !ReferenceEquals(c, config) &&
                                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"There is already a configuration called '{name}'.";
            return;
        }

        var renamed = config.Clone();
        renamed.Name = name;

        // Replacing the item, rather than editing it, is what makes the dropdown show the new name.
        _selectedConfiguration = renamed;
        Configurations[Configurations.IndexOf(config)] = renamed;
        OnPropertyChanged(nameof(SelectedConfiguration));
        ConfigurationName = name;

        Persist();
        Status = $"Renamed the configuration to '{name}'.";
    }

    private void DeleteConfiguration()
    {
        if (SelectedConfiguration is not { } config) return;

        var confirm = MessageBox.Show(
            $"Delete the saved configuration '{config.Name}'?",
            "PPObjectSearch", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        Configurations.Remove(config);
        _selectedConfiguration = null;
        OnPropertyChanged(nameof(SelectedConfiguration));

        Persist();
        Status = $"Deleted configuration '{config.Name}'.";
    }

    private void Persist()
    {
        _settings.ReferenceDataConfigurations = Configurations.Select(c => c.Clone()).ToList();
        _settings.Save();
    }

    // --- tables ----------------------------------------------------------------------------

    private async Task AddEntitiesAsync()
    {
        if (Source?.Client is not { } client) return;

        try
        {
            if (_sourceEntities is null || !ReferenceEquals(_sourceEntitiesFrom, client))
            {
                IsBusy = true;
                Status = $"Reading the table list from {SourceHeader}...";

                _sourceEntities = await client.GetEntitiesAsync(CancellationToken.None);
                _sourceEntitiesFrom = client;
            }
        }
        catch (Exception ex)
        {
            Status = "Could not read the table list - " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var picker = new EntityPickerViewModel(_sourceEntities, Entities.Select(e => e.LogicalName));

        var window = new Views.EntityPickerWindow { DataContext = picker, Owner = OwnerWindow() };
        if (window.ShowDialog() != true) return;

        var chosen = picker.SelectedEntities();
        foreach (var entity in chosen)
        {
            Add(new ReferenceEntityViewModel(
                new ReferenceEntityConfig
                {
                    LogicalName = entity.LogicalName,
                    DisplayName = entity.DisplayName
                },
                entity));
        }

        Status = $"{Entities.Count} table(s) configured.";
        if (chosen.Count > 0) IsDirty = true;
        RaiseCommandStates();
    }

    private async Task EditEntityAsync(ReferenceEntityViewModel? entity)
    {
        if (entity is null || Source?.Client is not { } client) return;

        var summary = entity.Entity ?? await ResolveEntityAsync(client, entity.LogicalName);

        if (summary is null)
        {
            Status = $"{entity.LogicalName} is not in {SourceHeader}, so its settings cannot be read.";
            return;
        }

        entity.Entity = summary;

        var settings = new ReferenceEntitySettingsViewModel(entity.Config, summary, client);
        var window = new Views.ReferenceEntitySettingsWindow { DataContext = settings, Owner = OwnerWindow() };

        _ = settings.LoadAsync(CancellationToken.None);

        if (window.ShowDialog() != true) return;

        entity.Refresh();
        IsDirty = true;
    }

    private async Task<EntitySummary?> ResolveEntityAsync(DataverseClient client, string logicalName)
    {
        try
        {
            if (_sourceEntities is null || !ReferenceEquals(_sourceEntitiesFrom, client))
            {
                IsBusy = true;
                Status = $"Reading the table list from {SourceHeader}...";

                _sourceEntities = await client.GetEntitiesAsync(CancellationToken.None);
                _sourceEntitiesFrom = client;
            }

            return _sourceEntities.FirstOrDefault(
                e => string.Equals(e.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Status = "Could not read the table list - " + ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Unticking the last table has to disable Compare, and these commands only
    /// re-evaluate when told to.</summary>
    private void Add(ReferenceEntityViewModel entity)
    {
        entity.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ReferenceEntityViewModel.IsEnabled)) return;

            CompareCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(TablesHeading));
            IsDirty = true;
        };

        Entities.Add(entity);
    }

    private void RemoveEntity(ReferenceEntityViewModel? entity)
    {
        if (entity is null) return;

        Entities.Remove(entity);
        IsDirty = true;
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        ReconcileCommand.RaiseCanExecuteChanged();
        CompareCommand.RaiseCanExecuteChanged();
        AddEntitiesCommand.RaiseCanExecuteChanged();
        EditEntityCommand.RaiseCanExecuteChanged();
        RemoveEntityCommand.RaiseCanExecuteChanged();
    }

    private Window? OwnerWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => ReferenceEquals(w.DataContext, this))
        ?? Application.Current?.MainWindow;

    // --- export ----------------------------------------------------------------------------

    /// <summary>
    /// One line per difference rather than one per row, so the file says which column differs and
    /// what each side holds. Rows present on one side only contribute a single line with no column.
    /// </summary>
    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"reference-data-{SourceHeader}-{TargetHeader}.csv".Replace(' ', '-')
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = new List<string>
            {
                CsvExporter.Line(
                    "Table", "Key column(s)", "Key", "Name", "Status", "Column",
                    $"{SourceHeader} value", $"{TargetHeader} value", "Source id", "Target id")
            };

            foreach (var row in RowsView.Cast<RecordComparison>())
            {
                if (row.Differences.Count == 0)
                {
                    lines.Add(Line(row, null));
                    continue;
                }

                foreach (var difference in row.Differences) lines.Add(Line(row, difference));
            }

            CsvExporter.WriteLines(dialog.FileName, lines);
            Status = $"Exported {lines.Count - 1:N0} line(s) to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Line(RecordComparison row, ColumnComparison? difference) => CsvExporter.Line(
        row.EntityLogicalName, row.KeyLabel, row.Key, row.Name, row.StatusLabel,
        difference?.Column.LogicalName,
        difference?.SourceValue,
        difference?.TargetValue,
        row.SourceId, row.TargetId);
}
