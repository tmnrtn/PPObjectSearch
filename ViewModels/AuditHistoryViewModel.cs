using System.Collections.ObjectModel;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// Who changed a configuration row and how - a flow turned off, a step disabled, a variable's
/// value replaced - from Dataverse auditing, where it is on.
/// </summary>
public sealed class AuditHistoryViewModel : ObservableObject
{
    private readonly DataverseClient _client;
    private readonly SolutionComponentItem _item;
    private CancellationTokenSource? _cts;

    public AuditHistoryViewModel(DataverseClient client, SolutionComponentItem item, string environment)
    {
        _client = client;
        _item = item;
        Title = $"Audit history — {item.PrimaryLabel} ({environment})";
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
    }

    public string Title { get; }
    public string Heading => $"{_item.ComponentTypeName}: {_item.PrimaryLabel}";

    public ObservableCollection<AuditRecord> Records { get; } = new();
    public bool ShowsRow => Records.Any(r => r.Row is not null);

    public AsyncRelayCommand RefreshCommand { get; }

    private string _auditState = string.Empty;
    /// <summary>Whether auditing is recording changes, said above the list.</summary>
    public string AuditState
    {
        get => _auditState;
        private set => SetProperty(ref _auditState, value);
    }

    private bool _isAuditOff;
    public bool IsAuditOff
    {
        get => _isAuditOff;
        private set => SetProperty(ref _isAuditOff, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task LoadAsync()
    {
        if (DataverseClient.AuditTable(_item.ComponentType) is not { } table) return;

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;

        try
        {
            Status = "Reading the audit history...";
            var state = await _client.GetAuditStatusAsync(table, cts.Token);
            AuditState = state.Describe(table);
            IsAuditOff = !state.IsOn;

            var records = await _client.GetAuditHistoryAsync(_item.ComponentType, _item.ObjectId, cts.Token);
            if (cts.IsCancellationRequested) return;

            Records.Clear();
            foreach (var record in records) Records.Add(record);
            OnPropertyChanged(nameof(ShowsRow));

            Status = records.Count == 0
                ? "No audited changes were found for this row."
                : $"{records.Count:N0} audited change(s), newest first.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (DataverseException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Status = "You cannot read audit history here - it needs the View Audit History privilege.";
        }
        catch (Exception ex)
        {
            Status = "Could not read the audit history - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsBusy = false;
        }
    }

    public void Detach() => _cts?.Cancel();
}
