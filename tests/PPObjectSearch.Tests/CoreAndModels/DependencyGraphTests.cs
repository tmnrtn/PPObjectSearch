using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.CoreAndModels;

public class DependencyGraphTests
{
    private static readonly Guid Column = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Form = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid View = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid App = Guid.Parse("00000000-0000-0000-0000-000000000004");

    private static SolutionComponentItem Item(Guid id, string name, int type = 2) =>
        new() { Name = name, ComponentTypeName = "Attribute", ComponentType = type, ObjectId = id };

    private static DependencyRef Ref(Guid id, string type) =>
        new() { ObjectId = id, ComponentType = 60, ComponentTypeName = type, Direction = DependencyDirection.Dependent };

    /// <summary>Column ← Form, View; Form ← App; App ← Form (a cycle).</summary>
    private static Task<IReadOnlyList<DependencyRef>> Fetch(Guid id, int type, DependencyDirection direction, CancellationToken ct) =>
        Task.FromResult(Dependents(id));

    private static IReadOnlyList<DependencyRef> Dependents(Guid id)
    {
        if (id == Column) return [Ref(Form, "System Form"), Ref(View, "Saved Query")];
        if (id == Form) return [Ref(App, "Model-driven App")];
        if (id == App) return [Ref(Form, "System Form")];
        return [];
    }

    [Fact]
    public async Task Dependents_are_walked_level_by_level_without_looping()
    {
        var known = new Dictionary<Guid, SolutionComponentItem> { [Form] = Item(Form, "Main form", 60) };

        var graph = await DependencyWalker.WalkAsync(Fetch, Item(Column, "new_status"), DependencyDirection.Dependent,
            id => known.GetValueOrDefault(id));

        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(1, graph.Nodes[Form].Level);
        Assert.Equal(2, graph.Nodes[App].Level);
        Assert.Equal("Main form", graph.Nodes[Form].Name);
        Assert.True(graph.Nodes[Form].InSolution);
        Assert.False(graph.Nodes[View].InSolution);
        Assert.Equal("Saved Query 00000000", graph.Nodes[View].Name);

        // Each arrow reads "depends on".
        Assert.Contains(new DependencyEdge(Form, Column), graph.Edges);
        Assert.Contains(new DependencyEdge(App, Form), graph.Edges);
        Assert.Contains(new DependencyEdge(Form, App), graph.Edges);
        Assert.False(graph.IsTruncated);
    }

    [Fact]
    public async Task The_walk_stops_at_its_limits_and_says_so()
    {
        var shallow = await DependencyWalker.WalkAsync(Fetch, Item(Column, "c"), DependencyDirection.Dependent, _ => null, maxDepth: 1);
        Assert.Equal(3, shallow.Nodes.Count);
        Assert.True(shallow.IsTruncated);

        var small = await DependencyWalker.WalkAsync(Fetch, Item(Column, "c"), DependencyDirection.Dependent, _ => null, maxNodes: 2);
        Assert.Equal(2, small.Nodes.Count);
        Assert.True(small.IsTruncated);
    }

    [Fact]
    public async Task Requirements_point_the_other_way()
    {
        var graph = await DependencyWalker.WalkAsync(Fetch, Item(Column, "c"), DependencyDirection.Required, _ => null, maxDepth: 1);

        Assert.Contains(new DependencyEdge(Column, Form), graph.Edges);
    }

    [Fact]
    public async Task Mermaid_quotes_labels_and_marks_what_is_outside_the_solution()
    {
        var known = new Dictionary<Guid, SolutionComponentItem> { [Form] = Item(Form, "Main \"form\" <v2>", 60) };
        var graph = await DependencyWalker.WalkAsync(Fetch, Item(Column, "new_status"), DependencyDirection.Dependent,
            id => known.GetValueOrDefault(id), maxDepth: 1);

        var mermaid = graph.ToMermaid("new_status");

        Assert.StartsWith("---\ntitle: new_status\n---\nflowchart LR\n", mermaid.Replace("\r\n", "\n"));
        Assert.Contains("Main #quot;form#quot; #lt;v2#gt;", mermaid);
        Assert.Contains("n1 --> n0", mermaid);
        Assert.Contains("classDef outside", mermaid);
        Assert.Contains("class n2 outside", mermaid);
    }
}
