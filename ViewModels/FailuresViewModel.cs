using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// "What is failing here?" Cloud flow runs, classic workflow jobs and plug-in trace entries that
/// ended in error over a period - by component, by error and over time - with the failures behind
/// each row one click away.
/// </summary>
public sealed class FailuresViewModel : ObservableObject
{
    private const int ProcessType = 29;
    private const int PluginTypeType = 90;
    private const int PluginStepType = 92;

    private readonly EnvironmentSessionViewModel _session;
    private CancellationTokenSource? _cts;
    private FailureData? _data;
    private DateTimeOffset _from;
    private DateTimeOffset _to;

    public FailuresViewModel(EnvironmentSessionViewModel session)
    {
        _session = session;

        var today = DateTime.Today;
        _customFrom = today.AddDays(-7);
        _customTo = today;

        LoadCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        ExportCsvCommand = new RelayCommand(_ => Export(markdown: false), _ => _data is { Events.Count: > 0 });
        ExportMarkdownCommand = new RelayCommand(_ => Export(markdown: true), _ => _data is not null);
        OpenEventCommand = new RelayCommand(p => OpenEvent(p as FailureEvent ?? SelectedEvent),
            p => (p as FailureEvent ?? SelectedEvent) is not null);

        _session.PropertyChanged += OnSessionChanged;
    }

    public string Title => $"Failures — {_session.Title}";

    public ObservableCollection<FailureSummaryRow> Components { get; } = new();
    public ObservableCollection<FailureErrorGroup> Errors { get; } = new();
    public ObservableCollection<FailureBucket> Trend { get; } = new();
    public ObservableCollection<FailureEvent> DrillEvents { get; } = new();

    public AsyncRelayCommand LoadCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }
    public RelayCommand OpenEventCommand { get; }

    // ---------------------------------------------------------------- period

    public IReadOnlyList<string> Periods { get; } = ["Last hour", "Last 24 hours", "Last 7 days", "Last 30 days", "Custom"];

    private string _period = "Last 24 hours";
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

    // ---------------------------------------------------------------- what to read

    private bool _includeFlows = true;
    public bool IncludeFlows
    {
        get => _includeFlows;
        set => SetProperty(ref _includeFlows, value);
    }

    private bool _includeClassicWorkflows = true;
    public bool IncludeClassicWorkflows
    {
        get => _includeClassicWorkflows;
        set => SetProperty(ref _includeClassicWorkflows, value);
    }

    private bool _includePlugins = true;
    public bool IncludePlugins
    {
        get => _includePlugins;
        set => SetProperty(ref _includePlugins, value);
    }

    /// <summary>A solution other than the default one is on screen, so failures can be narrowed to it.</summary>
    public bool HasSolution => _session.SelectedSolution is { IsDefaultSolution: false };

    public string SolutionLabel => HasSolution ? $"Only {_session.SelectedSolution!.DisplayLabel}" : string.Empty;

    private bool _solutionOnly;
    /// <summary>Only the flows, workflows and plug-ins in the solution on screen.</summary>
    public bool SolutionOnly
    {
        get => _solutionOnly;
        set => SetProperty(ref _solutionOnly, value);
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(EnvironmentSessionViewModel.SelectedSolution) or null)) return;
        OnPropertyChanged(nameof(HasSolution));
        OnPropertyChanged(nameof(SolutionLabel));
    }

    private FailureQuery BuildQuery()
    {
        if (!SolutionOnly || !HasSolution)
        {
            return new FailureQuery { CloudFlows = IncludeFlows, ClassicWorkflows = IncludeClassicWorkflows, Plugins = IncludePlugins };
        }

        var items = _session.AllItems;
        return new FailureQuery
        {
            CloudFlows = IncludeFlows,
            ClassicWorkflows = IncludeClassicWorkflows,
            Plugins = IncludePlugins,
            WorkflowIds = items.Where(i => i.ComponentType == ProcessType).Select(i => i.ObjectId).ToHashSet(),
            PluginTypeNames = items.Where(i => i.ComponentType == PluginTypeType).Select(i => i.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            PluginStepIds = items.Where(i => i.ComponentType == PluginStepType).Select(i => i.ObjectId).ToHashSet()
        };
    }

    // ---------------------------------------------------------------- drill-down

    private FailureSummaryRow? _selectedComponent;
    public FailureSummaryRow? SelectedComponent
    {
        get => _selectedComponent;
        set
        {
            if (SetProperty(ref _selectedComponent, value) && value is not null) ShowEvents(value.Events);
        }
    }

    private FailureErrorGroup? _selectedError;
    public FailureErrorGroup? SelectedError
    {
        get => _selectedError;
        set
        {
            if (SetProperty(ref _selectedError, value) && value is not null) ShowEvents(value.Events);
        }
    }

    private FailureEvent? _selectedEvent;
    public FailureEvent? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (SetProperty(ref _selectedEvent, value)) OpenEventCommand.RaiseCanExecuteChanged();
        }
    }

    private void ShowEvents(IEnumerable<FailureEvent> events)
    {
        DrillEvents.Clear();
        foreach (var e in events) DrillEvents.Add(e);
        SelectedEvent = null;
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
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private string _notes = string.Empty;
    /// <summary>Sources that could not be read, or were cut short.</summary>
    public string Notes
    {
        get => _notes;
        private set => SetProperty(ref _notes, value);
    }

    // ---------------------------------------------------------------- load

    public async Task LoadAsync()
    {
        if (_session.Client is not { } client) return;

        if (!IncludeFlows && !IncludeClassicWorkflows && !IncludePlugins)
        {
            Status = "Choose at least one of cloud flows, classic workflows and plug-ins.";
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
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
            var data = await client.GetFailuresAsync(from, to, query, progress, cts.Token);
            if (cts.IsCancellationRequested) return;

            _data = data;
            _from = from;
            _to = to;

            var components = FailureOverview.ByComponent(data);
            Components.Clear();
            foreach (var row in components) Components.Add(row);

            Errors.Clear();
            foreach (var group in FailureOverview.ByError(data)) Errors.Add(group);

            Trend.Clear();
            foreach (var bucket in FailureOverview.Trend(data, from, to)) Trend.Add(bucket);

            _selectedComponent = null;
            _selectedError = null;
            OnPropertyChanged(nameof(SelectedComponent));
            OnPropertyChanged(nameof(SelectedError));
            ShowEvents(data.Events.OrderByDescending(e => e.When));

            Notes = string.Join(Environment.NewLine, data.Notes);

            var scope = query.WorkflowIds is not null ? $" in {_session.SelectedSolution?.DisplayLabel}" : string.Empty;
            Status = data.Events.Count == 0
                ? $"No failures{scope} from {from.ToLocalTime():yyyy-MM-dd HH:mm} to {to.ToLocalTime():yyyy-MM-dd HH:mm}."
                : $"{data.Events.Count:N0} failure(s) across {components.Count:N0} component(s){scope} from " +
                  $"{from.ToLocalTime():yyyy-MM-dd HH:mm} to {to.ToLocalTime():yyyy-MM-dd HH:mm}. Choose a row to see its failures.";
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
            if (ReferenceEquals(_cts, cts)) IsBusy = false;
            ExportCsvCommand.RaiseCanExecuteChanged();
            ExportMarkdownCommand.RaiseCanExecuteChanged();
        }
    }

    // ---------------------------------------------------------------- open

    private void OpenEvent(FailureEvent? failure)
    {
        if (failure is null) return;

        if (failure.Source == FailureSource.Plugin)
        {
            var item = (failure.StepId is { } stepId
                           ? _session.AllItems.FirstOrDefault(i => i.ComponentType == PluginStepType && i.ObjectId == stepId)
                           : null)
                       ?? _session.AllItems.FirstOrDefault(i => i.ComponentType == PluginTypeType &&
                                                                string.Equals(i.Name, failure.ComponentKey, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                _session.OpenDetails(item, DetailsTab.TraceLog);
                return;
            }
        }
        else if (failure.WorkflowId is { } workflowId && _session.FindLoaded(workflowId) is { } item)
        {
            _session.OpenDetails(item, DetailsTab.Runs);
            return;
        }

        Status = $"{failure.ComponentName} is not in the loaded list, so its details cannot be opened here. " +
                 "Load the default solution to reach every component.";
    }

    /// <summary>Lets go of the session when the window closes, and stops any read in progress.</summary>
    public void Detach()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _cts?.Cancel();
    }

    // ---------------------------------------------------------------- export

    private void Export(bool markdown)
    {
        if (_data is not { } data) return;

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
