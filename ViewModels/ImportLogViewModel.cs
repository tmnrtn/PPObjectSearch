using System.Collections.ObjectModel;
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

        SaveXmlCommand = new RelayCommand(_ => SaveXml(), _ => _rawXml is not null);
        CopyProblemsCommand = new RelayCommand(_ => CopyProblems(), _ => _log is { } l && l.Failures + l.Warnings > 0);
        OpenCommand = new RelayCommand(p => Open(p as ImportLogRow ?? SelectedRow), p => ItemFor(p as ImportLogRow ?? SelectedRow) is not null);
    }

    public string Title => $"Import log — {_entry.SolutionName} {_entry.Version}";

    public ObservableCollection<ImportLogRow> Rows { get; } = new();
    public ListCollectionView RowsView { get; }

    public RelayCommand SaveXmlCommand { get; }
    public RelayCommand CopyProblemsCommand { get; }
    public RelayCommand OpenCommand { get; }

    private bool _problemsOnly;
    public bool ProblemsOnly
    {
        get => _problemsOnly;
        set
        {
            if (SetProperty(ref _problemsOnly, value)) RowsView.Refresh();
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
        private set => SetProperty(ref _status, value);
    }

    private double? _progress;
    /// <summary>The job's progress while it runs; null once finished.</summary>
    public double? Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(IsRunning));
        }
    }

    public bool IsRunning => Progress is not null;

    /// <summary>Stops following a running import - the window is closing.</summary>
    public void Stop() => _cts.Cancel();

    public async Task LoadAsync()
    {
        try
        {
            var jobs = await _client.GetImportJobsAsync(_entry.SolutionName, _cts.Token);
            _job = DataverseClient.MatchImportJob(jobs, _entry);

            if (_job is null)
            {
                Status = jobs.Count == 0
                    ? $"No import job is kept for {_entry.SolutionName} - Dataverse removes them after a while, and exports and uninstalls have none."
                    : "No import job for this solution started near this operation - Dataverse may have removed it.";
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
            _rawXml = await _client.GetImportJobDataAsync(_job.Id, _cts.Token);

            if (string.IsNullOrWhiteSpace(_rawXml))
            {
                Status = "The import job has no log.";
                return;
            }

            _log = ImportLogParser.Parse(_rawXml);
            Rows.Clear();
            foreach (var row in _log.Rows) Rows.Add(row);
            ProblemsOnly = _log.Failures + _log.Warnings > 0;

            Status = $"{_log.Rows.Count:N0} component(s): {_log.Failures:N0} failure(s), {_log.Warnings:N0} warning(s)." +
                     (_job.StartedOn is { } s && _job.CompletedOn is { } c ? $" Took {(c - s).TotalMinutes:N1} min." : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        catch (Exception ex)
        {
            Progress = null;
            Status = "Could not read the import log - " + ex.Message;
        }
        finally
        {
            SaveXmlCommand.RaiseCanExecuteChanged();
            CopyProblemsCommand.RaiseCanExecuteChanged();
        }
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
