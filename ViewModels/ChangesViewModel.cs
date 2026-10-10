using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// "Something broke yesterday - what changed?" Components modified recently, with who modified
/// them where the table records it, on one timeline with solution imports, upgrades and uninstalls.
/// </summary>
public sealed class ChangesViewModel : ObservableObject
{
    private readonly EnvironmentSessionViewModel _session;
    private CancellationTokenSource? _cts;

    public ChangesViewModel(EnvironmentSessionViewModel session)
    {
        _session = session;
        EntriesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = o => o is ChangeEntry e &&
                                  (!UnmanagedOnly || (!e.IsSolutionOperation && !e.IsManaged)) &&
                                  (SelectedType is null or "All types" || e.Type.StartsWith(SelectedType, StringComparison.Ordinal));

        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        ExportCsvCommand = new RelayCommand(_ => Export(markdown: false), _ => Entries.Count > 0);
        ExportMarkdownCommand = new RelayCommand(_ => Export(markdown: true), _ => Entries.Count > 0);
        OpenCommand = new RelayCommand(p => Open(p as ChangeEntry ?? SelectedEntry), p => (p as ChangeEntry ?? SelectedEntry)?.Item is not null);
    }

    public string Title => $"Recent changes — {_session.Title}";

    /// <summary>The tab the window was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session => _session;

    public ObservableCollection<ChangeEntry> Entries { get; } = new();
    public ListCollectionView EntriesView { get; }
    public ObservableCollection<string> Types { get; } = new() { "All types" };

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }
    public RelayCommand OpenCommand { get; }

    public IReadOnlyList<string> Ranges { get; } = ["Last 24 hours", "Last 7 days", "Last 30 days"];

    private string _range = "Last 7 days";
    public string Range
    {
        get => _range;
        set
        {
            if (SetProperty(ref _range, value)) _ = LoadAsync();
        }
    }

    internal static TimeSpan Span(string range) => range switch
    {
        "Last 24 hours" => TimeSpan.FromDays(1),
        "Last 30 days" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(7)
    };

    private string? _selectedType = "All types";
    public string? SelectedType
    {
        get => _selectedType;
        set
        {
            if (SetProperty(ref _selectedType, value)) Refresh();
        }
    }

    private bool _unmanagedOnly;
    /// <summary>Direct customisations - the usual cause of drift.</summary>
    public bool UnmanagedOnly
    {
        get => _unmanagedOnly;
        set
        {
            if (SetProperty(ref _unmanagedOnly, value)) Refresh();
        }
    }

    private ChangeEntry? _selectedEntry;
    public ChangeEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value)) OpenCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    private string _readSummary = string.Empty;
    /// <summary>"Read 214 component changes and 3 solution operations in 2.4 s".</summary>
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

    private string _warnings = string.Empty;
    /// <summary>A read cut short, or solution history that could not be read - the warning banner.</summary>
    public string Warnings
    {
        get => _warnings;
        private set => SetProperty(ref _warnings, value);
    }

    /// <summary>The changes the type and unmanaged filters let through.</summary>
    public int ShownCount => EntriesView.Count;

    public string CountLabel => $"{ShownCount:N0} change{(ShownCount == 1 ? string.Empty : "s")}";

    public bool HasEntries => ShownCount > 0;

    /// <summary>Why the list is empty and what to change - shown in its place.</summary>
    public string EmptyHeading => (Entries.Count > 0, IsBusy) switch
    {
        (true, _) => "Nothing matches",
        (_, true) => "Reading changes",
        _ => "No changes"
    };

    public string EmptyText => (Entries.Count > 0, IsBusy) switch
    {
        (true, _) when UnmanagedOnly => "No change matches the type and Unmanaged only. Choose All types, or clear Unmanaged only.",
        (true, _) => "No change matches the type. Choose All types.",
        (_, true) => $"Components changed and solutions imported in the {Range.ToLowerInvariant()} are being read.",
        _ => $"Nothing was changed, imported or uninstalled in the {Range.ToLowerInvariant()}. Try a longer time range."
    };

    private DateTimeOffset _since;

    public async Task LoadAsync()
    {
        if (_session.Client is not { } client) return;

        var defaultSolution = _session.Solutions.FirstOrDefault(s => s.IsDefaultSolution);
        if (defaultSolution is null)
        {
            Status = "The default solution is not listed in this tab, so the environment's changes cannot be read.";
            return;
        }

        var superseded = _cts;
        var cts = _cts = new CancellationTokenSource();
        if (superseded is not null) await superseded.CancelAsync();
        IsBusy = true;
        _since = DateTimeOffset.Now - Span(Range);

        try
        {
            Status = "Reading components changed since " + _since.ToString("yyyy-MM-dd HH:mm") + "...";
            var clock = Stopwatch.StartNew();
            var notes = new List<string>();
            var changed = await client.GetRecentlyChangedAsync(defaultSolution.SolutionId, _since, cts.Token);

            Status = $"Reading who changed {changed.Count:N0} component(s)...";
            var by = await client.GetModifiedByAsync(changed, cts.Token);

            IReadOnlyList<SolutionHistoryEntry> history;
            try
            {
                history = await client.GetSolutionHistoryAsync(cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                history = Array.Empty<SolutionHistoryEntry>();
                Log.Warn("Solution history could not be read for the timeline", ex);
                notes.Add("Solution history could not be read, so imports, upgrades and uninstalls are missing from the timeline - " + ex.Message);
            }

            if (cts.IsCancellationRequested) return;

            Entries.Clear();
            foreach (var entry in ChangeTimeline.Build(changed, by, history, _since)) Entries.Add(entry);

            var previous = SelectedType;
            Types.Clear();
            Types.Add("All types");
            foreach (var type in Entries.Select(e => e.IsSolutionOperation ? e.Type : e.Item!.ComponentTypeName)
                         .Distinct().OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase))
            {
                Types.Add(type);
            }
            _selectedType = Types.Contains(previous ?? string.Empty) ? previous : "All types";
            OnPropertyChanged(nameof(SelectedType));

            Refresh();

            if (changed.IsTruncated)
            {
                notes.Add($"Only the newest {Dataverse.DataverseClient.MaxRecentChanges:N0} changed components were read - " +
                          "older changes in this time range are not listed. Choose a shorter time range to see them all.");
            }
            Warnings = string.Join(Environment.NewLine, notes);

            var components = Entries.Count(e => !e.IsSolutionOperation);
            var operations = Entries.Count(e => e.IsSolutionOperation);
            ReadSummary = $"Read {components:N0} component change{(components == 1 ? string.Empty : "s")} and " +
                          $"{operations:N0} solution operation{(operations == 1 ? string.Empty : "s")} " +
                          $"in the {Range.ToLowerInvariant()} in {clock.Elapsed.TotalSeconds:0.0} s";
            Status = string.Empty;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = "Could not read the changes - " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsBusy = false;
            ExportCsvCommand.RaiseCanExecuteChanged();
            ExportMarkdownCommand.RaiseCanExecuteChanged();
        }
    }

    private void Refresh()
    {
        EntriesView.Refresh();
        OnPropertyChanged(nameof(ShownCount));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(EmptyHeading));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void Open(ChangeEntry? entry)
    {
        if (entry?.Item is { } item) _session.OpenDetails(item);
    }

    private void Export(bool markdown)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = markdown ? "Markdown (*.md)|*.md" : "CSV file (*.csv)|*.csv",
            FileName = $"changes-{_session.Title}.{(markdown ? "md" : "csv")}".Replace(' ', '-')
        };
        if (dialog.ShowDialog() != true) return;

        var shown = EntriesView.Cast<ChangeEntry>().ToList();

        try
        {
            if (markdown)
            {
                File.WriteAllText(dialog.FileName, ChangeTimeline.ToMarkdown(shown, _session.Title, _since));
            }
            else
            {
                CsvExporter.WriteLines(dialog.FileName,
                    shown.Select(e => CsvExporter.Line(e.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), e.Kind, e.What, e.Type,
                            e.By, e.Detail, e.ManagedLabel))
                        .Prepend(CsvExporter.Line("When", "Kind", "What", "Type", "Modified by", "Result", "State")));
            }

            Status = $"Exported {shown.Count:N0} line(s) to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not export - " + ex.Message;
        }
    }
}
