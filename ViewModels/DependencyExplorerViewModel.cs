using System.Collections.ObjectModel;
using System.Diagnostics;
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
    public string Where => (IsGroup, Item) switch
    {
        (true, _) => string.Empty,
        (_, null) => "Outside this solution",
        _ => "In this solution"
    };
    public bool IsOutside => !IsGroup && Item is null;
    public string ManagedLabel => Item switch
    {
        null => string.Empty,
        { IsManaged: true } => "managed",
        _ => "unmanaged"
    };

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
public sealed class DependencyExplorerViewModel : ObservableObject, IDisposable
{
    private readonly DataverseClient _client;
    private readonly SolutionComponentItem _item;
    private readonly IReadOnlyDictionary<Guid, SolutionComponentItem> _known;
    private readonly Action<SolutionComponentItem>? _open;
    private CancellationTokenSource? _cts;

    /// <summary>Stops a walk still running when the window closes; nobody is left to read the answer.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public DependencyExplorerViewModel(
        DataverseClient client,
        SolutionComponentItem item,
        IReadOnlyDictionary<Guid, SolutionComponentItem> known,
        Action<SolutionComponentItem>? open,
        EnvironmentSessionViewModel? session = null)
    {
        _client = client;
        _item = item;
        _known = known;
        _open = open;
        Session = session;

        Roots.Add(Group("What depends on this", DependencyDirection.Dependent));
        Roots.Add(Group("What this needs", DependencyDirection.Required));
        Roots[0].IsExpanded = true;

        ImpactCommand = new AsyncRelayCommand(_ => ImpactAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        CopyMermaidCommand = new RelayCommand(_ => CopyMermaid());
        OpenCommand = new RelayCommand(p => Open(p), p => ItemOf(p) is not null);
    }

    public string Title => $"Dependencies — {_item.PrimaryLabel}";

    /// <summary>"Dependencies of contoso_status" - the window heading.</summary>
    public string Heading => $"Dependencies of {_item.PrimaryLabel}";

    /// <summary>The tab the window was opened from, for its environment line - null when it was not passed.</summary>
    public EnvironmentSessionViewModel? Session { get; }

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
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    private string _status = "Expand a component to see what it depends on, or what depends on it.";
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    private string _readSummary = string.Empty;
    /// <summary>"Read 37 dependents in 2.1 s, 5 of them outside this solution" - once the impact is walked.</summary>
    public string ReadSummary
    {
        get => _readSummary;
        private set
        {
            if (SetProperty(ref _readSummary, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>The status bar's left side: what is happening, else what the last walk covered.</summary>
    public string StatusLine => string.IsNullOrEmpty(Status) ? ReadSummary : Status;

    private string _warning = string.Empty;
    /// <summary>A walk cut short at its depth or size limit - the warning banner.</summary>
    public string Warning
    {
        get => _warning;
        private set => SetProperty(ref _warning, value);
    }

    /// <summary>The removal impact has been walked, so its list (or its empty state) means something.</summary>
    public bool IsImpactWalked => _impactGraph is not null;

    public bool HasImpact => Impact.Count > 0;

    /// <summary>"37 affected" once the impact is walked; empty before.</summary>
    public string ImpactCountLabel => IsImpactWalked ? $"{Impact.Count:N0} affected" : string.Empty;

    /// <summary>Why the impact list is empty - walking, not walked yet, or nothing depends on this.</summary>
    public string EmptyHeading => (IsBusy, IsImpactWalked) switch
    {
        (true, _) => "Walking the dependents",
        (_, true) => "Nothing depends on this",
        _ => "Not walked yet"
    };

    public string EmptyText => (IsBusy, IsImpactWalked) switch
    {
        (true, _) => "The list fills in when the walk is done - the count so far is in the status bar.",
        (_, true) => "Removing it breaks nothing that Dataverse tracks.",
        _ => $"Press What breaks if I remove this? to follow what depends on it, and what depends on those, up to {DependencyWalker.DefaultMaxDepth} levels."
    };

    private void ShowImpact()
    {
        OnPropertyChanged(nameof(IsImpactWalked));
        OnPropertyChanged(nameof(HasImpact));
        OnPropertyChanged(nameof(ImpactCountLabel));
        OnPropertyChanged(nameof(EmptyHeading));
        OnPropertyChanged(nameof(EmptyText));
    }

    private DependencyTreeNode Group(string label, DependencyDirection direction) =>
        new(label, string.Empty, null, null, direction, parent => ChildrenAsync(parent, _item.ObjectId, _item.ComponentType));

    private async Task<IReadOnlyList<DependencyTreeNode>> ChildrenAsync(DependencyTreeNode parent, Guid objectId, int componentType)
    {
        var references = await _client.GetDependenciesAsync(objectId, componentType, parent.Direction, CancellationToken.None);
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
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        Impact.Clear();
        _impactGraph = null; // so a walk that is stopped or fails does not read as "nothing depends on this"
        ShowImpact();
        Warning = string.Empty;
        var clock = Stopwatch.StartNew();

        try
        {
            var graph = await DependencyWalker.WalkAsync(
                (id, type, direction, ct) => _client.GetDependenciesAsync(id, type, direction, ct),
                _item, DependencyDirection.Dependent,
                id => _known.TryGetValue(id, out var match) ? match : null,
                progress: new Progress<int>(count => Status = $"Walking dependents... {count:N0} component(s) so far"),
                ct: cts.Token);

            _impactGraph = graph;

            foreach (var node in graph.Nodes.Values.Where(n => n.Level > 0)
                         .OrderBy(n => n.InSolution)
                         .ThenBy(n => n.Level)
                         .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Impact.Add(node);
            }

            ShowImpact();

            // The count is on the right, an empty list explains itself, and a cut-short walk is a warning.
            var outside = Impact.Count(n => !n.InSolution);
            ReadSummary = $"Read {Impact.Count:N0} dependent{(Impact.Count == 1 ? string.Empty : "s")} in {clock.Elapsed.TotalSeconds:0.0} s" +
                          (Impact.Count == 0 ? string.Empty : $", {outside:N0} of them outside this solution");
            Warning = graph.IsTruncated
                ? $"The walk stopped after {DependencyWalker.DefaultMaxDepth} levels or {DependencyWalker.DefaultMaxNodes:N0} components - more may depend on this than is listed."
                : string.Empty;
            Status = string.Empty;
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
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
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
