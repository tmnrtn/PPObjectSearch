using System.Collections.ObjectModel;
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
        AuditCommand = new RelayCommand(p => OpenAudit(p as ChangeEntry ?? SelectedEntry), p => HasAudit(p as ChangeEntry ?? SelectedEntry));
    }

    public string Title => $"Recent changes — {_session.Title}";

    public ObservableCollection<ChangeEntry> Entries { get; } = new();
    public ListCollectionView EntriesView { get; }
    public ObservableCollection<string> Types { get; } = new() { "All types" };

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }
    public RelayCommand OpenCommand { get; }

    /// <summary>Who changed a flow's state, a step's state or a variable's value, from Dataverse auditing.</summary>
    public RelayCommand AuditCommand { get; }

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
            if (!SetProperty(ref _selectedEntry, value)) return;
            OpenCommand.RaiseCanExecuteChanged();
            AuditCommand.RaiseCanExecuteChanged();
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
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

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

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        _since = DateTimeOffset.Now - Span(Range);

        try
        {
            Status = "Reading components changed since " + _since.ToString("yyyy-MM-dd HH:mm") + "...";
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
            Status = $"{Entries.Count(e => !e.IsSolutionOperation):N0} component change(s) and " +
                     $"{Entries.Count(e => e.IsSolutionOperation):N0} solution operation(s) in the {Range.ToLowerInvariant()}." +
                     (changed.IsTruncated ? $" Only the newest {Dataverse.DataverseClient.MaxRecentChanges:N0} components were read." : string.Empty);
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

    private void Refresh() => EntriesView.Refresh();

    private void Open(ChangeEntry? entry)
    {
        if (entry?.Item is { } item) _session.OpenDetails(item);
    }

    internal static bool HasAudit(ChangeEntry? entry) =>
        entry?.Item is { } item && item.ObjectId != Guid.Empty && Dataverse.DataverseClient.HasAuditHistory(item.ComponentType);

    private void OpenAudit(ChangeEntry? entry)
    {
        if (!HasAudit(entry) || _session.Client is not { } client) return;

        new Views.AuditHistoryWindow
        {
            DataContext = new AuditHistoryViewModel(client, entry!.Item!, _session.Title),
            Owner = System.Windows.Application.Current.MainWindow
        }.Show();
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
