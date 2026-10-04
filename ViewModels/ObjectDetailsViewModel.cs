using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// Everything worth knowing about one object beyond its row: which solutions carry it (the
/// layering question that bites during deployments), what depends on it in both directions
/// (the question worth asking before deleting anything), and - for a table - what it owns,
/// down to the properties of each column, relationship, key, form, view, chart and dashboard.
/// </summary>
public sealed class ObjectDetailsViewModel : ObservableObject
{
    /// <summary>solutioncomponent type code for a table.</summary>
    private const int TableComponentType = 1;

    private readonly DataverseClient _client;
    private readonly IReadOnlyDictionary<Guid, SolutionComponentItem> _known;

    /// <summary>
    /// Bumped on every child selection. Arrowing down a long column list starts a fetch per row,
    /// and only the last one asked for may write its results.
    /// </summary>
    private int _propertyRequest;

    public ObjectDetailsViewModel(
        DataverseClient client,
        SolutionComponentItem item,
        IReadOnlyDictionary<Guid, SolutionComponentItem> known,
        string? environmentId = null,
        Action<string?>? openUrl = null,
        EnvironmentSessionViewModel? session = null,
        DetailsShortcut? openOn = null)
    {
        _openUrl = openUrl ?? DefaultOpenUrl;
        Session = session;
        if (session is not null) session.PropertyChanged += OnSessionChanged;
        _client = client;
        _known = known;
        _environmentId = environmentId;
        Item = item;
        Kind = DetailsTabs.KindOf(item);
        _selectedTab = DetailsTabs.Resolve(Kind, openOn?.Tab ?? DetailsTab.Default);
        _preferredGroup = openOn?.Group;

        OpenLinkCommand = new RelayCommand(_ => OpenUrl(item.MakerUrl), _ => item.MakerUrl is not null);
        OpenInPowerAutomateCommand = new RelayCommand(_ => OpenUrl(FlowUrl), _ => FlowUrl is not null);
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());
        ViewLayerChangesCommand = new RelayCommand(
            p => ShowLayerChanges(p as ComponentLayer ?? SelectedLayer),
            p => (p as ComponentLayer ?? SelectedLayer) is not null);
        CopyPropertyCommand = new RelayCommand(p => CopyProperty(p as ComponentProperty));
        CopyIdCommand = new RelayCommand(_ => CopyId(), _ => item.ObjectId != Guid.Empty);

        // The tab headers carry counts, which follow the collections as they load.
        Solutions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SolutionsTabCount));
        Layers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SolutionsTabCount));
        Dependents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DependencyCount));
        Required.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DependencyCount));
        ChildGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ComponentCount));
    }

    public RelayCommand CopyIdCommand { get; }

    // ---------------------------------------------------------------- tabs

    /// <summary>What the object is, which decides its tabs and their order.</summary>
    public ObjectKind Kind { get; }

    private DetailsTab _selectedTab;
    /// <summary>
    /// The tab on show. It starts on the tab the caller asked for, or else the type's own first
    /// tab, and moves when a run is asked to be shown on the diagram.
    /// </summary>
    public DetailsTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value)) OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>For a table opened on one group of its components - its columns, say.</summary>
    private readonly TableChildKind? _preferredGroup;

    /// <summary>"System jobs" for a classic workflow, whose runs are system jobs.</summary>
    public string RunsTabHeader => IsClassicWorkflow ? "System jobs" : "Run history";

    // ---------------------------------------------------------------- flow state and Power Automate

    /// <summary>The flow in Power Automate. Null for anything but a cloud flow, or where the environment id is unknown.</summary>
    public string? FlowUrl => IsCloudFlow
        ? MakerPortalLinkBuilder.BuildFlowUrl(_environmentId, Item.WorkflowIdUnique?.ToString() ?? Item.ObjectId.ToString())
        : null;

    public bool HasFlowUrl => FlowUrl is not null;

    public RelayCommand OpenInPowerAutomateCommand { get; }

    private bool? _isFlowOn;
    /// <summary>Whether the cloud flow is turned on; null until read, and for anything else.</summary>
    public bool? IsFlowOn
    {
        get => _isFlowOn;
        private set
        {
            if (SetProperty(ref _isFlowOn, value)) OnPropertyChanged(nameof(FlowStateLabel));
        }
    }

    public string? FlowStateLabel => IsFlowOn switch
    {
        true => "On",
        false => "Off",
        null => null
    };

    // ---------------------------------------------------------------- web resource content / flow definition

    private const int WebResourceComponentType = 61;

    public bool IsWebResource => Item.ComponentType == WebResourceComponentType;

    /// <summary>Web resources have content, cloud flows a JSON definition; both show as text.</summary>
    public bool HasSource => IsWebResource || IsCloudFlow;

    public string SourceTabHeader => IsCloudFlow ? "Definition" : "Source";

    private FlowDiagramViewModel? _flowDiagram;
    /// <summary>A cloud flow drawn as the designer draws it, once its definition has been read.</summary>
    public FlowDiagramViewModel? FlowDiagram
    {
        get => _flowDiagram;
        private set
        {
            var previous = _flowDiagram;
            if (!SetProperty(ref _flowDiagram, value)) return;
            if (previous is not null) previous.PropertyChanged -= OnDiagramChanged;
            if (value is not null) value.PropertyChanged += OnDiagramChanged;
            OnPropertyChanged(nameof(HasFlowDiagram));
            OnPropertyChanged(nameof(StatusText));
            ShowRunOnDiagramCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasFlowDiagram => FlowDiagram is not null;

    private void OnDiagramChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FlowDiagramViewModel.StatusLine)) OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// The status bar: on the Design tab, the diagram's own line - the run on show and how its
    /// steps went; elsewhere, what the window read.
    /// </summary>
    public string StatusText => SelectedTab == DetailsTab.Design && FlowDiagram is { } diagram && !IsBusy
        ? diagram.StatusLine
        : Status;

    private PowerAutomate.PowerAutomateClient? _powerAutomate;
    private AsyncRelayCommand? _showRunOnDiagramCommand;

    /// <summary>A cloud flow run drawn on the Design tab, step by step, from the Power Automate API.</summary>
    public AsyncRelayCommand ShowRunOnDiagramCommand => _showRunOnDiagramCommand ??= new AsyncRelayCommand(
        p => ShowRunOnDiagramAsync(p as ProcessRun ?? SelectedRun),
        p => CanShowOnDiagram(p as ProcessRun ?? SelectedRun));

    private bool CanShowOnDiagram(ProcessRun? run) =>
        IsCloudFlow && FlowDiagram is not null && !string.IsNullOrWhiteSpace(run?.Name);

    private async Task ShowRunOnDiagramAsync(ProcessRun? run)
    {
        if (run is null || FlowDiagram is not { } diagram) return;

        SelectedTab = DetailsTab.Design;

        if (string.IsNullOrWhiteSpace(_environmentId))
        {
            diagram.SetRunNotice(
                "This environment's Power Platform id could not be found, so the run cannot be read from Power Automate. " +
                "Add it under EnvironmentIds in settings.json.", isError: true);
            return;
        }

        // The run's own record of the flow id is what Power Automate addresses it by.
        var flowId = run.FlowId ?? Item.WorkflowIdUnique?.ToString() ?? Item.ObjectId.ToString();

        diagram.SetRunNotice($"Reading run {FlowDiagramViewModel.ShortRunName(run.Name)} from Power Automate...");

        try
        {
            _powerAutomate ??= _client.CreatePowerAutomateClient();
            var detail = await _powerAutomate.GetRunAsync(_environmentId, flowId, run.Name);
            diagram.ShowRun(detail, _powerAutomate);
        }
        catch (Exception ex)
        {
            diagram.SetRunNotice(ex.Message, isError: true);
        }
    }

    private string _designStatus = "Reading the flow's definition...";
    /// <summary>Shown in place of the diagram while it loads, or when it could not be drawn.</summary>
    public string DesignStatus
    {
        get => _designStatus;
        private set => SetProperty(ref _designStatus, value);
    }

    private CodeLanguage _sourceLanguage;
    /// <summary>How the source is highlighted: JSON for a flow, by type for a web resource.</summary>
    public CodeLanguage SourceLanguage
    {
        get => _sourceLanguage;
        private set => SetProperty(ref _sourceLanguage, value);
    }

    /// <summary>The highlighting for a web resource type; images and the like have none.</summary>
    internal static CodeLanguage LanguageFor(int webResourceType) => webResourceType switch
    {
        1 => CodeLanguage.Html,
        2 => CodeLanguage.Css,
        3 => CodeLanguage.JavaScript,
        4 or 9 or 11 or 12 => CodeLanguage.Xml, // XML, XSL, SVG, RESX
        _ => CodeLanguage.None
    };

    /// <summary>Said in the status when a file has a language but is too big to colour.</summary>
    private static string HighlightNote(string? text, CodeLanguage language) =>
        language != CodeLanguage.None && !string.IsNullOrEmpty(text) && !CodeHighlighting.ShouldHighlight(text)
            ? " · not highlighted (too large, or minified)"
            : string.Empty;

    private string? _sourceText;
    public string? SourceText
    {
        get => _sourceText;
        private set
        {
            if (!SetProperty(ref _sourceText, value)) return;
            CopySourceCommand.RaiseCanExecuteChanged();
            SaveSourceCommand.RaiseCanExecuteChanged();
        }
    }

    private string _sourceStatus = string.Empty;
    public string SourceStatus
    {
        get => _sourceStatus;
        private set => SetProperty(ref _sourceStatus, value);
    }

    private string _sourceFileName = "content.txt";

    public RelayCommand CopySourceCommand => _copySourceCommand ??= new RelayCommand(
        _ => Status = ClipboardText.TryCopy(SourceText ?? string.Empty, out var failure)
            ? $"Copied the {SourceTabHeader.ToLowerInvariant()} ({SourceText!.Length:N0} characters)."
            : $"Could not copy - the clipboard is held by another application ({failure}).",
        _ => !string.IsNullOrEmpty(SourceText));
    private RelayCommand? _copySourceCommand;

    public RelayCommand SaveSourceCommand => _saveSourceCommand ??= new RelayCommand(_ => SaveSource(), _ => !string.IsNullOrEmpty(SourceText));
    private RelayCommand? _saveSourceCommand;

    private void SaveSource()
    {
        var extension = System.IO.Path.GetExtension(_sourceFileName);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = _sourceFileName,
            Filter = string.IsNullOrEmpty(extension)
                ? "All files (*.*)|*.*"
                : $"{extension.TrimStart('.').ToUpperInvariant()} file (*{extension})|*{extension}|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            System.IO.File.WriteAllText(dialog.FileName, SourceText, new System.Text.UTF8Encoding(false));
            Status = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Parses the definition off the UI thread - a large flow is a large document.</summary>
    private async Task LoadFlowDiagramAsync(string? definition)
    {
        FlowDiagram = null;

        if (string.IsNullOrWhiteSpace(definition))
        {
            DesignStatus = "This flow has no definition stored in Dataverse, so there is nothing to draw.";
            return;
        }

        try
        {
            var design = await Task.Run(() => FlowDesignParser.Parse(definition));
            var diagram = new FlowDiagramViewModel(design, Item.PrimaryLabel);
            FlowDiagram = diagram;

            if (diagram.ChildFlowIds.Count > 0)
            {
                try
                {
                    diagram.SetChildFlowNames(await _client.GetWorkflowNamesAsync(diagram.ChildFlowIds));
                }
                catch (Exception ex)
                {
                    Services.Log.Warn("Child flow names could not be read", ex);
                    // The names are a nicety; without them the cards still show the ids.
                }
            }
        }
        catch (Exception ex)
        {
            DesignStatus = "Could not draw the flow - " + ex.Message;
        }
    }

    private async Task LoadSourceAsync(List<string> problems)
    {
        SourceText = null;

        try
        {
            if (IsCloudFlow)
            {
                var flow = await _client.GetCloudFlowAsync(Item.ObjectId);
                var definition = flow.Definition;
                IsFlowOn = flow.IsOn;
                await LoadFlowDiagramAsync(definition);
                SourceLanguage = CodeLanguage.Json;
                SourceText = string.IsNullOrWhiteSpace(definition) ? null : TextDiff.Prettify(definition);
                _sourceFileName = SafeFileName(Item.PrimaryLabel) + ".json";
                SourceStatus = SourceText is null
                    ? "This flow has no definition stored in Dataverse."
                    : $"The flow's definition (workflow.clientdata), {SourceText.Length:N0} characters." +
                      HighlightNote(SourceText, SourceLanguage);
                return;
            }

            var content = await _client.GetWebResourceContentAsync(Item.ObjectId);
            SourceLanguage = LanguageFor(content.Type);
            SourceText = content.Text;

            // "new_/scripts/account.js" saves as "account.js".
            var leaf = content.Name.Split('/', '\\').LastOrDefault(s => s.Length > 0) ?? content.Name;
            _sourceFileName = SafeFileName(System.IO.Path.HasExtension(leaf) ? leaf : leaf + content.FileExtension);

            SourceStatus = content.Text is null
                ? $"{content.TypeLabel} - binary content ({content.ByteCount:N0} bytes), not shown as text."
                : $"{content.Name} · {content.TypeLabel} · {content.ByteCount:N0} bytes" +
                  HighlightNote(content.Text, SourceLanguage);
        }
        catch (Exception ex)
        {
            SourceStatus = $"Could not read the {SourceTabHeader.ToLowerInvariant()} - {ex.Message}";
            problems.Add(SourceTabHeader.ToLowerInvariant() + ": " + ex.Message);
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "content" : safe;
    }

    // ---------------------------------------------------------------- environment variables

    /// <summary>A definition (380) or one of its value records (381).</summary>
    public bool IsEnvironmentVariable => Item.ComponentType is 380 or 381;

    private EnvironmentVariableInfo? _environmentVariable;
    public EnvironmentVariableInfo? EnvironmentVariable
    {
        get => _environmentVariable;
        private set => SetProperty(ref _environmentVariable, value);
    }

    private string _environmentVariableStatus = string.Empty;
    public string EnvironmentVariableStatus
    {
        get => _environmentVariableStatus;
        private set => SetProperty(ref _environmentVariableStatus, value);
    }

    private async Task LoadEnvironmentVariableAsync(List<string> problems)
    {
        try
        {
            EnvironmentVariable = await _client.GetEnvironmentVariableAsync(Item.ObjectId, Item.ComponentType == 381);
            EnvironmentVariableStatus = EnvironmentVariable.ValueRecordCount > 1
                ? $"This definition has {EnvironmentVariable.ValueRecordCount} value records - there should be at most one. The first is shown."
                : EnvironmentVariable.EffectiveSource;
        }
        catch (Exception ex)
        {
            EnvironmentVariable = null;
            EnvironmentVariableStatus = "Could not read the environment variable - " + ex.Message;
            problems.Add("environment variable: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- row count

    private CancellationTokenSource? _countCts;

    /// <summary>Counts, or - while a count is running - stops it.</summary>
    public AsyncRelayCommand CountRowsCommand => _countRowsCommand ??= new AsyncRelayCommand(_ => CountRowsAsync());
    private AsyncRelayCommand? _countRowsCommand;

    private bool _isCounting;
    public bool IsCounting
    {
        get => _isCounting;
        private set
        {
            if (SetProperty(ref _isCounting, value)) OnPropertyChanged(nameof(CountButtonLabel));
        }
    }

    public string CountButtonLabel => IsCounting ? "Stop counting" : "Count rows";

    private string _rowCountText = string.Empty;
    /// <summary>"36,250,112 rows", or progress while counting.</summary>
    public string RowCountText
    {
        get => _rowCountText;
        private set
        {
            if (!SetProperty(ref _rowCountText, value)) return;
            OnPropertyChanged(nameof(RowCountValue));
            OnPropertyChanged(nameof(RowCountUnit));
        }
    }

    /// <summary>The count alone - "~36,250,112" - for the header, which puts "rows" beneath it.</summary>
    public string RowCountValue => RowCountText switch
    {
        var t when t.EndsWith(" rows", StringComparison.Ordinal) => t[..^5],
        var t when t.EndsWith(" row", StringComparison.Ordinal) => t[..^4],
        var t => t
    };

    /// <summary>"rows" under a count; nothing under "Counting..." or "Count failed".</summary>
    public string RowCountUnit => RowCountText.EndsWith(" rows", StringComparison.Ordinal) ? "rows"
        : RowCountText.EndsWith(" row", StringComparison.Ordinal) ? "row"
        : string.Empty;

    private string? _rowCountDetail;
    /// <summary>How the count was reached, and the daily snapshot beside it.</summary>
    public string? RowCountDetail
    {
        get => _rowCountDetail;
        private set => SetProperty(ref _rowCountDetail, value);
    }

    private string? _rowCountWarning;
    /// <summary>Set when the exact count and the snapshot disagree in the way a paging cap would explain.</summary>
    public string? RowCountWarning
    {
        get => _rowCountWarning;
        private set => SetProperty(ref _rowCountWarning, value);
    }

    /// <summary>The page-number paging the count relies on is documented as capped here.</summary>
    private const long SimplePagingLimit = 50_000;

    private RowCountSnapshot? _snapshot;

    /// <summary>
    /// Dataverse's stored count, shown as soon as the window opens: instant, but refreshed about
    /// daily, so it stands until an exact count replaces it.
    /// </summary>
    private async Task LoadRowCountSnapshotAsync()
    {
        try
        {
            _snapshot = await _client.GetRowCountSnapshotAsync(Item.ObjectId);
        }
        catch
        {
            _snapshot = null;
        }

        // An exact count made meanwhile is the better answer; leave it alone.
        if (IsCounting || RowCountDetail?.StartsWith("Exact", StringComparison.Ordinal) == true) return;

        if (_snapshot is { } s)
        {
            RowCountText = $"~{s.Rows:N0} rows";
            RowCountDetail = StoredCountDescription(s) + " Count rows gives an exact, current number.";
        }
    }

    private static string StoredCountDescription(RowCountSnapshot snapshot) =>
        snapshot.LastUpdated is { } at
            ? $"Dataverse's stored count, last refreshed {at.ToLocalTime():yyyy-MM-dd HH:mm}."
            : "Dataverse's stored count, refreshed about daily.";

    private async Task CountRowsAsync()
    {
        if (IsCounting)
        {
            _countCts?.Cancel();
            return;
        }

        var cts = _countCts = new CancellationTokenSource();
        IsCounting = true;
        RowCountWarning = null;
        RowCountText = "Counting...";

        try
        {
            var progress = new Progress<string>(text => RowCountText = text);
            var result = await _client.CountRowsAsync(Item.ObjectId, _snapshot?.Rows, progress, cts.Token);

            RowCountText = result.Rows == 1 ? "1 row" : $"{result.Rows:N0} rows";
            RowCountDetail =
                $"Exact count by {result.Method}, {result.Requests} request(s) in {result.Elapsed.TotalSeconds:0.0} s." +
                (_snapshot is { } s ? $" {StoredCountDescription(s)} It said {s.Rows:N0}." : string.Empty);

            if (result.Rows == SimplePagingLimit && _snapshot?.Rows > SimplePagingLimit)
            {
                RowCountWarning =
                    $"Counted exactly {SimplePagingLimit:N0}, but Dataverse's stored count says {_snapshot.Rows:N0}. " +
                    "Dataverse can cap page-number paging at 50,000 rows, so the table is probably larger than counted.";
            }
        }
        catch (OperationCanceledException)
        {
            RowCountText = _snapshot is { } s ? $"~{s.Rows:N0} rows" : "Count stopped";
            RowCountDetail = _snapshot is { } t ? StoredCountDescription(t) + " The exact count was stopped." : null;
        }
        catch (Exception ex)
        {
            RowCountText = _snapshot is { } s ? $"~{s.Rows:N0} rows" : "Count failed";
            RowCountDetail = _snapshot is { } t ? StoredCountDescription(t) : null;
            RowCountWarning = "The exact count failed: " + ex.Message;
        }
        finally
        {
            IsCounting = false;
            cts.Dispose();
            if (ReferenceEquals(_countCts, cts)) _countCts = null;
        }
    }

    // ---------------------------------------------------------------- run history and trace log

    private const int ProcessComponentType = 29;

    // The category code is what to go by; the labels cover items loaded from a cache written
    // before the code was kept. Dataverse labels category 5 "Modern Flow" and 0 "Workflow".
    public bool IsCloudFlow => Kind == ObjectKind.CloudFlow;

    public bool IsClassicWorkflow => Kind == ObjectKind.ClassicWorkflow;

    /// <summary>Cloud flows have runs, classic workflows have system jobs; both land in one tab.</summary>
    public bool HasRunHistory => IsCloudFlow || IsClassicWorkflow;

    /// <summary>A plug-in assembly, plug-in type or registration step - anything the trace log can name.</summary>
    public bool HasTraceLog => Item.ComponentType is 90 or 91 or 92;

    /// <summary>The Power Platform environment id, for Power Automate run links. Null where unknown.</summary>
    private readonly string? _environmentId;

    public ObservableCollection<ProcessRun> Runs { get; } = new();

    /// <summary>Opens a run - the one passed, else the selected one - in Power Automate.</summary>
    public RelayCommand OpenRunCommand => _openRunCommand ??= new RelayCommand(
        p => OpenUrl((p as ProcessRun ?? SelectedRun)?.PortalUrl),
        p => (p as ProcessRun ?? SelectedRun)?.PortalUrl is not null);
    private RelayCommand? _openRunCommand;

    public ObservableCollection<PluginTraceEntry> TraceEntries { get; } = new();

    private ProcessRun? _selectedRun;
    public ProcessRun? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (!SetProperty(ref _selectedRun, value)) return;
            OpenRunCommand.RaiseCanExecuteChanged();
            ShowRunOnDiagramCommand.RaiseCanExecuteChanged();
        }
    }

    private PluginTraceEntry? _selectedTrace;
    public PluginTraceEntry? SelectedTrace
    {
        get => _selectedTrace;
        set => SetProperty(ref _selectedTrace, value);
    }

    private string _runsStatus = string.Empty;
    /// <summary>Why the run list is empty, or what it covers.</summary>
    public string RunsStatus
    {
        get => _runsStatus;
        private set => SetProperty(ref _runsStatus, value);
    }

    private string _traceStatus = string.Empty;
    /// <summary>The organization's trace setting, and why the list may be short or empty.</summary>
    public string TraceStatus
    {
        get => _traceStatus;
        private set => SetProperty(ref _traceStatus, value);
    }

    private bool _isTraceOff;
    /// <summary>Tracing is switched off for the organization, so nothing new is being written.</summary>
    public bool IsTraceOff
    {
        get => _isTraceOff;
        private set => SetProperty(ref _isTraceOff, value);
    }

    public string RunsTabCount => Runs.Count >= DataverseClient.MaxRunHistory ? $"{Runs.Count}+" : $"{Runs.Count}";

    public string TraceTabCount =>
        TraceEntries.Count >= DataverseClient.MaxRunHistory ? $"{TraceEntries.Count}+" : $"{TraceEntries.Count}";

    private async Task LoadRunsAsync(List<string> problems)
    {
        Runs.Clear();
        SelectedRun = null;
        var dataverseFailed = false;

        try
        {
            var runs = IsCloudFlow
                ? await _client.GetCloudFlowRunsAsync(Item.ObjectId)
                : await _client.GetClassicWorkflowRunsAsync(Item.ObjectId);

            foreach (var run in runs)
            {
                // The run's own record of the flow id is what Power Automate addresses it by; the
                // solution-independent id and then the workflow id stand in where it is missing.
                if (IsCloudFlow)
                {
                    run.PortalUrl = MakerPortalLinkBuilder.BuildFlowRunUrl(
                        _environmentId,
                        run.FlowId ?? Item.WorkflowIdUnique?.ToString() ?? Item.ObjectId.ToString(),
                        run.Name);
                }

                Runs.Add(run);
            }
            SelectedRun = Runs.FirstOrDefault(r => r.Outcome == RunOutcome.Failed) ?? Runs.FirstOrDefault();

            var failed = Runs.Count(r => r.Outcome == RunOutcome.Failed);
            RunsStatus = Runs.Count == 0
                ? IsCloudFlow
                    ? "No runs recorded in Dataverse. Cloud flow run history is kept for 28 days by default, " +
                      "and only while flow run history in Dataverse is turned on for the environment."
                    : "No system jobs found for this workflow. Completed jobs may have been cleaned up."
                : $"Latest {Runs.Count} run(s), {failed} failed.";
        }
        catch (Exception ex)
        {
            RunsStatus = "Could not read the run history - " + ex.Message;
            problems.Add("runs: " + ex.Message);
            dataverseFailed = true;
        }
        finally
        {
            OnPropertyChanged(nameof(RunsTabCount));
        }

        // Dataverse records a run once it is done, and not at once: runs in progress, and ones that
        // have just finished or been cancelled, come from Power Automate's live list.
        if (IsCloudFlow) await MergeLiveRunsAsync(dataverseFailed);
    }

    private async Task MergeLiveRunsAsync(bool dataverseFailed)
    {
        var dataverseStatus = RunsStatus;

        if (string.IsNullOrWhiteSpace(_environmentId))
        {
            RunsStatus = dataverseStatus + " Runs in progress cannot be shown: the environment's Power Platform id is unknown.";
            return;
        }

        var flowId = Runs.FirstOrDefault(r => r.FlowId is not null)?.FlowId
                     ?? Item.WorkflowIdUnique?.ToString()
                     ?? Item.ObjectId.ToString();

        try
        {
            _powerAutomate ??= _client.CreatePowerAutomateClient();
            var live = await _powerAutomate.GetRunsAsync(_environmentId, flowId);

            var merged = RunHistoryMerge.Merge(Runs.ToList(), live);
            var selectedName = SelectedRun?.Name;

            Runs.Clear();
            foreach (var run in merged.Runs)
            {
                run.PortalUrl = MakerPortalLinkBuilder.BuildFlowRunUrl(_environmentId, run.FlowId ?? flowId, run.Name);
                Runs.Add(run);
            }

            SelectedRun = Runs.FirstOrDefault(r => r.Name == selectedName)
                          ?? Runs.FirstOrDefault(r => r.Outcome == RunOutcome.Failed)
                          ?? Runs.FirstOrDefault();

            RunsStatus = LiveRunsSummary(merged, dataverseFailed);
        }
        catch (Exception ex)
        {
            RunsStatus = dataverseStatus + " Runs in progress, or finished in the last few minutes, may be missing: " +
                         "Power Automate's live list could not be read - " + ex.Message;
        }
        finally
        {
            OnPropertyChanged(nameof(RunsTabCount));
        }
    }

    /// <summary>"Latest 50 runs, 2 failed. 1 running and 1 cancelled run are not in Dataverse yet - shown live."</summary>
    private string LiveRunsSummary(RunHistoryMerge.Result merged, bool dataverseFailed)
    {
        if (merged.Runs.Count == 0)
        {
            return "No runs in Dataverse or in Power Automate. Run history is kept for about 28 days.";
        }

        var failed = merged.Runs.Count(r => r.Outcome == RunOutcome.Failed);
        var text = $"Latest {merged.Runs.Count} run(s), {failed} failed.";

        if (merged.LiveOnly > 0)
        {
            var live = merged.Runs.Where(r => r.IsLiveOnly)
                .GroupBy(r => r.Status.ToLowerInvariant())
                .Select(g => $"{g.Count()} {g.Key}");
            text += $" {string.Join(", ", live)} not in Dataverse yet - shown live from Power Automate.";
        }

        if (merged.Updated > 0)
        {
            text += $" {merged.Updated} run(s) have moved on since Dataverse's copy and show their live status.";
        }

        if (dataverseFailed) text += " Dataverse's run history could not be read, so these come from Power Automate alone.";

        return text;
    }

    private async Task LoadTraceLogAsync(List<string> problems)
    {
        TraceEntries.Clear();
        SelectedTrace = null;

        PluginTraceSetting? setting = null;
        try
        {
            setting = await _client.GetPluginTraceSettingAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Warn("Plug-in trace setting could not be read", ex);
            // Reading the setting is a courtesy; the log itself is what matters.
        }

        IsTraceOff = setting == PluginTraceSetting.Off;

        try
        {
            foreach (var entry in await _client.GetPluginTraceLogAsync(Item.ObjectId, Item.ComponentType)
                                  ?? Array.Empty<PluginTraceEntry>())
            {
                TraceEntries.Add(entry);
            }

            SelectedTrace = TraceEntries.FirstOrDefault(t => t.HasException) ?? TraceEntries.FirstOrDefault();

            var settingNote = setting switch
            {
                PluginTraceSetting.Off => "Plug-in tracing is off in this environment, so no new entries are being written. " +
                                          "Turn it on under Settings > Administration > System settings > Customization.",
                PluginTraceSetting.Exception => "Tracing is set to exceptions only, so successful runs are not logged.",
                PluginTraceSetting.All => "Tracing is on for all executions.",
                _ => null
            };

            var exceptions = TraceEntries.Count(t => t.HasException);
            var found = TraceEntries.Count == 0
                ? "No trace log entries for this plug-in."
                : $"Latest {TraceEntries.Count} entr{(TraceEntries.Count == 1 ? "y" : "ies")}, {exceptions} with an exception.";

            TraceStatus = settingNote is null ? found : $"{found} {settingNote}";
        }
        catch (Exception ex)
        {
            TraceStatus = "Could not read the plug-in trace log - " + ex.Message;
            problems.Add("trace log: " + ex.Message);
        }
        finally
        {
            OnPropertyChanged(nameof(TraceTabCount));
        }
    }

    /// <summary>"3 · 3" - solutions, then layers.</summary>
    public string SolutionsTabCount => LayersSupported
        ? $"{Solutions.Count:N0} · {Layers.Count:N0}"
        : $"{Solutions.Count:N0}";

    public int DependencyCount => Dependents.Count + Required.Count;

    public int ComponentCount => ChildGroups.Sum(g => g.Count);

    private void CopyId()
    {
        var id = Item.ObjectId.ToString();

        Status = ClipboardText.TryCopy(id, out var failure)
            ? $"Copied object id: {id}"
            : $"Could not copy the object id - the clipboard is held by another application ({failure}).";
    }

    public SolutionComponentItem Item { get; }

    public RelayCommand OpenLinkCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ViewLayerChangesCommand { get; }
    public RelayCommand CopyPropertyCommand { get; }

    public ObservableCollection<ContainingSolution> Solutions { get; } = new();
    public ObservableCollection<DependencyRef> Dependents { get; } = new();
    public ObservableCollection<DependencyRef> Required { get; } = new();
    public ObservableCollection<ComponentLayer> Layers { get; } = new();

    /// <summary>What the table owns, one group per kind. Empty for anything that is not a table.</summary>
    public ObservableCollection<TableChildGroup> ChildGroups { get; } = new();

    /// <summary>The selected group's children, after the filter box.</summary>
    public ObservableCollection<TableChild> Children { get; } = new();

    /// <summary>Every property of the selected child, fetched the first time it is selected.</summary>
    public ObservableCollection<ComponentProperty> ChildProperties { get; } = new();

    /// <summary>Only a table has child components; every other type hides the whole section.</summary>
    public bool IsTable => Item.ComponentType == TableComponentType;

    private ComponentLayer? _selectedLayer;
    public ComponentLayer? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (SetProperty(ref _selectedLayer, value)) ViewLayerChangesCommand.RaiseCanExecuteChanged();
        }
    }

    private TableChildGroup? _selectedChildGroup;
    public TableChildGroup? SelectedChildGroup
    {
        get => _selectedChildGroup;
        set
        {
            if (SetProperty(ref _selectedChildGroup, value)) ApplyChildFilter();
        }
    }

    private string _childFilter = string.Empty;
    /// <summary>Narrows the selected group - a table with 400 columns needs it.</summary>
    public string ChildFilter
    {
        get => _childFilter;
        set
        {
            if (SetProperty(ref _childFilter, value)) ApplyChildFilter();
        }
    }

    private TableChild? _selectedChild;
    public TableChild? SelectedChild
    {
        get => _selectedChild;
        set
        {
            if (SetProperty(ref _selectedChild, value)) _ = ShowChildPropertiesAsync(value);
        }
    }

    private bool _isLoadingProperties;
    public bool IsLoadingProperties
    {
        get => _isLoadingProperties;
        private set => SetProperty(ref _isLoadingProperties, value);
    }

    private string _childStatus = "Select a component to see its properties.";
    /// <summary>Stands in for the property grid whenever there is nothing in it.</summary>
    public string ChildStatus
    {
        get => _childStatus;
        private set => SetProperty(ref _childStatus, value);
    }

    private bool _layersSupported = true;
    /// <summary>False when this object's type has no known mapping into the layers API - the
    /// section is hidden rather than shown empty, which would read as "no unmanaged layer".</summary>
    public bool LayersSupported
    {
        get => _layersSupported;
        private set
        {
            if (SetProperty(ref _layersSupported, value)) OnPropertyChanged(nameof(SolutionsTabCount));
        }
    }

    private bool _hasUnmanagedLayer;
    public bool HasUnmanagedLayer
    {
        get => _hasUnmanagedLayer;
        private set => SetProperty(ref _hasUnmanagedLayer, value);
    }

    /// <summary>The tab this window was opened from - its environment's name, type and colour.</summary>
    public EnvironmentSessionViewModel? Session { get; }

    public bool HasSession => Session is not null;

    /// <summary>"Get report — Process · ECT-PreProd": the environment, so windows from different ones tell apart.</summary>
    public string Title => Session is { } session
        ? $"{Item.PrimaryLabel} — {Item.ComponentTypeName} · {session.Title}"
        : $"{Item.PrimaryLabel} — {Item.ComponentTypeName}";

    /// <summary>Stops following the tab, so a closed window is not kept alive by the tab it came from.</summary>
    public void Detach()
    {
        if (Session is not null) Session.PropertyChanged -= OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EnvironmentSessionViewModel.Title)) OnPropertyChanged(nameof(Title));
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(StatusText));
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText));
        }
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Loading...";

        var problems = new List<string>();

        try
        {
            Solutions.Clear();
            Dependents.Clear();
            Required.Clear();
            Layers.Clear();
            ClearChildComponents();

            // Each is useful on its own, so one failing must not hide the others.
            try
            {
                foreach (var solution in await _client.GetContainingSolutionsAsync(Item.ObjectId))
                {
                    Solutions.Add(solution);
                }
            }
            catch (Exception ex)
            {
                problems.Add("solutions: " + ex.Message);
            }

            await LoadDependenciesAsync(DependencyDirection.Dependent, Dependents, problems);
            await LoadDependenciesAsync(DependencyDirection.Required, Required, problems);

            // The stored count is one quick request, so it shows before the child components load.
            if (IsTable) await LoadRowCountSnapshotAsync();
            if (IsTable) await LoadChildComponentsAsync(problems);
            if (HasRunHistory) await LoadRunsAsync(problems);
            if (HasTraceLog) await LoadTraceLogAsync(problems);
            if (IsEnvironmentVariable) await LoadEnvironmentVariableAsync(problems);
            if (HasSource) await LoadSourceAsync(problems);

            try
            {
                var layers = await _client.GetComponentLayersAsync(Item.ObjectId, Item.ComponentType);
                LayersSupported = layers is not null;

                if (layers is not null)
                {
                    foreach (var layer in layers) Layers.Add(layer);
                }

                HasUnmanagedLayer = Layers.Any(l => l.IsUnmanagedLayer);
            }
            catch (Exception ex)
            {
                problems.Add("layers: " + ex.Message);
            }

            var summary = $"{Solutions.Count} solution(s), {Dependents.Count} dependent, {Required.Count} required";
            if (IsTable) summary += $", {ChildGroups.Sum(g => g.Count)} child component(s)";

            Status = problems.Count == 0
                ? summary + "."
                : "Some details could not be read - " + string.Join("; ", problems);
        }
        catch (Exception ex)
        {
            // Started without anyone awaiting it, so this is the only place it can be reported.
            Services.Log.Error($"Details for {Item.ObjectId} could not be loaded", ex);
            Status = "The details could not be loaded - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadDependenciesAsync(
        DependencyDirection direction,
        ObservableCollection<DependencyRef> target,
        List<string> problems)
    {
        try
        {
            foreach (var dependency in await _client.GetDependenciesAsync(Item.ObjectId, Item.ComponentType, direction))
            {
                // Most dependencies point at something already loaded, so a name is usually free.
                if (_known.TryGetValue(dependency.ObjectId, out var match))
                {
                    dependency.ResolvedName = match.PrimaryLabel;
                }

                target.Add(dependency);
            }
        }
        catch (Exception ex)
        {
            problems.Add($"{direction.ToString().ToLowerInvariant()} components: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads everything the table owns. The six reads are independent, so they run together and
    /// each is allowed to fail on its own - a tenant that blocks charts should not cost the user
    /// their columns.
    /// </summary>
    private async Task LoadChildComponentsAsync(List<string> problems)
    {
        TableIdentity? table;

        try
        {
            table = await _client.GetTableIdentityAsync(Item.ObjectId, Item.Name);
        }
        catch (Exception ex)
        {
            problems.Add("table metadata: " + ex.Message);
            return;
        }

        if (table is null)
        {
            problems.Add("table metadata: this component could not be matched to a table.");
            return;
        }

        var columns = _client.GetTableColumnsAsync(table);
        var relationships = _client.GetTableRelationshipsAsync(table);
        var keys = _client.GetTableKeysAsync(table);
        var forms = _client.GetTableFormsAsync(table);
        var views = _client.GetTableViewsAsync(table);
        var charts = _client.GetTableChartsAsync(table);

        var all = new List<TableChild>();
        all.AddRange(await GatherAsync(columns, "columns", problems));
        all.AddRange(await GatherAsync(relationships, "relationships", problems));
        all.AddRange(await GatherAsync(keys, "keys", problems));
        all.AddRange(await GatherAsync(forms, "forms", problems));
        all.AddRange(await GatherAsync(views, "views", problems));
        all.AddRange(await GatherAsync(charts, "charts", problems));

        // Every kind is listed even when it came back empty: "Views 0" is an answer, whereas a
        // missing section only raises the question of whether it was looked for.
        foreach (var kind in Enum.GetValues<TableChildKind>())
        {
            ChildGroups.Add(new TableChildGroup
            {
                Kind = kind,
                Label = GroupLabel(kind),
                Children = all
                    .Where(c => c.Kind == kind)
                    .OrderBy(c => c.PrimaryLabel, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            });
        }

        // Opened from a "Columns" or "Views" shortcut: that group, even when it is empty.
        SelectedChildGroup = ChildGroups.FirstOrDefault(g => g.Kind == _preferredGroup)
                             ?? ChildGroups.FirstOrDefault(g => !g.IsEmpty)
                             ?? ChildGroups.FirstOrDefault();
    }

    private static async Task<IReadOnlyList<TableChild>> GatherAsync(
        Task<IReadOnlyList<TableChild>> read,
        string what,
        List<string> problems)
    {
        try
        {
            return await read;
        }
        catch (Exception ex)
        {
            problems.Add($"{what}: {ex.Message}");
            return Array.Empty<TableChild>();
        }
    }

    private static string GroupLabel(TableChildKind kind) => kind switch
    {
        TableChildKind.Column => "Columns",
        TableChildKind.Relationship => "Relationships",
        TableChildKind.Key => "Keys",
        TableChildKind.Form => "Forms",
        TableChildKind.View => "Views",
        TableChildKind.Chart => "Charts",
        _ => "Dashboards"
    };

    private void ClearChildComponents()
    {
        // Nothing may still be in flight against the old list once it is gone.
        _propertyRequest++;

        ChildGroups.Clear();
        Children.Clear();
        ChildProperties.Clear();

        _selectedChildGroup = null;
        OnPropertyChanged(nameof(SelectedChildGroup));

        _selectedChild = null;
        OnPropertyChanged(nameof(SelectedChild));

        IsLoadingProperties = false;
        ChildStatus = "Select a component to see its properties.";
    }

    private void ApplyChildFilter()
    {
        // Clearing the list makes the grid report a null selection, so the one to restore has to
        // be remembered before that happens.
        var previous = SelectedChild;

        Children.Clear();

        var children = SelectedChildGroup?.Children ?? Array.Empty<TableChild>();
        var terms = ChildFilter.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var child in children)
        {
            if (terms.All(t => child.FilterIndex.Contains(t, StringComparison.Ordinal))) Children.Add(child);
        }

        // Typing in the filter box should not throw away what is already on screen, but a
        // selection the filter has just excluded - or one from another group - has to go.
        SelectedChild = previous is not null && Children.Contains(previous)
            ? previous
            : Children.Count == 1 ? Children[0] : null;
    }

    /// <summary>
    /// Fills the property grid for one child, from its cache where it has one. A form or view
    /// record carries its whole XML definition, which is why this is not read up front.
    /// </summary>
    private async Task ShowChildPropertiesAsync(TableChild? child)
    {
        var request = ++_propertyRequest;

        ChildProperties.Clear();

        if (child is null)
        {
            IsLoadingProperties = false;
            ChildStatus = "Select a component to see its properties.";
            return;
        }

        if (child.Properties is null)
        {
            IsLoadingProperties = true;
            ChildStatus = "Loading properties...";

            try
            {
                child.Properties = await _client.GetChildPropertiesAsync(child);
            }
            catch (Exception ex)
            {
                if (request != _propertyRequest) return;

                IsLoadingProperties = false;
                ChildStatus = "Properties could not be read - " + ex.Message;
                return;
            }

            // The user moved on while this was in flight; their current selection owns the grid.
            if (request != _propertyRequest) return;

            IsLoadingProperties = false;
        }

        foreach (var property in child.Properties) ChildProperties.Add(property);

        ChildStatus = ChildProperties.Count == 0
            ? "Dataverse returned no properties for this component."
            : string.Empty;
    }

    /// <summary>
    /// Puts one property's value on the clipboard. A property is read in order to use it
    /// somewhere else - a query, a ticket, a config file - and the grid is read-only, so a click
    /// is a shorter route than selecting text that cannot be selected.
    /// </summary>
    private void CopyProperty(ComponentProperty? property)
    {
        if (property is null) return;

        Status = ClipboardText.TryCopy(property.Value, out var failure)
            ? $"Copied {property.Name}: {Shorten(property.Value)}"
            : $"Could not copy {property.Name} - the clipboard is held by another application ({failure}).";
    }

    /// <summary>A form's XML went to the clipboard whole; the status bar only needs to say so.</summary>
    private static string Shorten(string value)
    {
        var line = value.ReplaceLineEndings(" ");
        return line.Length <= 120 ? line : line[..117] + "...";
    }

    /// <summary>
    /// Opens the diff for one layer. <see cref="Layers"/> runs top of the stack first, so the
    /// layer a given one sits on top of - the "before" side of the diff - is the next row down.
    /// </summary>
    private void ShowLayerChanges(ComponentLayer? layer)
    {
        if (layer is null) return;

        var index = Layers.IndexOf(layer);
        var below = index >= 0 && index + 1 < Layers.Count ? Layers[index + 1] : null;

        var window = new Views.LayerChangesWindow
        {
            DataContext = new LayerChangesViewModel(Item.PrimaryLabel, layer, below),
            Owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => ReferenceEquals(w.DataContext, this)) ?? Application.Current.MainWindow
        };

        window.Show();
    }

    /// <summary>Opens links in the environment's browser profile - supplied by the tab that opened this window.</summary>
    private readonly Action<string?> _openUrl;

    private void OpenUrl(string? url) => _openUrl(url);

    private static void DefaultOpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            LinkLauncher.Open(url, null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
