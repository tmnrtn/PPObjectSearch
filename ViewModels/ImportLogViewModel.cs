using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// The import log behind a solution history row: every component's result, error and timing,
/// from importjob.data. An import still running is followed until it finishes.
/// </summary>
public sealed class ImportLogViewModel : ObservableObject
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly EnvironmentSessionViewModel _session;
    private readonly DataverseClient _client;
    private readonly SolutionHistoryEntry _entry;
    private readonly CancellationTokenSource _cts = new();
    private ImportJobInfo? _job;
    private string? _rawXml;
    private ImportLog? _log;

    public ImportLogViewModel(EnvironmentSessionViewModel session, DataverseClient client, SolutionHistoryEntry entry)
    {
        _session = session;
        _client = client;
        _entry = entry;

        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => !ProblemsOnly || o is ImportLogRow { IsProblem: true };

        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        SaveXmlCommand = new RelayCommand(_ => SaveXml(), _ => _rawXml is not null);
        CopyProblemsCommand = new RelayCommand(_ => CopyProblems(), _ => _log is { } l && l.Failures + l.Warnings > 0);
        OpenCommand = new RelayCommand(p => Open(p as ImportLogRow ?? SelectedRow), p => ItemFor(p as ImportLogRow ?? SelectedRow) is not null);
    }

    public string Title => $"Import log — {_entry.SolutionName} {_entry.Version}";

    /// <summary>The tab the window was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session => _session;

    /// <summary>The solution and version the import brought in, after the heading.</summary>
    public string SolutionLabel => $"{_entry.SolutionName} {_entry.Version}";

    public ObservableCollection<ImportLogRow> Rows { get; } = new();
    public ListCollectionView RowsView { get; }

    /// <summary>Reads the import job and its log again.</summary>
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand SaveXmlCommand { get; }
    public RelayCommand CopyProblemsCommand { get; }
    public RelayCommand OpenCommand { get; }

    private bool _problemsOnly;
    public bool ProblemsOnly
    {
        get => _problemsOnly;
        set
        {
            if (SetProperty(ref _problemsOnly, value)) Refresh();
        }
    }

    private ImportLogRow? _selectedRow;
    public ImportLogRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value)) OpenCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status = "Finding the import job...";
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    private string _readSummary = string.Empty;
    /// <summary>"Read 412 components - 2 failures, 5 warnings - in 1.3 s. The import took 4.2 min."</summary>
    public string ReadSummary
    {
        get => _readSummary;
        private set
        {
            if (SetProperty(ref _readSummary, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>The status bar's left side: what is happening, else what the last read covered.</summary>
    public string StatusLine => string.IsNullOrEmpty(Status) ? ReadSummary : Status;

    private bool _isBusy;
    /// <summary>Finding the job, following it while it runs, or reading its log.</summary>
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

    /// <summary>The rows the problems-only filter lets through.</summary>
    public int ShownCount => RowsView.Count;

    public string CountLabel => ProblemsOnly
        ? $"{ShownCount:N0} of {Rows.Count:N0} component{(Rows.Count == 1 ? string.Empty : "s")}"
        : $"{Rows.Count:N0} component{(Rows.Count == 1 ? string.Empty : "s")}";

    public bool HasRows => ShownCount > 0;

    private string _noLogText = string.Empty;

    /// <summary>Why the list is empty and what to change - shown in its place.</summary>
    public string EmptyHeading => Rows.Count > 0 ? "No failures or warnings"
        : IsRunning ? "Import in progress"
        : IsBusy ? "Reading the import log"
        : "No import log";

    public string EmptyText => Rows.Count > 0
        ? "Every component imported cleanly. Clear Failures and warnings only to see them all."
        : IsRunning
            ? $"The log appears once the import finishes - {Progress:N0}% so far, checked every {PollInterval.TotalSeconds:N0} seconds."
            : IsBusy
                ? "Finding the import job behind this solution history row."
                : _noLogText;

    private double? _progress;
    /// <summary>The job's progress while it runs; null once finished.</summary>
    public double? Progress
    {
        get => _progress;
        private set
        {
            if (!SetProperty(ref _progress, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    public bool IsRunning => Progress is not null;

    /// <summary>Stops following a running import - the window is closing.</summary>
    public void Stop() => _cts.Cancel();

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _noLogText = "The import log lists no components.";

        try
        {
            Status = "Finding the import job...";
            var jobs = await _client.GetImportJobsAsync(_entry.SolutionName, _cts.Token);
            _job = DataverseClient.MatchImportJob(jobs, _entry);

            if (_job is null)
            {
                _noLogText = jobs.Count == 0
                    ? $"No import job is kept for {_entry.SolutionName} - Dataverse removes them after a while, and exports and uninstalls have none."
                    : "No import job for this solution started near this operation - Dataverse may have removed it.";
                Status = $"No import job found for {_entry.SolutionName} {_entry.Version}.";
                return;
            }

            // A running import is followed: its progress, then its log once it finishes.
            while (!_job.IsFinished)
            {
                Progress = _job.Progress ?? 0;
                Status = $"Import in progress - {Progress:N0}%. Refreshing every {PollInterval.TotalSeconds:N0} seconds.";
                await Task.Delay(PollInterval, _cts.Token);
                _job = await _client.GetImportJobAsync(_job.Id, _cts.Token) ?? _job;
            }

            Progress = null;
            Status = "Reading the import log...";
            var clock = Stopwatch.StartNew();
            _rawXml = await _client.GetImportJobDataAsync(_job.Id, _cts.Token);

            if (string.IsNullOrWhiteSpace(_rawXml))
            {
                _noLogText = "The import job has no log - Dataverse keeps none for some operations.";
                Status = "The import job has no log.";
                return;
            }

            _log = ImportLogParser.Parse(_rawXml);
            Rows.Clear();
            foreach (var row in _log.Rows) Rows.Add(row);
            _problemsOnly = _log.Failures + _log.Warnings > 0;
            OnPropertyChanged(nameof(ProblemsOnly));
            Refresh();

            ReadSummary = $"Read {_log.Rows.Count:N0} component{(_log.Rows.Count == 1 ? string.Empty : "s")} - " +
                          $"{_log.Failures:N0} failure{(_log.Failures == 1 ? string.Empty : "s")}, " +
                          $"{_log.Warnings:N0} warning{(_log.Warnings == 1 ? string.Empty : "s")} - in {clock.Elapsed.TotalSeconds:0.0} s." +
                          (_job.StartedOn is { } s && _job.CompletedOn is { } c ? $" The import took {(c - s).TotalMinutes:N1} min." : string.Empty);
            Status = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        catch (Exception ex)
        {
            Progress = null;
            _noLogText = "The import log could not be read - the status bar says why. Read again to try once more.";
            Status = "Could not read the import log - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
            SaveXmlCommand.RaiseCanExecuteChanged();
            CopyProblemsCommand.RaiseCanExecuteChanged();
        }
    }

    private void Refresh()
    {
        RowsView.Refresh();
        OnPropertyChanged(nameof(ShownCount));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(EmptyHeading));
        OnPropertyChanged(nameof(EmptyText));
    }

    private SolutionComponentItem? ItemFor(ImportLogRow? row) =>
        row?.Id is { } id ? _session.FindLoaded(id) : null;

    private void Open(ImportLogRow? row)
    {
        if (ItemFor(row) is { } item) _session.OpenDetails(item);
    }

    private void SaveXml()
    {
        if (_rawXml is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "XML file (*.xml)|*.xml",
            FileName = $"{_entry.SolutionName}-{_entry.Version}-import-log.xml"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, _rawXml);
            Status = $"Saved to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not save - " + ex.Message;
        }
    }

    private void CopyProblems()
    {
        if (_log is null) return;

        try
        {
            Clipboard.SetText(_log.ProblemsAsMarkdown($"{_entry.SolutionName} {_entry.Version}"));
            Status = "Failures and warnings copied as Markdown.";
        }
        catch (Exception ex)
        {
            Status = "Could not copy - " + ex.Message;
        }
    }
}
