using System.Text;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>A component in a dependency graph.</summary>
public sealed class DependencyNode
{
    public required Guid ObjectId { get; init; }
    public required int ComponentType { get; init; }
    public required string TypeName { get; init; }
    public required string Name { get; init; }

    /// <summary>Steps from the component the walk started at.</summary>
    public required int Level { get; init; }

    /// <summary>The loaded list's row for it - present when it is in the solution being viewed.</summary>
    public SolutionComponentItem? Item { get; init; }

    public bool InSolution => Item is not null;
    public string Where => InSolution ? "In this solution" : "Outside this solution";
    public string ManagedLabel => Item switch
    {
        null => string.Empty,
        { IsManaged: true } => "Managed",
        _ => "Unmanaged"
    };
}

/// <summary>"A depends on B": removing B breaks A.</summary>
public sealed record DependencyEdge(Guid Dependent, Guid Required);

public sealed class DependencyGraph
{
    public Dictionary<Guid, DependencyNode> Nodes { get; } = new();
    public HashSet<DependencyEdge> Edges { get; } = new();

    /// <summary>The walk stopped at its depth or size limit, so some components were not followed further.</summary>
    public bool IsTruncated { get; set; }

    /// <summary>The graph as a Mermaid flowchart: each arrow reads "depends on".</summary>
    public string ToMermaid(string? title = null)
    {
        var ids = Nodes.Keys.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => $"n{x.i}");
        var md = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(title)) md.AppendLine("---").AppendLine($"title: {Text(title)}").AppendLine("---");
        md.AppendLine("flowchart LR");

        foreach (var node in Nodes.Values.OrderBy(n => n.Level).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            md.AppendLine($"    {ids[node.ObjectId]}[\"{Text(node.Name)}<br/><small>{Text(node.TypeName)}</small>\"]");
        }

        foreach (var edge in Edges.Where(e => ids.ContainsKey(e.Dependent) && ids.ContainsKey(e.Required)))
        {
            md.AppendLine($"    {ids[edge.Dependent]} --> {ids[edge.Required]}");
        }

        // Outside the solution is what surprises people at import, so it stands out.
        var outside = Nodes.Values.Where(n => !n.InSolution && n.Level > 0).Select(n => ids[n.ObjectId]).ToList();
        if (outside.Count > 0)
        {
            md.AppendLine("    classDef outside stroke-dasharray: 4 3");
            md.AppendLine($"    class {string.Join(",", outside)} outside");
        }

        return md.ToString();
    }

    /// <summary>Mermaid labels are quoted; quotes and angle brackets inside would end them early.</summary>
    private static string Text(string value) =>
        value.Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;").Replace("\r", " ").Replace("\n", " ");
}

/// <summary>Walks dependencies breadth first, a bounded number of levels and components.</summary>
public static class DependencyWalker
{
    public const int DefaultMaxDepth = 4;
    public const int DefaultMaxNodes = 300;

    public delegate Task<IReadOnlyList<DependencyRef>> Fetch(Guid objectId, int componentType, DependencyDirection direction, CancellationToken ct);

    /// <param name="direction">
    /// Dependent: what would break if the start were removed, and what would break if those were.
    /// Required: what the start needs, and what that needs.
    /// </param>
    public static async Task<DependencyGraph> WalkAsync(
        Fetch fetch,
        SolutionComponentItem start,
        DependencyDirection direction,
        Func<Guid, SolutionComponentItem?> known,
        int maxDepth = DefaultMaxDepth,
        int maxNodes = DefaultMaxNodes,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var graph = new DependencyGraph();
        graph.Nodes[start.ObjectId] = new DependencyNode
        {
            ObjectId = start.ObjectId, ComponentType = start.ComponentType, TypeName = start.ComponentTypeName,
            Name = start.PrimaryLabel, Level = 0, Item = known(start.ObjectId) ?? start
        };

        var frontier = new List<DependencyNode> { graph.Nodes[start.ObjectId] };

        for (var level = 1; level <= maxDepth && frontier.Count > 0; level++)
        {
            var next = new List<DependencyNode>();

            foreach (var node in frontier)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var dependency in await fetch(node.ObjectId, node.ComponentType, direction, ct).ConfigureAwait(false))
                {
                    graph.Edges.Add(direction == DependencyDirection.Dependent
                        ? new DependencyEdge(dependency.ObjectId, node.ObjectId)
                        : new DependencyEdge(node.ObjectId, dependency.ObjectId));

                    if (graph.Nodes.ContainsKey(dependency.ObjectId)) continue;

                    if (graph.Nodes.Count >= maxNodes)
                    {
                        graph.IsTruncated = true;
                        continue;
                    }

                    var item = known(dependency.ObjectId);
                    var added = new DependencyNode
                    {
                        ObjectId = dependency.ObjectId,
                        ComponentType = dependency.ComponentType,
                        TypeName = dependency.ComponentTypeName,
                        Name = item?.PrimaryLabel ?? dependency.ResolvedName ?? $"{dependency.ComponentTypeName} {dependency.ObjectId.ToString()[..8]}",
                        Level = level,
                        Item = item
                    };

                    graph.Nodes[added.ObjectId] = added;
                    next.Add(added);
                }

                progress?.Report(graph.Nodes.Count);
            }

            frontier = next;
        }

        if (frontier.Count > 0) graph.IsTruncated = true;
        return graph;
    }
}
