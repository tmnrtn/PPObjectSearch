using PPObjectSearch.Core;
using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

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

/// <summary>A trigger or action, drawn as a card - with its branches below it if it is a container.</summary>
public sealed class FlowCardViewModel : FlowStepViewModel
{
    public FlowCardViewModel(FlowNode node, FlowCardViewModel? parent)
    {
        Node = node;
        Parent = parent;

        RunAfterBadges = node.RunAfter
            .Where(r => !r.IsDefault)
            .Select(r => $"Runs if {r.Action.Replace('_', ' ')} {string.Join(" or ", r.Statuses.Select(Outcome))}")
            .ToList();
    }

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
    public string? Detail => Node.Detail;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Node.Detail);
    public string? Description => Node.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Node.Description);
    public FlowNodeCategory Category => Node.Category;
    public FlowNodeKind Kind => Node.Kind;

    /// <summary>"Runs if Try failed or timedout" - the error-handling paths, which are easy to miss.</summary>
    public IReadOnlyList<string> RunAfterBadges { get; }
    public bool HasRunAfterBadges => RunAfterBadges.Count > 0;

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

    internal string SearchText =>
        $"{Node.DisplayName} {Node.Name} {Node.TypeLabel} {Node.Connector} {Node.Operation} {Node.Detail} {Node.Description}";
}

/// <summary>
/// A cloud flow drawn as the designer draws it: trigger, then steps top to bottom, branches side
/// by side. Read-only - selecting a step shows its JSON, nothing here can change the flow.
/// </summary>
public sealed class FlowDiagramViewModel : ObservableObject
{
    public const double MinZoom = 0.4;
    public const double MaxZoom = 1.5;

    private readonly List<FlowCardViewModel> _cards = new();

    public FlowDiagramViewModel(FlowDesign design)
    {
        Design = design;

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
        }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>Every run-after of the selected step, the default ones included.</summary>
    public string SelectedRunAfter => Selected?.Node.RunAfter is { Count: > 0 } runAfter
        ? string.Join(Environment.NewLine, runAfter.Select(r =>
            $"{r.Action.Replace('_', ' ')}: {string.Join(", ", r.Statuses)}"))
        : Selected?.Node.Kind == FlowNodeKind.Trigger ? "The trigger starts the flow." : "Runs first in its container.";

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

    public string MatchLabel => string.IsNullOrWhiteSpace(SearchText)
        ? string.Empty
        : MatchCount == 1 ? "1 match" : $"{MatchCount:N0} matches";

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

    private IReadOnlyList<FlowStepViewModel> Build(FlowSequence sequence, FlowCardViewModel? parent, bool firstIsFirst)
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
