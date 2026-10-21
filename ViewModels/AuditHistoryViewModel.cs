using System.Collections.ObjectModel;
using System.Diagnostics;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// Who changed a configuration row and how - a flow turned off, a step disabled, a variable's
/// value replaced - from Dataverse auditing, where it is on.
/// </summary>
public sealed class AuditHistoryViewModel : ObservableObject, IDisposable
{
    private readonly DataverseClient _client;
    private readonly SolutionComponentItem _item;
    private CancellationTokenSource? _cts;
    private string? _table;
    private bool _readFailed;

    public AuditHistoryViewModel(EnvironmentSessionViewModel session, DataverseClient client, SolutionComponentItem item)
    {
        Session = session;
        _client = client;
        _item = item;
        Title = $"Audit history — {item.PrimaryLabel} ({session.Title})";
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
    }

    /// <summary>The tab the window was opened from, for the header's environment line.</summary>
    public EnvironmentSessionViewModel Session { get; }

    public string Title { get; }

    /// <summary>The component, under the heading: its name, then what it is.</summary>
    public string Name => _item.PrimaryLabel;
    public string TypeLabel => string.IsNullOrWhiteSpace(_item.SubType)
        ? _item.ComponentTypeName
        : $"{_item.SubType} · {_item.ComponentTypeName}";

    public ObservableCollection<AuditRecord> Records { get; } = new();

    /// <summary>A variable's history covers its definition and its value rows, so it says which; one row needs no column.</summary>
    public bool ShowsRow => Records.Any(r => r.Row is not null);

    public bool HasRecords => Records.Count > 0;

    public AsyncRelayCommand RefreshCommand { get; }

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

    private string TypeName => (string.IsNullOrWhiteSpace(_item.SubType) ? _item.ComponentTypeName : _item.SubType).ToLowerInvariant();

    public async Task LoadAsync()
    {
        if (DataverseClient.AuditTable(_item.ComponentType) is not { } table) return;
        _table = table;

        if (_cts is { } running) await running.CancelAsync();
        var cts = _cts = new CancellationTokenSource();
        _readFailed = false;
        IsBusy = true;

        try
        {
            Status = "Reading the audit history...";
            var clock = Stopwatch.StartNew();
            var state = await _client.GetAuditStatusAsync(table, cts.Token);
            var records = await _client.GetAuditHistoryAsync(_item.ComponentType, _item.ObjectId, cts.Token);
            if (cts.IsCancellationRequested) return;

            Records.Clear();
            foreach (var record in records) Records.Add(record);
            OnPropertyChanged(nameof(ShowsRow));
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(CountLabel));

            IsAuditOff = !state.IsOn;
            IsAuditPartial = IsPartial(records, _item.CreatedOn);
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
        var created = _item.CreatedOn!.Value.ToLocalTime();
        var missing = $"The audited changes start on {oldest:yyyy-MM-dd}, but this {TypeName} was created on {created:yyyy-MM-dd}: " +
                      "changes made before then were not recorded, or their records have been deleted.";
        return state.IsOn ? missing : state.Describe(table) + " " + missing;
    }

    /// <summary>Stops a read still running when the window closes; nobody is left to see it.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
