using System.Text;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>One line of the timeline: a component changed, or a solution operation.</summary>
public sealed class ChangeEntry
{
    public required DateTimeOffset When { get; init; }
    public required string Kind { get; init; }
    public required string What { get; init; }
    public required string Type { get; init; }
    public string? By { get; init; }
    public string? Detail { get; init; }
    public bool IsManaged { get; init; }

    /// <summary>The component, for opening its details; null for a solution operation.</summary>
    public SolutionComponentItem? Item { get; init; }

    public bool IsSolutionOperation => Item is null;
    public string ManagedLabel => IsSolutionOperation ? string.Empty : IsManaged ? "Managed" : "Unmanaged";
}

/// <summary>What changed in an environment and when: component edits and solution operations on one timeline.</summary>
public static class ChangeTimeline
{
    public static IReadOnlyList<ChangeEntry> Build(
        IEnumerable<SolutionComponentItem> changed,
        IReadOnlyDictionary<Guid, string> modifiedBy,
        IEnumerable<SolutionHistoryEntry> history,
        DateTimeOffset since)
    {
        var entries = new List<ChangeEntry>();

        foreach (var item in changed)
        {
            if (item.ModifiedOn is not { } when || when < since) continue;

            entries.Add(new ChangeEntry
            {
                When = when,
                Kind = "Component",
                What = item.PrimaryLabel,
                Type = item.SubType is { Length: > 0 } sub ? $"{item.ComponentTypeName} · {sub}" : item.ComponentTypeName,
                By = modifiedBy.TryGetValue(item.ObjectId, out var by) ? by : null,
                IsManaged = item.IsManaged,
                Item = item
            });
        }

        foreach (var operation in history)
        {
            if ((operation.StartTime ?? operation.EndTime) is not { } when || when < since) continue;

            entries.Add(new ChangeEntry
            {
                When = when,
                Kind = "Solution",
                What = operation.SolutionName,
                Type = operation.OperationLabel,
                Detail = $"{operation.ResultLabel}" +
                         (operation.Version is { Length: > 0 } v ? $" · version {v}" : string.Empty) +
                         (operation.IsManaged is { } managed ? managed ? " · managed" : " · unmanaged" : string.Empty) +
                         (operation.ExceptionMessage is { Length: > 0 } error ? $" · {error}" : string.Empty)
            });
        }

        return entries.OrderByDescending(e => e.When).ToList();
    }

    public static string ToMarkdown(IEnumerable<ChangeEntry> entries, string environment, DateTimeOffset since)
    {
        static string Cell(string? text) => (text ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

        var md = new StringBuilder()
            .AppendLine($"# Changes in {environment}")
            .AppendLine()
            .AppendLine($"Since {since:yyyy-MM-dd HH:mm}, newest first.")
            .AppendLine()
            .AppendLine("| When | Kind | What | Type | By / result |")
            .AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var e in entries)
        {
            md.AppendLine($"| {e.When.ToLocalTime():yyyy-MM-dd HH:mm} | {e.Kind} | {Cell(e.What)} | {Cell(e.Type)} | {Cell(e.By ?? e.Detail)} |");
        }

        return md.ToString();
    }
}
