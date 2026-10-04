using System.Collections.ObjectModel;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>A planned action and, once it has run, what became of it.</summary>
public sealed class ReconcileRow : ObservableObject
{
    public required ReconcilePlanItem Item { get; init; }

    public string Table => Item.Table;
    public string ActionLabel => Item.ActionLabel;
    public string Key => Item.Key;
    public string? Name => Item.Name;
    public string Detail => Item.Detail;
    public bool IsDelete => Item.Action == ReconcileAction.Delete;
    public ReconcileAction Action => Item.Action;

    /// <summary>A row the comparison cannot vouch for: listed, never included, never written.</summary>
    public bool IsBlocked => Item.IsBlocked;

    private bool _isIncluded = true;
    /// <summary>Whether this row's action is switched on. Rows whose action is off stay listed,
    /// faded, so turning an action on shows exactly what it adds - but they are never written.</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (SetProperty(ref _isIncluded, value)) OnPropertyChanged(nameof(ResultLabel));
        }
    }

    private string _result = "Pending";
    public string Result
    {
        get => _result;
        set
        {
            if (SetProperty(ref _result, value)) OnPropertyChanged(nameof(ResultLabel));
        }
    }

    /// <summary>What the Result column shows: the outcome once run, "Skipped" for an action left off.</summary>
    public string ResultLabel =>
        Succeeded is null && IsBlocked ? Item.BlockedReason!
        : Succeeded is null && !IsIncluded ? "Skipped"
        : Result;

    private bool? _succeeded;
    public bool? Succeeded
    {
        get => _succeeded;
        set
        {
            if (!SetProperty(ref _succeeded, value)) return;
            OnPropertyChanged(nameof(HasFailed));
            OnPropertyChanged(nameof(ResultLabel));
        }
    }

    public bool HasFailed => Succeeded == false;
}

/// <summary>A column the run would write, which can be left out of this run only.</summary>
public sealed class ReconcileColumnOption : ObservableObject
{
    public required string Table { get; init; }
    public required EntityColumn Column { get; init; }

    public string Key => ReferenceDataWriter.ColumnKey(Table, Column.LogicalName);
    public string Label => $"{Table} · {Column.Label}";

    private bool _isIncluded = true;
    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }
}

/// <summary>
/// The confirmation and the run, in one window. Nothing is written until Apply is pressed, and
/// Apply is unavailable until the write guard has cleared the target environment - and, where the
/// run includes deletions, until those have been acknowledged separately.
/// </summary>
public sealed class ReconcileViewModel : ObservableObject
{
    private readonly DataverseClient _targetClient;
    private readonly IReadOnlyDictionary<string, EntitySummary> _targetEntities;
    private readonly IReadOnlyList<RecordComparison> _selected;
    private readonly string? _account;
    private readonly string? _writeLogFolder;
    private readonly List<UndoStep> _undo = new();
    private WriteLog? _log;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _impactCts;

    public ReconcileViewModel(
        IReadOnlyList<RecordComparison> selected,
        string sourceName,
        string targetName,
        DataverseClient targetClient,
        IReadOnlyDictionary<string, EntitySummary> targetEntities,
        WritePermission permission,
        EnvironmentSku sourceSku = EnvironmentSku.Unknown,
        string? account = null,
        string? writeLogFolder = null)
    {
        _selected = selected;
        _account = account;
        _writeLogFolder = writeLogFolder;
        _targetClient = targetClient;
        _targetEntities = targetEntities;

        SourceName = sourceName;
        TargetName = targetName;
        SourceSku = sourceSku;
        Permission = permission;

        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => CanApply);
        RetryFailedCommand = new AsyncRelayCommand(
            _ => RunAsync(Rows.Where(r => r.HasFailed).ToList()),
            _ => Permission.Allowed && HasRun && !IsRunning && HasFailures && !_hasUndone);
        CancelRunCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);
        UndoCommand = new AsyncRelayCommand(_ => UndoAsync(), _ => CanUndo);
        ExportPlanCommand = new RelayCommand(_ => ExportPlan());
        OpenRunLogCommand = new RelayCommand(_ => OpenRunLog());

        // Every action the selection could take, planned once; the toggles only include or exclude.
        foreach (var item in ReferenceDataWriter.Plan(_selected, new ReconcileOptions(true, true, true)))
        {
            Rows.Add(new ReconcileRow { Item = item });
        }

        // Every column a create or update here would write, so one can be left out of this run
        // without touching the saved configuration. Keys stay: without them the row would not match.
        foreach (var option in Rows
                     .Where(r => !r.IsBlocked && r.Action != ReconcileAction.Delete)
                     .SelectMany(r => (r.Action == ReconcileAction.Update
                             ? r.Item.Row.Differences.Select(d => d.Column)
                             : r.Item.Row.Plan.ValueColumns)
                         .Where(c => !c.IsPrimaryId && !r.Item.Row.Plan.KeyColumns.Contains(c))
                         .Select(c => (r.Table, Column: c)))
                     .DistinctBy(x => ReferenceDataWriter.ColumnKey(x.Table, x.Column.LogicalName),
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Column.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            var column = new ReconcileColumnOption { Table = option.Table, Column = option.Column };
            column.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ReconcileColumnOption.IsIncluded)) Rebuild();
            };
            Columns.Add(column);
        }

        AvailableCreates = Rows.Count(r => r.Action == ReconcileAction.Create);
        AvailableUpdates = Rows.Count(r => r.Action == ReconcileAction.Update);
        AvailableDeletes = Rows.Count(r => r.Action == ReconcileAction.Delete);

        Rebuild();
    }

    public string SourceName { get; }
    public string TargetName { get; }
    public EnvironmentSku SourceSku { get; }
    public EnvironmentSku TargetSku => Permission.Type.Sku;
    public WritePermission Permission { get; }

    /// <summary>The target's host, under its name in the header.</summary>
    public string TargetHost =>
        Uri.TryCreate(_targetClient.EnvironmentUrl, UriKind.Absolute, out var uri) ? uri.Host : _targetClient.EnvironmentUrl;

    /// <summary>What each action card offers, whether or not it is switched on.</summary>
    public int AvailableCreates { get; }
    public int AvailableUpdates { get; }
    public int AvailableDeletes { get; }

    /// <summary>The action cards lock while a run is going and when the guard refuses the target.</summary>
    public bool CanEditActions => !IsRunning && !HasRun && Permission.Allowed;

    public ObservableCollection<ReconcileRow> Rows { get; } = new();

    /// <summary>The columns this run would write, each of which can be left out of it.</summary>
    public ObservableCollection<ReconcileColumnOption> Columns { get; } = new();

    public bool HasColumns => Columns.Count > 0;

    private IReadOnlySet<string> ExcludedColumns =>
        Columns.Where(c => !c.IsIncluded).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string ColumnsSummary
    {
        get
        {
            var left = Columns.Count(c => !c.IsIncluded);
            return left == 0
                ? $"Columns: all {Columns.Count:N0} written"
                : $"Columns: {left:N0} of {Columns.Count:N0} left out of this run";
        }
    }

    public AsyncRelayCommand ApplyCommand { get; }

    /// <summary>Writes the rows that failed once more - after throttling or a dropped connection,
    /// say. Each still carries the version it was compared at, so a row changed since is refused.</summary>
    public AsyncRelayCommand RetryFailedCommand { get; }

    public bool HasFailures => Rows.Any(r => r.HasFailed);
    public RelayCommand CancelRunCommand { get; }

    /// <summary>Reverses everything this window wrote, newest first, from the copies kept before each write.</summary>
    public AsyncRelayCommand UndoCommand { get; }

    /// <summary>The plan as a CSV, for review or a change ticket before anything is applied.</summary>
    public RelayCommand ExportPlanCommand { get; }

    public RelayCommand OpenRunLogCommand { get; }

    /// <summary>Asks before undoing. Replaced in tests.</summary>
    internal Func<string, bool> Confirm { get; set; } = message =>
        System.Windows.MessageBox.Show(message, "Undo run", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    /// <summary>Where this window's writes are recorded; null until something has been written.</summary>
    public string? RunLogPath => _log?.Path;

    private bool _hasUndone;

    public bool CanUndo => Permission.Allowed && HasRun && !IsRunning && !_hasUndone && _undo.Count > 0;

    public int UndoCount => _undo.Count;

    public string Title => $"Reconcile — {SourceName} → {TargetName}";

    public string TargetDescription =>
        $"{TargetName}  -  {Permission.Type.SkuLabel}" +
        (Permission.IsAllowlisted ? "  (allowlisted for writes)" : string.Empty);

    public string GuardMessage => Permission.Reason;
    public bool IsBlocked => !Permission.Allowed;

    private bool _create = true;
    public bool Create
    {
        get => _create;
        set
        {
            if (SetProperty(ref _create, value)) Rebuild();
        }
    }

    private bool _update = true;
    public bool Update
    {
        get => _update;
        set
        {
            if (SetProperty(ref _update, value)) Rebuild();
        }
    }

    private bool _delete;
    /// <summary>Off to begin with. Deleting is the one action here that destroys data, so it is
    /// never part of a run by default.</summary>
    public bool Delete
    {
        get => _delete;
        set
        {
            if (!SetProperty(ref _delete, value)) return;

            if (!value) DeleteAcknowledged = false;
            Rebuild();

            ImpactCheck = value ? CheckDeleteImpactAsync() : CancelImpactCheck();
        }
    }

    private bool _deleteAcknowledged;
    public bool DeleteAcknowledged
    {
        get => _deleteAcknowledged;
        set
        {
            if (!SetProperty(ref _deleteAcknowledged, value)) return;

            ApplyCommand.RaiseCanExecuteChanged();
            RaiseApplyState();
        }
    }

    /// <summary>The count of what the deletes reach beyond themselves; awaited by tests.</summary>
    internal Task? ImpactCheck { get; private set; }

    private bool _isCheckingImpact;
    /// <summary>While the effect of the deletes is being counted, they cannot be acknowledged.</summary>
    public bool IsCheckingImpact
    {
        get => _isCheckingImpact;
        private set
        {
            if (!SetProperty(ref _isCheckingImpact, value)) return;
            OnPropertyChanged(nameof(CanAcknowledgeDelete));
            ApplyCommand.RaiseCanExecuteChanged();
            RaiseApplyState();
        }
    }

    public bool CanAcknowledgeDelete => IsNotRunning && !IsCheckingImpact;

    private string _deleteImpact = string.Empty;
    /// <summary>What else Dataverse would delete, block on, or unlink.</summary>
    public string DeleteImpactText
    {
        get => _deleteImpact;
        private set
        {
            if (!SetProperty(ref _deleteImpact, value)) return;
            OnPropertyChanged(nameof(HasDeleteImpact));
        }
    }

    public bool HasDeleteImpact => !string.IsNullOrEmpty(DeleteImpactText);

    private Task CancelImpactCheck()
    {
        _impactCts?.Cancel();
        DeleteImpactText = string.Empty;
        IsCheckingImpact = false;
        return Task.CompletedTask;
    }

    private async Task CheckDeleteImpactAsync()
    {
        _impactCts?.Cancel();
        var cts = _impactCts = new CancellationTokenSource();

        var deletes = Rows
            .Where(r => r.IsDelete && r.IsIncluded && r.Item.Row.Target is not null)
            .Select(r => (r.Table, r.Item.Row.Target!.Id))
            .ToList();

        if (deletes.Count == 0) return;

        IsCheckingImpact = true;
        DeleteImpactText = "Checking what else these deletes would reach...";

        try
        {
            var lines = await DeleteImpact.CheckAsync(_targetClient, _targetEntities, deletes, cts.Token);
            if (cts.IsCancellationRequested) return;

            DeleteImpactText = DeleteImpact.Describe(lines);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            DeleteImpactText = "Could not check what else these deletes would reach - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_impactCts, cts)) IsCheckingImpact = false;
        }
    }

    /// <summary>Deletions that will actually run - zero while the Delete action is off.</summary>
    public int DeleteCount => Rows.Count(r => r.IsDelete && r.IsIncluded);
    public bool HasDeletes => DeleteCount > 0;

    private int IncludedCount => Rows.Count(r => r.IsIncluded);

    /// <summary>"Apply 5 changes", or what is still in the way of applying.</summary>
    public string ApplyLabel =>
        HasDeletes && !DeleteAcknowledged ? "Confirm deletion to apply"
        : IncludedCount == 1 ? "Apply 1 change"
        : $"Apply {IncludedCount:N0} changes";

    /// <summary>Acknowledged deletions turn Apply red: this run destroys data.</summary>
    public bool IsDestructiveApply => HasDeletes && DeleteAcknowledged;

    private void RaiseApplyState()
    {
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(IsDestructiveApply));
    }

    public string DeleteAcknowledgement =>
        $"Yes, delete {DeleteCount:N0} row(s) from {TargetName}. A copy of each is kept so Undo can put it back, " +
        "but rows Dataverse removes with them through cascading relationships are not kept.";

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;

            ApplyCommand.RaiseCanExecuteChanged();
            CancelRunCommand.RaiseCanExecuteChanged();
            RetryFailedCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsNotRunning));
            OnPropertyChanged(nameof(CanAcknowledgeDelete));
            OnPropertyChanged(nameof(CanEditActions));
        }
    }

    public bool IsNotRunning => !IsRunning;

    private bool _hasRun;
    /// <summary>Once a run has happened the window stops offering another one - a second Apply
    /// over rows whose state has just changed would be working from a stale comparison.</summary>
    public bool HasRun
    {
        get => _hasRun;
        private set
        {
            if (!SetProperty(ref _hasRun, value)) return;

            ApplyCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanEditActions));
        }
    }

    /// <summary>True once anything was actually written, so the caller knows to re-compare.</summary>
    public bool AnyWritesSucceeded { get; private set; }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(FooterText));
        }
    }

    /// <summary>The run's progress once it starts; until then, what Apply would do.</summary>
    public string FooterText => string.IsNullOrEmpty(Status) ? Summary : Status;

    private string _summary = string.Empty;
    public string Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value)) OnPropertyChanged(nameof(FooterText));
        }
    }

    internal bool CanApply =>
        Permission.Allowed && !IsRunning && !HasRun && IncludedCount > 0 &&
        (!HasDeletes || (DeleteAcknowledged && !IsCheckingImpact));

    private void Rebuild()
    {
        var options = new ReconcileOptions(Create, Update, Delete);
        var excluded = ExcludedColumns;

        foreach (var row in Rows)
        {
            // An update whose every differing column is left out has nothing to write.
            var nothingLeft = row.Action == ReconcileAction.Update && excluded.Count > 0 &&
                              row.Item.Row.Differences.All(d =>
                                  excluded.Contains(ReferenceDataWriter.ColumnKey(row.Table, d.Column.LogicalName)));

            row.IsIncluded = options.Allows(row.Action) && !row.IsBlocked && !nothingLeft;
        }

        OnPropertyChanged(nameof(ColumnsSummary));

        var included = Rows.Where(r => r.IsIncluded).ToList();
        var creates = included.Count(r => r.Action == ReconcileAction.Create);
        var updates = included.Count(r => r.Action == ReconcileAction.Update);
        var deletes = included.Count(r => r.Action == ReconcileAction.Delete);

        var skipped = _selected.Count - Rows.Count;
        var blocked = Rows.Count(r => r.IsBlocked);
        var writes = included.Count == 1 ? "1 write" : $"{included.Count:N0} writes";

        Summary = $"{writes}: {creates:N0} create · {updates:N0} update · {deletes:N0} delete. " +
                  "Only compared columns are written; rows are written one at a time." +
                  (skipped > 0 ? $" {skipped:N0} of the selected rows need nothing." : string.Empty) +
                  (blocked > 0 ? $" {blocked:N0} row(s) cannot be written safely - see Result." : string.Empty);

        OnPropertyChanged(nameof(DeleteCount));
        OnPropertyChanged(nameof(HasDeletes));
        OnPropertyChanged(nameof(DeleteAcknowledgement));
        RaiseApplyState();
        ApplyCommand.RaiseCanExecuteChanged();
    }

    private async Task ApplyAsync()
    {
        // The button already waits for these; the run checks them again rather than trusting it.
        if (!CanApply) return;

        // Only the actions switched on; the faded rows are there to look at, never to write.
        var options = new ReconcileOptions(Create, Update, Delete);
        await RunAsync(Rows.Where(r => r.IsIncluded && !r.IsBlocked && options.Allows(r.Action)).ToList());
    }

    private async Task RunAsync(IReadOnlyList<ReconcileRow> rows)
    {
        if (!Permission.Allowed || rows.Count == 0) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsRunning = true;

        // Every write is kept: the row as it was, what was sent, and how to take it back.
        var writer = new ReferenceDataWriter(_targetClient, _targetEntities)
        {
            SaveSnapshots = true,
            ExcludedColumns = ExcludedColumns
        };
        _log ??= WriteLog.Start("reconcile", _writeLogFolder);
        OnPropertyChanged(nameof(RunLogPath));
        var succeeded = 0;
        var failed = 0;
        var done = 0;

        // Parents before the rows that refer to them, and deletes last - children first - so a
        // run stopped part-way has destroyed as little as possible.
        var toWrite = ReferenceDataWriter.OrderForWriting(rows, r => r.Item);

        try
        {
            foreach (var row in toWrite)
            {
                ct.ThrowIfCancellationRequested();

                done++;
                Status = $"({done}/{toWrite.Count}) {row.ActionLabel.ToLowerInvariant()} {row.Table} {row.Key}...";

                // Stop takes effect between rows: a write cancelled half-way leaves no telling
                // whether it landed, so the one in flight is always allowed to finish.
                var outcome = await writer.ApplyAsync(row.Item, CancellationToken.None);
                Record(outcome);

                row.Succeeded = outcome.Succeeded;
                row.Result = outcome.Message;

                if (outcome.Succeeded)
                {
                    succeeded++;
                    AnyWritesSucceeded = true;
                }
                else
                {
                    failed++;
                }
            }

            Status = failed == 0
                ? $"Done - {succeeded:N0} row(s) written to {TargetName}."
                : $"Finished with problems - {succeeded:N0} written, {failed:N0} failed. " +
                  "Each failure is on its own row.";
        }
        catch (OperationCanceledException)
        {
            // Rows already written stay written; stopping does not roll anything back.
            Status = $"Stopped after {succeeded:N0} row(s). Nothing already written has been undone.";
        }
        finally
        {
            IsRunning = false;
            HasRun = true;
            OnPropertyChanged(nameof(HasFailures));
            RetryFailedCommand.RaiseCanExecuteChanged();
            RaiseUndoState();

            if (_log?.Problem is { } problem) Status += " " + problem;
        }
    }

    private void Record(ReconcileOutcome outcome)
    {
        var item = outcome.Item;
        if (outcome.Undo is { } undo) _undo.Add(undo);

        _log?.Append(new WriteLogEntry
        {
            Run = _log.Run,
            Tool = "reconcile",
            Environment = _targetClient.EnvironmentUrl,
            Account = _account,
            Table = item.Table,
            Id = outcome.Id,
            Action = item.Action.ToString(),
            Key = item.Key,
            Name = item.Name,
            Columns = (item.Action == ReconcileAction.Update
                    ? item.Row.Differences.Select(d => d.Column)
                    : item.Row.Plan.ValueColumns)
                .Select(c => c.LogicalName)
                .Where(c => !ExcludedColumns.Contains(ReferenceDataWriter.ColumnKey(item.Table, c)))
                .ToList(),
            Before = outcome.Before,
            After = WriteUndo.ToJson(outcome.Written),
            Succeeded = outcome.Succeeded,
            Message = outcome.Message,
            Undo = outcome.Undo
        });
    }

    private void RaiseUndoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoCount));
        UndoCommand.RaiseCanExecuteChanged();
    }

    private async Task UndoAsync()
    {
        if (!CanUndo) return;

        if (!Confirm(
                $"Undo {_undo.Count:N0} write(s) in {TargetName}?\n\n" +
                "Deleted rows are re-created with their original ids, updated columns are set back to what they " +
                "held, and created rows are deleted. Anyone else's edits to those columns since the run are " +
                "overwritten."))
        {
            return;
        }

        IsRunning = true;
        var undone = 0;
        var failed = new List<string>();

        try
        {
            // Newest first, so a child created after its parent goes before the parent does, and a
            // parent deleted after its children is back before they are.
            var steps = Enumerable.Reverse(_undo).ToList();

            foreach (var step in steps)
            {
                undone++;
                Status = $"Undoing ({undone}/{steps.Count}) - {step.Label}...";

                string? error = null;
                try
                {
                    await WriteUndo.ApplyAsync(_targetClient, step, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex.Message;
                    failed.Add($"{step.Label}: {ex.Message}");
                }

                _log?.Append(new WriteLogEntry
                {
                    Run = _log.Run,
                    Tool = "reconcile",
                    Environment = _targetClient.EnvironmentUrl,
                    Account = _account,
                    Table = step.EntitySet,
                    Id = step.Id,
                    Action = "Undo" + step.Method,
                    After = step.Body,
                    Succeeded = error is null,
                    Message = error ?? "Undone."
                });
            }

            _hasUndone = true;
            AnyWritesSucceeded = true;

            Status = failed.Count == 0
                ? $"Undone - {steps.Count:N0} write(s) reversed in {TargetName}."
                : $"Undo finished with problems - {failed.Count:N0} of {steps.Count:N0} could not be reversed: " +
                  string.Join("; ", failed.Take(3)) + (failed.Count > 3 ? " ..." : string.Empty);
        }
        finally
        {
            IsRunning = false;
            RaiseUndoState();
        }
    }

    private void ExportPlan()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"reconcile-plan-{SourceName}-{TargetName}.csv".Replace(' ', '-')
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = ReconcilePlanExport.Lines(
                Rows.Select(r => (r.Item, r.IsIncluded, r.ResultLabel)), SourceName, TargetName, ExcludedColumns);
            CsvExporter.WriteLines(dialog.FileName, lines);
            Status = $"Plan exported - {lines.Count - 1:N0} line(s) to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = "Could not export the plan - " + ex.Message;
        }
    }

    private void OpenRunLog()
    {
        var folder = _writeLogFolder ?? WriteLog.DefaultFolder;

        try
        {
            System.IO.Directory.CreateDirectory(folder);

            // Straight to this window's file where there is one; otherwise the folder of past runs.
            var start = _log is not null && System.IO.File.Exists(_log.Path)
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_log.Path}\"")
                : new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true };

            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            Status = $"Could not open the run log folder ({folder}) - {ex.Message}";
        }
    }
}
