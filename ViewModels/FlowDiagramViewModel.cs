using PPObjectSearch.Core;
using System.Collections.ObjectModel;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>What the step pane's code shows: the step's definition, or its inputs or outputs in the run.</summary>
public enum FlowCodePane
{
    Definition,
    Inputs,
    Outputs
}

/// <summary>A step in a drawn sequence: a card, or parallel branches.</summary>
public abstract class FlowStepViewModel : ObservableObject
{
    /// <summary>The first step of a sequence has no connector drawn above it.</summary>
    public bool IsFirst { get; init; }
}

/// <summary>A labelled column of steps: a condition's Yes, a switch case, a parallel branch.</summary>
public sealed class FlowBranchViewModel
{
    public FlowBranchViewModel(string label, IReadOnlyList<FlowStepViewModel> steps)
    {
        Label = label;
        Steps = steps;
    }

    public string Label { get; }
    public bool HasLabel => Label.Length > 0;
    public IReadOnlyList<FlowStepViewModel> Steps { get; }
    public bool IsEmpty => Steps.Count == 0;

    /// <summary>"Yes" and "No" are coloured like the designer's; anything else is neutral.</summary>
    public string Tone => Label switch
    {
        "Yes" => "Yes",
        "No" => "No",
        _ => "Neutral"
    };
}

/// <summary>Branches that run side by side.</summary>
public sealed class FlowParallelViewModel : FlowStepViewModel
{
    public required IReadOnlyList<FlowBranchViewModel> Branches { get; init; }

    public string Heading => $"Parallel · {Branches.Count} branches";
}

/// <summary>An outcome a step can wait on, for its colour.</summary>
public enum RunAfterTone
{
    Succeeded,
    Failed,
    TimedOut,
    Skipped,
    Other
}

/// <summary>One outcome in a run-after condition, drawn as a coloured chip.</summary>
public sealed record FlowStatusChip(string Label, string Glyph, RunAfterTone Tone);

/// <summary>
/// "After Try: Failed, Timed out" - the outcomes of one earlier step that let this step run.
/// Only conditions other than plain success are drawn; success alone is the default and says nothing.
/// </summary>
public sealed record FlowRunAfterCondition(string After, IReadOnlyList<FlowStatusChip> Statuses)
{
    public string AfterLabel => $"after {After}";
}

/// <summary>A trigger or action, drawn as a card - with its branches below it if it is a container.</summary>
public sealed class FlowCardViewModel : FlowStepViewModel
{
    public FlowCardViewModel(FlowNode node, FlowCardViewModel? parent)
    {
        Node = node;
        Parent = parent;

        RunAfterConditions = node.RunAfter
            .Where(r => !r.IsDefault)
            .Select(r => new FlowRunAfterCondition(r.Action.Replace('_', ' '), r.Statuses.Select(Chip).ToList()))
            .ToList();

        RunAfterSummary = string.Join(Environment.NewLine, node.RunAfter
            .Where(r => !r.IsDefault)
            .Select(r => $"Runs if {r.Action.Replace('_', ' ')} {string.Join(" or ", r.Statuses.Select(Outcome))}"));
    }

    /// <summary>The chip for one run-after status, as the designer words and colours it.</summary>
    internal static FlowStatusChip Chip(string status) => status.ToLowerInvariant() switch
    {
        "succeeded" => new FlowStatusChip("Succeeded", "\uE73E", RunAfterTone.Succeeded),
        "failed" => new FlowStatusChip("Failed", "\uE711", RunAfterTone.Failed),
        "timedout" => new FlowStatusChip("Timed out", "\uE823", RunAfterTone.TimedOut),
        "skipped" => new FlowStatusChip("Skipped", "\uE893", RunAfterTone.Skipped),
        _ => new FlowStatusChip(status, "\uE946", RunAfterTone.Other)
    };

    /// <summary>A run-after status as words: "TimedOut" reads "timed out".</summary>
    internal static string Outcome(string status) => status.ToLowerInvariant() switch
    {
        "succeeded" => "succeeded",
        "failed" => "failed",
        "skipped" => "was skipped",
        "timedout" => "timed out",
        var other => other
    };

    public FlowNode Node { get; }
    public FlowCardViewModel? Parent { get; }

    public string Title => Node.DisplayName;
    public string Summary => Node.Summary;

    /// <summary>What a screen reader says for the card: the step, what it does, and in a run how it went.</summary>
    public string AccessibleName => InRun ? $"{Title}, {Summary}, {RunBadge}" : $"{Title}, {Summary}";
    /// <summary>A short particular - for a child flow call, the child flow's name once it is known.</summary>
    public string? Detail => ChildFlowName ?? Node.Detail;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    private string? _childFlowName;
    /// <summary>"Escalate case" - the flow a "Run a child flow" step calls, read from Dataverse.</summary>
    public string? ChildFlowName
    {
        get => _childFlowName;
        internal set
        {
            if (!SetProperty(ref _childFlowName, value)) return;
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(HasDetail));
            OnPropertyChanged(nameof(DetailTooltip));
        }
    }

    /// <summary>The detail in full - a long loop expression is cut short on the card - with a child flow's id.</summary>
    public string? DetailTooltip => Node.ChildFlowId is { } id ? $"{Detail}{Environment.NewLine}Child flow {id}" : Detail;
    public string? Description => Node.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Node.Description);
    public FlowNodeCategory Category => Node.Category;
    public FlowNodeKind Kind => Node.Kind;

    /// <summary>
    /// The run-after conditions other than plain success - drawn on the connector into the step,
    /// where the designer draws them. Empty for an ordinary step, which then shows nothing extra.
    /// </summary>
    public IReadOnlyList<FlowRunAfterCondition> RunAfterConditions { get; }
    public bool HasRunAfterConditions => RunAfterConditions.Count > 0;

    /// <summary>"Runs if Try failed or timed out" - the conditions in words, for the tooltip and search.</summary>
    public string RunAfterSummary { get; }

    public IReadOnlyList<FlowBranchViewModel> Branches { get; internal set; } = Array.Empty<FlowBranchViewModel>();
    public bool IsContainer => Node.IsContainer;

    /// <summary>Steps inside, at every depth - for the collapsed label.</summary>
    public int InnerCount { get; internal set; }

    public string CollapsedLabel => InnerCount == 1 ? "1 step inside" : $"{InnerCount:N0} steps inside";

    /// <summary>A Segoe Fluent glyph for the kind of step.</summary>
    public string Glyph => Node.Kind switch
    {
        FlowNodeKind.Trigger => "",
        FlowNodeKind.Condition => "",
        FlowNodeKind.Switch => "",
        FlowNodeKind.ForEach or FlowNodeKind.Until => "",
        FlowNodeKind.Scope => "",
        _ => Node.Category switch
        {
            FlowNodeCategory.Connector => "",
            FlowNodeCategory.Data => "",
            FlowNodeCategory.Variable => "",
            FlowNodeCategory.Http => "",
            FlowNodeCategory.ChildFlow => "",
            FlowNodeCategory.Control when Node.Type.Equals("Terminate", StringComparison.OrdinalIgnoreCase) => "",
            FlowNodeCategory.Control => "",
            _ => ""
        }
    };

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private RelayCommand? _toggleCommand;
    public RelayCommand ToggleCommand => _toggleCommand ??= new RelayCommand(_ => IsExpanded = !IsExpanded);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _isMatch;
    public bool IsMatch
    {
        get => _isMatch;
        set => SetProperty(ref _isMatch, value);
    }

    // ---------------------------------------------------------------- a run

    private bool _inRun;
    private FlowActionResult? _result;

    /// <summary>The step's result in the run on show; null when it was never reached, or no run is shown.</summary>
    public FlowActionResult? Result => _result;

    /// <summary>Null when the diagram shows the design alone; NotRun when the run never reached the step.</summary>
    public FlowStepOutcome? RunOutcome => _inRun ? _result?.Outcome ?? FlowStepOutcome.NotRun : null;

    public bool InRun => _inRun;

    /// <summary>Skipped and unreached steps fade back, so the path the run took stands out.</summary>
    public bool IsDimmed => RunOutcome is FlowStepOutcome.Skipped or FlowStepOutcome.NotRun;

    public string RunGlyph => RunOutcome switch
    {
        FlowStepOutcome.Succeeded => "\uE73E",
        FlowStepOutcome.Failed => "\uE711",
        FlowStepOutcome.Skipped => "\uE893",
        FlowStepOutcome.TimedOut => "\uE823",
        FlowStepOutcome.Cancelled => "\uE71A",
        FlowStepOutcome.Running => "\uE895",
        FlowStepOutcome.Waiting => "\uE916",
        FlowStepOutcome.NotRun => "\uE738",
        _ => "\uE946"
    };

    public string RunLabel => RunOutcome switch
    {
        FlowStepOutcome.Succeeded => "Succeeded",
        FlowStepOutcome.Failed => "Failed",
        FlowStepOutcome.Skipped => "Skipped",
        FlowStepOutcome.TimedOut => "Timed out",
        FlowStepOutcome.Cancelled => "Cancelled",
        FlowStepOutcome.Running => "Running",
        FlowStepOutcome.Waiting => "Waiting",
        FlowStepOutcome.NotRun => "Did not run",
        null => string.Empty,
        _ => _result?.Status ?? "Unknown"
    };

    public string? DurationLabel => _result?.Duration is { } d ? FormatDuration(d) : null;

    /// <summary>"Failed · 1.2 s" - the badge on the card.</summary>
    public string RunBadge => DurationLabel is { } d && RunOutcome is not FlowStepOutcome.Skipped ? $"{RunLabel} · {d}" : RunLabel;

    private string? _iterationLabel;
    /// <summary>"4 iterations · 1 failed", on a loop once its iterations have been counted.</summary>
    public string? IterationLabel
    {
        get => _iterationLabel;
        internal set
        {
            if (SetProperty(ref _iterationLabel, value)) OnPropertyChanged(nameof(HasIterationLabel));
        }
    }

    public bool HasIterationLabel => !string.IsNullOrEmpty(IterationLabel);

    /// <summary>Inside a loop, a step's own result is its last pass; its iterations say the rest.</summary>
    public bool IsInsideLoop
    {
        get
        {
            for (var parent = Parent; parent is not null; parent = parent.Parent)
            {
                if (parent.Kind is FlowNodeKind.ForEach or FlowNodeKind.Until) return true;
            }

            return false;
        }
    }

    internal void SetResult(bool inRun, FlowActionResult? result)
    {
        _inRun = inRun;
        _result = result;
        IterationLabel = null;

        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(InRun));
        OnPropertyChanged(nameof(RunOutcome));
        OnPropertyChanged(nameof(IsDimmed));
        OnPropertyChanged(nameof(RunGlyph));
        OnPropertyChanged(nameof(RunLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(RunBadge));
        OnPropertyChanged(nameof(AccessibleName));
    }

    internal static string FormatDuration(TimeSpan d) => FlowRunFormat.Duration(d);

    internal string SearchText =>
        $"{Node.DisplayName} {Node.Name} {Node.TypeLabel} {Node.Connector} {Node.Operation} {Detail} {Node.Description} {RunAfterSummary}";
}

/// <summary>
/// A cloud flow drawn as the designer draws it: trigger, then steps top to bottom, branches side
/// by side. Read-only - selecting a step shows its JSON, nothing here can change the flow.
/// </summary>
public sealed class FlowDiagramViewModel : ObservableObject, IDisposable
{
    public const double MinZoom = 0.4;
    public const double MaxZoom = 1.5;

    private readonly List<FlowCardViewModel> _cards = new();
    private readonly string? _flowName;

    public FlowDiagramViewModel(FlowDesign design, string? flowName = null)
    {
        Design = design;
        _flowName = flowName;

        var steps = new List<FlowStepViewModel>();

        if (design.Triggers.Count == 1)
        {
            steps.Add(Card(design.Triggers[0], null, isFirst: true));
        }
        else if (design.Triggers.Count > 1)
        {
            steps.Add(new FlowParallelViewModel
            {
                IsFirst = true,
                Branches = design.Triggers.Select(t => new FlowBranchViewModel(string.Empty, new[] { Card(t, null, isFirst: true) })).ToList()
            });
        }

        steps.AddRange(Build(design.Actions, null, firstIsFirst: steps.Count == 0));
        Steps = steps;

        SelectCommand = new RelayCommand(p => Selected = p as FlowCardViewModel);
        ExpandAllCommand = new RelayCommand(_ => SetExpanded(true));
        CollapseAllCommand = new RelayCommand(_ => SetExpanded(false));
        ZoomInCommand = new RelayCommand(_ => Zoom += 0.1);
        ZoomOutCommand = new RelayCommand(_ => Zoom -= 0.1);
        ResetZoomCommand = new RelayCommand(_ => Zoom = 1);
        CopyMermaidCommand = new RelayCommand(_ => CopyMermaid());
        JumpToFailureCommand = new RelayCommand(_ => JumpToFailure(), _ => FailedCards.Any());
        ClearRunCommand = new RelayCommand(_ => ClearRun(), _ => IsRunMode);
        ShowInputsCommand = new AsyncRelayCommand(_ => ShowContentAsync(inputs: true), _ => InputsLink is not null);
        ShowOutputsCommand = new AsyncRelayCommand(_ => ShowContentAsync(inputs: false), _ => OutputsLink is not null);
        ShowDefinitionCommand = new RelayCommand(_ => ClearContent(), _ => HasContent);
        CopyCodeCommand = new RelayCommand(_ => CopyCode(), _ => !string.IsNullOrEmpty(DetailCode));
    }

    public FlowDesign Design { get; }

    /// <summary>The top-level sequence: the trigger, then the actions.</summary>
    public IReadOnlyList<FlowStepViewModel> Steps { get; }

    public IReadOnlyList<FlowCardViewModel> Cards => _cards;

    public RelayCommand SelectCommand { get; }
    public RelayCommand ExpandAllCommand { get; }
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ResetZoomCommand { get; }
    public RelayCommand CopyMermaidCommand { get; }

    /// <summary>The whole flow as a Mermaid flowchart, for documentation.</summary>
    public string Mermaid => FlowMermaidExporter.ToMermaid(Design, _flowName, _childFlowNames);

    private IReadOnlyDictionary<Guid, string> _childFlowNames = new Dictionary<Guid, string>();

    /// <summary>The ids of every child flow the flow calls, for looking up their names.</summary>
    public IReadOnlyList<Guid> ChildFlowIds() =>
        _cards.Select(c => c.Node.ChildFlowId).OfType<Guid>().Distinct().ToList();

    /// <summary>
    /// Names the child flows on their cards. One the environment does not have - deleted, or the
    /// flow imported without it - says so, rather than leaving a bare id.
    /// </summary>
    public void SetChildFlowNames(IReadOnlyDictionary<Guid, string> names)
    {
        _childFlowNames = names;

        foreach (var card in _cards)
        {
            if (card.Node.ChildFlowId is not { } id) continue;
            card.ChildFlowName = names.TryGetValue(id, out var name) ? name : "Child flow not found in this environment";
        }
    }

    private string _notice = string.Empty;
    /// <summary>A short confirmation in the toolbar - what the last copy did.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>
    /// The window's status bar while the diagram is on show: the last copy, or the run on show
    /// and how its steps went, or the design's size.
    /// </summary>
    public string StatusLine
    {
        get
        {
            if (!string.IsNullOrEmpty(Notice)) return Notice;
            if (_run is null) return Summary;

            var steps = _cards.Count(c => c.Result is not null);
            var failed = FailedCards.Count();
            var skipped = _cards.Count(c => c.RunOutcome == FlowStepOutcome.Skipped);

            return $"Showing run {ShortRunName(_run.RunName)} · {steps:N0} step(s), {failed:N0} failed, {skipped:N0} skipped" +
                   " · run details are kept about 28 days";
        }
    }

    // ---------------------------------------------------------------- a run on the diagram

    private PowerAutomateClient? _runClient;
    private FlowRunDetail? _run;
    private readonly Dictionary<string, Task<FlowRepetitions>> _repetitions = new(StringComparer.Ordinal);

    public RelayCommand JumpToFailureCommand { get; }
    public RelayCommand ClearRunCommand { get; }
    public AsyncRelayCommand ShowInputsCommand { get; }
    public AsyncRelayCommand ShowOutputsCommand { get; }
    public RelayCommand ShowDefinitionCommand { get; }

    /// <summary>Raised when the diagram goes back to the design alone.</summary>
    public event EventHandler? RunCleared;

    private string? _runNotice;
    /// <summary>"Reading run ...", or why a run could not be shown - in the banner's place.</summary>
    public string? RunNotice
    {
        get => _runNotice;
        private set
        {
            if (!SetProperty(ref _runNotice, value)) return;
            OnPropertyChanged(nameof(HasRunNotice));
            OnPropertyChanged(nameof(HasRunBanner));
        }
    }

    public bool HasRunNotice => !string.IsNullOrEmpty(RunNotice);

    private bool _isRunNoticeError;
    public bool IsRunNoticeError
    {
        get => _isRunNoticeError;
        private set => SetProperty(ref _isRunNoticeError, value);
    }

    /// <summary>The banner shows a run, or news about one.</summary>
    public bool HasRunBanner => IsRunMode || HasRunNotice;

    /// <summary>Says what is happening with a run that is not on show yet - or why it cannot be.</summary>
    public void SetRunNotice(string? notice, bool isError = false)
    {
        IsRunNoticeError = isError;
        RunNotice = notice;
    }

    public FlowRunDetail? Run => _run;
    public bool IsRunMode => _run is not null;

    /// <summary>"Run …CU01 · Failed".</summary>
    public string RunHeading => _run is null
        ? string.Empty
        : $"Run {ShortRunName(_run.RunName)} · {RunStatusLabel(_run.Outcome, _run.Status)}";

    /// <summary>"Started 2026-10-01 09:15:02 · took 12.4 s · 2 steps failed".</summary>
    public string RunDetail
    {
        get
        {
            if (_run is null) return string.Empty;

            var parts = new List<string>();
            if (_run.StartTime is { } start) parts.Add($"Started {start:yyyy-MM-dd HH:mm:ss}");
            if (_run.Duration is { } took) parts.Add($"took {FlowCardViewModel.FormatDuration(took)}");

            var failed = FailedCards.Count();
            if (failed > 0) parts.Add(failed == 1 ? "1 step failed" : $"{failed} steps failed");
            if (_run.ViaAdminScope) parts.Add("read with environment admin access");

            return string.Join(" · ", parts);
        }
    }

    public string? RunError => ErrorText(_run?.ErrorMessage, _run?.ErrorCode);

    /// <summary>"Code: message", or the message alone where there is no code; null without a message.</summary>
    private static string? ErrorText(string? message, string? code) => (message, code) switch
    {
        ({ Length: > 0 }, { Length: > 0 }) => $"{code}: {message}",
        ({ Length: > 0 }, _) => message,
        _ => null
    };

    public bool HasRunError => RunError is not null;

    private string? _runMismatch;
    /// <summary>
    /// Steps the run reports that the flow no longer has. The diagram is the flow as it is now; a
    /// step renamed or removed since the run has nowhere to show its result, so the gap is named
    /// rather than left to read as "did not run".
    /// </summary>
    public string? RunMismatch
    {
        get => _runMismatch;
        private set
        {
            if (SetProperty(ref _runMismatch, value)) OnPropertyChanged(nameof(HasRunMismatch));
        }
    }

    public bool HasRunMismatch => RunMismatch is not null;

    /// <summary>"Run failed" - the run strip's lead, in the outcome's colour.</summary>
    public string RunOutcomeText => _run is null
        ? string.Empty
        : "Run " + RunStatusLabel(_run.Outcome, _run.Status).ToLowerInvariant();

    /// <summary>"2026-09-30 22:14 · 4.2 s · trigger Modified" - the rest of the strip.</summary>
    public string RunStripText
    {
        get
        {
            if (_run is null) return string.Empty;

            var parts = new List<string>();
            if (_run.StartTime is { } start) parts.Add($"{start:yyyy-MM-dd HH:mm}");
            if (_run.Duration is { } took) parts.Add(FlowCardViewModel.FormatDuration(took));
            if (_run.Trigger?.Name is { Length: > 0 } trigger) parts.Add("trigger " + trigger.Replace('_', ' '));

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Everything the strip leaves out: the run id, the step count, the run's own error.</summary>
    public string RunToolTip => _run is null
        ? string.Empty
        : string.Join(Environment.NewLine, new[] { RunHeading, RunDetail, RunError }.Where(t => !string.IsNullOrEmpty(t)));

    public bool IsRunSucceeded => _run?.Outcome == FlowStepOutcome.Succeeded;

    private bool _isRunFailed;
    public bool IsRunFailed
    {
        get => _isRunFailed;
        private set => SetProperty(ref _isRunFailed, value);
    }

    /// <summary>
    /// The steps that failed in their own right, top to bottom - the order the diagram draws them.
    /// A scope, condition or loop fails whenever something inside it does, with the generic
    /// "An action failed"; counting or selecting it would point at the wrapper (the "Try" scope of
    /// every try/catch flow) instead of the step that broke. So a failed container only counts
    /// when nothing inside it failed.
    /// </summary>
    private IEnumerable<FlowCardViewModel> FailedCards
    {
        get
        {
            var failed = _cards.Where(IsFailed).ToList();
            var holdsFailure = new HashSet<FlowCardViewModel>();

            foreach (var parent in failed.Select(card => card.Parent))
            {
                for (var holder = parent; holder is not null; holder = holder.Parent) holdsFailure.Add(holder);
            }

            return failed.Where(c => !holdsFailure.Contains(c));
        }
    }

    private static bool IsFailed(FlowCardViewModel card) =>
        card.RunOutcome is FlowStepOutcome.Failed or FlowStepOutcome.TimedOut;

    /// <summary>
    /// Colours the diagram by one run: each step's outcome and duration, skipped and unreached
    /// steps faded, loops counted. The first failure, if any, is selected.
    /// </summary>
    public void ShowRun(FlowRunDetail run, PowerAutomateClient client)
    {
        SetRunNotice(null);
        CancelRunReads();
        _runCts = new CancellationTokenSource();
        _run = run;
        _runClient = client;
        _repetitions.Clear();

        var triggerName = Design.Triggers.Count == 1 ? Design.Triggers[0].Name : null;

        foreach (var card in _cards) card.SetResult(inRun: true, ResultFor(card, run, triggerName));

        var drawn = _cards.Select(c => c.Node.Name).ToHashSet(StringComparer.Ordinal);
        var missing = run.Actions.Keys.Where(name => !drawn.Contains(name)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        RunMismatch = missing.Count == 0 ? null : MismatchNote(missing);

        IsRunFailed = run.Outcome is FlowStepOutcome.Failed or FlowStepOutcome.TimedOut;
        RaiseRunState();

        Work.Track(CountIterationsAsync());

        if (FailedCards.Any()) JumpToFailure();
    }

    /// <summary>What the run says about one card's step; null where it has nothing on it.</summary>
    private static FlowActionResult? ResultFor(FlowCardViewModel card, FlowRunDetail run, string? triggerName)
    {
        if (card.Kind != FlowNodeKind.Trigger) return run.Actions.TryGetValue(card.Node.Name, out var r) ? r : null;

        // The run names its trigger; a flow has one, whatever it is called now.
        return run.Trigger is { } t && (t.Name == card.Node.Name || card.Node.Name == triggerName) ? t : null;
    }

    /// <summary>Steps of the run the diagram no longer has - renamed or removed since it ran.</summary>
    private static string MismatchNote(List<string> missing)
    {
        var steps = missing.Count == 1 ? "1 step in this run is" : $"{missing.Count} steps in this run are";
        var them = missing.Count == 1 ? "it" : "them";
        var end = missing.Count > 6 ? ", ..." : ".";
        return $"{steps} no longer in the flow (renamed or removed since), so the diagram cannot show {them}: " +
               string.Join(", ", missing.Take(6).Select(n => n.Replace('_', ' '))) + end +
               " The flow has changed since this run, so steps added since show as did not run.";
    }

    /// <summary>Back to the design alone.</summary>
    public void ClearRun()
    {
        CancelRunReads();
        _run = null;
        _runClient = null;
        _repetitions.Clear();

        foreach (var card in _cards) card.SetResult(inRun: false, result: null);

        RunMismatch = null;
        IsRunFailed = false;
        RaiseRunState();
        RunCleared?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseRunState()
    {
        OnPropertyChanged(nameof(Run));
        OnPropertyChanged(nameof(IsRunMode));
        OnPropertyChanged(nameof(HasRunBanner));
        OnPropertyChanged(nameof(RunHeading));
        OnPropertyChanged(nameof(RunDetail));
        OnPropertyChanged(nameof(RunError));
        OnPropertyChanged(nameof(HasRunError));
        OnPropertyChanged(nameof(RunOutcomeText));
        OnPropertyChanged(nameof(RunStripText));
        OnPropertyChanged(nameof(RunToolTip));
        OnPropertyChanged(nameof(IsRunSucceeded));
        OnPropertyChanged(nameof(StatusLine));
        JumpToFailureCommand.RaiseCanExecuteChanged();
        ClearRunCommand.RaiseCanExecuteChanged();
        RefreshSelectedRun();
    }

    /// <summary>Selects the first failed step, opening whatever holds it.</summary>
    private void JumpToFailure()
    {
        if (FailedCards.FirstOrDefault() is not { } failed) return;

        for (var parent = failed.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        Selected = failed;
    }

    /// <summary>
    /// "4 iterations · 1 failed" on each loop that ran. A loop's own entry lists no iterations;
    /// a step inside it lists one per pass, so the first such step that ran is asked.
    /// </summary>
    private async Task CountIterationsAsync()
    {
        var run = _run;

        foreach (var loop in _cards.Where(c => c.Kind is FlowNodeKind.ForEach or FlowNodeKind.Until &&
                                               c.RunOutcome is not (FlowStepOutcome.Skipped or FlowStepOutcome.NotRun)))
        {
            if (!await CountLoopAsync(run, loop)) return;
        }
    }

    /// <summary>Labels one loop with its passes. False once another run is on show, so counting stops.</summary>
    private async Task<bool> CountLoopAsync(FlowRunDetail? run, FlowCardViewModel loop)
    {
        // Every step that runs once per pass of this loop - including a nested loop or scope,
        // which list their passes the same way. A plain step is the cheapest to ask.
        var inside = _cards.Where(c =>
                !ReferenceEquals(c, loop) && NearestLoop(c) == loop &&
                c.RunOutcome is not (FlowStepOutcome.Skipped or FlowStepOutcome.NotRun))
            .OrderBy(c => c.IsContainer)
            .ToList();

        if (inside.Count == 0) return true;

        try
        {
            var repetitions = await RepetitionsAsync(inside[0].Node.Name);
            if (!ReferenceEquals(run, _run)) return false;

            // Which passes failed is a question for the steps that failed, not the one asked
            // for the count: a Compose that never fails would report a clean loop beside a
            // failing HTTP step. Each failing step's failed passes are gathered, once each.
            var failedPasses = FailedPasses(repetitions);

            foreach (var step in inside.Skip(1).Where(c => !c.IsContainer && IsFailed(c)))
            {
                var passes = await RepetitionsAsync(step.Node.Name);
                if (!ReferenceEquals(run, _run)) return false;

                failedPasses.UnionWith(FailedPasses(passes));
            }

            var failed = (failedPasses.Count, IsFailed(loop)) switch
            {
                ( > 0, _) => $" · {failedPasses.Count:N0} failed",
                (_, true) => " · failed",
                _ => string.Empty
            };

            loop.IterationLabel = CountLabel(repetitions) + failed;
        }
        catch (Exception ex)
        {
            Services.Log.Warn("Loop iterations could not be counted", ex);
            // A count is a nicety; the diagram stands without it.
        }

        return true;
    }

    private static HashSet<string> FailedPasses(FlowRepetitions repetitions) =>
        repetitions
            .Where(r => r.Outcome is FlowStepOutcome.Failed or FlowStepOutcome.TimedOut)
            .Select(r => r.Label)
            .ToHashSet(StringComparer.Ordinal);

    private static FlowCardViewModel? NearestLoop(FlowCardViewModel card)
    {
        for (var parent = card.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Kind is FlowNodeKind.ForEach or FlowNodeKind.Until) return parent;
        }

        return null;
    }

    private Task<FlowRepetitions> RepetitionsAsync(string actionName)
    {
        if (_run is null || _runClient is null) return Task.FromResult(new FlowRepetitions());

        if (!_repetitions.TryGetValue(actionName, out var task))
        {
            task = FetchRepetitionsAsync(_run, _runClient, actionName, _runCts?.Token ?? default);

            // A read can fail before it ever yields, in which case its own clean-up has already
            // run - so a task that is already faulted is not kept either.
            if (!task.IsFaulted && !task.IsCanceled) _repetitions[actionName] = task;
        }

        return task;
    }

    /// <summary>
    /// A failed read is not kept: one throttled or dropped request would otherwise answer "could
    /// not read iterations" for that step for as long as the run stayed on show.
    /// </summary>
    private async Task<FlowRepetitions> FetchRepetitionsAsync(
        FlowRunDetail run, PowerAutomateClient client, string actionName, CancellationToken ct)
    {
        try
        {
            return await client.GetRepetitionsAsync(run, actionName, ct);
        }
        catch
        {
            if (ReferenceEquals(run, _run)) _repetitions.Remove(actionName);
            throw;
        }
    }

    /// <summary>What the diagram is reading in the background - counts, iterations, content.</summary>
    internal BackgroundWork Work { get; } = new();

    /// <summary>Iterations and content reads for the run on show; cancelled when it goes.</summary>
    private CancellationTokenSource? _runCts;

    /// <summary>Stops everything still being read for the run - the window is closing.</summary>
    public void Detach() => CancelRunReads();

    public void Dispose() => CancelRunReads();

    /// <summary>
    /// Cancels the reads for the run that was on show. Its source can go with it: the reads hold the
    /// token, which still answers once disposed, and new ones take theirs from the field.
    /// </summary>
    private void CancelRunReads()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }

    private static string CountLabel(FlowRepetitions repetitions)
    {
        var count = repetitions.Count == 1 ? "1 iteration" : $"{repetitions.Count:N0} iterations";
        return repetitions.IsTruncated ? $"{repetitions.Count:N0}+ iterations" : count;
    }

    // ---------------------------------------------------------------- the selected step in the run

    /// <summary>"Failed · 1.2 s · started 09:15:02".</summary>
    public string SelectedRunStatus
    {
        get
        {
            if (Selected is not { InRun: true } card) return string.Empty;

            var parts = new List<string> { card.RunLabel };
            if (card.DurationLabel is { } d && card.RunOutcome is not FlowStepOutcome.Skipped) parts.Add(d);
            if (card.Result?.StartTime is { } start) parts.Add($"started {start:HH:mm:ss}");
            if (card.IsInsideLoop && card.Result is not null) parts.Add("last iteration");

            return string.Join(" · ", parts);
        }
    }

    public string? SelectedError => ErrorText(
        SelectedIteration?.ErrorMessage ?? Selected?.Result?.ErrorMessage,
        SelectedIteration?.ErrorCode ?? Selected?.Result?.ErrorCode);

    public bool HasSelectedError => SelectedError is not null;

    public ObservableCollection<FlowRepetition> SelectedIterations { get; } = new();
    public bool HasSelectedIterations => SelectedIterations.Count > 0;

    private FlowRepetition? _selectedIteration;
    /// <summary>An iteration of the selected step; its inputs and outputs replace the step's last pass.</summary>
    public FlowRepetition? SelectedIteration
    {
        get => _selectedIteration;
        set
        {
            if (!SetProperty(ref _selectedIteration, value)) return;

            ClearContent();
            OnPropertyChanged(nameof(SelectedError));
            OnPropertyChanged(nameof(HasSelectedError));
            RaiseContentCommands();
        }
    }

    private string _iterationStatus = string.Empty;
    public string IterationStatus
    {
        get => _iterationStatus;
        private set => SetProperty(ref _iterationStatus, value);
    }

    private string? InputsLink => SelectedIteration?.InputsLink ?? Selected?.Result?.InputsLink;
    private string? OutputsLink => SelectedIteration?.OutputsLink ?? Selected?.Result?.OutputsLink;

    private string? _content;
    private string _contentTitle = "Definition";
    private string? _contentError;
    private bool _isContentLoading;

    /// <summary>The JSON in the side panel: the step's definition, or the inputs or outputs asked for.</summary>
    public string? DetailCode => _content ?? Selected?.Node.Json;
    public string DetailCodeTitle => _contentTitle;
    public bool HasContent => _content is not null;

    public string? ContentError
    {
        get => _contentError;
        private set
        {
            if (SetProperty(ref _contentError, value)) OnPropertyChanged(nameof(HasContentError));
        }
    }

    public bool HasContentError => ContentError is not null;

    public bool IsContentLoading
    {
        get => _isContentLoading;
        private set => SetProperty(ref _isContentLoading, value);
    }

    private int _selectionVersion;

    /// <summary>
    /// Bumped by every request for inputs or outputs and by anything that clears the pane - a new
    /// step, a new iteration, Definition - so only the latest request may fill it. The selection
    /// version alone missed a change of iteration and a quick Inputs-then-Outputs.
    /// </summary>
    private int _contentVersion;

    /// <summary>On a new selection in a run: its iterations, if it is inside a loop.</summary>
    private void RefreshSelectedRun()
    {
        _selectionVersion++;
        SelectedIterations.Clear();
        _selectedIteration = null;
        OnPropertyChanged(nameof(SelectedIteration));
        IterationStatus = string.Empty;
        ClearContent();

        OnPropertyChanged(nameof(SelectedRunStatus));
        OnPropertyChanged(nameof(SelectedError));
        OnPropertyChanged(nameof(HasSelectedError));
        OnPropertyChanged(nameof(HasSelectedIterations));
        RaiseContentCommands();

        if (Selected is { InRun: true, IsInsideLoop: true, Result: not null } card && !card.IsContainer)
        {
            Work.Track(LoadIterationsAsync(card, _selectionVersion));
        }
    }

    private async Task LoadIterationsAsync(FlowCardViewModel card, int version)
    {
        IterationStatus = "Reading iterations...";

        try
        {
            var repetitions = await RepetitionsAsync(card.Node.Name);
            if (version != _selectionVersion) return;

            foreach (var repetition in repetitions) SelectedIterations.Add(repetition);

            var failed = repetitions.Count(r => r.Outcome is FlowStepOutcome.Failed or FlowStepOutcome.TimedOut);
            var failedNote = failed > 0 ? $", {failed:N0} failed" : string.Empty;
            var truncatedNote = repetitions.IsTruncated ? $" - only the first {repetitions.Count:N0} could be read" : string.Empty;
            IterationStatus = repetitions.Count == 0
                ? "No iterations recorded."
                : $"{repetitions.Count:N0} iteration(s){failedNote}{truncatedNote} - pick one to see its inputs and outputs.";
            OnPropertyChanged(nameof(HasSelectedIterations));
        }
        catch (Exception ex)
        {
            if (version == _selectionVersion) IterationStatus = "Could not read iterations - " + ex.Message;
        }
    }

    private async Task ShowContentAsync(bool inputs)
    {
        var link = inputs ? InputsLink : OutputsLink;
        if (link is null || _runClient is null) return;

        var version = ++_contentVersion;
        var what = inputs ? "Inputs" : "Outputs";
        var of = SelectedIteration is { } iteration ? $" of iteration {iteration.Label}" : string.Empty;

        _contentKind = inputs ? FlowCodePane.Inputs : FlowCodePane.Outputs;
        IsContentLoading = true;
        ContentError = null;

        try
        {
            var content = await _runClient.GetContentAsync(link, _runCts?.Token ?? default);
            if (version != _contentVersion) return;

            _content = content;
            _contentTitle = what + of;
        }
        catch (OperationCanceledException)
        {
            // The run went or the window closed; nothing is waiting for this any more.
        }
        catch (Exception ex)
        {
            if (version == _contentVersion)
            {
                ContentError = $"Could not read the {what.ToLowerInvariant()} - {ex.Message}";
                if (_content is null) _contentKind = FlowCodePane.Definition;
            }
        }
        finally
        {
            if (version == _contentVersion)
            {
                IsContentLoading = false;
                RaiseContent();
            }
        }
    }

    private void ClearContent()
    {
        _contentVersion++;
        IsContentLoading = false;
        _content = null;
        _contentKind = FlowCodePane.Definition;
        _contentTitle = "Definition";
        ContentError = null;
        RaiseContent();
    }

    private void RaiseContent()
    {
        OnPropertyChanged(nameof(DetailCode));
        OnPropertyChanged(nameof(DetailCodeTitle));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsDefinitionShown));
        OnPropertyChanged(nameof(IsInputsShown));
        OnPropertyChanged(nameof(IsOutputsShown));
        ShowDefinitionCommand.RaiseCanExecuteChanged();
        CopyCodeCommand.RaiseCanExecuteChanged();
    }

    private void RaiseContentCommands()
    {
        ShowInputsCommand.RaiseCanExecuteChanged();
        ShowOutputsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanShowInputs));
        OnPropertyChanged(nameof(CanShowOutputs));
    }

    // ---------------------------------------------------------------- Definition | Inputs | Outputs

    /// <summary>Which the code pane shows - set as soon as inputs or outputs are asked for.</summary>
    private FlowCodePane _contentKind = FlowCodePane.Definition;

    /// <summary>Inputs and outputs exist only for a step that ran, in the run on show.</summary>
    public bool CanShowInputs => InputsLink is not null;
    public bool CanShowOutputs => OutputsLink is not null;

    // The switch's three segments. Choosing one does what the old buttons did; un-choosing is
    // the other segment's business.
    public bool IsDefinitionShown
    {
        get => _contentKind == FlowCodePane.Definition;
        set
        {
            if (value && _contentKind != FlowCodePane.Definition) ClearContent();
        }
    }

    public bool IsInputsShown
    {
        get => _contentKind == FlowCodePane.Inputs;
        set
        {
            if (value && _contentKind != FlowCodePane.Inputs && CanShowInputs) Work.Track(ShowContentAsync(inputs: true));
        }
    }

    public bool IsOutputsShown
    {
        get => _contentKind == FlowCodePane.Outputs;
        set
        {
            if (value && _contentKind != FlowCodePane.Outputs && CanShowOutputs) Work.Track(ShowContentAsync(inputs: false));
        }
    }

    public RelayCommand CopyCodeCommand { get; }

    private void CopyCode()
    {
        var code = DetailCode ?? string.Empty;
        Notice = ClipboardText.TryCopy(code, out var failure)
            ? $"Copied the step's {DetailCodeTitle.ToLowerInvariant()} ({code.Length:N0} characters)."
            : "Could not copy - " + failure;
    }

    /// <summary>The tail of a run name, which is what tells runs apart: "...CU01".</summary>
    internal static string ShortRunName(string name) => name.Length > 12 ? "…" + name[^8..] : name;

    internal static string RunStatusLabel(FlowStepOutcome outcome, string status) => outcome switch
    {
        FlowStepOutcome.Succeeded => "Succeeded",
        FlowStepOutcome.Failed => "Failed",
        FlowStepOutcome.TimedOut => "Timed out",
        FlowStepOutcome.Cancelled => "Cancelled",
        FlowStepOutcome.Running => "Running",
        FlowStepOutcome.Waiting => "Waiting",
        _ => status
    };

    private void CopyMermaid()
    {
        var mermaid = Mermaid;

        Notice = ClipboardText.TryCopy(mermaid, out var failure)
            ? $"Copied as Mermaid ({mermaid.Split('\n').Length:N0} lines) - paste it into a wiki, README or mermaid.live."
            : "Could not copy - " + failure;
    }

    public string Summary =>
        $"{Design.ActionCount:N0} step(s)" + (Design.Triggers.Count == 1 ? $" after the trigger" : string.Empty) +
        (Design.Warnings.Count > 0 ? $" · {Design.Warnings.Count} warning(s)" : string.Empty);

    public string Warnings => string.Join("  ", Design.Warnings);
    public bool HasWarnings => Design.Warnings.Count > 0;

    private FlowCardViewModel? _selected;
    public FlowCardViewModel? Selected
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (!SetProperty(ref _selected, value)) return;

            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedRunAfter));
            OnPropertyChanged(nameof(SelectedRunOutcome));
            Notice = string.Empty;
            RefreshSelectedRun();
        }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>The selected step's outcome, for its pill in the step pane; null outside a run.</summary>
    public FlowStepOutcome? SelectedRunOutcome => Selected is { InRun: true } card ? card.RunOutcome : null;

    /// <summary>Every run-after of the selected step, the default ones included.</summary>
    public string SelectedRunAfter => Selected?.Node switch
    {
        { RunAfter: { Count: > 0 } runAfter } => string.Join(Environment.NewLine, runAfter.Select(r =>
            $"{r.Action.Replace('_', ' ')}: {string.Join(", ", r.Statuses)}")),
        { Kind: FlowNodeKind.Trigger } => "The trigger starts the flow.",
        _ => "Runs first in its container."
    };

    private string _searchText = string.Empty;
    /// <summary>Highlights matching steps, and opens any collapsed container holding one.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty)) ApplySearch();
        }
    }

    private int _matchCount;
    public int MatchCount
    {
        get => _matchCount;
        private set
        {
            if (SetProperty(ref _matchCount, value)) OnPropertyChanged(nameof(MatchLabel));
        }
    }

    public string MatchLabel => (string.IsNullOrWhiteSpace(SearchText), MatchCount) switch
    {
        (true, _) => string.Empty,
        (_, 1) => "1 match",
        _ => $"{MatchCount:N0} matches"
    };

    private double _zoom = 1;
    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, MinZoom, MaxZoom), 2);
            if (SetProperty(ref _zoom, clamped)) OnPropertyChanged(nameof(ZoomLabel));
        }
    }

    public string ZoomLabel => $"{Zoom:P0}";

    private void ApplySearch()
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;

        foreach (var card in _cards)
        {
            var match = terms.Length > 0 &&
                        terms.All(t => card.SearchText.Contains(t, StringComparison.CurrentCultureIgnoreCase));
            card.IsMatch = match;

            if (!match) continue;

            count++;
            for (var parent = card.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        }

        MatchCount = count;
        OnPropertyChanged(nameof(MatchLabel));
    }

    private void SetExpanded(bool expanded)
    {
        foreach (var card in _cards.Where(c => c.IsContainer)) card.IsExpanded = expanded;
    }

    // ---------------------------------------------------------------- building

    private List<FlowStepViewModel> Build(FlowSequence sequence, FlowCardViewModel? parent, bool firstIsFirst)
    {
        var steps = new List<FlowStepViewModel>();

        foreach (var step in sequence.Steps)
        {
            var isFirst = steps.Count == 0 && firstIsFirst;

            steps.Add(step switch
            {
                FlowActionStep a => Card(a.Node, parent, isFirst),
                FlowParallelStep p => new FlowParallelViewModel
                {
                    IsFirst = isFirst,
                    Branches = p.Branches.Select(b => new FlowBranchViewModel(string.Empty, Build(b, parent, firstIsFirst: true))).ToList()
                },
                _ => throw new InvalidOperationException($"Unknown flow step {step.GetType().Name}.")
            });
        }

        return steps;
    }

    private FlowCardViewModel Card(FlowNode node, FlowCardViewModel? parent, bool isFirst)
    {
        var card = new FlowCardViewModel(node, parent) { IsFirst = isFirst };
        _cards.Add(card);

        if (node.IsContainer)
        {
            card.Branches = node.Branches
                .Select(b => new FlowBranchViewModel(b.Label, Build(b.Steps, card, firstIsFirst: true)))
                .ToList();

            card.InnerCount = _cards.Count(c => IsInside(c, card));
        }

        return card;
    }

    private static bool IsInside(FlowCardViewModel card, FlowCardViewModel container)
    {
        for (var parent = card.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, container)) return true;
        }

        return false;
    }
}
