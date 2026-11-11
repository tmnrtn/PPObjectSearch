using System.Collections.ObjectModel;
using System.IO;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// The records one user or team owns, table by table - read-only. Only rows owned by the
/// principal itself: what a user's teams own is not counted as theirs. Each table opens in Table
/// data on just those rows.
/// </summary>
public sealed class OwnedRecordsViewModel : ObservableObject, IDisposable
{
    private readonly DataverseClient _client;
    private CancellationTokenSource? _cts;

    public OwnedRecordsViewModel(EnvironmentSessionViewModel session, DataverseClient client, Guid ownerId, string ownerName, bool isTeam)
    {
        Session = session;
        _client = client;
        OwnerId = ownerId;
        OwnerName = ownerName;
        IsTeam = isTeam;

        RunCommand = new AsyncRelayCommand(_ => RunAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        ShowRowsCommand = new RelayCommand(p => ShowRows(p as OwnedRecordCount), p => p is OwnedRecordCount { Failed: false });
        ExportCsvCommand = new RelayCommand(_ => Export(), _ => !IsBusy && Rows.Count > 0);
    }

    /// <summary>The tab the window was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session { get; }

    public Guid OwnerId { get; }
    public string OwnerName { get; }
    public bool IsTeam { get; }
    public string OwnerKind => IsTeam ? "Team" : "User";

    public string Title => $"Owned records — {OwnerName}";

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ShowRowsCommand { get; }
    public RelayCommand ExportCsvCommand { get; }

    /// <summary>The tables the owner has rows in, largest first; then any that could not be counted.</summary>
    public ObservableCollection<OwnedRecordCount> Rows { get; } = new();

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ExportCsvCommand.RaiseCanExecuteChanged();
        }
    }

    private int _done;
    public int Done
    {
        get => _done;
        private set
        {
            if (SetProperty(ref _done, value)) OnPropertyChanged(nameof(Progress));
        }
    }

    private int _total;
    public int Total
    {
        get => _total;
        private set
        {
            if (SetProperty(ref _total, value)) OnPropertyChanged(nameof(Progress));
        }
    }

    /// <summary>The status bar while counting. Apart from <see cref="Status"/>, which a late progress report must not overwrite.</summary>
    public string Progress => Total == 0 ? "Reading which tables have an owner..." : $"Counted {Done:N0} of {Total:N0} tables...";

    private string _summary = string.Empty;
    /// <summary>"Alice owns 1,234 records in 12 tables." - or what is being counted.</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private string _status = string.Empty;
    /// <summary>The status bar: how long the count took, failures, exports.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Counts every table with an owner. Run again from the window to count afresh.</summary>
    public async Task RunAsync()
    {
        if (IsBusy) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        Rows.Clear();
        ExportCsvCommand.RaiseCanExecuteChanged();
        Done = Total = 0;
        Summary = $"Counting the records {OwnerName} owns...";
        Status = string.Empty;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Done = p.Done;
            Total = p.Total;
        });

        try
        {
            var result = await OwnedRecords.CountAsync(_client, OwnerId, progress, ct);

            foreach (var row in result.Owned.Concat(result.Failed)) Rows.Add(row);
            Summary = SummaryOf(result);
            Status = $"Counted {result.TablesCounted:N0} tables with an owner in {clock.Elapsed.TotalSeconds:0.0} s." +
                     (result.Failed.Count > 0 ? $" {result.Failed.Count:N0} could not be counted - they are listed last, with why." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            Summary = "Stopped before every table was counted.";
            Status = "Count again to start over.";
        }
        catch (Exception ex)
        {
            Summary = "Could not count the records.";
            Status = "Could not read which tables have an owner - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal string SummaryOf(OwnedRecordsResult result) => result.Owned.Count == 0
        ? $"{OwnerName} owns no records" + (result.Failed.Count > 0 ? " in the tables that could be counted." : ".")
        : $"{OwnerName} owns {result.TotalRows:N0} record(s) in {result.Owned.Count:N0} table(s).";

    /// <summary>The table in Table data, filtered to this owner's rows.</summary>
    private void ShowRows(OwnedRecordCount? count)
    {
        if (count is not { Failed: false }) return;
        Session.OpenTableData(count.Table.LogicalName, OwnerFilter(OwnerId, OwnerName));
    }

    internal static TableServerFilter OwnerFilter(Guid ownerId, string ownerName) =>
        new($"_ownerid_value eq {ownerId}", $"Owner equals '{ownerName}'");

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"owned-records-{OwnerName}.csv".Replace(' ', '-')
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            CsvExporter.WriteLines(dialog.FileName, CsvLines());
            Status = $"Exported {Rows.Count:N0} table(s) to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not export - " + ex.Message;
        }
    }

    internal IEnumerable<string> CsvLines() =>
        Rows.Select(r => CsvExporter.Line(r.Table.LogicalName, r.Table.DisplayName,
                r.Failed ? string.Empty : r.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Error))
            .Prepend(CsvExporter.Line("Table", "Display name", "Rows owned", "Could not count"));

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
