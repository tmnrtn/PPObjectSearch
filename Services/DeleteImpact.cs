using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>Rows of one table that a run's deletes reach through one relationship.</summary>
public sealed record DeleteImpactLine(string Table, string Via, string Behaviour, int Count)
{
    /// <summary>Dataverse stops counting here, so the count is a floor.</summary>
    public const int CountCap = 5000;

    public string CountLabel => Count switch
    {
        < 0 => "?",
        >= CountCap => $"{CountCap:N0}+",
        _ => Count.ToString("N0")
    };

    /// <summary>Deleted along with the rows: the gravest effect, in Red.</summary>
    public bool IsCascade => Behaviour == "Cascade" && Count > 0;

    /// <summary>Still pointing at the rows, so those deletes fail: in Amber.</summary>
    public bool IsBlocking => Behaviour == "Restrict" && Count > 0;

    public bool IsUncounted => Count < 0;

    /// <summary>The line's words after its count: the table, the relationship and what happens.</summary>
    public string Description => (Behaviour, Count) switch
    {
        (_, < 0) => $"{Table} · via {Via} - not counted, the table cannot be read here",
        ("Cascade", _) => $"{Table} · via {Via} - deleted with them",
        ("Restrict", _) => $"{Table} · via {Via} - still point at them, so those deletes fail",
        ("RemoveLink", _) => $"{Table} · via {Via} - lose their reference to them",
        _ => $"{Table} · via {Via}"
    };
}

/// <summary>
/// What deleting rows would do beyond those rows: children Dataverse deletes with them, children
/// that would block the delete, and children that would lose their reference. Counted before the
/// delete is acknowledged, so the acknowledgement is of the whole effect and not only the rows listed.
/// </summary>
public static class DeleteImpact
{
    public static async Task<IReadOnlyList<DeleteImpactLine>> CheckAsync(
        DataverseClient client,
        IReadOnlyDictionary<string, EntitySummary> entities,
        IEnumerable<(string Table, Guid Id)> deletes,
        CancellationToken ct = default)
    {
        var lines = new List<DeleteImpactLine>();

        foreach (var group in deletes.GroupBy(d => d.Table, StringComparer.OrdinalIgnoreCase))
        {
            var ids = group.Select(d => d.Id).Distinct().ToList();
            var behaviours = await client.GetDeleteBehavioursAsync(group.Key, ct).ConfigureAwait(false);

            foreach (var behaviour in behaviours)
            {
                if (!entities.TryGetValue(behaviour.ReferencingEntity, out var referencing))
                {
                    // A table the session cannot read rows from - a virtual or internal one - is
                    // not guessed at; it is named so the gap is visible.
                    lines.Add(new DeleteImpactLine(behaviour.ReferencingEntity, behaviour.ReferencingAttribute,
                        behaviour.Behaviour, -1));
                    continue;
                }

                var count = await client.CountReferencingAsync(referencing, behaviour.ReferencingAttribute, ids, ct)
                    .ConfigureAwait(false);

                if (count > 0)
                {
                    lines.Add(new DeleteImpactLine(behaviour.ReferencingEntity, behaviour.ReferencingAttribute,
                        behaviour.Behaviour, count));
                }
            }
        }

        return lines;
    }

    /// <summary>The lines worth a row each - counted ones, cascades first, then blocks - and the uncounted.</summary>
    public static IReadOnlyList<DeleteImpactLine> Shown(IReadOnlyList<DeleteImpactLine> lines) =>
        lines.Where(l => l.Count != 0)
            .OrderBy(Rank)
            .ThenByDescending(l => l.Count)
            .ToList();

    private static int Rank(DeleteImpactLine line) => (line.IsCascade, line.IsBlocking, line.IsUncounted) switch
    {
        (true, _, _) => 0,
        (_, true, _) => 1,
        (_, _, true) => 3,
        _ => 2
    };

    /// <summary>One sentence per kind of effect, or a plain "nothing else" when there is none.</summary>
    public static string Describe(IReadOnlyList<DeleteImpactLine> lines)
    {
        static string List(IEnumerable<DeleteImpactLine> of) =>
            string.Join(", ", of.Select(l => $"{l.CountLabel} {l.Table} (via {l.Via})"));

        var counted = lines.Where(l => l.Count > 0).ToList();
        var parts = new List<string>();

        var cascade = counted.Where(l => l.Behaviour == "Cascade").ToList();
        if (cascade.Count > 0) parts.Add($"Dataverse will also delete {List(cascade)}.");

        var restrict = counted.Where(l => l.Behaviour == "Restrict").ToList();
        if (restrict.Count > 0) parts.Add($"{List(restrict)} still point at them and will make those deletes fail.");

        var unlink = counted.Where(l => l.Behaviour == "RemoveLink").ToList();
        if (unlink.Count > 0) parts.Add($"{List(unlink)} will lose their reference to them.");

        var unknown = lines.Where(l => l.Count < 0).Select(l => l.Table).Distinct().ToList();
        if (unknown.Count > 0) parts.Add($"Not counted (table not readable here): {string.Join(", ", unknown)}.");

        return parts.Count == 0
            ? "No other rows point at these through a cascading, restricting or unlinking relationship."
            : string.Join(" ", parts);
    }
}
