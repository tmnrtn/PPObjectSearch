namespace PPObjectSearch.Models;

/// <summary>How a run or a trace entry ended, for its status pill.</summary>
public enum RunOutcome
{
    Succeeded,
    Failed,
    Running,
    Cancelled,
    Other
}

/// <summary>
/// One execution of a process: a cloud flow run from the flowrun table, or a classic workflow's
/// system job from asyncoperation. Both end up as the same row so the tab reads the same.
/// </summary>
public sealed class ProcessRun
{
    /// <summary>The run id for a cloud flow; the system job name for a classic workflow.</summary>
    public required string Name { get; init; }

    public required string Status { get; init; }
    public RunOutcome Outcome { get; init; }
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public long? DurationMs { get; init; }
    public string? TriggerType { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>The record a classic workflow ran against, where the system job names one.</summary>
    public string? Regarding { get; init; }

    /// <summary>The id Power Automate knows the flow by, as the run records it (flowrun.workflowid).</summary>
    public string? FlowId { get; init; }

    /// <summary>
    /// Known only to Power Automate so far - in progress, or finished too recently for Dataverse's
    /// copy. Dataverse records a run once it is done, and not at once.
    /// </summary>
    public bool IsLiveOnly { get; init; }

    public string? LiveNote => IsLiveOnly ? "Not in Dataverse yet - read live from Power Automate." : null;

    /// <summary>This run's detail page in Power Automate; null where it cannot be built.</summary>
    public string? PortalUrl { get; set; }

    public bool HasPortalUrl => PortalUrl is not null;

    public string DurationLabel => FormatDuration(DurationMs);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage) || !string.IsNullOrWhiteSpace(ErrorCode);

    internal static string FormatDuration(long? ms) => ms switch
    {
        null => string.Empty,
        < 1000 => $"{ms} ms",
        < 60_000 => $"{ms / 1000.0:0.0} s",
        _ => $"{TimeSpan.FromMilliseconds(ms.Value):h\\:mm\\:ss}"
    };
}

/// <summary>One plug-in or custom workflow activity execution from the plug-in trace log.</summary>
public sealed class PluginTraceEntry
{
    public required Guid Id { get; init; }
    public DateTimeOffset? CreatedOn { get; init; }
    public required string TypeName { get; init; }
    public string? MessageName { get; init; }
    public string? PrimaryEntity { get; init; }

    /// <summary>"Synchronous" or "Asynchronous".</summary>
    public string? Mode { get; init; }

    public int? Depth { get; init; }
    public long? DurationMs { get; init; }
    public string? MessageBlock { get; init; }
    public string? ExceptionDetails { get; init; }
    public Guid? CorrelationId { get; init; }

    public bool HasException => !string.IsNullOrWhiteSpace(ExceptionDetails);

    public RunOutcome Outcome => HasException ? RunOutcome.Failed : RunOutcome.Succeeded;

    public string OutcomeLabel => HasException ? "Exception" : "Completed";

    public string DurationLabel => ProcessRun.FormatDuration(DurationMs);

    /// <summary>The class name without its namespace, which is what fits in a column.</summary>
    public string ShortTypeName
    {
        get
        {
            var dot = TypeName.LastIndexOf('.');
            return dot >= 0 && dot < TypeName.Length - 1 ? TypeName[(dot + 1)..] : TypeName;
        }
    }
}

/// <summary>The organization's plug-in trace setting, which decides whether there is anything to read.</summary>
public enum PluginTraceSetting
{
    Off = 0,
    Exception = 1,
    All = 2
}

/// <summary>An environment variable as the connected environment resolves it.</summary>
public sealed class EnvironmentVariableInfo
{
    public required string SchemaName { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public required string TypeLabel { get; init; }

    /// <summary>A secret holds an Azure Key Vault reference, not the secret itself.</summary>
    public bool IsSecret { get; init; }

    public string? DefaultValue { get; init; }

    /// <summary>The value record set in this environment, if any.</summary>
    public string? CurrentValue { get; init; }

    public bool HasCurrentValue { get; init; }

    /// <summary>More than one value record is a broken state worth pointing out.</summary>
    public int ValueRecordCount { get; init; }

    /// <summary>What a flow or app actually gets: the current value, else the default.</summary>
    public string? EffectiveValue => HasCurrentValue ? CurrentValue : DefaultValue;

    public string EffectiveSource => HasCurrentValue
        ? "The current value set in this environment is in effect."
        : DefaultValue is null
            ? "Neither a current value nor a default is set, so the variable has no value here."
            : "No current value is set in this environment, so the default is in effect.";
}

/// <summary>One solution operation recorded in msdyn_solutionhistory - an import, upgrade, export and so on.</summary>
public sealed class SolutionHistoryEntry
{
    public required Guid Id { get; init; }
    public required string SolutionName { get; init; }
    public string? Version { get; init; }

    /// <summary>msdyn_operation: 0 import, 1 uninstall, 2 export, 3 publish, ...</summary>
    public int? OperationCode { get; init; }
    public required string Operation { get; init; }
    public string? SubOperation { get; init; }

    public bool? IsManaged { get; init; }
    public bool? IsPatch { get; init; }
    public bool? OverwriteCustomizations { get; init; }
    public string? PublisherName { get; init; }
    public string? PackageName { get; init; }
    public string? PackageVersion { get; init; }

    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public int? TotalSeconds { get; init; }

    /// <summary>msdyn_status: 0 started, 1 completed, 2 queued.</summary>
    public int? StatusCode { get; init; }

    /// <summary>msdyn_result: true success, false failure; only meaningful once completed.</summary>
    public bool? Succeeded { get; init; }

    public string? ErrorCode { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? ExceptionStack { get; init; }
    public int? RetryCount { get; init; }
    public string? ActivityId { get; init; }
    public string? CorrelationId { get; init; }

    public bool IsFinished => StatusCode == 1;

    public RunOutcome Outcome =>
        !IsFinished ? RunOutcome.Running
        : Succeeded == true ? RunOutcome.Succeeded
        : RunOutcome.Failed;

    public string ResultLabel => StatusCode switch
    {
        0 => "In progress",
        2 => "Queued",
        _ => Succeeded == true ? "Succeeded" : "Failed"
    };

    /// <summary>"Import · Upgrade" - the sub operation only where it adds something.</summary>
    public string OperationLabel =>
        string.IsNullOrWhiteSpace(SubOperation) || string.Equals(SubOperation, "None", StringComparison.OrdinalIgnoreCase)
            ? Operation
            : $"{Operation} · {SubOperation}";

    public string ManagedLabel => IsManaged switch
    {
        true => "Managed",
        false => "Unmanaged",
        _ => string.Empty
    };

    public string DurationLabel => TotalSeconds is { } s
        ? ProcessRun.FormatDuration(s * 1000L)
        : StartTime is { } start && EndTime is { } end ? ProcessRun.FormatDuration((long)(end - start).TotalMilliseconds) : string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ExceptionMessage) || !string.IsNullOrWhiteSpace(ErrorCode);

    /// <summary>What the search box matches against.</summary>
    public string SearchText => string.Join(" ", SolutionName, Version, PublisherName, PackageName, Operation, SubOperation, ErrorCode)
        .ToLowerInvariant();
}
