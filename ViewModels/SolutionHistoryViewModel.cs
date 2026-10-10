using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

/// <summary>The segmented filter above the history.</summary>
public enum SolutionHistoryFilter
{
    All,
    Imports,
    Uninstalls,
    Exports,
    Failed
}

/// <summary>
/// What has been done to an environment's solutions - imports, upgrades, uninstalls, exports and
/// publishes - from msdyn_solutionhistory, the same record the maker portal's Solution history shows.
/// </summary>
public sealed class SolutionHistoryViewModel : ObservableObject
{
    private readonly DataverseClient _client;

    public SolutionHistoryViewModel(EnvironmentSessionViewModel session, DataverseClient client)
    {
        Session = session;
        _client = client;

        EntriesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = Filter;

        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());
        ExportCommand = new RelayCommand(_ => Export(), _ => EntriesView.Count > 0);
        ImportLogCommand = new RelayCommand(_ => OpenImportLog(), _ => SelectedEntry is not null);
    }

    /// <summary>
    /// Where to save the export, from the suggested file name - null when the user cancels. Asks
    /// with a dialog; replaced in tests.
    /// </summary>
    internal Func<string, string?> ChooseExportFile { get; set; } = suggested =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = suggested };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    /// <summary>The selected operation's import log - every component's result, error and timing.</summary>
    public RelayCommand ImportLogCommand { get; }

    private void OpenImportLog()
    {
        if (SelectedEntry is not { } entry) return;

        new Views.ImportLogWindow
        {
            DataContext = new ImportLogViewModel(Session, _client, entry),
            Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                    ?? System.Windows.Application.Current.MainWindow
        }.Show();
    }

    public EnvironmentSessionViewModel Session { get; }

    public string Title => $"Solution history — {Session.Title}";

    public ObservableCollection<SolutionHistoryEntry> Entries { get; } = new();
    public ListCollectionView EntriesView { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ExportCommand { get; }

    private SolutionHistoryEntry? _selectedEntry;
    public SolutionHistoryEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value)) ImportLogCommand.RaiseCanExecuteChanged();
        }
    }

    private SolutionHistoryFilter _filter;
    public SolutionHistoryFilter StatusFilter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value)) Refresh();
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) Refresh();
        }
    }

    private int _countAll, _countImports, _countUninstalls, _countExports, _countFailed;
    public int CountAll { get => _countAll; private set => SetProperty(ref _countAll, value); }
    public int CountImports { get => _countImports; private set => SetProperty(ref _countImports, value); }
    public int CountUninstalls { get => _countUninstalls; private set => SetProperty(ref _countUninstalls, value); }
    public int CountExports { get => _countExports; private set => SetProperty(ref _countExports, value); }
    public int CountFailed { get => _countFailed; private set => SetProperty(ref _countFailed, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    private string _status = "Loading solution history...";
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

    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Loading solution history...";

        try
        {
            var entries = await _client.GetSolutionHistoryAsync();

            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);

            Refresh();
            SelectedEntry = EntriesView.Cast<SolutionHistoryEntry>().FirstOrDefault();

            var failed = Entries.Count(e => e.Outcome == RunOutcome.Failed);
            var oldest = Entries.Where(e => e.StartTime is not null).Select(e => e.StartTime!.Value).DefaultIfEmpty().Min();

            var recent = Entries.Count >= DataverseClient.MaxSolutionHistory ? " (the most recent)" : string.Empty;
            var since = oldest == default ? "." : $" back to {oldest:yyyy-MM-dd}.";
            var failures = failed > 0 ? $" {failed:N0} failed." : string.Empty;
            Status = Entries.Count == 0
                ? "No solution operations recorded."
                : $"{Entries.Count:N0} operation(s){recent}{since}{failures}";
        }
        catch (Exception ex)
        {
            Status = "Could not read the solution history - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Refresh()
    {
        EntriesView.Refresh();

        var searched = Entries.Where(MatchesSearch).ToList();
        CountAll = searched.Count;
        CountImports = searched.Count(e => e.OperationCode == 0);
        CountUninstalls = searched.Count(e => e.OperationCode == 1);
        CountExports = searched.Count(e => e.OperationCode is 2 or 10);
        CountFailed = searched.Count(e => e.Outcome == RunOutcome.Failed);

        Summary = $"{EntriesView.Count:N0} of {Entries.Count:N0}";
        ExportCommand.RaiseCanExecuteChanged();
    }

    private bool Filter(object obj) =>
        obj is SolutionHistoryEntry entry &&
        MatchesSearch(entry) &&
        StatusFilter switch
        {
            SolutionHistoryFilter.Imports => entry.OperationCode == 0,
            SolutionHistoryFilter.Uninstalls => entry.OperationCode == 1,
            SolutionHistoryFilter.Exports => entry.OperationCode is 2 or 10,
            SolutionHistoryFilter.Failed => entry.Outcome == RunOutcome.Failed,
            _ => true
        };

    private bool MatchesSearch(SolutionHistoryEntry entry) =>
        SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(term => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase));

    private void Export()
    {
        if (ChooseExportFile($"solution-history-{Session.Title}.csv".Replace(' ', '-')) is not { } path) return;

        try
        {
            var lines = new List<string>
            {
                "Result,Solution,Version,Operation,Sub operation,Managed,Publisher,Started,Ended,Duration (s),Error code,Exception"
            };

            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            lines.AddRange(EntriesView.Cast<SolutionHistoryEntry>().Select(e => Services.CsvExporter.Line(
                e.ResultLabel, e.SolutionName, e.Version, e.Operation, e.SubOperation, e.ManagedLabel, e.PublisherName,
                e.StartTime?.ToString("yyyy-MM-dd HH:mm:ss", invariant), e.EndTime?.ToString("yyyy-MM-dd HH:mm:ss", invariant),
                e.TotalSeconds?.ToString(invariant), e.ErrorCode, e.ExceptionMessage)));

            Services.CsvExporter.WriteLines(path, lines);
            Status = $"Exported {lines.Count - 1:N0} operation(s) to {path}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
