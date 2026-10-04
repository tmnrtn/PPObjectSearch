using System.Collections.ObjectModel;
using System.Windows;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One component in the dependency tree; its own dependencies are read when it is first expanded.</summary>
public sealed class DependencyTreeNode : ObservableObject
{
    private readonly Func<DependencyTreeNode, Task<IReadOnlyList<DependencyTreeNode>>>? _load;
    private bool _loaded;

    private static readonly DependencyTreeNode Placeholder = new("Loading…", string.Empty, null, null, DependencyDirection.Dependent, null);

    public DependencyTreeNode(
        string name, string typeName, DependencyRef? reference, SolutionComponentItem? item,
        DependencyDirection direction, Func<DependencyTreeNode, Task<IReadOnlyList<DependencyTreeNode>>>? load,
        DependencyTreeNode? parent = null)
    {
        Name = name;
        TypeName = typeName;
        Reference = reference;
        Item = item;
        Direction = direction;
        Parent = parent;
        _load = load;
        if (load is not null) Children.Add(Placeholder);
    }

    public string Name { get; }
    public string TypeName { get; }
    public DependencyRef? Reference { get; }
    public SolutionComponentItem? Item { get; }
    public DependencyDirection Direction { get; }
    public DependencyTreeNode? Parent { get; }

    public bool IsGroup => Reference is null && Item is null;
    public string Where => IsGroup ? string.Empty : Item is null ? "Outside this solution" : "In this solution";
    public bool IsOutside => !IsGroup && Item is null;
    public string ManagedLabel => Item is null ? string.Empty : Item.IsManaged ? "managed" : "unmanaged";

    public ObservableCollection<DependencyTreeNode> Children { get; } = new();

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value) _ = LoadAsync();
        }
    }

    internal async Task LoadAsync()
    {
        if (_loaded || _load is null) return;
        _loaded = true;

        try
        {
            var children = await _load(this);
            Children.Clear();
            foreach (var child in children) Children.Add(child);
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new DependencyTreeNode("Could not read - " + ex.Message, string.Empty, null, null, Direction, null, this));
        }
    }
}

/// <summary>
/// The dependency graph around one component: a tree in each direction, expanded level by level,
/// and the removal impact - everything that depends on it, transitively, with what lies outside
/// the solution called out.
/// </summary>
public sealed class DependencyExplorerViewModel : ObservableObject
{
    private readonly DataverseClient _client;
    private readonly SolutionComponentItem _item;
    private readonly IReadOnlyDictionary<Guid, SolutionComponentItem> _known;
    private readonly Action<SolutionComponentItem>? _open;
    private CancellationTokenSource? _cts;

    public DependencyExplorerViewModel(
        DataverseClient client,
        SolutionComponentItem item,
        IReadOnlyDictionary<Guid, SolutionComponentItem> known,
        Action<SolutionComponentItem>? open)
    {
        _client = client;
        _item = item;
        _known = known;
        _open = open;

        Roots.Add(Group("What depends on this", DependencyDirection.Dependent));
        Roots.Add(Group("What this needs", DependencyDirection.Required));
        Roots[0].IsExpanded = true;

        ImpactCommand = new AsyncRelayCommand(_ => ImpactAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        CopyMermaidCommand = new RelayCommand(_ => CopyMermaid());
        OpenCommand = new RelayCommand(p => Open(p), p => ItemOf(p) is not null);
    }

    public string Title => $"Dependencies — {_item.PrimaryLabel}";

    public ObservableCollection<DependencyTreeNode> Roots { get; } = new();
    public ObservableCollection<DependencyNode> Impact { get; } = new();

    public AsyncRelayCommand ImpactCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CopyMermaidCommand { get; }
    public RelayCommand OpenCommand { get; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ImpactCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status = "Expand a component to see what it depends on, or what depends on it.";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private DependencyTreeNode Group(string label, DependencyDirection direction) =>
        new(label, string.Empty, null, null, direction, parent => ChildrenAsync(parent, _item.ObjectId, _item.ComponentType));

    private async Task<IReadOnlyList<DependencyTreeNode>> ChildrenAsync(DependencyTreeNode parent, Guid objectId, int componentType)
    {
        var references = await _client.GetDependenciesAsync(objectId, componentType, parent.Direction);
        var ancestors = new HashSet<Guid> { _item.ObjectId };
        for (var p = parent; p is not null; p = p.Parent) if (p.Reference is { } r) ancestors.Add(r.ObjectId);

        return references
            .Select(r =>
            {
                var item = _known.TryGetValue(r.ObjectId, out var match) ? match : null;
                var name = item?.PrimaryLabel ?? $"{r.ComponentTypeName} {r.ObjectId.ToString()[..8]}";

                // A cycle is shown once, not expanded forever.
                var load = ancestors.Contains(r.ObjectId)
                    ? null
                    : (Func<DependencyTreeNode, Task<IReadOnlyList<DependencyTreeNode>>>)(node => ChildrenAsync(node, r.ObjectId, r.ComponentType));

                return new DependencyTreeNode(name, r.ComponentTypeName, r, item, parent.Direction, load, parent);
            })
            .OrderBy(n => n.Item is not null)
            .ThenBy(n => n.TypeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private DependencyGraph? _impactGraph;

    private async Task ImpactAsync()
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Impact.Clear();

        try
        {
            var graph = await DependencyWalker.WalkAsync(
                (id, type, direction, ct) => _client.GetDependenciesAsync(id, type, direction, ct),
                _item, DependencyDirection.Dependent,
                id => _known.TryGetValue(id, out var match) ? match : null,
                progress: new Progress<int>(count => Status = $"Walking dependents... {count:N0} component(s) so far"),
                ct: _cts.Token);

            _impactGraph = graph;

            foreach (var node in graph.Nodes.Values.Where(n => n.Level > 0)
                         .OrderBy(n => n.InSolution)
                         .ThenBy(n => n.Level)
                         .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Impact.Add(node);
            }

            var outside = Impact.Count(n => !n.InSolution);
            Status = Impact.Count == 0
                ? "Nothing depends on this - removing it breaks nothing that Dataverse tracks."
                : $"Removing this would affect {Impact.Count:N0} component(s), {outside:N0} of them outside this solution." +
                  (graph.IsTruncated ? $" (Stopped after {DependencyWalker.DefaultMaxDepth} levels or {DependencyWalker.DefaultMaxNodes:N0} components.)" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = "Could not walk the dependencies - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The impact graph once walked; otherwise whatever of the tree has been expanded - so what is
    /// copied is what is on screen.
    /// </summary>
    internal DependencyGraph CurrentGraph()
    {
        if (_impactGraph is not null) return _impactGraph;

        var graph = new DependencyGraph();
        graph.Nodes[_item.ObjectId] = new DependencyNode
        {
            ObjectId = _item.ObjectId, ComponentType = _item.ComponentType, TypeName = _item.ComponentTypeName,
            Name = _item.PrimaryLabel, Level = 0, Item = _item
        };

        void Add(DependencyTreeNode node, Guid parentId, int level)
        {
            if (node.Reference is not { } reference) return;

            graph.Nodes.TryAdd(reference.ObjectId, new DependencyNode
            {
                ObjectId = reference.ObjectId, ComponentType = reference.ComponentType, TypeName = reference.ComponentTypeName,
                Name = node.Name, Level = level, Item = node.Item
            });
            graph.Edges.Add(node.Direction == DependencyDirection.Dependent
                ? new DependencyEdge(reference.ObjectId, parentId)
                : new DependencyEdge(parentId, reference.ObjectId));

            foreach (var child in node.Children) Add(child, reference.ObjectId, level + 1);
        }

        foreach (var root in Roots)
        {
            foreach (var child in root.Children) Add(child, _item.ObjectId, 1);
        }

        return graph;
    }

    private void CopyMermaid()
    {
        try
        {
            Clipboard.SetText(CurrentGraph().ToMermaid(_item.PrimaryLabel));
            Status = _impactGraph is null
                ? "Copied the expanded tree as Mermaid."
                : "Copied the removal impact as Mermaid.";
        }
        catch (Exception ex)
        {
            Status = "Could not copy - " + ex.Message;
        }
    }

    private static SolutionComponentItem? ItemOf(object? p) => p switch
    {
        DependencyTreeNode node => node.Item,
        DependencyNode node => node.Item,
        _ => null
    };

    private void Open(object? p)
    {
        if (ItemOf(p) is { } item) _open?.Invoke(item);
    }
}
