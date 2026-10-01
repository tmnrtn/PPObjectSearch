using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>
/// A cloud flow's run history from both sources. Dataverse's flowrun table is fast and needs no
/// ownership of the flow, but it records a run only once it is done, and not at once - so a run in
/// progress, or one cancelled a moment ago, is missing or stale there. Power Automate's own list is
/// live. The two are merged on the run id.
/// </summary>
public static class RunHistoryMerge
{
    public sealed record Result(IReadOnlyList<ProcessRun> Runs, int LiveOnly, int Updated);

    /// <summary>
    /// Every run from either source, newest first. Where both have a run, Power Automate's status,
    /// times and error win - they are current - and Dataverse fills anything it lacks. A run only
    /// Power Automate has is marked as such. A Dataverse run older than Power Automate's list
    /// reaches is kept as it was.
    /// </summary>
    public static Result Merge(IReadOnlyList<ProcessRun> dataverse, IReadOnlyList<ProcessRun> live)
    {
        var stored = dataverse
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<ProcessRun>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int liveOnly = 0, updated = 0;

        foreach (var run in live)
        {
            if (!seen.Add(run.Name)) continue;

            if (!stored.TryGetValue(run.Name, out var known))
            {
                merged.Add(run);
                liveOnly++;
                continue;
            }

            if (!string.Equals(known.Status, run.Status, StringComparison.OrdinalIgnoreCase)) updated++;

            merged.Add(new ProcessRun
            {
                Name = known.Name,
                Status = run.Status,
                Outcome = run.Outcome,
                StartTime = run.StartTime ?? known.StartTime,
                EndTime = run.EndTime ?? known.EndTime,
                DurationMs = run.DurationMs ?? known.DurationMs,
                TriggerType = known.TriggerType ?? run.TriggerType,
                ErrorCode = run.ErrorCode ?? known.ErrorCode,
                ErrorMessage = run.ErrorMessage ?? known.ErrorMessage,
                Regarding = known.Regarding,
                FlowId = known.FlowId ?? run.FlowId,
                IsLiveOnly = false
            });
        }

        merged.AddRange(dataverse.Where(r => seen.Add(r.Name)));

        return new Result(
            merged.OrderByDescending(r => r.StartTime ?? DateTimeOffset.MaxValue).ToList(),
            liveOnly,
            updated);
    }
}
