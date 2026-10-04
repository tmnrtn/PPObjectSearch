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
    private CancellationTokenSource? _cts;

    public ReconcileViewModel(
        IReadOnlyList<RecordComparison> selected,
        string sourceName,
        string targetName,
        DataverseClient targetClient,
        IReadOnlyDictionary<string, EntitySummary> targetEntities,
        WritePermission permission,
        EnvironmentSku sourceSku = EnvironmentSku.Unknown)
    {
        _selected = selected;
        _targetClient = targetClient;
        _targetEntities = targetEntities;

        SourceName = sourceName;
        TargetName = targetName;
        SourceSku = sourceSku;
        Permission = permission;

        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => CanApply);
        RetryFailedCommand = new AsyncRelayCommand(
            _ => RunAsync(Rows.Where(r => r.HasFailed).ToList()),
            _ => Permission.Allowed && HasRun && !IsRunning && HasFailures);
        CancelRunCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);

        // Every action the selection could take, planned once; the toggles only include or exclude.
        foreach (var item in ReferenceDataWriter.Plan(_selected, new ReconcileOptions(true, true, true)))
        {
            Rows.Add(new ReconcileRow { Item = item });
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

    public AsyncRelayCommand ApplyCommand { get; }

    /// <summary>Writes the rows that failed once more - after throttling or a dropped connection,
    /// say. Each still carries the version it was compared at, so a row changed since is refused.</summary>
    public AsyncRelayCommand RetryFailedCommand { get; }

    public bool HasFailures => Rows.Any(r => r.HasFailed);
    public RelayCommand CancelRunCommand { get; }

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
        $"Yes, permanently delete {DeleteCount:N0} row(s) from {TargetName}. This cannot be undone.";

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
            OnPropertyChanged(nameof(IsNotRunning));
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
        (!HasDeletes || DeleteAcknowledged);

    private void Rebuild()
    {
        var options = new ReconcileOptions(Create, Update, Delete);
        foreach (var row in Rows) row.IsIncluded = options.Allows(row.Action) && !row.IsBlocked;

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

        var writer = new ReferenceDataWriter(_targetClient, _targetEntities);
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
        }
    }
}
