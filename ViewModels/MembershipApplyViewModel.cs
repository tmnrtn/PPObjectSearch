using System.Collections.ObjectModel;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

public enum MembershipChangeKind
{
    Add,
    Remove
}

/// <summary>One user to add to or remove from a team or queue, and why.</summary>
public sealed record MembershipChange(
    MembershipChangeKind Kind,
    Guid SystemUserId,
    string Name,
    string? Upn,
    string Reason,
    bool IncludedByDefault = true);

/// <summary>A change in the confirmation list: whether it is ticked, and once run, what became of it.</summary>
public sealed class MembershipChangeRow : ObservableObject
{
    public MembershipChangeRow(MembershipChange change, bool canToggle)
    {
        Change = change;
        CanToggle = canToggle;
        // A forecast is what Dataverse will do regardless, so every row of one counts.
        _isChecked = !canToggle || change.IncludedByDefault;
    }

    public MembershipChange Change { get; }
    public MembershipChangeKind Kind => Change.Kind;
    public string Name => Change.Name;
    public string? Upn => Change.Upn;
    public string Reason => Change.Reason;
    public bool IsRemove => Kind == MembershipChangeKind.Remove;

    /// <summary>False where Dataverse decides what happens and the rows are only a forecast.</summary>
    public bool CanToggle { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value)) CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _isEditable;
    /// <summary>Tickable now: not a forecast, and no run has started.</summary>
    public bool IsEditable
    {
        get => _isEditable;
        set => SetProperty(ref _isEditable, value && CanToggle);
    }

    private bool _isKindEnabled = true;
    /// <summary>Whether this row's card (Add or Remove) is switched on.</summary>
    public bool IsKindEnabled
    {
        get => _isKindEnabled;
        set
        {
            if (!SetProperty(ref _isKindEnabled, value)) return;
            OnPropertyChanged(nameof(IsIncluded));
            OnPropertyChanged(nameof(ResultLabel));
        }
    }

    /// <summary>Ticked and in an action that is switched on: this row will be written.</summary>
    public bool IsIncluded => IsKindEnabled && IsChecked;

    internal event EventHandler? CheckedChanged;

    private string _result = "Pending";
    public string Result
    {
        get => _result;
        set
        {
            if (SetProperty(ref _result, value)) OnPropertyChanged(nameof(ResultLabel));
        }
    }

    public string ResultLabel => Succeeded is null && !IsIncluded ? "Skipped" : Result;

    private bool? _succeeded;
    /// <summary>True done, false failed, null not run. A forecast that has not landed yet is also null.</summary>
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

    private bool _isPending;
    /// <summary>A forecast the run did not (yet) bring about - amber rather than red.</summary>
    public bool IsPending
    {
        get => _isPending;
        set => SetProperty(ref _isPending, value);
    }
}

/// <summary>What the confirmation window is being asked to do, and how.</summary>
public sealed class MembershipApplyRequest
{
    public required string Operation { get; init; }

    /// <summary>What is being changed, e.g. "Team  Sales (EMEA)".</summary>
    public required string TargetName { get; init; }
    public required string TargetKind { get; init; }

    /// <summary>What it is being made to match, e.g. "Entra group  Sales-RBAC".</summary>
    public required string SourceName { get; init; }
    public required string SourceKind { get; init; }

    public required string EnvironmentName { get; init; }
    public required string EnvironmentHost { get; init; }

    /// <summary>Who is signed in, for the run log.</summary>
    public string? Account { get; init; }

    /// <summary>Where the run log goes; the default folder unless a test says otherwise.</summary>
    public string? WriteLogFolder { get; init; }
    public required WritePermission Permission { get; init; }

    public required IReadOnlyList<MembershipChange> Changes { get; init; }

    /// <summary>Writes one change. Set this or <see cref="ApplyAll"/>.</summary>
    public Func<MembershipChange, CancellationToken, Task>? ApplyEach { get; init; }

    /// <summary>
    /// One call that makes all the changes itself (SyncGroupMembersToTeam). The rows are then a
    /// forecast, cannot be ticked individually, and are checked against <see cref="ReadMemberIds"/>.
    /// </summary>
    public Func<CancellationToken, Task>? ApplyAll { get; init; }

    /// <summary>The target's members after the run, to report what actually changed.</summary>
    public required Func<CancellationToken, Task<IReadOnlySet<Guid>>> ReadMemberIds { get; init; }

    /// <summary>Anything the person confirming should know first.</summary>
    public string? Note { get; init; }

    /// <summary>What a successful Add row says, where "Added" would overstate it.</summary>
    public string AddedResult { get; init; } = "Added";

    /// <summary>The verb for Add rows on the button and card, e.g. "Pull in".</summary>
    public string AddVerb { get; init; } = "Add";

    /// <summary>
    /// One more step after the per-row changes, run only if at least one of them succeeded - e.g.
    /// a team sync once users have been provisioned. Its outcome is added to the run's status.
    /// </summary>
    public Func<CancellationToken, Task>? AfterAll { get; init; }

    /// <summary>What <see cref="AfterAll"/> is called in the status, e.g. "SyncGroupMembersToTeam".</summary>
    public string AfterAllLabel { get; init; } = "Follow-up";
}

/// <summary>
/// The preview and the confirmation, in one window. Every change is listed before anything is
/// written; nothing is written until Apply; Apply is unavailable while the write guard refuses the
/// environment, and while any removal in the run has not been acknowledged separately.
/// </summary>
public sealed class MembershipApplyViewModel : ObservableObject
{
    private readonly MembershipApplyRequest _request;
    private WriteLog? _log;
    private CancellationTokenSource? _cts;

    public MembershipApplyViewModel(MembershipApplyRequest request)
    {
        _request = request;

        foreach (var change in request.Changes.OrderBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new MembershipChangeRow(change, canToggle: !IsForecast);
            row.CheckedChanged += (_, _) => Rebuild();
            Rows.Add(row);
        }

        AvailableAdds = Rows.Count(r => r.Kind == MembershipChangeKind.Add);
        AvailableRemoves = Rows.Count(r => r.Kind == MembershipChangeKind.Remove);

        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => CanApply);
        CancelRunCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);
        ExportPlanCommand = new RelayCommand(_ => ExportPlan());
        OpenRunLogCommand = new RelayCommand(_ => OpenRunLog());

        RaiseEditState();
        Rebuild();
    }

    public ObservableCollection<MembershipChangeRow> Rows { get; } = new();

    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand CancelRunCommand { get; }

    /// <summary>The planned changes as CSV, for review before Apply.</summary>
    public RelayCommand ExportPlanCommand { get; }

    public RelayCommand OpenRunLogCommand { get; }

    /// <summary>Where this window's changes are recorded; null until something has been attempted.</summary>
    public string? RunLogPath => _log?.Path;

    public string Title => $"{_request.Operation} — {_request.TargetName}";
    public string Operation => _request.Operation;
    public string TargetName => _request.TargetName;
    public string TargetKind => _request.TargetKind;
    public string SourceName => _request.SourceName;
    public string SourceKind => _request.SourceKind;
    public string EnvironmentName => _request.EnvironmentName;
    public string EnvironmentHost => _request.EnvironmentHost;
    public EnvironmentSku Sku => _request.Permission.Type.Sku;
    public WritePermission Permission => _request.Permission;
    public string GuardMessage => Permission.Reason;
    public bool IsBlocked => !Permission.Allowed;
    public string? Note => _request.Note;
    public string AddVerb => _request.AddVerb;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    /// <summary>The rows forecast what Dataverse will do rather than being written one by one.</summary>
    public bool IsForecast => _request.ApplyAll is not null;
    public bool IsPerRow => !IsForecast;

    public int AvailableAdds { get; }
    public int AvailableRemoves { get; }

    public bool CanEditActions => IsPerRow && !IsRunning && !HasRun && Permission.Allowed;

    private bool _add = true;
    public bool Add
    {
        get => _add;
        set
        {
            if (SetProperty(ref _add, value)) Rebuild();
        }
    }

    private bool _remove = true;
    public bool Remove
    {
        get => _remove;
        set
        {
            if (!SetProperty(ref _remove, value)) return;
            if (!value) RemoveAcknowledged = false;
            Rebuild();
        }
    }

    private bool _removeAcknowledged;
    public bool RemoveAcknowledged
    {
        get => _removeAcknowledged;
        set
        {
            if (!SetProperty(ref _removeAcknowledged, value)) return;
            RaiseApplyState();
        }
    }

    public int AddCount => Rows.Count(r => r.Kind == MembershipChangeKind.Add && r.IsIncluded);
    public int RemoveCount => Rows.Count(r => r.IsRemove && r.IsIncluded);
    public bool HasRemoves => RemoveCount > 0;

    /// <summary>
    /// A forecast sync with nothing expected to change is still worth running - it is how a
    /// lagging team is nudged - so it needs no rows to be allowed.
    /// </summary>
    private bool HasWork => IsForecast || AddCount + RemoveCount > 0;

    public string RemoveAcknowledgement => IsForecast
        ? $"Yes, let Dataverse change the membership of {TargetName} - it is expected to remove {RemoveCount:N0} user(s)."
        : $"Yes, remove {RemoveCount:N0} user(s) from {TargetName}.";

    public string ApplyLabel
    {
        get
        {
            if (HasRemoves && !RemoveAcknowledged) return "Confirm removals to apply";
            if (IsForecast) return "Run sync";

            return (AddCount, RemoveCount) switch
            {
                (0, 0) => "Nothing to apply",
                (var a, 0) => $"{AddVerb} {a:N0}",
                (0, var r) => $"Remove {r:N0}",
                var (a, r) => $"{AddVerb} {a:N0} · remove {r:N0}"
            };
        }
    }

    public bool IsDestructiveApply => HasRemoves && RemoveAcknowledged;

    private bool CanApply =>
        Permission.Allowed && !IsRunning && !HasRun && HasWork && (!HasRemoves || RemoveAcknowledged);

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            ApplyCommand.RaiseCanExecuteChanged();
            CancelRunCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsNotRunning));
            RaiseEditState();
        }
    }

    public bool IsNotRunning => !IsRunning;

    private bool _hasRun;
    /// <summary>One run per window: a second over membership that has just changed would be a guess.</summary>
    public bool HasRun
    {
        get => _hasRun;
        private set
        {
            if (!SetProperty(ref _hasRun, value)) return;
            ApplyCommand.RaiseCanExecuteChanged();
            RaiseEditState();
        }
    }

    /// <summary>The acknowledgement stays usable in a forecast, where the action cards are hidden.</summary>
    public bool CanAcknowledge => !IsRunning && !HasRun && Permission.Allowed;

    public bool HasNoRows => Rows.Count == 0;

    private void RaiseEditState()
    {
        foreach (var row in Rows) row.IsEditable = CanEditActions;
        OnPropertyChanged(nameof(CanEditActions));
        OnPropertyChanged(nameof(CanAcknowledge));
    }

    /// <summary>True once anything may have changed, so the caller knows to read membership again.</summary>
    public bool AnyWritesAttempted { get; private set; }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(FooterText));
        }
    }

    private string _summary = string.Empty;
    public string Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value)) OnPropertyChanged(nameof(FooterText));
        }
    }

    public string FooterText => string.IsNullOrEmpty(Status) ? Summary : Status;

    private void Rebuild()
    {
        foreach (var row in Rows)
        {
            row.IsKindEnabled = row.Kind == MembershipChangeKind.Add ? Add : Remove;
        }

        if (RemoveCount == 0) RemoveAcknowledged = false;

        Summary = IsForecast
            ? $"Expected: {AddCount:N0} added · {RemoveCount:N0} removed. Dataverse decides the actual changes; " +
              "membership is read again afterwards to show what happened."
            : $"{AddCount:N0} to add · {RemoveCount:N0} to remove, one user at a time. Untick anyone to leave them as they are.";

        OnPropertyChanged(nameof(AddCount));
        OnPropertyChanged(nameof(RemoveCount));
        OnPropertyChanged(nameof(HasRemoves));
        OnPropertyChanged(nameof(RemoveAcknowledgement));
        RaiseApplyState();
    }

    private void RaiseApplyState()
    {
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(IsDestructiveApply));
        ApplyCommand.RaiseCanExecuteChanged();
    }

    private async Task ApplyAsync()
    {
        if (!Permission.Allowed) return;

        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        IsRunning = true;

        _log ??= WriteLog.Start("membership", _request.WriteLogFolder);
        OnPropertyChanged(nameof(RunLogPath));

        try
        {
            if (_request.ApplyAll is { } applyAll)
            {
                await RunForecastAsync(applyAll, ct);
            }
            else
            {
                await RunEachAsync(_request.ApplyEach!, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. Changes already made have not been undone.";
        }
        catch (Exception ex)
        {
            Status = "Failed - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();

            IsRunning = false;
            HasRun = true;
            if (_log?.Problem is { } problem) Status += " " + problem;
        }
    }

    private void Record(string action, MembershipChangeRow? row, bool succeeded, string message)
    {
        _log?.Append(new WriteLogEntry
        {
            Run = _log.Run,
            Tool = "membership",
            Environment = EnvironmentHost,
            Account = _request.Account,
            Table = $"{TargetKind}: {TargetName}",
            Id = row?.Change.SystemUserId,
            Action = action,
            Key = row?.Upn,
            Name = row?.Name,
            Succeeded = succeeded,
            Message = message
        });
    }

    private void ExportPlan()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"membership-plan-{TargetName}.csv".Replace(' ', '-')
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = PlanLines();
            CsvExporter.WriteLines(dialog.FileName, lines);
            Status = $"Plan exported - {lines.Count - 1:N0} line(s) to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = "Could not export the plan - " + ex.Message;
        }
    }

    /// <summary>One line per user: what would change, why, and whether it is ticked.</summary>
    internal IReadOnlyList<string> PlanLines()
    {
        var lines = new List<string>
        {
            CsvExporter.Line("Operation", "Environment", TargetKind, SourceKind, "Change", "Included", "User", "UPN",
                "User id", "Reason", "Result")
        };

        foreach (var row in Rows)
        {
            lines.Add(CsvExporter.Line(Operation, EnvironmentHost, TargetName, SourceName,
                row.IsRemove ? "Remove" : AddVerb, row.IsIncluded ? "Yes" : "No", row.Name, row.Upn,
                row.Change.SystemUserId.ToString(), row.Reason, row.ResultLabel));
        }

        return lines;
    }

    private void OpenRunLog()
    {
        var folder = _request.WriteLogFolder ?? WriteLog.DefaultFolder;

        try
        {
            System.IO.Directory.CreateDirectory(folder);

            var start = _log is not null && System.IO.File.Exists(_log.Path)
                ? new System.Diagnostics.ProcessStartInfo(Auth.AppPaths.Explorer, $"/select,\"{_log.Path}\"")
                : new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true };

            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            Status = $"Could not open the run log folder ({folder}) - {ex.Message}";
        }
    }

    private async Task RunEachAsync(Func<MembershipChange, CancellationToken, Task> applyEach, CancellationToken ct)
    {
        var toWrite = Rows.Where(r => r.IsIncluded).ToList();
        int done = 0, succeeded = 0, failed = 0;

        foreach (var row in toWrite)
        {
            ct.ThrowIfCancellationRequested();

            done++;
            Status = $"({done}/{toWrite.Count}) {(row.IsRemove ? "removing" : "adding")} {row.Name}...";
            AnyWritesAttempted = true;

            try
            {
                // Stop takes effect between users; the change in flight is allowed to finish, so
                // its row says what actually happened.
                await applyEach(row.Change, CancellationToken.None);
                row.Succeeded = true;
                row.Result = row.IsRemove ? "Removed" : _request.AddedResult;
                succeeded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                row.Succeeded = false;
                row.Result = ex.Message;
                failed++;
            }

            Record(row.IsRemove ? "Remove" : "Add", row, row.Succeeded == true, row.Result);
        }

        var outcome = failed == 0
            ? $"Done - {succeeded:N0} change(s) made to {TargetName}."
            : $"Finished with problems - {succeeded:N0} made, {failed:N0} failed. Each failure is on its own row.";

        if (succeeded > 0 && _request.AfterAll is { } afterAll)
        {
            Status = $"Running {_request.AfterAllLabel}...";
            try
            {
                await afterAll(ct);
                outcome += $" Then {_request.AfterAllLabel} succeeded.";
                Record(_request.AfterAllLabel, null, true, "Succeeded.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcome += $" Then {_request.AfterAllLabel} failed - {ex.Message}";
                Record(_request.AfterAllLabel, null, false, ex.Message);
            }
        }

        Status = outcome;
    }

    private async Task RunForecastAsync(Func<CancellationToken, Task> applyAll, CancellationToken ct)
    {
        var before = await _request.ReadMemberIds(ct);

        Status = $"Asking Dataverse to sync {TargetName}...";
        AnyWritesAttempted = true;

        try
        {
            await applyAll(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record("Sync", null, false, ex.Message);
            throw;
        }

        Record("Sync", null, true, $"Requested; {before.Count:N0} member(s) before.");

        Status = "Sync requested. Reading the team's membership again...";
        var after = await _request.ReadMemberIds(ct);

        var pending = 0;
        foreach (var row in Rows)
        {
            var isMember = after.Contains(row.Change.SystemUserId);
            var happened = row.IsRemove ? !isMember : isMember;

            if (happened)
            {
                row.Succeeded = true;
                row.Result = row.IsRemove ? "Removed" : "Added";
            }
            else
            {
                row.IsPending = true;
                row.Result = row.IsRemove ? "Still a member" : "Not added";
                pending++;
            }

            Record(row.IsRemove ? "Remove (by sync)" : "Add (by sync)", row, happened, row.Result);
        }

        var added = after.Count(id => !before.Contains(id));
        var removed = before.Count(id => !after.Contains(id));

        Status = $"Sync ran: {before.Count:N0} → {after.Count:N0} members ({added:N0} added, {removed:N0} removed). " +
                 (pending == 0
                     ? "Every expected change has landed."
                     : $"{pending:N0} expected change(s) have not landed - the sync can finish later, so re-read in a few minutes, " +
                       "and use Diagnose for anyone it leaves behind.");
    }
}
