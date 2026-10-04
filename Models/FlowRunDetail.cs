namespace PPObjectSearch.Models;

/// <summary>What became of one step in one run.</summary>
public enum FlowStepOutcome
{
    Succeeded,
    Failed,
    Skipped,
    TimedOut,
    Cancelled,
    Running,
    Waiting,

    /// <summary>A status this app does not recognise - shown as it came.</summary>
    Other,

    /// <summary>The run has no record of the step: it was never reached.</summary>
    NotRun
}

/// <summary>A step's result in a run, as the Power Automate API reports it.</summary>
public sealed record FlowActionResult(
    string Name,
    string Status,
    FlowStepOutcome Outcome,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    string? ErrorCode,
    string? ErrorMessage,
    string? InputsLink,
    string? OutputsLink)
{
    public TimeSpan? Duration => StartTime is { } start && EndTime is { } end && end >= start ? end - start : null;
}

/// <summary>How run times read: "340 ms", "1.2 s", "3m 05s", "2h 10m".</summary>
public static class FlowRunFormat
{
    public static string Duration(TimeSpan d) =>
        d.TotalSeconds < 1 ? $"{d.TotalMilliseconds:N0} ms"
        : d.TotalMinutes < 1 ? $"{d.TotalSeconds:0.#} s"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m {d.Seconds:00}s"
        : $"{(int)d.TotalHours}h {d.Minutes:00}m";
}

/// <summary>One iteration of a step inside a loop.</summary>
/// <summary>
/// A step's iterations, as many as were read. A loop over a large array can have more than the
/// paging limit allows; then <see cref="IsTruncated"/> says the list (and any count of it) is a
/// lower bound, not the whole.
/// </summary>
public sealed class FlowRepetitions : List<FlowRepetition>
{
    public bool IsTruncated { get; set; }
}

public sealed record FlowRepetition(
    string Label,
    string Status,
    FlowStepOutcome Outcome,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    string? ErrorCode,
    string? ErrorMessage,
    string? InputsLink,
    string? OutputsLink)
{
    public TimeSpan? Duration => StartTime is { } start && EndTime is { } end && end >= start ? end - start : null;

    /// <summary>"#3 · Failed · 1.2 s" - a line in the iterations list.</summary>
    public string Summary => Duration is { } d ? $"{Label} · {Status} · {FlowRunFormat.Duration(d)}" : $"{Label} · {Status}";
}

/// <summary>A run of a cloud flow, step by step.</summary>
public sealed record FlowRunDetail(
    string RunName,
    string Status,
    FlowStepOutcome Outcome,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    FlowActionResult? Trigger,
    IReadOnlyDictionary<string, FlowActionResult> Actions,
    string? ErrorCode,
    string? ErrorMessage,
    string RunUrl,
    bool ViaAdminScope)
{
    public TimeSpan? Duration => StartTime is { } start && EndTime is { } end && end >= start ? end - start : null;
}
