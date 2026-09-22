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
        }
    }

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

            return string.Join("  |  ", parts);
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(KeyDescription));
        OnPropertyChanged(nameof(Detail));
    }
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
        SaveConfigurationCommand = new RelayCommand(_ => SaveConfiguration(), _ => !string.IsNullOrWhiteSpace(ConfigurationName));
        DeleteConfigurationCommand = new RelayCommand(_ => DeleteConfiguration(), _ => SelectedConfiguration is not null);

        ExportCommand = new RelayCommand(_ => Export(), _ => _all.Count > 0);

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
    public RelayCommand DeleteConfigurationCommand { get; }
    public RelayCommand ExportCommand { get; }

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

            OnPropertyChanged(nameof(TargetHeader));
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

            DetailColumns.Clear();

            foreach (var column in value?.AllColumns() ?? Array.Empty<ColumnComparison>())
            {
                DetailColumns.Add(column);
            }

            OnPropertyChanged(nameof(DetailHeading));
        }
    }

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
            if (SetProperty(ref _matchLookupsByName, value) && _fetched.Count > 0) Recompare();
        }
    }

    private int _maxRowsPerEntity = DataverseClient.DefaultMaxRecordsPerEntity;
    public int MaxRowsPerEntity
    {
        get => _maxRowsPerEntity;
        set => SetProperty(ref _maxRowsPerEntity, Math.Clamp(value, 1, 100_000));
    }

    private bool _showMatches;
    public bool ShowMatches
    {
        get => _showMatches;
        set
        {
            if (SetProperty(ref _showMatches, value)) RefreshView();
        }
    }

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
        IReadOnlyList<DataRecord> Target);

    private async Task CompareAsync()
    {
        if (Source?.Client is not { } sourceClient || Target?.Client is not { } targetClient) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

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

                _fetched.Add(new FetchedEntity(plan.Plan, source, target));

                // A table that came back exactly at the cap was almost certainly truncated, and a
                // truncated side reports every unread row as missing from it.
                if (source.Count >= MaxRowsPerEntity || target.Count >= MaxRowsPerEntity)
                {
                    _fetchWarnings.Add($"{entity.LogicalName}: hit the {MaxRowsPerEntity:N0} row cap, so the result " +
                                       "is partial. Raise the cap or add a filter.");
                }
            }

            Recompare();
            Status = BuildStatus(_fetched.Count);
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

            var result = ReferenceDataComparer.Compare(fetched.Plan, fetched.Source, fetched.Target);

            foreach (var warning in result.Warnings) Warnings.Add(warning);
            _all.AddRange(result.Rows);
        }

        SelectedRow = null;
        BuildEntityFilters();
        RefreshView();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private string BuildStatus(int tableCount)
    {
        if (_all.Count == 0) return $"{tableCount} table(s) compared - no rows read.";

        var noun = tableCount == 1 ? "table" : "tables";
        var differing = _all.Count(r => r.Status != RecordCompareStatus.Same);

        return differing == 0
            ? $"{tableCount} {noun} compared - every row matches."
            : $"{tableCount} {noun} compared - {differing:N0} row(s) need attention.";
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
    }

    private bool FilterRow(object obj)
    {
        if (obj is not RecordComparison row) return false;

        if (!ShowMatches && row.Status == RecordCompareStatus.Same) return false;

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

        RaiseCommandStates();
    }

    private void NewConfiguration()
    {
        SelectedConfiguration = null;
        ConfigurationName = string.Empty;
        Entities.Clear();
        RaiseCommandStates();
    }

    private void SaveConfiguration()
    {
        var name = ConfigurationName.Trim();
        if (name.Length == 0) return;

        var saved = new ReferenceDataConfig
        {
            Name = name,
            Entities = Entities.Select(e => e.Config.Clone()).ToList(),
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
        Status = $"Saved configuration '{name}'.";
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

                _sourceEntities = await client.GetEntitiesAsync();
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

        foreach (var entity in picker.SelectedEntities)
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

        _ = settings.LoadAsync();

        if (window.ShowDialog() == true) entity.Refresh();
    }

    private async Task<EntitySummary?> ResolveEntityAsync(DataverseClient client, string logicalName)
    {
        try
        {
            if (_sourceEntities is null || !ReferenceEquals(_sourceEntitiesFrom, client))
            {
                IsBusy = true;
                Status = $"Reading the table list from {SourceHeader}...";

                _sourceEntities = await client.GetEntitiesAsync();
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
            if (e.PropertyName == nameof(ReferenceEntityViewModel.IsEnabled)) CompareCommand.RaiseCanExecuteChanged();
        };

        Entities.Add(entity);
    }

    private void RemoveEntity(ReferenceEntityViewModel? entity)
    {
        if (entity is null) return;

        Entities.Remove(entity);
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
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
                string.Join(",", new[]
                {
                    "Table", "Key column(s)", "Key", "Name", "Status", "Column",
                    $"{SourceHeader} value", $"{TargetHeader} value", "Source id", "Target id"
                }.Select(Escape))
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

            System.IO.File.WriteAllLines(dialog.FileName, lines, new System.Text.UTF8Encoding(true));
            Status = $"Exported {lines.Count - 1:N0} line(s) to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Line(RecordComparison row, ColumnComparison? difference) => string.Join(",", new[]
    {
        row.EntityLogicalName, row.KeyLabel, row.Key, row.Name, row.StatusLabel,
        difference?.Column.LogicalName,
        difference is null ? null : difference.SourceValue,
        difference is null ? null : difference.TargetValue,
        row.SourceId, row.TargetId
    }.Select(Escape));

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        // Excel reads a leading =, +, - or @ as a formula.
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;

        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
    }
}
