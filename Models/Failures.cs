namespace PPObjectSearch.Models;

public enum FailureSource
{
    CloudFlow,
    ClassicWorkflow,
    Plugin
}

/// <summary>One failure: a flow run, a system job or a plug-in trace entry that ended in error.</summary>
public sealed class FailureEvent
{
    public required FailureSource Source { get; init; }

    /// <summary>What failed, as one key per component: the workflow id, or the plug-in type name.</summary>
    public required string ComponentKey { get; init; }
    public required string ComponentName { get; init; }

    public required DateTimeOffset When { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>The flow run's name, the system job's name, or the trace entry's id.</summary>
    public string? RunName { get; init; }

    /// <summary>The record it ran against, where known.</summary>
    public string? Regarding { get; init; }

    /// <summary>A plug-in failure's message and table, e.g. "Update of account".</summary>
    public string? Context { get; init; }

    /// <summary>The workflow (flow or classic) id, for opening its details.</summary>
    public Guid? WorkflowId { get; init; }

    /// <summary>The plug-in step that failed, where the trace says.</summary>
    public Guid? StepId { get; init; }

    public string SourceLabel => Source switch
    {
        FailureSource.CloudFlow => "Cloud flow",
        FailureSource.ClassicWorkflow => "Classic workflow",
        _ => "Plug-in"
    };

    public string ErrorLine => string.IsNullOrWhiteSpace(ErrorMessage)
        ? ErrorCode ?? "(no error text)"
        : ErrorMessage!.Split('\n', 2)[0].Trim();

    /// <summary>
    /// Where it failed, as far as the source says: a plug-in's message and table, a flow run's error
    /// code. A run's failing action is not in the run record, so a flow stops at its code.
    /// </summary>
    public string StepLabel => Source switch
    {
        FailureSource.Plugin => string.IsNullOrWhiteSpace(Context) ? "Plug-in" : Context!,
        FailureSource.CloudFlow => string.IsNullOrWhiteSpace(ErrorCode) ? "Flow run" : ErrorCode!,
        _ => "System job"
    };
}

/// <summary>Everything read for one period: the failures, and how many runs there were where that is known.</summary>
public sealed class FailureData
{
    public List<FailureEvent> Events { get; } = new();

    /// <summary>Total cloud flow runs in the period, by workflow id key - for a failure rate.</summary>
    public Dictionary<string, int> RunCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sources that could not be read, or were cut short, said rather than left empty.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>How much was read for this: flow runs of every outcome, failed system jobs, traced exceptions.</summary>
    public int FlowRunsRead { get; set; }
    public int SystemJobsRead { get; set; }
    public int TraceLogsRead { get; set; }
}

/// <summary>What to read failures for. Null sets mean the whole environment.</summary>
public sealed class FailureQuery
{
    public bool CloudFlows { get; init; } = true;
    public bool ClassicWorkflows { get; init; } = true;
    public bool Plugins { get; init; } = true;

    /// <summary>Only these workflows (flows and classic) - a solution's - when set.</summary>
    public IReadOnlySet<Guid>? WorkflowIds { get; init; }

    /// <summary>Only these plug-in types (by type name) or steps (by id), when either is set.</summary>
    public IReadOnlySet<string>? PluginTypeNames { get; init; }
    public IReadOnlySet<Guid>? PluginStepIds { get; init; }
}
