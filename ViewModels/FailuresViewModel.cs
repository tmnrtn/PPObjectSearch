using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// "What is failing here?" Cloud flow runs, classic workflow jobs and plug-in trace entries that
/// ended in error over a period - a summary band with the trend and each source's count, then the
/// failing components, each with its failures grouped by error and its latest ones a click away.
/// </summary>
public sealed class FailuresViewModel : ObservableObject, IDisposable
{
    private const int ProcessType = 29;
    private const int PluginTypeType = 90;
    private const int PluginStepType = 92;

    /// <summary>How many of the selected component's latest failures are listed.</summary>
    internal const int RecentFailureCount = 8;

    private readonly EnvironmentSessionViewModel _session;
    private CancellationTokenSource? _cts;
    private FailureData? _data;
    private FailureData? _shown;
    private DateTimeOffset _from;
    private DateTimeOffset _to;
    private int _stateRequest;

    public FailuresViewModel(EnvironmentSessionViewModel session)
    {
        _session = session;

        // The pickers hold calendar days; this is today's, in local time. Like a picked day it has
        // no kind: Range gives it today's offset, which a local-kind day from before a clock change
        // would not take.
        var today = DateTimeOffset.UtcNow.ToLocalTime().Date;
        _customFrom = today.AddDays(-7);
        _customTo = today;

        LoadCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        ExportCsvCommand = new RelayCommand(_ => Export(markdown: false), _ => _shown is { Events.Count: > 0 });
        ExportMarkdownCommand = new RelayCommand(_ => Export(markdown: true), _ => _shown is not null);
        OpenEventCommand = new RelayCommand(p => OpenEvent(p as FailureEvent), p => p is FailureEvent);
        OpenLatestFailedRunCommand = new RelayCommand(
            _ => OpenEvent(SelectedComponent?.Events[0]), _ => SelectedComponent is not null);
        OpenComponentDetailsCommand = new RelayCommand(
            _ => OpenDetails(SelectedComponent), _ => SelectedComponent is not null);

        _session.PropertyChanged += OnSessionChanged;
    }

    public string Title => $"Failures — {_session.Title}";

    /// <summary>The tab the window was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session => _session;

    public ObservableCollection<FailureSummaryRow> Components { get; } = new();
    public ObservableCollection<FailureTrendBar> Trend { get; } = new();

    public AsyncRelayCommand LoadCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }
    public RelayCommand OpenEventCommand { get; }
    public RelayCommand OpenLatestFailedRunCommand { get; }
    public RelayCommand OpenComponentDetailsCommand { get; }

    // ---------------------------------------------------------------- period

    public IReadOnlyList<string> Periods { get; } = ["Last hour", "Last 24 hours", "Last 7 days", "Last 30 days", "Custom"];

    private string _period = "Last 7 days";
    public string Period
    {
        get => _period;
        set
        {
            if (!SetProperty(ref _period, value)) return;
            OnPropertyChanged(nameof(IsCustom));
            if (!IsCustom) _ = LoadAsync();
        }
    }

    public bool IsCustom => Period == "Custom";

    private DateTime? _customFrom;
    public DateTime? CustomFrom
    {
        get => _customFrom;
        set => SetProperty(ref _customFrom, value);
    }

    private DateTime? _customTo;
    /// <summary>The last day of a custom period, included to its end.</summary>
    public DateTime? CustomTo
    {
        get => _customTo;
        set => SetProperty(ref _customTo, value);
    }

    /// <summary>
    /// The period a choice covers. A custom period runs from the start of its first day to the
    /// end of its last day (or now, if sooner); a missing date falls back to the last seven days.
    /// </summary>
    internal static (DateTimeOffset From, DateTimeOffset To) Range(string period, DateTime? from, DateTime? to, DateTimeOffset now)
    {
        switch (period)
        {
            case "Last hour": return (now.AddHours(-1), now);
            case "Last 24 hours": return (now.AddDays(-1), now);
            case "Last 30 days": return (now.AddDays(-30), now);
            case "Custom": break;
            default: return (now.AddDays(-7), now);
        }

        if (from is { } a && to is { } b && a.Date > b.Date) (from, to) = (b, a);

        var start = from is { } f ? new DateTimeOffset(f.Date, now.Offset) : now.AddDays(-7);
        var end = to is { } t ? new DateTimeOffset(t.Date.AddDays(1), now.Offset) : now;
        if (end > now) end = now;
        if (end < start) (start, end) = (end, start);
        return (start, end);
    }

    // ---------------------------------------------------------------- sources

    // Every source is read each time; the pills only choose which are counted, so turning one
    // back on needs no second read.

    private bool _includeFlows = true;
    public bool IncludeFlows
    {
        get => _includeFlows;
        set
        {
            if (SetProperty(ref _includeFlows, value)) Show();
        }
    }

    private bool _includeClassicWorkflows = true;
    public bool IncludeClassicWorkflows
    {
        get => _includeClassicWorkflows;
        set
        {
            if (SetProperty(ref _includeClassicWorkflows, value)) Show();
        }
    }

    private bool _includePlugins = true;
    public bool IncludePlugins
    {
        get => _includePlugins;
        set
        {
            if (SetProperty(ref _includePlugins, value)) Show();
        }
    }

    /// <summary>Each source's failures in the read, whichever pills are on.</summary>
    public int FlowFailureCount => Count(FailureSource.CloudFlow);
    public int WorkflowFailureCount => Count(FailureSource.ClassicWorkflow);
    public int PluginFailureCount => Count(FailureSource.Plugin);

    private int Count(FailureSource source) => _data?.Events.Count(e => e.Source == source) ?? 0;

    private HashSet<FailureSource> Sources()
    {
        var sources = new HashSet<FailureSource>();
        if (IncludeFlows) sources.Add(FailureSource.CloudFlow);
        if (IncludeClassicWorkflows) sources.Add(FailureSource.ClassicWorkflow);
        if (IncludePlugins) sources.Add(FailureSource.Plugin);
        return sources;
    }

    /// <summary>A solution other than the default one is on screen, so failures can be narrowed to it.</summary>
    public bool HasSolution => _session.SelectedSolution is { IsDefaultSolution: false };

    public string SolutionLabel => HasSolution ? $"Only {_session.SelectedSolution!.DisplayLabel}" : string.Empty;

    private bool _solutionOnly;
    /// <summary>Only the flows, workflows and plug-ins in the solution on screen - read again when changed.</summary>
    public bool SolutionOnly
    {
        get => _solutionOnly;
        set
        {
            if (SetProperty(ref _solutionOnly, value) && _data is not null) _ = LoadAsync();
        }
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(EnvironmentSessionViewModel.SelectedSolution) or null)) return;
        OnPropertyChanged(nameof(HasSolution));
        OnPropertyChanged(nameof(SolutionLabel));
    }

    private FailureQuery BuildQuery()
    {
        if (!SolutionOnly || !HasSolution) return new FailureQuery();

        var items = _session.AllItems;
        return new FailureQuery
        {
            WorkflowIds = items.Where(i => i.ComponentType == ProcessType).Select(i => i.ObjectId).ToHashSet(),
            PluginTypeNames = items.Where(i => i.ComponentType == PluginTypeType).Select(i => i.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            PluginStepIds = items.Where(i => i.ComponentType == PluginStepType).Select(i => i.ObjectId).ToHashSet()
        };
    }

    // ---------------------------------------------------------------- summary

    public int TotalFailures => _shown?.Events.Count ?? 0;

    public string TotalLabel => $"failure{(TotalFailures == 1 ? string.Empty : "s")} in {Components.Count:N0} " +
                                $"component{(Components.Count == 1 ? string.Empty : "s")}";

    public string ComponentCountLabel => $"{Components.Count:N0} component{(Components.Count == 1 ? string.Empty : "s")}";

    public bool HasComponents => Components.Count > 0;

    /// <summary>Why the list is empty and what to change - shown in its place.</summary>
    public string EmptyHeading => Sources().Count == 0 ? "No sources included" : "No failures";

    public string EmptyText => (Sources().Count == 0, SolutionOnly && HasSolution) switch
    {
        (true, _) => "Include flow runs, workflow jobs or plug-ins above.",
        (_, true) => $"Nothing in {_session.SelectedSolution?.DisplayLabel} failed in this period. Try a longer period, or the whole environment.",
        _ => "Nothing failed in this period. Try a longer period, or include more sources."
    };

    private string _readSummary = string.Empty;
    /// <summary>"Read 1,284 flow runs, 96 system jobs and 0 trace logs in 6.1 s".</summary>
    public string ReadSummary
    {
        get => _readSummary;
        private set
        {
            if (SetProperty(ref _readSummary, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    internal static string Describe(FailureData data, TimeSpan took) =>
        $"Read {data.FlowRunsRead:N0} flow run{(data.FlowRunsRead == 1 ? string.Empty : "s")}, " +
        $"{data.SystemJobsRead:N0} system job{(data.SystemJobsRead == 1 ? string.Empty : "s")} and " +
        $"{data.TraceLogsRead:N0} trace log{(data.TraceLogsRead == 1 ? string.Empty : "s")} in {took.TotalSeconds:0.0} s";

    // ---------------------------------------------------------------- the selected component

    public ObservableCollection<FailureErrorGroup> SelectedErrors { get; } = new();
    public ObservableCollection<FailureEvent> RecentFailures { get; } = new();

    private FailureSummaryRow? _selectedComponent;
    public FailureSummaryRow? SelectedComponent
    {
        get => _selectedComponent;
        set
        {
            if (!SetProperty(ref _selectedComponent, value)) return;

            SelectedErrors.Clear();
            RecentFailures.Clear();
            SelectedStateLabel = null;

            if (value is not null)
            {
                foreach (var group in FailureOverview.ByError(value.Events)) SelectedErrors.Add(group);
                foreach (var failure in value.Events.Take(RecentFailureCount)) RecentFailures.Add(failure);
                _ = LoadStateAsync(value);
            }

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedRunsLabel));
            OnPropertyChanged(nameof(SelectedErrorsHeading));
            OpenLatestFailedRunCommand.RaiseCanExecuteChanged();
            OpenComponentDetailsCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => SelectedComponent is not null;

    /// <summary>"18 of 212 runs failed · first 2026-09-30 08:02" - or the failures alone where runs are not counted.</summary>
    public string SelectedRunsLabel => SelectedComponent is { } c
        ? $"{FailureCount(c)} · first {c.First.ToLocalTime():yyyy-MM-dd HH:mm}"
        : string.Empty;

    /// <summary>"18 of 212 runs failed", or "3 failures" where runs are not counted.</summary>
    private static string FailureCount(FailureSummaryRow component)
    {
        if (component.Runs is { } runs) return $"{component.Failures:N0} of {runs:N0} runs failed";

        return component.Failures == 1 ? "1 failure" : $"{component.Failures:N0} failures";
    }

    public string SelectedErrorsHeading => $"Grouped by error · {SelectedErrors.Count:N0}";

    private string? _selectedStateLabel;
    /// <summary>On or Off, Activated or Draft: whether the failing flow or workflow is still switched on.</summary>
    public string? SelectedStateLabel
    {
        get => _selectedStateLabel;
        private set
        {
            if (!SetProperty(ref _selectedStateLabel, value)) return;
            OnPropertyChanged(nameof(IsSelectedOn));
        }
    }

    private bool _isSelectedOn;
    public bool IsSelectedOn
    {
        get => _isSelectedOn;
        private set => SetProperty(ref _isSelectedOn, value);
    }

    /// <summary>One read for the state chip; a failure to read just leaves the chip off.</summary>
    private async Task LoadStateAsync(FailureSummaryRow row)
    {
        var request = ++_stateRequest;
        var failure = row.Events[0];
        if (_session.Client is not { } client || failure.WorkflowId is not { } id) return;

        var kind = failure.Source == FailureSource.CloudFlow ? SwitchableKind.CloudFlow : SwitchableKind.Process;
        try
        {
            var states = await client.GetSwitchStatesAsync(kind, [id], CancellationToken.None);
            if (request != _stateRequest || !states.TryGetValue(id, out var on)) return;

            IsSelectedOn = on;
            SelectedStateLabel = on ? Switchable.States(kind).On : Switchable.States(kind).Off;
        }
        catch (Exception ex)
        {
            Log.Warn("The failing component's state could not be read", ex);
        }
    }

    // ---------------------------------------------------------------- state

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            LoadCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status = string.Empty;
    /// <summary>Progress while reading, and anything an action has to say.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>The status bar's left side: what is happening, else what the last read covered.</summary>
    public string StatusLine => string.IsNullOrEmpty(Status) ? ReadSummary : Status;

    private string _notes = string.Empty;
    /// <summary>Sources that could not be read, or were cut short - the warning banner.</summary>
    public string Notes
    {
        get => _notes;
        private set => SetProperty(ref _notes, value);
    }

    // ---------------------------------------------------------------- load

    public async Task LoadAsync()
    {
        if (_session.Client is not { } client) return;

        var superseded = _cts;
        var cts = _cts = new CancellationTokenSource();
        if (superseded is not null) await superseded.CancelAsync();
        IsBusy = true;

        var (from, to) = Range(Period, CustomFrom, CustomTo, DateTimeOffset.Now);
        var query = BuildQuery();
        var progress = new Progress<string>(message =>
        {
            if (ReferenceEquals(_cts, cts)) Status = message;
        });

        try
        {
            Status = $"Reading failures from {from.ToLocalTime():yyyy-MM-dd HH:mm} to {to.ToLocalTime():yyyy-MM-dd HH:mm}...";
            var clock = Stopwatch.StartNew();
            var data = await client.GetFailuresAsync(from, to, query, progress, cts.Token);
            if (cts.IsCancellationRequested) return;

            _data = data;
            _from = from;
            _to = to;

            Notes = string.Join(Environment.NewLine, data.Notes);
            ReadSummary = Describe(data, clock.Elapsed);
            Status = string.Empty;
            Show();
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = "Could not read the failures - " + ex.Message;
        }
        finally
        {
            // Only a newer read keeps the flag; a closed window (no source left) clears it too.
            if (_cts is null || ReferenceEquals(_cts, cts)) IsBusy = false;
        }
    }

    /// <summary>The last read, narrowed to the sources that are on: the band, the list and its first row.</summary>
    private void Show()
    {
        if (_data is { } data)
        {
            var shown = _shown = FailureOverview.Only(data, Sources());

            Components.Clear();
            foreach (var row in FailureOverview.ByComponent(shown)) Components.Add(row);

            Trend.Clear();
            foreach (var bar in FailureOverview.Bars(FailureOverview.Trend(shown, _from, _to))) Trend.Add(bar);

            SelectedComponent = Components.FirstOrDefault();
        }

        OnPropertyChanged(nameof(TotalFailures));
        OnPropertyChanged(nameof(TotalLabel));
        OnPropertyChanged(nameof(ComponentCountLabel));
        OnPropertyChanged(nameof(HasComponents));
        OnPropertyChanged(nameof(EmptyHeading));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(FlowFailureCount));
        OnPropertyChanged(nameof(WorkflowFailureCount));
        OnPropertyChanged(nameof(PluginFailureCount));
        ExportCsvCommand.RaiseCanExecuteChanged();
        ExportMarkdownCommand.RaiseCanExecuteChanged();
    }

    // ---------------------------------------------------------------- open

    /// <summary>The loaded row a failure belongs to: its plug-in step or type, or its workflow.</summary>
    private SolutionComponentItem? ItemFor(FailureEvent failure)
    {
        if (failure.Source == FailureSource.Plugin)
        {
            return (failure.StepId is { } stepId
                       ? _session.AllItems.FirstOrDefault(i => i.ComponentType == PluginStepType && i.ObjectId == stepId)
                       : null)
                   ?? _session.AllItems.FirstOrDefault(i => i.ComponentType == PluginTypeType &&
                                                            string.Equals(i.Name, failure.ComponentKey, StringComparison.OrdinalIgnoreCase));
        }

        return failure.WorkflowId is { } workflowId ? _session.FindLoaded(workflowId) : null;
    }

    /// <summary>A failure, where it can be seen: a flow's or workflow's runs, a plug-in's trace log.</summary>
    private void OpenEvent(FailureEvent? failure)
    {
        if (failure is null) return;

        if (ItemFor(failure) is { } item)
        {
            _session.OpenDetails(item, failure.Source == FailureSource.Plugin ? DetailsTab.TraceLog : DetailsTab.Runs);
            return;
        }

        NotLoaded(failure);
    }

    private void OpenDetails(FailureSummaryRow? row)
    {
        if (row is null) return;

        if (ItemFor(row.Events[0]) is { } item) _session.OpenDetails(item);
        else NotLoaded(row.Events[0]);
    }

    private void NotLoaded(FailureEvent failure) =>
        Status = $"{failure.ComponentName} is not in the loaded list, so its details cannot be opened here. " +
                 "Load the default solution to reach every component.";

    /// <summary>Lets go of the session when the window closes, and stops any read in progress.</summary>
    public void Detach()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Detach();
        _cts?.Dispose();
        _cts = null;
    }

    // ---------------------------------------------------------------- export

    /// <summary>What is on screen: the sources that are on, as read.</summary>
    private void Export(bool markdown)
    {
        if (_shown is not { } data) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = markdown ? "Markdown (*.md)|*.md" : "CSV file (*.csv)|*.csv",
            FileName = $"failures-{_session.Title}.{(markdown ? "md" : "csv")}".Replace(' ', '-')
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (markdown)
            {
                File.WriteAllText(dialog.FileName, FailureOverview.ToMarkdown(data, _session.Title, _from, _to));
                Status = $"Exported the overview to {dialog.FileName}.";
            }
            else
            {
                var events = data.Events.OrderByDescending(e => e.When).ToList();
                CsvExporter.WriteLines(dialog.FileName,
                    events.Select(e => CsvExporter.Line(e.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), e.SourceLabel,
                            e.ComponentName, e.ErrorCode, e.ErrorMessage, e.Context, e.Regarding, e.RunName))
                        .Prepend(CsvExporter.Line("When", "Kind", "Component", "Error code", "Error", "Context", "Regarding", "Run")));
                Status = $"Exported {events.Count:N0} failure(s) to {dialog.FileName}.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Could not export - " + ex.Message;
        }
    }
}
