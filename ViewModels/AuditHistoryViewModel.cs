using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// One row of a table whose audit history is shown - from the table data window.
/// </summary>
/// <param name="Name">The row's primary name, where it was read.</param>
/// <param name="ColumnNames">The table's columns' display names by logical name, so each change names its column as the app does.</param>
/// <param name="CreatedOn">When the row was created, where it was read - to tell whether the history reaches back that far.</param>
public sealed record AuditedRow(
    EntitySummary Table,
    Guid Id,
    string? Name,
    IReadOnlyDictionary<string, string> ColumnNames,
    DateTimeOffset? CreatedOn = null);

/// <summary>
/// Who changed a row and how - a flow turned off, a step disabled, a variable's value replaced, a
/// table row's columns edited - from Dataverse auditing, where it is on.
/// </summary>
public sealed class AuditHistoryViewModel : ObservableObject, IDisposable
{
    private Func<Action<int, int>, CancellationToken, Task<IReadOnlyList<AuditRecord>>> _read;
    private DateTimeOffset? _createdOn;
    private readonly string _typeName;
    private readonly DataverseClient _client;
    private CancellationTokenSource? _cts;
    private string? _table;
    private bool _readFailed;

    /// <summary>A row named by a link or by its table and id, whose table and row are found as the window loads.</summary>
    private readonly RecordReference? _reference;

    public AuditHistoryViewModel(EnvironmentSessionViewModel session, DataverseClient client, SolutionComponentItem item)
        : this(session, client, item.PrimaryLabel,
            string.IsNullOrWhiteSpace(item.SubType) ? item.ComponentTypeName : $"{item.SubType} · {item.ComponentTypeName}",
            (string.IsNullOrWhiteSpace(item.SubType) ? item.ComponentTypeName : item.SubType).ToLowerInvariant(),
            DataverseClient.AuditTable(item.ComponentType), item.CreatedOn,
            (_, ct) => client.GetAuditHistoryAsync(item.ComponentType, item.ObjectId, ct))
    {
    }

    /// <summary>Any row of any table, its changes' values formatted and its columns named by <see cref="AuditedRow.ColumnNames"/>.</summary>
    public AuditHistoryViewModel(EnvironmentSessionViewModel session, DataverseClient client, AuditedRow row)
        : this(session, client, string.IsNullOrWhiteSpace(row.Name) ? row.Id.ToString() : row.Name,
            row.Table.Label, "row", row.Table.LogicalName, row.CreatedOn, ReadRow(client, row))
    {
    }

    /// <summary>
    /// Any row from a link to it, or its table and id: the window opens at once, then finds the
    /// table and the row's name. A row since deleted still has its history.
    /// </summary>
    public AuditHistoryViewModel(EnvironmentSessionViewModel session, DataverseClient client, RecordReference reference)
        : this(session, client, reference.Id.ToString(), reference.Table, "row", null, null,
            (_, _) => Task.FromResult<IReadOnlyList<AuditRecord>>([]))
    {
        _reference = reference;
    }

    private static Func<Action<int, int>, CancellationToken, Task<IReadOnlyList<AuditRecord>>> ReadRow(DataverseClient client, AuditedRow row) =>
        async (progress, ct) => Named(await client.GetRecordAuditHistoryAsync(row.Table.LogicalName, row.Id, progress, ct), row.ColumnNames);

    private AuditHistoryViewModel(EnvironmentSessionViewModel session, DataverseClient client, string name, string typeLabel,
        string typeName, string? table, DateTimeOffset? createdOn,
        Func<Action<int, int>, CancellationToken, Task<IReadOnlyList<AuditRecord>>> read)
    {
        Session = session;
        _client = client;
        _name = name;
        _typeLabel = typeLabel;
        _typeName = typeName;
        _table = table;
        _createdOn = createdOn;
        _read = read;
        _title = $"Audit history — {name} ({session.Title})";
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        ExportCsvCommand = new RelayCommand(_ => Export(markdown: false), _ => HasRecords);
        ExportMarkdownCommand = new RelayCommand(_ => Export(markdown: true), _ => HasRecords);
    }

    /// <summary>Each change's columns by their display names, where the table has one for them.</summary>
    internal static IReadOnlyList<AuditRecord> Named(IReadOnlyList<AuditRecord> records, IReadOnlyDictionary<string, string> columnNames) =>
        records.Select(r => r.WithChanges(r.Changes
                .Select(c => columnNames.TryGetValue(c.Column, out var name) ? c with { DisplayName = name } : c)
                .ToList()))
            .ToList();

    /// <summary>The tab the window was opened from, for the header's environment line.</summary>
    public EnvironmentSessionViewModel Session { get; }

    private string _title;
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    private string _name;
    /// <summary>What the history is of, under the heading: its name, then what it is.</summary>
    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    private string _typeLabel;
    public string TypeLabel
    {
        get => _typeLabel;
        private set => SetProperty(ref _typeLabel, value);
    }

    public ObservableCollection<AuditRecord> Records { get; } = new();

    /// <summary>A variable's history covers its definition and its value rows, so it says which; one row needs no column.</summary>
    public bool ShowsRow => Records.Any(r => r.Row is not null);

    public bool HasRecords => Records.Count > 0;

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }

    private string _auditState = string.Empty;
    /// <summary>Whether auditing is recording changes, and whether the history shown is all of it.</summary>
    public string AuditState
    {
        get => _auditState;
        private set => SetProperty(ref _auditState, value);
    }

    private bool _isAuditOff;
    /// <summary>Off for the environment or the table, or not known: nothing new is being recorded.</summary>
    public bool IsAuditOff
    {
        get => _isAuditOff;
        private set
        {
            if (SetProperty(ref _isAuditOff, value)) OnPropertyChanged(nameof(ShowsAuditBanner));
        }
    }

    private bool _isAuditPartial;
    /// <summary>
    /// The history starts after the component was created and has no record of its creation: changes
    /// made before the oldest one shown were not recorded - auditing was off then - or have been deleted.
    /// </summary>
    public bool IsAuditPartial
    {
        get => _isAuditPartial;
        private set
        {
            if (SetProperty(ref _isAuditPartial, value)) OnPropertyChanged(nameof(ShowsAuditBanner));
        }
    }

    /// <summary>The auditing state is a warning when it means changes are missing, else a quiet line.</summary>
    public bool ShowsAuditBanner => IsAuditOff || IsAuditPartial;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string CountLabel => Records.Count == 1 ? "1 change" : $"{Records.Count:N0} changes";

    public string EmptyHeading => (IsBusy, _readFailed) switch
    {
        (true, _) => "Reading the audit history",
        (_, true) => "The audit history was not read",
        _ => "No audited changes"
    };

    /// <summary>Why the list is empty: auditing off, nothing changed, or the read failed (the status bar says why).</summary>
    public string EmptyText => (IsBusy, _readFailed) switch
    {
        (true, _) => string.Empty,
        (_, true) => Status,
        _ when IsAuditOff => $"Auditing is not recording changes to this {TypeName}, so there is nothing to show.",
        _ => $"Nothing about this {TypeName} has changed while auditing was on."
    };

    private string TypeName => _typeName;

    public async Task LoadAsync()
    {
        if (_table is null && _reference is null) return;

        if (_cts is { } running) await running.CancelAsync();
        var cts = _cts = new CancellationTokenSource();
        _readFailed = false;
        IsBusy = true;

        try
        {
            if (_table is null && !await FindRowAsync(_reference!, cts.Token))
            {
                _readFailed = true;
                return;
            }

            Status = "Reading the audit history…";
            var clock = Stopwatch.StartNew();
            var state = await _client.GetAuditStatusAsync(_table!, cts.Token);
            var records = await _read((done, of) => Status = $"Reading the values of the changes - {done:N0} of {of:N0}…", cts.Token);
            if (cts.IsCancellationRequested) return;

            Records.Clear();
            foreach (var record in records) Records.Add(record);
            OnPropertyChanged(nameof(ShowsRow));
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(CountLabel));
            ExportCsvCommand.RaiseCanExecuteChanged();
            ExportMarkdownCommand.RaiseCanExecuteChanged();

            IsAuditOff = !state.IsOn;
            IsAuditPartial = IsPartial(records, _createdOn);
            AuditState = Describe(state, records);
            OnPropertyChanged(nameof(EmptyText));

            Status = $"Read {records.Count:N0} audit record{(records.Count == 1 ? string.Empty : "s")} in {clock.Elapsed.TotalSeconds:0.0} s";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (DataverseException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _readFailed = true;
            Status = "You cannot read audit history here - it needs the View Audit History privilege.";
        }
        catch (Exception ex)
        {
            _readFailed = true;
            Status = "Could not read the audit history - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                IsBusy = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// The table a link or a typed name means, the row's name and creation date, and its columns'
    /// display names - what the table data window knows already. False, saying why, when there is no such table.
    /// </summary>
    private async Task<bool> FindRowAsync(RecordReference reference, CancellationToken ct)
    {
        Status = "Finding the row…";
        if (await _client.FindTableAsync(reference.Table, ct) is not { } table)
        {
            Status = reference.TypeCode is { } code
                ? $"There is no table with type code {code} in {Session.Title}."
                : $"There is no table named '{reference.Table}' in {Session.Title} - check its logical name.";
            return false;
        }

        var columns = await _client.GetEntityColumnsAsync(table.LogicalName, ct);
        var names = columns.Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
            .ToDictionary(c => c.LogicalName, c => c.DisplayName!, StringComparer.OrdinalIgnoreCase);
        var hasCreatedOn = columns.Any(c => string.Equals(c.LogicalName, "createdon", StringComparison.OrdinalIgnoreCase));
        var heading = await _client.GetRowHeadingAsync(table, reference.Id, hasCreatedOn, ct);

        var row = new AuditedRow(table, reference.Id, heading.Name, names, heading.CreatedOn);
        _table = table.LogicalName;
        _createdOn = row.CreatedOn;
        _read = ReadRow(_client, row);
        Name = string.IsNullOrWhiteSpace(row.Name) ? row.Id.ToString() : row.Name;
        TypeLabel = heading.Exists == false ? $"{table.Label} · not there now - deleted?" : table.Label;
        Title = $"Audit history — {Name} ({Session.Title})";
        return true;
    }

    /// <summary>
    /// History that has no Create and starts after the component was created is missing its start.
    /// Without a creation date, or with nothing recorded at all, there is no telling.
    /// </summary>
    internal static bool IsPartial(IReadOnlyList<AuditRecord> records, DateTimeOffset? createdOn) =>
        records.Count > 0 && !records.Any(r => r.IsCreate) &&
        createdOn is { } created && records.Min(r => r.When) > created.AddMinutes(1);

    private string Describe(AuditStatus state, IReadOnlyList<AuditRecord> records)
    {
        var table = _table ?? string.Empty;
        if (!IsAuditPartial) return state.Describe(table);

        var oldest = records.Min(r => r.When).ToLocalTime();
        var created = _createdOn!.Value.ToLocalTime();
        var missing = $"The audited changes start on {oldest:yyyy-MM-dd}, but this {TypeName} was created on {created:yyyy-MM-dd}: " +
                      "changes made before then were not recorded, or their records have been deleted.";
        return state.IsOn ? missing : state.Describe(table) + " " + missing;
    }

    /// <summary>What a Markdown export says above its table.</summary>
    internal IEnumerable<string> ExportNotes()
    {
        yield return $"{TypeLabel}: {Name}";
        yield return $"Environment: {Session.Title}";
        if (!string.IsNullOrEmpty(AuditState)) yield return AuditState;
        yield return $"Changes: {CountLabel}";
    }

    /// <summary>Every change listed, one line per column changed, its values whole rather than shortened.</summary>
    private void Export(bool markdown)
    {
        var name = string.Concat($"audit-{Name}-{Session.Title}".Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = markdown ? "Markdown (*.md)|*.md" : "CSV file (*.csv)|*.csv",
            FileName = $"{name}.{(markdown ? "md" : "csv")}"
        };
        if (dialog.ShowDialog() != true) return;

        var headers = AuditHistoryExport.Headers(ShowsRow);
        var rows = AuditHistoryExport.Rows(Records, ShowsRow);

        try
        {
            if (markdown)
            {
                File.WriteAllText(dialog.FileName, TableDataExport.Markdown($"Audit history — {Name}", ExportNotes(), headers, rows));
            }
            else
            {
                CsvExporter.WriteLines(dialog.FileName, TableDataExport.CsvLines(headers, rows));
            }

            Status = $"Exported {CountLabel} to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not export - " + ex.Message;
        }
    }

    /// <summary>Stops a read still running when the window closes; nobody is left to see it.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
