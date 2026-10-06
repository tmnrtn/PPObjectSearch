using System.Text;
using System.Text.RegularExpressions;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>One component's failures in the period.</summary>
public sealed class FailureSummaryRow
{
    public required FailureSource Source { get; init; }
    public required string ComponentKey { get; init; }
    public required string ComponentName { get; init; }
    public required int Failures { get; init; }

    /// <summary>All runs in the period, where known (cloud flows); null otherwise.</summary>
    public int? Runs { get; init; }

    public required DateTimeOffset First { get; init; }
    public required DateTimeOffset Last { get; init; }
    public required string TopError { get; init; }
    public required int TopErrorCount { get; init; }
    public required IReadOnlyList<FailureEvent> Events { get; init; }

    public string SourceLabel => Events[0].SourceLabel;
    public double? FailureRate => Runs is > 0 ? (double)Failures / Runs.Value : null;
    public string RateLabel => FailureRate is { } r ? $"{Math.Round(r * 100):0}%" : "—";
    public string RunsLabel => Runs?.ToString("N0") ?? "—";
}

/// <summary>Failures sharing one cause, however many components it spans.</summary>
public sealed class FailureErrorGroup
{
    public required string Error { get; init; }
    public required int Failures { get; init; }
    public required int Components { get; init; }
    public required DateTimeOffset Last { get; init; }
    public required IReadOnlyList<FailureEvent> Events { get; init; }

    public string ComponentNames => string.Join(", ", Events.Select(e => e.ComponentName).Distinct().Take(5)) +
                                    (Components > 5 ? $" and {Components - 5} more" : string.Empty);
}

/// <summary>Failures in one hour or day of the period.</summary>
public sealed record FailureBucket(DateTimeOffset Start, string Label, int Failures, double Share);

/// <summary>One bar of the summary band's trend: its size, how strongly it is drawn, and what it says.</summary>
public sealed record FailureTrendBar(double Width, double Height, double Opacity, string Short, string ToolTip);

/// <summary>Summaries over a period's failures - pure, so they can be checked without an environment.</summary>
public static partial class FailureOverview
{
    public static IReadOnlyList<FailureSummaryRow> ByComponent(FailureData data) =>
        data.Events
            .GroupBy(e => (e.Source, e.ComponentKey))
            .Select(g =>
            {
                var events = g.OrderByDescending(e => e.When).ToList();
                var top = events.GroupBy(e => Normalise(e.ErrorLine)).OrderByDescending(x => x.Count()).First();

                return new FailureSummaryRow
                {
                    Source = g.Key.Source,
                    ComponentKey = g.Key.ComponentKey,
                    ComponentName = events[0].ComponentName,
                    Failures = events.Count,
                    Runs = data.RunCounts.TryGetValue(g.Key.ComponentKey, out var runs) ? Math.Max(runs, events.Count) : null,
                    First = events[^1].When,
                    Last = events[0].When,
                    TopError = top.First().ErrorLine,
                    TopErrorCount = top.Count(),
                    Events = events
                };
            })
            .OrderByDescending(r => r.Failures)
            .ThenByDescending(r => r.Last)
            .ToList();

    public static IReadOnlyList<FailureErrorGroup> ByError(FailureData data) => ByError(data.Events);

    public static IReadOnlyList<FailureErrorGroup> ByError(IEnumerable<FailureEvent> events) =>
        events
            .GroupBy(e => Normalise(e.ErrorLine))
            .Select(g =>
            {
                var events = g.OrderByDescending(e => e.When).ToList();
                return new FailureErrorGroup
                {
                    Error = events[0].ErrorLine,
                    Failures = events.Count,
                    Components = events.Select(e => (e.Source, e.ComponentKey)).Distinct().Count(),
                    Last = events[0].When,
                    Events = events
                };
            })
            .OrderByDescending(g => g.Failures)
            .ToList();

    /// <summary>Failures per hour for a period up to two days long, per day beyond that.</summary>
    public static IReadOnlyList<FailureBucket> Trend(FailureData data, DateTimeOffset from, DateTimeOffset to)
    {
        var hourly = to - from <= TimeSpan.FromDays(2);
        var step = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);

        DateTimeOffset Floor(DateTimeOffset t)
        {
            var local = t.ToLocalTime();
            return hourly
                ? new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset)
                : new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        }

        var counts = data.Events.GroupBy(e => Floor(e.When)).ToDictionary(g => g.Key, g => g.Count());
        var buckets = new List<(DateTimeOffset Start, int Count)>();

        for (var t = Floor(from); t <= to; t += step) buckets.Add((t, counts.GetValueOrDefault(t)));

        var max = Math.Max(1, buckets.Count == 0 ? 1 : buckets.Max(b => b.Count));
        return buckets
            .Select(b => new FailureBucket(b.Start, hourly ? b.Start.ToString("ddd HH:00") : b.Start.ToString("ddd d MMM"),
                b.Count, (double)b.Count / max))
            .ToList();
    }

    /// <summary>Tallest bar in the summary band, in pixels.</summary>
    public const double TrendHeight = 30;

    /// <summary>
    /// The trend as bars: 18px wide for a week or less of days, narrower as there are more, the
    /// tallest <see cref="TrendHeight"/>. An empty bucket is a faint 2px stub, so the days still read.
    /// </summary>
    public static IReadOnlyList<FailureTrendBar> Bars(IReadOnlyList<FailureBucket> buckets)
    {
        if (buckets.Count == 0) return [];

        var hourly = buckets.Count > 1 && buckets[1].Start - buckets[0].Start < TimeSpan.FromDays(1);
        var width = buckets.Count <= 10 ? 18 : Math.Max(2, Math.Floor(190.0 / buckets.Count) - 3);
        var max = Math.Max(1, buckets.Max(b => b.Failures));

        return buckets
            .Select(b => new FailureTrendBar(
                width,
                Math.Max(2, Math.Round(b.Failures * TrendHeight / max)),
                b.Failures == 0 ? 0.25 : 1,
                hourly
                    ? b.Start.Hour % 6 == 0 ? b.Start.ToString("HH") : string.Empty
                    : buckets.Count <= 10 ? b.Start.ToString("ddd")[..1] : string.Empty,
                $"{b.Label}: {b.Failures:N0} failure{(b.Failures == 1 ? string.Empty : "s")}"))
            .ToList();
    }

    /// <summary>The same read with only some sources' failures - what the source pills show.</summary>
    public static FailureData Only(FailureData data, IReadOnlySet<FailureSource> sources)
    {
        var only = new FailureData
        {
            FlowRunsRead = data.FlowRunsRead,
            SystemJobsRead = data.SystemJobsRead,
            TraceLogsRead = data.TraceLogsRead
        };

        only.Events.AddRange(data.Events.Where(e => sources.Contains(e.Source)));
        only.Notes.AddRange(data.Notes);
        foreach (var (key, count) in data.RunCounts) only.RunCounts[key] = count;
        return only;
    }

    /// <summary>
    /// An error message with what varies between occurrences taken out - ids, numbers, quoted
    /// values - so one cause failing on many records reads as one error.
    /// </summary>
    public static string Normalise(string message)
    {
        var text = GuidPattern().Replace(message, "{id}");
        text = QuotedPattern().Replace(text, "'…'");
        text = NumberPattern().Replace(text, "#");
        return WhitespacePattern().Replace(text, " ").Trim().ToLowerInvariant();
    }

    public static string ToMarkdown(FailureData data, string environment, DateTimeOffset from, DateTimeOffset to)
    {
        static string Cell(string? text) => (text ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

        var rows = ByComponent(data);
        var md = new StringBuilder()
            .AppendLine($"# Failures in {environment}")
            .AppendLine()
            .AppendLine($"{from.ToLocalTime():yyyy-MM-dd HH:mm} to {to.ToLocalTime():yyyy-MM-dd HH:mm}: " +
                        $"{data.Events.Count:N0} failure(s) across {rows.Count:N0} component(s).")
            .AppendLine()
            .AppendLine("| Component | Kind | Failures | Runs | Rate | Last | Most common error |")
            .AppendLine("| --- | --- | ---: | ---: | ---: | --- | --- |");

        foreach (var r in rows)
        {
            md.AppendLine($"| {Cell(r.ComponentName)} | {r.SourceLabel} | {r.Failures:N0} | {r.RunsLabel} | {r.RateLabel} | " +
                          $"{r.Last.ToLocalTime():yyyy-MM-dd HH:mm} | {Cell(r.TopError)} |");
        }

        md.AppendLine().AppendLine("## By error").AppendLine()
          .AppendLine("| Error | Failures | Components |").AppendLine("| --- | ---: | --- |");
        foreach (var g in ByError(data)) md.AppendLine($"| {Cell(g.Error)} | {g.Failures:N0} | {Cell(g.ComponentNames)} |");

        if (data.Notes.Count > 0)
        {
            md.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var note in data.Notes) md.AppendLine($"- {note}");
        }

        return md.ToString();
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex("'[^']*'|\"[^\"]*\"")]
    private static partial Regex QuotedPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
