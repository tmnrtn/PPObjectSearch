namespace PPObjectSearch.Models;

/// <summary>The shape a step takes in the designer.</summary>
public enum FlowNodeKind
{
    Trigger,
    Action,
    Condition,
    Switch,
    ForEach,
    Until,
    Scope
}

/// <summary>What sort of work a step does, for its colour and icon.</summary>
public enum FlowNodeCategory
{
    Trigger,
    Control,
    Connector,
    Data,
    Variable,
    Http,
    ChildFlow,
    Other
}

/// <summary>
/// One entry of an action's runAfter: the step it follows, and on which outcomes. Anything other
/// than plain "Succeeded" is how error handling is built, so it is worth showing.
/// </summary>
public sealed record FlowRunAfter(string Action, IReadOnlyList<string> Statuses)
{
    public bool IsDefault => Statuses.Count == 1 && string.Equals(Statuses[0], "Succeeded", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A trigger or an action.</summary>
public sealed class FlowNode
{
    /// <summary>The name in the definition, unique within the flow - "Get_a_row_by_ID".</summary>
    public required string Name { get; init; }

    /// <summary>The name as the designer shows it - "Get a row by ID".</summary>
    public string DisplayName => Name.Replace('_', ' ');

    /// <summary>The raw type - "OpenApiConnection", "If", "Compose".</summary>
    public required string Type { get; init; }

    public required FlowNodeKind Kind { get; init; }
    public required FlowNodeCategory Category { get; init; }

    /// <summary>"Compose", "Condition", "Initialize variable" - or the connector operation's name.</summary>
    public required string TypeLabel { get; init; }

    /// <summary>"Microsoft Dataverse", for connector steps.</summary>
    public string? Connector { get; init; }

    /// <summary>"List rows", for connector steps.</summary>
    public string? Operation { get; init; }

    /// <summary>A short particular - a variable's name, a loop's input, a recurrence.</summary>
    public string? Detail { get; init; }

    /// <summary>For "Run a child flow": the child flow's workflowid in Dataverse.</summary>
    public Guid? ChildFlowId { get; init; }

    /// <summary>The note added in the designer, if any.</summary>
    public string? Description { get; init; }

    public IReadOnlyList<FlowRunAfter> RunAfter { get; init; } = Array.Empty<FlowRunAfter>();

    /// <summary>For a condition, switch, loop or scope: the actions it holds, branch by branch.</summary>
    public IReadOnlyList<FlowBranch> Branches { get; init; } = Array.Empty<FlowBranch>();

    /// <summary>The step's own JSON, without the actions it contains - those are steps of their own.</summary>
    public required string Json { get; init; }

    public bool IsContainer => Kind is FlowNodeKind.Condition or FlowNodeKind.Switch or FlowNodeKind.ForEach
        or FlowNodeKind.Until or FlowNodeKind.Scope;

    /// <summary>"Microsoft Dataverse · List rows", or the type label for a built-in step.</summary>
    public string Summary => Connector is not null
        ? Operation is not null ? $"{Connector} · {Operation}" : Connector
        : TypeLabel;
}

/// <summary>A labelled set of steps inside a container: "Yes", "No", "Case: 3", "Default".</summary>
public sealed record FlowBranch(string Label, FlowSequence Steps);

/// <summary>Steps in the order they run.</summary>
public sealed record FlowSequence(IReadOnlyList<FlowStep> Steps)
{
    public static readonly FlowSequence Empty = new(Array.Empty<FlowStep>());
}

public abstract record FlowStep;

/// <summary>A single trigger or action.</summary>
public sealed record FlowActionStep(FlowNode Node) : FlowStep;

/// <summary>Branches that run side by side, after the same step.</summary>
public sealed record FlowParallelStep(IReadOnlyList<FlowSequence> Branches) : FlowStep;

/// <summary>A cloud flow's design, read from its definition.</summary>
public sealed record FlowDesign(
    IReadOnlyList<FlowNode> Triggers,
    FlowSequence Actions,
    int ActionCount,
    IReadOnlyList<string> Warnings);
