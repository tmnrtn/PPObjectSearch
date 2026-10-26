using System.Text.Json;

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

    /// <summary>The step's name as the designer shows it: Power Automate stores spaces as underscores.</summary>
    public string Label => Name.Replace('_', ' ');

    /// <summary>The step's own error as one line: its message, else its code.</summary>
    public string ErrorLine => ErrorText.FirstLine(ErrorMessage) ?? ErrorCode ?? "No error text";
}

/// <summary>
/// Why a step failed, read from its outputs where its error says nothing more than a code: an HTTP
/// or connector step's response - its status and whatever message its body carries.
/// </summary>
public static class FlowStepError
{
    /// <summary>How deep into a response body a message is looked for.</summary>
    private const int MaxDepth = 4;

    private static readonly string[] MessageKeys = ["message", "Message", "error_description", "errorMessage", "value"];

    /// <summary>"HTTP 404: File Not Found." - or null when the outputs are not a response.</summary>
    public static string? FromOutputs(string? outputs)
    {
        if (string.IsNullOrWhiteSpace(outputs)) return null;

        try
        {
            using var doc = JsonDocument.Parse(outputs);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            int? status = root.TryGetProperty("statusCode", out var code) && code.TryGetInt32(out var n) ? n : null;
            var message = root.TryGetProperty("body", out var body) ? Message(body, 0) : null;

            return (status, message) switch
            {
                ({ } s, { } m) => $"HTTP {s}: {m}",
                ({ } s, null) => $"HTTP {s}",
                (null, { } m) => m,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first message in a body: a string body itself, else a message key, nearest the top first.</summary>
    private static string? Message(JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return depth == 0 ? ErrorText.FirstLine(element.GetString()) : null;

            case JsonValueKind.Object when depth < MaxDepth:
                foreach (var key in MessageKeys)
                {
                    if (!element.TryGetProperty(key, out var value)) continue;
                    if (value.ValueKind == JsonValueKind.String && ErrorText.FirstLine(value.GetString()) is { } text) return text;
                    // SharePoint's "message": {"lang": "en-US", "value": "File Not Found."}
                    if (value.ValueKind == JsonValueKind.Object && Message(value, depth + 1) is { } inner) return inner;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object && Message(property.Value, depth + 1) is { } nested)
                        return nested;
                }

                return null;

            default:
                return null;
        }
    }
}

/// <summary>How run times read: "340 ms", "1.2 s", "3m 05s", "2h 10m".</summary>
public static class FlowRunFormat
{
    public static string Duration(TimeSpan d) => d switch
    {
        { TotalSeconds: < 1 } => $"{d.TotalMilliseconds:N0} ms",
        { TotalMinutes: < 1 } => $"{d.TotalSeconds:0.#} s",
        { TotalHours: < 1 } => $"{(int)d.TotalMinutes}m {d.Seconds:00}s",
        _ => $"{(int)d.TotalHours}h {d.Minutes:00}m"
    };
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
    /// <summary>What a scope, loop or condition reports when a step inside it failed.</summary>
    private const string ChildFailedCode = "ActionFailed";

    public TimeSpan? Duration => StartTime is { } start && EndTime is { } end && end >= start ? end - start : null;

    /// <summary>
    /// The steps that failed of themselves, earliest first. A run's own error is mostly just "An
    /// action failed", and so is that of every scope, loop and condition around the failed step;
    /// those are left out while any step says more.
    /// </summary>
    public IReadOnlyList<FlowActionResult> FailedSteps
    {
        get
        {
            var failed = Actions.Values
                .Where(a => a.Outcome is FlowStepOutcome.Failed or FlowStepOutcome.TimedOut)
                .OrderBy(a => a.StartTime ?? DateTimeOffset.MaxValue)
                .ToList();
            var own = failed.Where(a => !string.Equals(a.ErrorCode, ChildFailedCode, StringComparison.OrdinalIgnoreCase)).ToList();
            return own.Count > 0 ? own : failed;
        }
    }
}
