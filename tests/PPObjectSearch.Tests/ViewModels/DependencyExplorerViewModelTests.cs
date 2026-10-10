using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The dependency explorer: the tree in each direction, expanded level by level, and the removal impact.</summary>
public class DependencyExplorerViewModelTests
{
    private static readonly Guid Column = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Form = Guid.Parse("f0000000-0000-0000-0000-000000000002");
    private static readonly Guid View = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid App = Guid.Parse("b0000000-0000-0000-0000-000000000004");
    private static readonly Guid Table = Guid.Parse("d0000000-0000-0000-0000-000000000005");

    private static readonly SolutionComponentItem Start = new()
    {
        Name = "new_status", DisplayName = "Status", ComponentType = 2, ComponentTypeName = "Attribute", ObjectId = Column
    };

    private static readonly SolutionComponentItem MainForm = new()
    {
        Name = "Main form", ComponentType = 60, ComponentTypeName = "System Form", ObjectId = Form, IsManaged = true
    };

    private static readonly IReadOnlyDictionary<Guid, SolutionComponentItem> Known =
        new Dictionary<Guid, SolutionComponentItem> { [Form] = MainForm };

    private static string Rows(string prefix, params (Guid Id, string Type)[] rows) => JsonSerializer.Serialize(new
    {
        value = rows.Select(r => new Dictionary<string, object>
        {
            [prefix + "objectid"] = r.Id,
            [prefix + "type"] = 60,
            [prefix + "type@OData.Community.Display.V1.FormattedValue"] = r.Type
        })
    });

    private static string Dependents(params (Guid Id, string Type)[] rows) => Rows("dependentcomponent", rows);

    private static string DependentsUrl(Guid id) => $"RetrieveDependentComponents(ObjectId=@p1,ComponentType=@p2)?@p1={id}";

    /// <summary>Status ← Main form (known), Saved view; Main form ← App; App ← Main form (a cycle).</summary>
    private static FakeHttpHandler Graph() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, DependentsUrl(Column), Dependents((Form, "System Form"), (View, "Saved Query")))
        .OnJson(HttpMethod.Get, DependentsUrl(Form), Dependents((App, "Model-driven App")))
        .OnJson(HttpMethod.Get, DependentsUrl(App), Dependents((Form, "System Form")))
        .OnJson(HttpMethod.Get, "RetrieveDependentComponents(", """{"value":[]}""")
        .OnJson(HttpMethod.Get, "RetrieveRequiredComponents(", Rows("requiredcomponent", (Table, "Entity")));

    private static DependencyExplorerViewModel Explorer(FakeHttpHandler handler, Action<SolutionComponentItem>? open = null) =>
        new(Fakes.Dataverse(handler), Start, Known, open);

    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!Holds(condition))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The condition was never met.");
            await Task.Delay(5);
        }
    }

    /// <summary>A tree level loads in the background; a check that meets it mid-change simply asks again.</summary>
    private static bool Holds(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Task Loaded(DependencyTreeNode node) => Until(() => node.Children.All(c => c.Name != "Loading…"));

    [Fact]
    public void The_window_is_named_after_the_component()
    {
        var explorer = Explorer(Graph());

        Assert.Equal("Dependencies — Status", explorer.Title);
        Assert.Equal("Dependencies of Status", explorer.Heading);
        Assert.Null(explorer.Session);
        Assert.Equal("Expand a component to see what it depends on, or what depends on it.", explorer.StatusLine);
    }

    [Fact]
    public async Task What_depends_on_it_is_listed_at_once_and_what_it_needs_waits_to_be_expanded()
    {
        var handler = Graph();
        var explorer = Explorer(handler);
        var (dependents, required) = (explorer.Roots[0], explorer.Roots[1]);

        await Loaded(dependents);

        Assert.Equal(["What depends on this", "What this needs"], explorer.Roots.Select(r => r.Name));
        Assert.True(dependents.IsGroup);
        Assert.Equal(string.Empty, dependents.Where);
        Assert.False(dependents.IsOutside);
        Assert.Equal("Loading…", Assert.Single(required.Children).Name);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("RetrieveRequiredComponents"));
    }

    [Fact]
    public async Task Components_outside_the_solution_come_first_and_are_named_by_type_and_id()
    {
        var explorer = Explorer(Graph());

        await Loaded(explorer.Roots[0]);

        var children = explorer.Roots[0].Children;
        Assert.Equal(["Saved Query a0000000", "Main form"], children.Select(c => c.Name));

        var outside = children[0];
        Assert.True(outside.IsOutside);
        Assert.Equal("Outside this solution", outside.Where);
        Assert.Equal(string.Empty, outside.ManagedLabel);
        Assert.Equal("Saved Query", outside.TypeName);
        Assert.Same(explorer.Roots[0], outside.Parent);

        var inside = children[1];
        Assert.False(inside.IsOutside);
        Assert.Equal("In this solution", inside.Where);
        Assert.Equal("managed", inside.ManagedLabel);
        Assert.Same(MainForm, inside.Item);
        Assert.Equal(DependencyDirection.Dependent, inside.Direction);
    }

    [Fact]
    public async Task Expanding_a_component_reads_its_own_dependents_and_a_cycle_stops()
    {
        var explorer = Explorer(Graph());
        await Loaded(explorer.Roots[0]);
        var form = explorer.Roots[0].Children.Single(c => c.Name == "Main form");

        form.IsExpanded = true;
        await Loaded(form);
        var app = Assert.Single(form.Children);
        app.IsExpanded = true;
        await Loaded(app);

        Assert.Equal("Model-driven App b0000000", app.Name);
        var backToForm = Assert.Single(app.Children);
        Assert.Equal("Main form", backToForm.Name);
        Assert.Empty(backToForm.Children);
    }

    [Fact]
    public async Task Expanding_what_it_needs_asks_the_other_way()
    {
        var handler = Graph();
        var explorer = Explorer(handler);
        var required = explorer.Roots[1];

        required.IsExpanded = true;
        await Loaded(required);

        var table = Assert.Single(required.Children);
        Assert.Equal("Entity d0000000", table.Name);
        Assert.Equal(DependencyDirection.Required, table.Direction);
        Assert.Contains(handler.Requests, r => r.Url.Contains($"RetrieveRequiredComponents(ObjectId=@p1,ComponentType=@p2)?@p1={Column}&@p2=2"));
    }

    [Fact]
    public async Task A_level_that_cannot_be_read_says_so_in_its_place()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "RetrieveDependentComponents(", HttpStatusCode.Forbidden, "No access");
        var explorer = Explorer(handler);

        await Loaded(explorer.Roots[0]);

        var notice = Assert.Single(explorer.Roots[0].Children);
        Assert.StartsWith("Could not read - ", notice.Name);
        Assert.Contains("No access", notice.Name);
        Assert.True(notice.IsGroup);
        Assert.Same(explorer.Roots[0], notice.Parent);
    }

    [Fact]
    public void Before_the_impact_is_walked_the_list_explains_how_to_walk_it()
    {
        var explorer = Explorer(Graph());

        Assert.False(explorer.IsImpactWalked);
        Assert.False(explorer.HasImpact);
        Assert.Equal(string.Empty, explorer.ImpactCountLabel);
        Assert.Equal("Not walked yet", explorer.EmptyHeading);
        Assert.Contains($"up to {DependencyWalker.DefaultMaxDepth} levels", explorer.EmptyText);
        Assert.True(explorer.ImpactCommand.CanExecute(null));
        Assert.False(explorer.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_impact_lists_everything_that_depends_on_it_outside_the_solution_first()
    {
        var explorer = Explorer(Graph());

        await explorer.ImpactCommand.ExecuteAsync(null);

        Assert.True(explorer.IsImpactWalked);
        Assert.True(explorer.HasImpact);
        Assert.Equal(["Saved Query a0000000", "Model-driven App b0000000", "Main form"], explorer.Impact.Select(n => n.Name));
        Assert.Equal("3 affected", explorer.ImpactCountLabel);
        Assert.StartsWith("Read 3 dependents in ", explorer.ReadSummary);
        Assert.EndsWith(", 2 of them outside this solution", explorer.ReadSummary);
        Assert.Equal(string.Empty, explorer.Warning);
        Assert.False(explorer.IsBusy);
    }

    [Fact]
    public async Task Nothing_depending_on_it_is_an_answer_of_its_own()
    {
        var explorer = Explorer(new FakeHttpHandler().OnJson(HttpMethod.Get, "RetrieveDependentComponents(", """{"value":[]}"""));

        await explorer.ImpactCommand.ExecuteAsync(null);

        Assert.True(explorer.IsImpactWalked);
        Assert.False(explorer.HasImpact);
        Assert.Equal("0 affected", explorer.ImpactCountLabel);
        Assert.Equal("Nothing depends on this", explorer.EmptyHeading);
        Assert.Equal("Removing it breaks nothing that Dataverse tracks.", explorer.EmptyText);
        Assert.StartsWith("Read 0 dependents in ", explorer.ReadSummary);
        Assert.DoesNotContain("outside", explorer.ReadSummary);
    }

    [Fact]
    public async Task A_single_dependent_is_counted_in_the_singular()
    {
        var explorer = Explorer(new FakeHttpHandler()
            .OnJson(HttpMethod.Get, DependentsUrl(Column), Dependents((View, "Saved Query")))
            .OnJson(HttpMethod.Get, "RetrieveDependentComponents(", """{"value":[]}"""));

        await explorer.ImpactCommand.ExecuteAsync(null);

        Assert.Equal("1 affected", explorer.ImpactCountLabel);
        Assert.StartsWith("Read 1 dependent in ", explorer.ReadSummary);
        Assert.EndsWith(", 1 of them outside this solution", explorer.ReadSummary);
    }

    [Fact]
    public async Task A_walk_cut_short_at_its_depth_warns_that_more_may_depend_on_it()
    {
        // Every component has one more dependent, without end.
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "RetrieveDependentComponents(", _ =>
            FakeHttpHandler.Json(Dependents((Guid.NewGuid(), "Process"))));
        var explorer = Explorer(handler);

        await explorer.ImpactCommand.ExecuteAsync(null);

        Assert.Equal(DependencyWalker.DefaultMaxDepth, explorer.Impact.Count);
        Assert.StartsWith($"The walk stopped after {DependencyWalker.DefaultMaxDepth} levels", explorer.Warning);
        Assert.StartsWith($"Read {DependencyWalker.DefaultMaxDepth} dependents in ", explorer.ReadSummary);
    }

    [Fact]
    public async Task A_walk_that_fails_says_why_and_is_not_taken_for_an_empty_one()
    {
        var explorer = Explorer(new FakeHttpHandler().OnError(HttpMethod.Get, "RetrieveDependentComponents(", HttpStatusCode.InternalServerError, "Boom"));

        await explorer.ImpactCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not walk the dependencies - ", explorer.Status);
        Assert.Contains("Boom", explorer.StatusLine);
        Assert.False(explorer.IsImpactWalked);
        Assert.Equal("Not walked yet", explorer.EmptyHeading);
    }

    [Fact]
    public async Task A_walk_can_be_stopped()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var calls = 0;

        // The tree's own first level is answered; the walk's first read waits until it is stopped.
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "RetrieveDependentComponents(", _ =>
            Interlocked.Increment(ref calls) == 1 ? Task.FromResult(FakeHttpHandler.Json("""{"value":[]}""")) : slow.Task);
        var explorer = Explorer(handler);
        await Until(() => handler.Requests.Count == 1);

        var walk = explorer.ImpactCommand.ExecuteAsync(null);
        await Until(() => handler.Requests.Count == 2);

        Assert.True(explorer.IsBusy);
        Assert.Equal("Walking the dependents", explorer.EmptyHeading);
        Assert.StartsWith("The list fills in when the walk is done", explorer.EmptyText);
        Assert.True(explorer.CancelCommand.CanExecute(null));
        Assert.False(explorer.ImpactCommand.CanExecute(null));

        explorer.CancelCommand.Execute(null);
        slow.SetCanceled();
        await walk;

        Assert.Equal("Stopped.", explorer.Status);
        Assert.False(explorer.IsBusy);
        Assert.False(explorer.IsImpactWalked);
    }

    [Fact]
    public async Task Closing_the_window_stops_a_walk_still_running()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var calls = 0;
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "RetrieveDependentComponents(", _ =>
            Interlocked.Increment(ref calls) == 1 ? Task.FromResult(FakeHttpHandler.Json("""{"value":[]}""")) : slow.Task);
        var explorer = Explorer(handler);
        await Until(() => handler.Requests.Count == 1);
        var walk = explorer.ImpactCommand.ExecuteAsync(null);
        await Until(() => handler.Requests.Count == 2);

        explorer.Dispose();
        slow.SetResult(FakeHttpHandler.Json(Dependents((Form, "System Form"), (View, "Saved Query"))));
        await walk;

        Assert.Equal("Stopped.", explorer.Status);
        Assert.Empty(explorer.Impact);
        Assert.False(explorer.IsImpactWalked);
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(explorer.IsBusy);
        Assert.False(explorer.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task What_is_copied_before_a_walk_is_the_tree_as_expanded()
    {
        var explorer = Explorer(Graph());
        await Loaded(explorer.Roots[0]);
        explorer.Roots[1].IsExpanded = true;
        await Loaded(explorer.Roots[1]);

        var graph = explorer.CurrentGraph();

        Assert.Equal([Column, View, Form, Table], graph.Nodes.Keys);
        Assert.Equal("Status", graph.Nodes[Column].Name);
        Assert.Equal(1, graph.Nodes[Form].Level);
        Assert.Contains(new DependencyEdge(Form, Column), graph.Edges);
        Assert.Contains(new DependencyEdge(Column, Table), graph.Edges);
    }

    [Fact]
    public async Task What_is_copied_after_a_walk_is_the_removal_impact()
    {
        var explorer = Explorer(Graph());

        await explorer.ImpactCommand.ExecuteAsync(null);
        var graph = explorer.CurrentGraph();

        Assert.Same(graph, explorer.CurrentGraph());
        Assert.Contains(App, graph.Nodes.Keys);
        Assert.Contains(new DependencyEdge(App, Form), graph.Edges);
    }

    [Fact]
    public async Task Only_a_component_in_the_solution_can_be_opened()
    {
        var opened = new List<SolutionComponentItem>();
        var explorer = Explorer(Graph(), opened.Add);
        await Loaded(explorer.Roots[0]);
        await explorer.ImpactCommand.ExecuteAsync(null);
        var (outside, inside) = (explorer.Roots[0].Children[0], explorer.Roots[0].Children[1]);
        var impactInside = explorer.Impact.Single(n => n.InSolution);

        Assert.False(explorer.OpenCommand.CanExecute(outside));
        Assert.True(explorer.OpenCommand.CanExecute(inside));
        Assert.True(explorer.OpenCommand.CanExecute(impactInside));
        Assert.False(explorer.OpenCommand.CanExecute("not a node"));

        explorer.OpenCommand.Execute(inside);
        explorer.OpenCommand.Execute(impactInside);
        explorer.OpenCommand.Execute(outside);

        Assert.Equal([MainForm, MainForm], opened);
    }

    [Fact]
    public async Task Without_somewhere_to_open_it_opening_does_nothing()
    {
        var explorer = Explorer(Graph());
        await Loaded(explorer.Roots[0]);
        var inside = explorer.Roots[0].Children.Single(c => c.Item is not null);

        explorer.OpenCommand.Execute(inside);

        Assert.True(explorer.OpenCommand.CanExecute(inside));
    }
}
