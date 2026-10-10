namespace PPObjectSearch.Models;

/// <summary>A tab of the object details window, so a caller can open it on the one it wants.</summary>
public enum DetailsTab
{
    /// <summary>The type's own first tab - see <see cref="DetailsTabs.DefaultFor"/>.</summary>
    Default,
    Overview,
    Design,
    Runs,
    Source,
    TraceLog,
    Value,
    Connections,
    Components,
    Layers,
    Dependencies
}

/// <summary>What an object is, as far as the details window's tabs are concerned.</summary>
public enum ObjectKind
{
    Other,
    CloudFlow,
    ClassicWorkflow,
    Plugin,
    WebResource,
    EnvironmentVariable,
    Table,
    ConnectionReference,
    CanvasApp,
    ModelDrivenApp,
    Agent,
    CustomApi,
    SecurityRole,
    OptionSet,
    BusinessProcessFlow
}

/// <summary>
/// A detail-pane button that opens the details window on one tab - and, for a table, on one
/// group of its components.
/// </summary>
public sealed record DetailsShortcut(string Label, DetailsTab Tab, TableChildKind? Group = null)
{
    public string ToolTip => Group is { } group
        ? $"Open details on this table's {group.ToString().ToLowerInvariant()}s"
        : $"Open details on the {Label} tab";
}

/// <summary>Which tabs an object has, in what order, and the shortcuts to them.</summary>
public static class DetailsTabs
{
    private const int TableType = 1;
    private const int ProcessType = 29;
    private const int WebResourceType = 61;

    // Most kinds without a richer view offer their layers; the shortcut is immutable, so one serves them all.
    private static readonly DetailsShortcut LayersShortcut = new("Layers", DetailsTab.Layers);

    // The category code is what to go by; the labels cover items loaded from a cache written
    // before the code was kept. Dataverse labels category 5 "Modern Flow" and 0 "Workflow".
    public static ObjectKind KindOf(SolutionComponentItem item) => item.ComponentType switch
    {
        TableType => ObjectKind.Table,
        WebResourceType => ObjectKind.WebResource,
        90 or 91 or 92 => ObjectKind.Plugin,
        380 or 381 => ObjectKind.EnvironmentVariable,
        ProcessType when IsCategory(item, 5, "Modern Flow", "Cloud Flow") => ObjectKind.CloudFlow,
        ProcessType when IsCategory(item, 0, "Workflow", "Workflow (classic)") => ObjectKind.ClassicWorkflow,
        // A connection reference's type code is assigned per environment, so its table name is what to go by.
        _ when string.Equals(item.ComponentLogicalName, "connectionreference", StringComparison.OrdinalIgnoreCase) =>
            ObjectKind.ConnectionReference,
        // Apps, agents, custom APIs, roles, choices and business process flows have an Overview.
        _ => Dataverse.DataverseClient.OverviewKindOf(item)
    };

    private static bool IsCategory(SolutionComponentItem item, int category, params string[] labels) =>
        item.ProcessCategory is { } c ? c == category : labels.Contains(item.SubType);

    /// <summary>The type's own tab, which is what the window is usually opened for.</summary>
    public static DetailsTab DefaultFor(ObjectKind kind) => kind switch
    {
        ObjectKind.CloudFlow => DetailsTab.Design,
        ObjectKind.ClassicWorkflow => DetailsTab.Runs,
        ObjectKind.Plugin => DetailsTab.TraceLog,
        ObjectKind.WebResource => DetailsTab.Source,
        ObjectKind.EnvironmentVariable => DetailsTab.Value,
        ObjectKind.Table => DetailsTab.Components,
        ObjectKind.ConnectionReference => DetailsTab.Connections,
        _ when HasOverview(kind) => DetailsTab.Overview,
        _ => DetailsTab.Layers
    };

    /// <summary>The kinds whose own tab is the Overview: a property list and the parts that make them up.</summary>
    public static bool HasOverview(ObjectKind kind) => kind is ObjectKind.CanvasApp or ObjectKind.ModelDrivenApp
        or ObjectKind.Agent or ObjectKind.CustomApi or ObjectKind.SecurityRole or ObjectKind.OptionSet
        or ObjectKind.BusinessProcessFlow;

    /// <summary>The tabs the window shows for this kind, type-specific first.</summary>
    public static IReadOnlyList<DetailsTab> TabsFor(ObjectKind kind)
    {
        IEnumerable<DetailsTab> own = kind switch
        {
            ObjectKind.CloudFlow => [DetailsTab.Design, DetailsTab.Runs, DetailsTab.Source, DetailsTab.Connections],
            ObjectKind.ClassicWorkflow => [DetailsTab.Runs],
            ObjectKind.Plugin => [DetailsTab.TraceLog],
            ObjectKind.WebResource => [DetailsTab.Source],
            ObjectKind.EnvironmentVariable => [DetailsTab.Value],
            ObjectKind.Table => [DetailsTab.Components],
            ObjectKind.ConnectionReference => [DetailsTab.Connections],
            _ when HasOverview(kind) => [DetailsTab.Overview],
            _ => []
        };

        return own.Concat([DetailsTab.Layers, DetailsTab.Dependencies]).ToList();
    }

    /// <summary>A tab the kind does not have falls back to its default, rather than to a hidden tab.</summary>
    public static DetailsTab Resolve(ObjectKind kind, DetailsTab requested) =>
        requested != DetailsTab.Default && TabsFor(kind).Contains(requested) ? requested : DefaultFor(kind);

    /// <summary>The detail pane's "Open in details" buttons for this object.</summary>
    public static IReadOnlyList<DetailsShortcut> ShortcutsFor(SolutionComponentItem item) => KindOf(item) switch
    {
        ObjectKind.CloudFlow =>
        [
            new("Design", DetailsTab.Design),
            new("Run history", DetailsTab.Runs),
            new("Definition", DetailsTab.Source)
        ],
        ObjectKind.ClassicWorkflow => [new("System jobs", DetailsTab.Runs), LayersShortcut],
        ObjectKind.Plugin => [new("Trace log", DetailsTab.TraceLog), LayersShortcut],
        ObjectKind.WebResource => [new("Source", DetailsTab.Source), LayersShortcut],
        ObjectKind.EnvironmentVariable => [new("Value", DetailsTab.Value)],
        ObjectKind.ConnectionReference => [new("Connection", DetailsTab.Connections), new("Used by", DetailsTab.Dependencies)],
        var kind when HasOverview(kind) => [new("Overview", DetailsTab.Overview), new("Dependencies", DetailsTab.Dependencies)],
        ObjectKind.Table =>
        [
            new("Columns", DetailsTab.Components, TableChildKind.Column),
            new("Relationships", DetailsTab.Components, TableChildKind.Relationship),
            new("Forms", DetailsTab.Components, TableChildKind.Form),
            new("Views", DetailsTab.Components, TableChildKind.View)
        ],
        _ => [LayersShortcut, new("Dependencies", DetailsTab.Dependencies)]
    };
}
