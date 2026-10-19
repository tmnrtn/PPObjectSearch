using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One of the table's columns in the column picker, with its tick.</summary>
public sealed class TableDataColumn : ObservableObject
{
    public required EntityColumn Column { get; init; }

    private bool _isShown;
    public bool IsShown
    {
        get => _isShown;
        set => SetProperty(ref _isShown, value);
    }

    public string Header => string.IsNullOrWhiteSpace(Column.DisplayName) ? Column.LogicalName : Column.DisplayName;
    public string LogicalName => Column.LogicalName;
    public string SelectName => Column.SelectName;
    public string TypeLabel => Column.TypeLabel;
    public string ToolTip => $"{Column.LogicalName} · {Column.TypeLabel}";

    public override string ToString() => Header;
}

/// <summary>One row as shown: the cells line up with <see cref="TableDataViewModel.ShownColumns"/>.</summary>
public sealed class TableDataRow(DataRecord record, IReadOnlyList<string?> cells)
{
    public DataRecord Record { get; } = record;
    public IReadOnlyList<string?> Cells { get; } = cells;

    public bool Matches(string text) => Cells.Any(c => c?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>One server-side condition: the OData the rows are read with, and the words it is shown as.</summary>
public sealed record TableServerFilter(string Filter, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An operator in the server filter's list, with the words it is shown as.</summary>
public sealed record TableFilterChoice(TableFilterOperator Operator)
{
    public string Label => TableRowFilter.Label(Operator);
    public override string ToString() => Label;
}

/// <summary>
/// A quick look at the rows of one table - reference and configuration data, lookup tables,
/// environment-specific settings. Strictly read-only: it reads through GETs and offers nothing that
/// writes, so the production write guard never comes into it.
/// </summary>
public sealed class TableDataViewModel : ObservableObject, IDisposable
{
    private readonly EnvironmentSessionViewModel _session;
    private readonly string? _initialTable;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<DataRecord> _records = [];

    /// <summary>Per lookup column, the tables it points at and their name columns - read once per table opened.</summary>
    private readonly Dictionary<string, IReadOnlyList<LookupNavigation>> _lookupNames = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <param name="tableLogicalName">A table to open once the list is read - from a table's details window.</param>
    public TableDataViewModel(EnvironmentSessionViewModel session, string? tableLogicalName = null)
    {
        _session = session;
        _initialTable = tableLogicalName;

        TablesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Tables);
        TablesView.Filter = o => o is EntitySummary t && MatchesTable(t, TableSearch);

        ColumnsView = new ListCollectionView(Columns);
        ColumnsView.Filter = o => o is TableDataColumn c &&
                                  (string.IsNullOrWhiteSpace(ColumnSearch) ||
                                   c.LogicalName.Contains(ColumnSearch.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                   c.Header.Contains(ColumnSearch.Trim(), StringComparison.OrdinalIgnoreCase));

        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is TableDataRow r && (string.IsNullOrWhiteSpace(QuickFilter) || r.Matches(QuickFilter.Trim()));

        RefreshCommand = new AsyncRelayCommand(_ => LoadRowsAsync(), _ => !IsBusy && SelectedTable is not null);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        ApplyColumnsCommand = new AsyncRelayCommand(_ => LoadRowsAsync(), _ => SelectedTable is not null);
        DefaultColumnsCommand = new RelayCommand(_ => ShowDefaultColumns(), _ => Columns.Count > 0);
        ApplyFilterCommand = new AsyncRelayCommand(_ => ApplyFilterAsync(), _ => FilterColumn is not null);
        ClearFilterCommand = new AsyncRelayCommand(_ => ClearFilterAsync(), _ => HasServerFilter);
        RemoveFilterCommand = new AsyncRelayCommand(p => RemoveFilterAsync(p as TableServerFilter), p => p is TableServerFilter);
        CopyValueCommand = new RelayCommand(_ => CopyCell(raw: false), _ => CurrentRow is not null && CurrentColumn >= 0);
        CopyRawValueCommand = new RelayCommand(_ => CopyCell(raw: true), _ => CurrentRow is not null && CurrentColumn >= 0);
        CopyColumnCommand = new RelayCommand(_ => CopyColumn(), _ => CurrentColumn >= 0 && HasRows);
        CopyRowIdCommand = new RelayCommand(_ => CopyRowId(), _ => CurrentRow is not null);
        ToggleColumnPickerCommand = new RelayCommand(_ => IsColumnPickerOpen = !IsColumnPickerOpen);
        ExportCsvCommand = new RelayCommand(_ => Export(markdown: false), _ => Rows.Count > 0);
        ExportMarkdownCommand = new RelayCommand(_ => Export(markdown: true), _ => Rows.Count > 0);
    }

    public string Title => $"Table data — {_session.Title}";

    /// <summary>The tab the window was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session => _session;

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand ApplyColumnsCommand { get; }
    public RelayCommand DefaultColumnsCommand { get; }
    public AsyncRelayCommand ApplyFilterCommand { get; }
    public AsyncRelayCommand ClearFilterCommand { get; }
    public AsyncRelayCommand RemoveFilterCommand { get; }
    public RelayCommand CopyValueCommand { get; }
    public RelayCommand CopyRawValueCommand { get; }
    public RelayCommand CopyColumnCommand { get; }
    public RelayCommand CopyRowIdCommand { get; }
    public RelayCommand ToggleColumnPickerCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }

    // ---------------------------------------------------------------- tables

    public ObservableCollection<EntitySummary> Tables { get; } = new();
    public ListCollectionView TablesView { get; }

    private string _tableSearch = string.Empty;
    /// <summary>Matches the display name or the logical name.</summary>
    public string TableSearch
    {
        get => _tableSearch;
        set
        {
            if (SetProperty(ref _tableSearch, value)) TablesView.Refresh();
        }
    }

    internal static bool MatchesTable(EntitySummary table, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var text = search.Trim();
        return table.LogicalName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               (table.DisplayName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private EntitySummary? _selectedTable;
    public EntitySummary? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (!SetProperty(ref _selectedTable, value)) return;

            OnPropertyChanged(nameof(TableHeading));
            RefreshCommand.RaiseCanExecuteChanged();
            ApplyColumnsCommand.RaiseCanExecuteChanged();
            if (value is not null) _ = OpenTableAsync(value);
        }
    }

    /// <summary>The table chosen, else the window's own name.</summary>
    public string TableHeading => SelectedTable?.Label ?? "Choose a table";

    // ---------------------------------------------------------------- columns

    /// <summary>Every readable column: the default ones first in the view's order, then the rest by name.</summary>
    public ObservableCollection<TableDataColumn> Columns { get; } = new();
    public ListCollectionView ColumnsView { get; }

    private string _columnSearch = string.Empty;
    public string ColumnSearch
    {
        get => _columnSearch;
        set
        {
            if (SetProperty(ref _columnSearch, value)) ColumnsView.Refresh();
        }
    }

    private IReadOnlyList<TableDataColumn> _shownColumns = [];
    /// <summary>The columns of the rows on screen - the grid's columns, in order.</summary>
    public IReadOnlyList<TableDataColumn> ShownColumns
    {
        get => _shownColumns;
        private set
        {
            if (SetProperty(ref _shownColumns, value)) OnPropertyChanged(nameof(ColumnsLabel));
        }
    }

    public string ColumnsLabel => $"Columns ({ShownColumns.Count})";

    private bool _isColumnPickerOpen;
    public bool IsColumnPickerOpen
    {
        get => _isColumnPickerOpen;
        set => SetProperty(ref _isColumnPickerOpen, value);
    }

    /// <summary>The columns shown before anyone picks: what the view shows, led by the primary name.</summary>
    private List<string> _defaultColumns = [];

    /// <summary>
    /// The primary name, then the default public view's columns. A table with no view shows its
    /// primary name and status; one with neither shows its id rather than nothing.
    /// </summary>
    internal static List<string> DefaultColumns(EntitySummary table, IReadOnlyList<EntityColumn> columns, IReadOnlyList<string> viewColumns)
    {
        var known = columns.Select(c => c.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = new List<string>();

        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && known.Contains(name) &&
                !chosen.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                chosen.Add(columns.First(c => c.LogicalName.Equals(name, StringComparison.OrdinalIgnoreCase)).LogicalName);
            }
        }

        Add(table.PrimaryNameAttribute);
        foreach (var name in viewColumns) Add(name);

        if (viewColumns.Count == 0) Add("statecode");
        if (chosen.Count == 0) Add(table.PrimaryIdAttribute);

        return chosen;
    }

    private void SetColumns(EntitySummary table, IReadOnlyList<EntityColumn> columns, IReadOnlyList<string> viewColumns)
    {
        _defaultColumns = DefaultColumns(table, columns, viewColumns);

        var ordered = _defaultColumns
            .Select(n => columns.First(c => c.LogicalName.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Concat(columns.Where(c => !_defaultColumns.Contains(c.LogicalName, StringComparer.OrdinalIgnoreCase))
                .OrderBy(c => string.IsNullOrWhiteSpace(c.DisplayName) ? c.LogicalName : c.DisplayName, StringComparer.CurrentCultureIgnoreCase));

        Columns.Clear();
        foreach (var column in ordered)
        {
            Columns.Add(new TableDataColumn
            {
                Column = column,
                IsShown = _defaultColumns.Contains(column.LogicalName, StringComparer.OrdinalIgnoreCase)
            });
        }

        ColumnSearch = string.Empty;
        DefaultColumnsCommand.RaiseCanExecuteChanged();

        HasStateCode = columns.Any(c => c.LogicalName.Equals("statecode", StringComparison.OrdinalIgnoreCase));
        _activeOnly = false;
        OnPropertyChanged(nameof(ActiveOnly));

        FilterColumn = Columns.FirstOrDefault();
    }

    private void ShowDefaultColumns()
    {
        foreach (var column in Columns)
        {
            column.IsShown = _defaultColumns.Contains(column.LogicalName, StringComparer.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- server filter

    private TableDataColumn? _filterColumn;
    public TableDataColumn? FilterColumn
    {
        get => _filterColumn;
        set
        {
            if (!SetProperty(ref _filterColumn, value)) return;

            FilterOperators.Clear();
            if (value is not null)
            {
                foreach (var op in TableRowFilter.OperatorsFor(value.Column)) FilterOperators.Add(new TableFilterChoice(op));
            }
            FilterOperator = FilterOperators.FirstOrDefault();
            OnPropertyChanged(nameof(FilterHint));
            ApplyFilterCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<TableFilterChoice> FilterOperators { get; } = new();

    private TableFilterChoice? _filterOperator;
    public TableFilterChoice? FilterOperator
    {
        get => _filterOperator;
        set
        {
            if (SetProperty(ref _filterOperator, value)) OnPropertyChanged(nameof(NeedsFilterValue));
        }
    }

    public bool NeedsFilterValue => FilterOperator is null || TableRowFilter.NeedsValue(FilterOperator.Operator);

    private string _filterValue = string.Empty;
    public string FilterValue
    {
        get => _filterValue;
        set => SetProperty(ref _filterValue, value);
    }

    /// <summary>What the value box takes, for its placeholder: "a number", "a date, such as 2026-10-19"...</summary>
    public string FilterHint => FilterColumn is null ? "Value" : TableRowFilter.ValueHint(FilterColumn.Column);

    /// <summary>The conditions the rows on screen were read with; a row has to match every one.</summary>
    public ObservableCollection<TableServerFilter> ServerFilters { get; } = new();

    public bool HasServerFilter => ServerFilters.Count > 0;

    /// <summary>"Name contains 'tax' and Rate greater than '5'" - every condition, as words.</summary>
    public string ServerFilterLabel => string.Join(" and ", ServerFilters.Select(f => f.Label));

    /// <summary>Adds the condition in the filter boxes to those already applied, and reads the rows again.</summary>
    private async Task ApplyFilterAsync()
    {
        if (FilterColumn is not { } column || FilterOperator is not { } choice) return;

        var value = FilterValue;

        // A choice can be typed by its label; the rows read so far say which number that is.
        if (TableRowFilter.IsChoice(column.Column) && TableRowFilter.NeedsValue(choice.Operator) &&
            !decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            var match = _records.FirstOrDefault(r => string.Equals(r.Label(column.SelectName), value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match?.Raw(column.SelectName) is not { } number)
            {
                Status = $"No row read so far has \"{value.Trim()}\" in {column.Header} - type the choice's number instead.";
                return;
            }
            value = number;
        }

        // A lookup compared by name goes through the tables it points at, which the metadata says.
        IReadOnlyList<LookupNavigation>? lookupNames = null;
        if (TableRowFilter.NeedsLookupNames(column.Column, choice.Operator, value))
        {
            lookupNames = await LookupNamesAsync(column);
            if (lookupNames is null) return;
        }

        var filter = TableRowFilter.Build(column.Column, choice.Operator, value, out var error, lookupNames);
        if (filter is null)
        {
            Status = error ?? "That filter cannot be built.";
            return;
        }

        if (ServerFilters.Any(f => f.Filter == filter))
        {
            Status = "That filter is already applied.";
            return;
        }

        ServerFilters.Add(new TableServerFilter(filter, TableRowFilter.NeedsValue(choice.Operator)
            ? $"{column.Header} {choice.Label} '{FilterValue.Trim()}'"
            : $"{column.Header} {choice.Label}"));
        FiltersChanged();
        FilterValue = string.Empty;

        await LoadRowsAsync();
    }

    /// <summary>The lookup's targets, read once; null - with the reason in the status - when they cannot be read.</summary>
    private async Task<IReadOnlyList<LookupNavigation>?> LookupNamesAsync(TableDataColumn column)
    {
        if (_lookupNames.TryGetValue(column.LogicalName, out var known)) return known;
        if (_session.Client is not { } client || SelectedTable is not { } table) return null;

        try
        {
            Status = $"Reading which tables {column.Header} points at...";
            var names = await client.GetLookupNavigationsAsync(table.LogicalName, column.LogicalName);
            _lookupNames[column.LogicalName] = names;
            Status = string.Empty;
            return names;
        }
        catch (Exception ex)
        {
            Status = $"Could not read which tables {column.Header} points at - " + ex.Message;
            return null;
        }
    }

    private async Task RemoveFilterAsync(TableServerFilter? filter)
    {
        if (filter is null || !ServerFilters.Remove(filter)) return;

        FiltersChanged();
        await LoadRowsAsync();
    }

    private async Task ClearFilterAsync()
    {
        ClearFilters();
        FilterValue = string.Empty;
        await LoadRowsAsync();
    }

    private void ClearFilters()
    {
        ServerFilters.Clear();
        FiltersChanged();
    }

    private void FiltersChanged()
    {
        OnPropertyChanged(nameof(HasServerFilter));
        OnPropertyChanged(nameof(ServerFilterLabel));
        ClearFilterCommand.RaiseCanExecuteChanged();
    }

    private bool _hasStateCode;
    /// <summary>Whether the table has an active/inactive status at all.</summary>
    public bool HasStateCode
    {
        get => _hasStateCode;
        private set => SetProperty(ref _hasStateCode, value);
    }

    private bool _activeOnly;
    /// <summary>Only rows whose status is Active (statecode 0) - read again on change.</summary>
    public bool ActiveOnly
    {
        get => _activeOnly;
        set
        {
            if (SetProperty(ref _activeOnly, value) && SelectedTable is not null) _ = LoadRowsAsync();
        }
    }

    /// <summary>The OData filter a read uses: the server filter, and Active only where ticked.</summary>
    public string? EffectiveFilter => TableRowFilter.And(
        [.. ServerFilters.Select(f => f.Filter), ActiveOnly && HasStateCode ? "statecode eq 0" : null]);

    // ---------------------------------------------------------------- row limit

    public IReadOnlyList<int> RowCaps { get; } = [500, DataverseClient.DefaultMaxRecordsPerEntity, 50_000];

    private int _rowCap = DataverseClient.DefaultMaxRecordsPerEntity;
    public int RowCap
    {
        get => _rowCap;
        set
        {
            if (SetProperty(ref _rowCap, value) && SelectedTable is not null) _ = LoadRowsAsync();
        }
    }

    // ---------------------------------------------------------------- rows

    public BulkObservableCollection<TableDataRow> Rows { get; } = new();
    public ListCollectionView RowsView { get; }

    private string _quickFilter = string.Empty;
    /// <summary>Narrows the rows already read, over every value shown - nothing is read again.</summary>
    public string QuickFilter
    {
        get => _quickFilter;
        set
        {
            if (SetProperty(ref _quickFilter, value)) RefreshRows();
        }
    }

    private bool _showRawValues;
    /// <summary>Ids and option numbers rather than the names and labels Dataverse formats them as.</summary>
    public bool ShowRawValues
    {
        get => _showRawValues;
        set
        {
            if (SetProperty(ref _showRawValues, value)) BuildRows(ShownColumns);
        }
    }

    internal static string? Cell(DataRecord record, string selectName, bool raw)
    {
        if (raw) return record.Raw(selectName);
        return record.Label(selectName) is { Length: > 0 } label ? label : record.Raw(selectName);
    }

    private void BuildRows(IReadOnlyList<TableDataColumn> columns)
    {
        var raw = ShowRawValues;
        Rows.ReplaceAll(_records.Select(r => new TableDataRow(r, columns.Select(c => Cell(r, c.SelectName, raw)).ToArray())));
        SetCurrentCell(null, -1);
        RefreshRows();
    }

    private void RefreshRows()
    {
        RowsView.Refresh();
        OnPropertyChanged(nameof(ShownCount));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(EmptyHeading));
        OnPropertyChanged(nameof(EmptyText));
        ExportCsvCommand.RaiseCanExecuteChanged();
        ExportMarkdownCommand.RaiseCanExecuteChanged();
    }

    public int ShownCount => RowsView.Count;

    public string CountLabel => ShownCount == Rows.Count
        ? $"{Rows.Count:N0} row{(Rows.Count == 1 ? string.Empty : "s")}"
        : $"{ShownCount:N0} of {Rows.Count:N0} rows";

    public bool HasRows => ShownCount > 0;

    public string EmptyHeading => (SelectedTable, Rows.Count > 0, IsBusy) switch
    {
        (null, _, _) => "Choose a table",
        (_, true, _) => "Nothing matches",
        (_, _, true) => "Reading rows",
        _ => "No rows"
    };

    public string EmptyText => (SelectedTable, Rows.Count > 0, IsBusy) switch
    {
        (null, _, _) => "Search for a table on the left by its display name or logical name.",
        (_, true, _) => "No row shown has that text. Clear the quick filter to see every row read.",
        ({ } table, _, true) => $"The rows of {table.Label} are being read.",
        _ when HasServerFilter || ActiveOnly => "No row matches the filters. Remove a filter, or untick Active only.",
        ({ } table, _, _) => $"{table.Label} has no rows."
    };

    // ---------------------------------------------------------------- reading

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    private string _readSummary = string.Empty;
    /// <summary>"Read 214 rows of Tax code (new_taxcode) in 0.8 s".</summary>
    public string ReadSummary
    {
        get => _readSummary;
        private set
        {
            if (SetProperty(ref _readSummary, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    public string StatusLine => string.IsNullOrEmpty(Status) ? ReadSummary : Status;

    private string _warnings = string.Empty;
    /// <summary>A read the row limit cut short - the warning banner.</summary>
    public string Warnings
    {
        get => _warnings;
        private set => SetProperty(ref _warnings, value);
    }

    /// <summary>Reads the environment's tables, then opens the table the window was opened for.</summary>
    public async Task LoadAsync()
    {
        await RunAsync("Could not read the tables", async (client, ct) =>
        {
            Status = "Reading the tables...";
            var tables = await client.GetEntitiesAsync(ct);
            ct.ThrowIfCancellationRequested();

            Tables.Clear();
            foreach (var table in tables.Where(t => t.IsReadable)) Tables.Add(table);

            ReadSummary = $"{Tables.Count:N0} tables - choose one to read its rows.";
            Status = string.Empty;
        });

        if (_initialTable is { Length: > 0 } name && SelectedTable is null)
        {
            var match = Tables.FirstOrDefault(t => t.LogicalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Status = $"{name} is not among the tables whose rows can be read here.";
                return;
            }

            TableSearch = match.LogicalName;
            SelectedTable = match;
            await Pending;
        }
    }

    /// <summary>The window's open reads; tests await this rather than the fire-and-forget the setters start.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    private Task OpenTableAsync(EntitySummary table) => Pending = RunAsync($"Could not read {table.Label}", async (client, ct) =>
    {
        Status = $"Reading the columns of {table.Label}...";
        _records = [];
        Rows.ReplaceAll([]);
        Warnings = string.Empty;
        ClearFilters();
        _lookupNames.Clear();
        FilterValue = string.Empty;
        QuickFilter = string.Empty;

        var columns = await client.GetEntityColumnsAsync(table.LogicalName, ct);
        var view = await client.GetDefaultViewColumnsAsync(table.LogicalName, ct);
        ct.ThrowIfCancellationRequested();

        SetColumns(table, columns, view);
        await ReadRowsAsync(client, table, ct);
    });

    /// <summary>Reads the chosen table's rows again - after a column, filter or limit change.</summary>
    public Task LoadRowsAsync()
    {
        if (SelectedTable is not { } table) return Task.CompletedTask;
        return Pending = RunAsync($"Could not read the rows of {table.Label}", (client, ct) => ReadRowsAsync(client, table, ct));
    }

    private async Task ReadRowsAsync(DataverseClient client, EntitySummary table, CancellationToken ct)
    {
        var columns = Columns.Where(c => c.IsShown).ToList();
        if (columns.Count == 0)
        {
            Status = "Tick at least one column to show.";
            return;
        }

        var filter = EffectiveFilter;
        Status = $"Reading {table.Label}...";
        var clock = Stopwatch.StartNew();

        var result = await client.GetTableRowsAsync(table, columns.Select(c => c.SelectName), filter, RowCap,
            n => Status = $"Reading {table.Label} - {n:N0} rows so far...", ct);
        ct.ThrowIfCancellationRequested();

        _records = result.Rows;
        Rows.ReplaceAll([]);
        ShownColumns = columns;
        BuildRows(columns);
        IsColumnPickerOpen = false;

        Warnings = result.IsTruncated
            ? $"Only the first {RowCap:N0} rows were read - {table.Label} has more{(filter is null ? string.Empty : " that match the filter")}. " +
              "Add a server filter, or raise the row limit, to see the rest."
            : string.Empty;

        var filtered = filter is null ? string.Empty : " matching the filters";
        ReadSummary = $"Read {result.Rows.Count:N0} row{(result.Rows.Count == 1 ? string.Empty : "s")}{filtered} of {table.Label} " +
                      $"in {clock.Elapsed.TotalSeconds:0.0} s";
        Status = string.Empty;
    }

    /// <summary>One read at a time: a newer one stops the one before, and only the newest reports.</summary>
    private async Task RunAsync(string failure, Func<DataverseClient, CancellationToken, Task> work)
    {
        if (_session.Client is not { } client) return;

        var superseded = _cts;
        var cts = _cts = new CancellationTokenSource();
        if (superseded is not null) await superseded.CancelAsync();
        IsBusy = true;

        try
        {
            await work(client, cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cts, cts)) Status = "Stopped.";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_cts, cts)) Status = failure + " - " + ex.Message;
        }
        finally
        {
            // Only a newer read keeps the flag; a closed window (no source left) clears it too.
            if (_cts is null || ReferenceEquals(_cts, cts)) IsBusy = false;
            RefreshRows();
        }
    }

    // ---------------------------------------------------------------- copy

    /// <summary>The row of the cell last clicked or right-clicked in the grid.</summary>
    public TableDataRow? CurrentRow { get; private set; }

    /// <summary>The position in <see cref="ShownColumns"/> of the cell last clicked; -1 for none.</summary>
    public int CurrentColumn { get; private set; } = -1;

    /// <summary>Told by the grid which cell is current, so the copy commands know what to copy.</summary>
    public void SetCurrentCell(TableDataRow? row, int column)
    {
        CurrentRow = row;
        CurrentColumn = row is null || column < 0 || column >= ShownColumns.Count ? -1 : column;
        CopyValueCommand.RaiseCanExecuteChanged();
        CopyRawValueCommand.RaiseCanExecuteChanged();
        CopyColumnCommand.RaiseCanExecuteChanged();
        CopyRowIdCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The value shown, or the raw one - a lookup's id, a choice's number.</summary>
    private void CopyCell(bool raw)
    {
        if (CurrentRow is not { } row || CurrentColumn < 0) return;

        var column = ShownColumns[CurrentColumn];
        Copy(Cell(row.Record, column.SelectName, raw) ?? string.Empty,
            $"{column.Header}{(raw ? " (raw)" : string.Empty)} of {Describe(row)}");
    }

    /// <summary>The column's value in every row shown, one per line - for a list of codes or ids.</summary>
    private void CopyColumn()
    {
        if (CurrentColumn < 0) return;

        var rows = RowsView.Cast<TableDataRow>().ToList();
        Copy(string.Join(Environment.NewLine, rows.Select(r => r.Cells[CurrentColumn] ?? string.Empty)),
            $"{ShownColumns[CurrentColumn].Header} of {rows.Count:N0} row(s)");
    }

    private void CopyRowId()
    {
        if (CurrentRow is not { } row) return;
        Copy(row.Record.Id.ToString(), $"the id of {Describe(row)}");
    }

    private static string Describe(TableDataRow row) =>
        string.IsNullOrWhiteSpace(row.Record.PrimaryName) ? "the row" : row.Record.PrimaryName;

    private void Copy(string text, string what) =>
        Status = ClipboardText.TryCopy(text, out var failure)
            ? $"Copied {what}."
            : $"Could not copy {what} - {failure}";

    // ---------------------------------------------------------------- export

    /// <summary>The headers and cells on screen, after both filters.</summary>
    internal (IReadOnlyList<string> Headers, List<IReadOnlyList<string?>> Rows) Shown() =>
        (ShownColumns.Select(c => c.Header).ToList(), RowsView.Cast<TableDataRow>().Select(r => r.Cells).ToList());

    /// <summary>What a Markdown export says above its table.</summary>
    internal IEnumerable<string> ExportNotes()
    {
        yield return $"Environment: {_session.Title}";
        foreach (var filter in ServerFilters) yield return $"Filter: {filter.Label}";
        if (ActiveOnly && HasStateCode) yield return "Active rows only";
        if (!string.IsNullOrWhiteSpace(QuickFilter)) yield return $"Quick filter: {QuickFilter.Trim()}";
        yield return $"Rows: {CountLabel}";
        if (!string.IsNullOrEmpty(Warnings)) yield return Warnings;
    }

    private void Export(bool markdown)
    {
        if (SelectedTable is not { } table) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = markdown ? "Markdown (*.md)|*.md" : "CSV file (*.csv)|*.csv",
            FileName = $"{table.LogicalName}-{_session.Title}.{(markdown ? "md" : "csv")}".Replace(' ', '-')
        };
        if (dialog.ShowDialog() != true) return;

        var (headers, rows) = Shown();

        try
        {
            if (markdown)
            {
                File.WriteAllText(dialog.FileName, TableDataExport.Markdown(table.Label, ExportNotes(), headers, rows));
            }
            else
            {
                CsvExporter.WriteLines(dialog.FileName, TableDataExport.CsvLines(headers, rows));
            }

            Status = $"Exported {rows.Count:N0} row(s) to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not export - " + ex.Message;
        }
    }
}
