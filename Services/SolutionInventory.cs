using System.Text;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>The solutions being documented, and the components each of them holds.</summary>
public sealed class DocumentationScope
{
    private readonly Dictionary<Guid, List<string>> _solutionsOf = new();

    public DocumentationScope(IReadOnlyList<SolutionInfo> solutions, IReadOnlyDictionary<Guid, IReadOnlyList<SolutionComponentItem>> components)
    {
        Solutions = solutions;
        Components = components;

        // A component in two of the solutions is documented once.
        var items = new List<SolutionComponentItem>();
        var seen = new HashSet<(int, Guid, string)>();
        foreach (var solution in solutions)
        {
            foreach (var item in components.GetValueOrDefault(solution.SolutionId) ?? [])
            {
                if (seen.Add((item.ComponentType, item.ObjectId, item.ObjectId == Guid.Empty ? item.Name : string.Empty))) items.Add(item);

                if (item.ObjectId == Guid.Empty) continue;
                if (!_solutionsOf.TryGetValue(item.ObjectId, out var names)) _solutionsOf[item.ObjectId] = names = new List<string>();
                if (!names.Contains(solution.UniqueName)) names.Add(solution.UniqueName);
            }
        }

        Items = items;
        ObjectIds = items.Where(i => i.ObjectId != Guid.Empty).Select(i => i.ObjectId).ToHashSet();
    }

    /// <summary>One solution and its components.</summary>
    public DocumentationScope(SolutionInfo solution, IReadOnlyList<SolutionComponentItem> items)
        : this([solution], new Dictionary<Guid, IReadOnlyList<SolutionComponentItem>> { [solution.SolutionId] = items })
    {
    }

    public IReadOnlyList<SolutionInfo> Solutions { get; }
    public IReadOnlyDictionary<Guid, IReadOnlyList<SolutionComponentItem>> Components { get; }

    /// <summary>Every component of every solution, once each.</summary>
    public IReadOnlyList<SolutionComponentItem> Items { get; }

    /// <summary>The ids of every component - what is "in scope".</summary>
    public IReadOnlySet<Guid> ObjectIds { get; }

    public bool IsMultiple => Solutions.Count > 1;

    /// <summary>The unique names of the solutions holding a component, comma separated.</summary>
    public string SolutionsOf(Guid objectId) =>
        _solutionsOf.TryGetValue(objectId, out var names) ? string.Join(", ", names) : string.Empty;

    public IEnumerable<SolutionComponentItem> OfType(int componentType) =>
        Items.Where(i => i.ComponentType == componentType && i.ObjectId != Guid.Empty);
}

/// <summary>One line of the scope summary: a headline number, where it comes from, and a note.</summary>
public sealed record ScopeMetric(string Area, string Metric, string Value, string? Notes = null);

/// <summary>
/// A support-scoping inventory of the solutions: how much automation, code and integration they
/// hold, how much data their tables carry, and how often their processes run. Each section adds a
/// page and lines to the scope summary. Environment-wide facts - integration identities,
/// SharePoint and email, totals across every table, system jobs - are added only when asked for,
/// since no solution holds them.
/// </summary>
internal sealed class SolutionInventory
{
    private const int ProcessType = 29;
    private const int PluginTypeType = 90;
    private const int PluginAssemblyType = 91;
    private const int PluginStepType = 92;
    private const int ServiceEndpointType = 95;
    private const int EnvironmentVariableType = 380;
    private const int BusinessRuleCategory = 2;
    private const int CloudFlowCategory = 5;

    /// <summary>How far back execution volumes look. Cloud flow runs are kept about 28 days by default.</summary>
    public const int ExecutionWindowDays = 30;

    private static readonly HashSet<string> AlwaysCounted = new(StringComparer.OrdinalIgnoreCase)
    {
        "audit", "asyncoperation", "workflowlog", "plugintracelog", "annotation", "activitymimeattachment", "activitypointer",
        "activityparty", "principalobjectaccess"
    };

    private readonly DataverseClient _client;
    private readonly DocumentationScope _scope;
    private readonly bool _environmentWide;

    private List<ProcessInfo>? _processes;
    private Dictionary<Guid, FlowDesign?>? _flows;

    public SolutionInventory(DataverseClient client, DocumentationScope scope, bool environmentWide)
    {
        _client = client;
        _scope = scope;
        _environmentWide = environmentWide;
    }

    public List<ScopeMetric> Metrics { get; } = new();

    private void Metric(string area, string metric, object value, string? notes = null) =>
        Metrics.Add(new ScopeMetric(area, metric, value switch
        {
            int i => i.ToString("N0"),
            long l => l.ToString("N0"),
            _ => value.ToString() ?? string.Empty
        }, string.IsNullOrWhiteSpace(notes) ? null : notes));

    /// <summary>The Solution column, when there is more than one solution to tell apart.</summary>
    private string[] WithSolution(params string[] headers) => _scope.IsMultiple ? [.. headers, "Solution"] : headers;

    private string[] WithSolution(Guid id, params string[] cells) => _scope.IsMultiple ? [.. cells, _scope.SolutionsOf(id)] : cells;

    // ---------------------------------------------------------------- processes

    private async Task<List<ProcessInfo>> ProcessesAsync(CancellationToken ct) =>
        _processes ??= (await _client.GetProcessesAsync(_scope.OfType(ProcessType).Select(i => i.ObjectId), ct).ConfigureAwait(false))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Each in-scope cloud flow's parsed definition - null where it has none or it cannot be read.</summary>
    private async Task<Dictionary<Guid, FlowDesign?>> FlowsAsync(CancellationToken ct)
    {
        if (_flows is not null) return _flows;

        var flows = (await ProcessesAsync(ct).ConfigureAwait(false)).Where(p => p.Category == CloudFlowCategory).ToList();
        var definitions = await _client.GetFlowDefinitionsAsync(flows.Select(f => f.Id), ct).ConfigureAwait(false);

        _flows = new Dictionary<Guid, FlowDesign?>();
        foreach (var flow in flows)
        {
            FlowDesign? design = null;
            if (definitions.TryGetValue(flow.Id, out var definition))
            {
                try
                {
                    design = FlowDesignParser.Parse(definition);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
                {
                    // A definition that will not parse is shown without its trigger and connectors.
                }
            }

            _flows[flow.Id] = design;
        }

        return _flows;
    }

    private static IReadOnlyList<string> ConnectorsOf(FlowDesign? design) =>
        design is null
            ? []
            : SolutionDocumenter.Nodes(design).Select(n => n.Connector).OfType<string>().Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task ProcessesPageAsync(List<DocPage> pages, CancellationToken ct)
    {
        var processes = await ProcessesAsync(ct).ConfigureAwait(false);
        var flows = await FlowsAsync(ct).ConfigureAwait(false);
        var md = new StringBuilder().AppendLine("# Processes").AppendLine();

        var byCategory = processes.GroupBy(p => p.CategoryLabel ?? "(unknown)")
            .Select(g => (Category: g.Key, Active: g.Count(p => p.IsActive), Total: g.Count()))
            .OrderByDescending(c => c.Total).ThenBy(c => c.Category, StringComparer.CurrentCultureIgnoreCase).ToList();

        md.AppendLine("**By category** - definitions; active means activated, or turned on for a flow").AppendLine();
        Markdown.Table(md, ["Category", "Active", "Total"], byCategory.Select(c => new[] { c.Category, $"{c.Active:N0}", $"{c.Total:N0}" }), numeric: [1, 2]);
        foreach (var c in byCategory) Metric("Automation", $"{c.Category} (active / total)", $"{c.Active:N0} / {c.Total:N0}");

        var rules = processes.Where(p => p.Category == BusinessRuleCategory).GroupBy(p => p.Table ?? "(none)")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new[] { g.Key, $"{g.Count():N0}", $"{g.Count(p => p.IsActive):N0}" }).ToList();
        if (rules.Count > 0)
        {
            md.AppendLine().AppendLine("**Business rules per table**").AppendLine();
            Markdown.Table(md, ["Table", "Business rules", "Active"], rules, numeric: [1, 2]);
        }

        var definitions = processes.Where(p => p.Category != CloudFlowCategory)
            .OrderBy(p => p.CategoryLabel).ThenBy(p => p.Table).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (definitions.Count > 0)
        {
            md.AppendLine().AppendLine("**Workflows, business rules, actions and business process flows**").AppendLine();
            Markdown.Table(md, WithSolution("Name", "Category", "Table", "State", "Mode", "Scope", "Starts on"),
                definitions.Select(p => WithSolution(p.Id, p.Name, p.CategoryLabel ?? string.Empty, p.Table ?? string.Empty,
                    p.IsActive ? "Active" : "Draft", p.Mode ?? string.Empty, p.Scope ?? string.Empty, p.Triggers)));
        }

        var cloudFlows = processes.Where(p => p.Category == CloudFlowCategory).ToList();
        if (cloudFlows.Count > 0)
        {
            md.AppendLine().AppendLine("**Cloud flows**").AppendLine();
            Markdown.Table(md, WithSolution("Name", "State", "Trigger", "Actions", "Connectors"),
                cloudFlows.Select(f =>
                {
                    var design = flows.GetValueOrDefault(f.Id);
                    return WithSolution(f.Id, f.Name, f.IsActive ? "On" : "Off",
                        design is null ? "_definition not read_" : design.Triggers.Count > 0 ? design.Triggers[0].Summary : "none",
                        design is null ? string.Empty : $"{design.ActionCount:N0}",
                        string.Join(", ", ConnectorsOf(design)));
                }));
        }

        if (processes.Count == 0) md.AppendLine().AppendLine("_No processes in scope._");
        pages.Add(new DocPage("Inventory", "Processes", md.ToString()));
    }

    // ---------------------------------------------------------------- plug-ins

    public async Task PluginsPageAsync(List<DocPage> pages, CancellationToken ct)
    {
        var endpointIds = _scope.OfType(ServiceEndpointType).Select(i => i.ObjectId).ToHashSet();

        var assemblies = (await _client.GetPluginAssembliesAsync(_scope.OfType(PluginAssemblyType).Select(i => i.ObjectId), ct).ConfigureAwait(false))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var types = (await _client.GetPluginTypesAsync(_scope.OfType(PluginTypeType).Select(i => i.ObjectId), assemblies.Select(a => a.Id), ct)
                .ConfigureAwait(false))
            .OrderBy(t => t.Assembly).ThenBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
        var steps = (await _client.GetInventoryStepsAsync(_scope.OfType(PluginStepType).Select(i => i.ObjectId), types.Select(t => t.Id), ct)
                .ConfigureAwait(false))
            .Where(s => s.HandlerId is not { } handler || !endpointIds.Contains(handler)).ToList();
        var apis = (await _client.GetCustomApisAsync(
                _scope.Items.Where(i => IsLogical(i, "customapi") && i.ObjectId != Guid.Empty).Select(i => i.ObjectId), ct).ConfigureAwait(false))
            .OrderBy(a => a.UniqueName, StringComparer.OrdinalIgnoreCase).ToList();

        var md = new StringBuilder().AppendLine("# Plug-ins and custom APIs").AppendLine();

        md.AppendLine("**Assemblies**").AppendLine();
        Markdown.Table(md, WithSolution("Assembly", "Version", "Isolation", "Source"),
            assemblies.Select(a => WithSolution(a.Id, a.Name, a.Version ?? string.Empty, a.Isolation ?? string.Empty, a.Source ?? string.Empty)));

        md.AppendLine().AppendLine("**Classes** - plug-ins and custom workflow activities, including every class in the assemblies above").AppendLine();
        Markdown.Table(md, ["Class", "Assembly", "Kind"],
            types.Select(t => new[] { t.TypeName, t.Assembly ?? string.Empty, t.IsWorkflowActivity ? "Custom workflow activity" : "Plug-in" }));

        md.AppendLine().AppendLine("**Steps per table** - the steps the solutions hold, and every step of the classes above").AppendLine();
        Markdown.Table(md, ["Table", "Steps", "Enabled", "Synchronous", "Asynchronous", "Messages"],
            steps.GroupBy(s => s.Table ?? "(any)").OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => new[]
                {
                    g.Key, $"{g.Count():N0}", $"{g.Count(s => s.IsEnabled):N0}", $"{g.Count(s => !s.IsAsync):N0}", $"{g.Count(s => s.IsAsync):N0}",
                    string.Join(", ", g.Select(s => s.Message).OfType<string>().Distinct().Order(StringComparer.OrdinalIgnoreCase))
                }), numeric: [1, 2, 3, 4]);

        md.AppendLine().AppendLine("**Custom APIs**").AppendLine();
        Markdown.Table(md, WithSolution("Unique name", "Display name", "Binding", "Bound table", "Function", "Private", "Plug-in type"),
            apis.Select(a => WithSolution(a.Id, a.UniqueName, a.DisplayName ?? string.Empty, a.Binding ?? string.Empty, a.BoundTable ?? string.Empty,
                a.IsFunction ? "Yes" : "No", a.IsPrivate ? "Yes" : "No", a.Implementation ?? string.Empty)));

        pages.Add(new DocPage("Inventory", "Plug-ins and custom APIs", md.ToString()));

        Metric("Plug-ins", "Plug-in assemblies", assemblies.Count, string.Join(", ", assemblies.Select(a => a.Name)));
        Metric("Plug-ins", "Plug-in classes", types.Count(t => !t.IsWorkflowActivity));
        Metric("Plug-ins", "Custom workflow activities", types.Count(t => t.IsWorkflowActivity));
        Metric("Plug-ins", "Plug-in steps (enabled / total)", $"{steps.Count(s => s.IsEnabled):N0} / {steps.Count:N0}",
            $"synchronous {steps.Count(s => !s.IsAsync):N0}, asynchronous {steps.Count(s => s.IsAsync):N0}; " +
            $"across {steps.Select(s => s.Table).Distinct().Count():N0} tables");
        Metric("Plug-ins", "Custom APIs", apis.Count);
    }

    private static bool IsLogical(SolutionComponentItem item, string logicalName) =>
        string.Equals(item.ComponentLogicalName, logicalName, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- integrations

    public async Task IntegrationsPageAsync(List<DocPage> pages, CancellationToken ct)
    {
        var md = new StringBuilder().AppendLine("# Integrations").AppendLine();

        var endpoints = await _client.GetServiceEndpointsAsync(_scope.OfType(ServiceEndpointType).Select(i => i.ObjectId), ct).ConfigureAwait(false);
        List<InventoryStep> endpointSteps = endpoints.Count == 0
            ? []
            : await _client.GetInventoryStepsAsync([], endpoints.Select(e => e.Id), ct).ConfigureAwait(false);

        md.AppendLine("**Service endpoints and webhooks**").AppendLine();
        Markdown.Table(md, WithSolution("Name", "Contract", "Target", "Format", "Authentication", "Steps", "Enabled steps"),
            endpoints.OrderBy(e => e.Name).Select(e => WithSolution(e.Id, e.Name, e.Contract ?? string.Empty, e.Target ?? string.Empty,
                e.Format ?? string.Empty, e.Auth ?? string.Empty,
                $"{endpointSteps.Count(s => s.HandlerId == e.Id):N0}", $"{endpointSteps.Count(s => s.HandlerId == e.Id && s.IsEnabled):N0}")));

        var references = (await _client.GetConnectionReferencesAsync(
                _scope.Items.Where(i => DataverseClient.IsConnectionReference(i) && i.ObjectId != Guid.Empty).Select(i => i.ObjectId), ct)
                .ConfigureAwait(false))
            .OrderBy(r => r.Connector).ThenBy(r => r.Label).ToList();
        md.AppendLine().AppendLine("**Connection references**").AppendLine();
        Markdown.Table(md, WithSolution("Name", "Logical name", "Connector", "Connection set"),
            references.Select(r => WithSolution(r.Id, r.Label, r.LogicalName, r.Connector, r.HasConnection ? "Yes" : "No")));

        var processes = await ProcessesAsync(ct).ConfigureAwait(false);
        var flows = await FlowsAsync(ct).ConfigureAwait(false);
        var usage = processes.Where(p => p.Category == CloudFlowCategory)
            .SelectMany(p => ConnectorsOf(flows.GetValueOrDefault(p.Id)).Select(c => (Connector: c, p.IsActive)))
            .GroupBy(x => x.Connector, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Connector: g.Key, Flows: g.Count(), Active: g.Count(x => x.IsActive)))
            .OrderByDescending(c => c.Flows).ThenBy(c => c.Connector).ToList();
        md.AppendLine().AppendLine("**Connectors used by cloud flows**").AppendLine();
        Markdown.Table(md, ["Connector", "Flows", "Flows on"], usage.Select(u => new[] { u.Connector, $"{u.Flows:N0}", $"{u.Active:N0}" }), numeric: [1, 2]);

        var connectors = await _client.GetCustomConnectorsAsync(
            _scope.Items.Where(i => (i.ComponentType is 371 or 372 || IsLogical(i, "connector")) && i.ObjectId != Guid.Empty).Select(i => i.ObjectId), ct)
            .ConfigureAwait(false);
        md.AppendLine().AppendLine("**Custom connectors**").AppendLine();
        Markdown.Table(md, WithSolution("Name", "Type"), connectors.OrderBy(c => c.Name).Select(c => WithSolution(c.Id, c.Name, c.Type ?? string.Empty)));

        var variables = _scope.OfType(EnvironmentVariableType).Count();
        md.AppendLine().AppendLine($"**Environment variables:** {variables:N0} definition(s).").AppendLine()
            .AppendLine("**Outbound calls from plug-in code** cannot be seen in metadata - review the plug-in source for HTTP and SDK calls.");

        Metric("Integrations", "Service endpoints / webhooks", endpoints.Count,
            string.Join(", ", endpoints.Select(e => $"{e.Name} ({e.Contract}, {endpointSteps.Count(s => s.HandlerId == e.Id && s.IsEnabled):N0} enabled steps)")));
        Metric("Integrations", "Connection references", references.Count, string.Join(", ", references.Select(r => r.Connector).Distinct()));
        Metric("Integrations", "Connectors used by cloud flows", usage.Count, string.Join(", ", usage.Select(u => $"{u.Connector} ({u.Flows:N0})")));
        Metric("Integrations", "Custom connectors", connectors.Count);
        Metric("Integrations", "Environment variables", variables);

        if (_environmentWide) await EnvironmentIntegrationsAsync(md, ct).ConfigureAwait(false);

        pages.Add(new DocPage("Inventory", "Integrations", md.ToString()));
    }

    private async Task EnvironmentIntegrationsAsync(StringBuilder md, CancellationToken ct)
    {
        md.AppendLine().AppendLine("## Environment-wide").AppendLine()
            .AppendLine("No solution holds these; they are the environment's.").AppendLine();

        var users = await _client.GetApplicationUsersAsync(ct).ConfigureAwait(false);
        var theirs = users.Where(u => !u.IsLikelyFirstParty).OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        md.AppendLine($"**Application users** - server-to-server identities; {users.Count - theirs.Count:N0} Microsoft service identities left out").AppendLine();
        Markdown.Table(md, ["Name", "Application id", "Enabled", "Business unit", "Security roles"],
            theirs.Select(u => new[] { u.Name, u.ApplicationId?.ToString() ?? string.Empty, u.IsEnabled ? "Yes" : "No", u.BusinessUnit ?? string.Empty,
                string.Join(", ", u.Roles) }));

        var sites = await _client.GetSharePointSitesAsync(ct).ConfigureAwait(false);
        md.AppendLine().AppendLine("**SharePoint sites** - document management").AppendLine();
        Markdown.Table(md, ["Name", "Address", "State"], sites.Select(s => new[] { s.Name, s.Detail ?? string.Empty, s.State ?? string.Empty }));

        var profiles = await _client.GetEmailServerProfilesAsync(ct).ConfigureAwait(false);
        md.AppendLine().AppendLine("**Email server profiles**").AppendLine();
        Markdown.Table(md, ["Name", "State"], profiles.Select(p => new[] { p.Name, p.State ?? string.Empty }));

        var mailboxes = await _client.AggregateAsync("mailbox", "mailboxes", "mailboxid",
            ["incomingemaildeliverymethod", "outgoingemaildeliverymethod"], ct: ct).ConfigureAwait(false);
        md.AppendLine().AppendLine("**Mailboxes by delivery method**").AppendLine();
        Markdown.Table(md, ["Incoming", "Outgoing", "Mailboxes"],
            mailboxes.OrderByDescending(m => m.Count).Select(m => new[] { m.Labels[0], m.Labels[1], $"{m.Count:N0}" }), numeric: [2]);

        var enabled = theirs.Where(u => u.IsEnabled).ToList();
        Metric("Integrations", "Application users (enabled, not Microsoft's)", enabled.Count, string.Join(", ", enabled.Select(u => u.Name)));
        Metric("Integrations", "SharePoint sites", sites.Count);
        Metric("Integrations", "Email server profiles", profiles.Count, $"{mailboxes.Sum(m => m.Count):N0} mailboxes");
    }

    // ---------------------------------------------------------------- data volumes

    public async Task DataVolumesPageAsync(List<DocPage> pages, CancellationToken ct)
    {
        var tables = await _client.GetInventoryTablesAsync(ct).ConfigureAwait(false);
        var wholeTables = await _client.GetTablesWithAllSubcomponentsAsync(_scope.Solutions.Select(s => s.SolutionId), ct).ConfigureAwait(false);
        var prefixes = _scope.Solutions.Select(s => s.PublisherPrefix).OfType<string>().Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var rows = tables.Select(t => ClassifyTable(t, _scope.ObjectIds, wholeTables, prefixes)).ToList();
        var custom = rows.Where(r => r.CustomInScope && !r.Table.IsIntersect).ToList();
        var intersects = rows.Count(r => r.CustomInScope && r.Table.IsIntersect);
        var extended = rows.Where(r => r.Extended).ToList();

        // Only the tables in scope are counted, unless the whole environment's total is wanted.
        var counted = _environmentWide ? tables.Where(IsCountable) : custom.Concat(extended).Select(r => r.Table);
        var counts = await _client.GetRecordCountsAsync(counted.Select(t => t.LogicalName), ct).ConfigureAwait(false);
        long? Records(InventoryTable t) => counts.TryGetValue(t.LogicalName, out var n) ? n : null;
        string Shown(long? n) => n is { } v ? $"{v:N0}" : string.Empty;

        var md = new StringBuilder().AppendLine("# Data volumes").AppendLine()
            .AppendLine("Record counts are Dataverse's stored snapshot (RetrieveTotalRecordCount), refreshed about daily. " +
                        "Storage in GB is not available here - see the Power Platform admin center, Resources > Capacity.")
            .AppendLine();

        md.AppendLine($"**Custom tables** - plus {intersects:N0} many-to-many intersect table(s)").AppendLine();
        Markdown.Table(md, WithSolution("Table", "Display name", "Records", "Custom columns", "Ownership", "Audit", "Change tracking"),
            custom.OrderByDescending(r => Records(r.Table) ?? -1).ThenBy(r => r.Table.LogicalName)
                .Select(r => WithSolution(r.Table.MetadataId, $"`{r.Table.LogicalName}`", r.Table.DisplayName ?? string.Empty, Shown(Records(r.Table)),
                    $"{r.ScopedColumns:N0}", r.Table.Ownership ?? string.Empty, r.Table.IsAuditEnabled ? "On" : "Off", r.Table.IsChangeTracked ? "On" : "Off")),
            numeric: [2, 3]);

        md.AppendLine().AppendLine("**Out-of-box tables** the solutions hold or add columns to - their records include data the solutions did not create").AppendLine();
        Markdown.Table(md, ["Table", "Display name", "Columns added", "Records"],
            extended.OrderByDescending(r => r.ScopedColumns).ThenBy(r => r.Table.LogicalName)
                .Select(r => new[] { $"`{r.Table.LogicalName}`", r.Table.DisplayName ?? string.Empty, $"{r.ScopedColumns:N0}", Shown(Records(r.Table)) }),
            numeric: [2, 3]);

        var largest = custom.Concat(extended).Where(r => Records(r.Table) is not null).OrderByDescending(r => Records(r.Table)).Take(10).ToList();

        Metric("Tables", "Custom tables", custom.Count, $"plus {intersects:N0} many-to-many intersect tables");
        Metric("Tables", "Out-of-box tables extended", extended.Count, string.Join(", ", extended.Take(10).Select(r => r.Table.LogicalName)));
        Metric("Tables", "Custom columns", custom.Sum(r => r.ScopedColumns) + extended.Sum(r => r.ScopedColumns),
            $"{custom.Sum(r => r.ScopedColumns):N0} on custom tables, {extended.Sum(r => r.ScopedColumns):N0} on out-of-box tables");
        Metric("Data volume", "Records in custom tables", custom.Sum(r => Records(r.Table) ?? 0));
        Metric("Data volume", "Records in extended out-of-box tables", extended.Sum(r => Records(r.Table) ?? 0), "includes data these solutions did not create");
        Metric("Data volume", "Largest tables", "-", string.Join(", ", largest.Select(r => $"{r.Table.LogicalName}: {Records(r.Table):N0}")));

        if (_environmentWide) await EnvironmentDataAsync(md, counts, ct).ConfigureAwait(false);

        pages.Add(new DocPage("Inventory", "Data volumes", md.ToString()));
    }

    /// <summary>A table's place in scope, and how many of its custom columns are the solutions'.</summary>
    internal sealed record TableScope(InventoryTable Table, bool CustomInScope, bool Extended, int ScopedColumns);

    /// <summary>
    /// A custom table is in scope when a solution holds it. Its columns are the solutions' when listed
    /// one by one or, where the table was added with all its subcomponents, when they are its own -
    /// every custom column of a custom table, and those carrying the solutions' publisher prefix on an
    /// out-of-box one (a msdyn_ column on account is not the solutions'). An out-of-box table counts
    /// as extended when a solution holds it or adds a column to it.
    /// </summary>
    internal static TableScope ClassifyTable(
        InventoryTable table, IReadOnlySet<Guid> objectIds, IReadOnlySet<Guid> wholeTables, IReadOnlyList<string> prefixes)
    {
        var held = objectIds.Contains(table.MetadataId);
        var whole = wholeTables.Contains(table.MetadataId);

        var columns = table.CustomColumns.Count(c =>
            objectIds.Contains(c.MetadataId) ||
            (whole && (table.IsCustom || prefixes.Any(p => c.LogicalName.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase)))));

        return new TableScope(table, table.IsCustom && held, !table.IsCustom && (held || columns > 0), columns);
    }

    /// <summary>Tables RetrieveTotalRecordCount can count and a person would ask about.</summary>
    internal static bool IsCountable(InventoryTable t) =>
        !t.IsPrivate && t.TableType is not ("Virtual" or "Elastic") &&
        (t.IsValidForAdvancedFind || t.IsCustom || AlwaysCounted.Contains(t.LogicalName));

    private async Task EnvironmentDataAsync(StringBuilder md, IReadOnlyDictionary<string, long> counts, CancellationToken ct)
    {
        md.AppendLine().AppendLine("## Environment-wide").AppendLine();

        var total = counts.Values.Sum();
        md.AppendLine($"**Records in every counted table:** {total:N0} across {counts.Count:N0} tables.").AppendLine();
        Metric("Data volume", "Records in every table (environment)", total, $"{counts.Count:N0} tables counted; a snapshot that can be a day behind");

        var files = new List<string[]>();

        var notes = await _client.AggregateAsync("annotation", "annotations", "annotationid", [],
            "<condition attribute='isdocument' operator='eq' value='1'/>", sumColumn: "filesize", ct: ct).ConfigureAwait(false);
        files.Add(Attachments("Note attachments", notes));

        IReadOnlyList<AggregateRow> mime;
        try
        {
            mime = await _client.AggregateAsync("activitymimeattachment", "activitymimeattachments", "activitymimeattachmentid", [],
                sumColumn: "filesize", ct: ct).ConfigureAwait(false);
        }
        catch (DataverseException ex) when (!DataverseClient.IsAggregateLimit(ex))
        {
            // Not every version has createdon on email attachments; without a period nothing is halved.
            mime = await _client.AggregateAsync("activitymimeattachment", "activitymimeattachments", "activitymimeattachmentid", [],
                sumColumn: "filesize", dateColumn: null, ct: ct).ConfigureAwait(false);
        }
        files.Add(Attachments("Email attachments", mime));

        md.AppendLine("**Attachments** stored in Dataverse").AppendLine();
        Markdown.Table(md, ["Kind", "Files", "MB"], files, numeric: [1, 2]);
        foreach (var f in files) Metric("Data volume", f[0], $"{f[1]} files", $"{f[2]} MB");

        if (counts.TryGetValue("sharepointdocumentlocation", out var locations))
        {
            md.AppendLine().AppendLine($"**SharePoint document locations:** {locations:N0} - documents held in SharePoint, not Dataverse file storage.");
            Metric("Data volume", "SharePoint document locations", locations, "documents held in SharePoint, not Dataverse file storage");
        }
    }

    private static string[] Attachments(string kind, IReadOnlyList<AggregateRow> rows) =>
        [kind, $"{rows.Sum(r => r.Count):N0}", $"{Math.Round(rows.Sum(r => r.Sum) / 1048576m, 1):N1}"];

    // ---------------------------------------------------------------- execution volumes

    public async Task ExecutionVolumesPageAsync(List<DocPage> pages, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-ExecutionWindowDays);
        var processes = await ProcessesAsync(ct).ConfigureAwait(false);
        var md = new StringBuilder().AppendLine($"# Execution volumes - last {ExecutionWindowDays} days").AppendLine();

        // A classic workflow runs as its activation - a copy with its own id and the definition's name.
        var classic = processes.Where(p => p.Category != CloudFlowCategory).ToList();
        var classicIds = classic.Select(p => p.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var classicNames = classic.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        IReadOnlyList<AggregateRow> runs = classic.Count == 0
            ? []
            : await _client.AggregateAsync("asyncoperation", "asyncoperations", "asyncoperationid", ["workflowactivationid", "statuscode"],
                "<condition attribute='operationtype' operator='eq' value='10'/>", from: since, ct: ct).ConfigureAwait(false);
        var byProcess = runs.Where(r => classicIds.Contains(r.Keys[0]) || classicNames.Contains(r.Labels[0]))
            .GroupBy(r => r.Labels[0], StringComparer.CurrentCultureIgnoreCase)
            .Select(g => Outcomes(g.Key, g, succeeded: r => r.Keys[1] == "30", failed: r => r.Keys[1] == "31"))
            .OrderByDescending(o => o.Runs).ToList();

        md.AppendLine("**Classic workflow runs per process** - system jobs, so successful runs that were deleted are not counted").AppendLine();
        Markdown.Table(md, ["Process", "Runs", "Succeeded", "Failed", "Waiting or other"], byProcess.Select(o => o.Cells), numeric: [1, 2, 3, 4]);
        Metric("Execution volume", $"Classic workflow runs - last {ExecutionWindowDays} days", byProcess.Sum(o => o.Runs), $"{byProcess.Count:N0} processes ran");

        var flows = processes.Where(p => p.Category == CloudFlowCategory).ToDictionary(p => p.Id.ToString(), StringComparer.OrdinalIgnoreCase);
        if (flows.Count > 0)
        {
            var (flowRuns, truncated) = await _client.GetFlowRunCountsAsync(since, ct).ConfigureAwait(false);
            var byFlow = flowRuns.Where(r => flows.ContainsKey(r.Keys[0]))
                .GroupBy(r => r.Keys[0], StringComparer.OrdinalIgnoreCase)
                .Select(g => Outcomes(flows[g.Key].Name, g,
                    succeeded: r => r.Keys[1].Equals("Succeeded", StringComparison.OrdinalIgnoreCase),
                    failed: r => r.Keys[1].Equals("Failed", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(o => o.Runs).ToList();

            md.AppendLine().AppendLine("**Cloud flow runs per flow** - from the flowrun table, which holds solution-aware flows' runs for about 28 days").AppendLine();
            Markdown.Table(md, ["Flow", "Runs", "Succeeded", "Failed", "Other"], byFlow.Select(o => o.Cells), numeric: [1, 2, 3, 4]);
            if (truncated) md.AppendLine().AppendLine($"_Only the first {DataverseClient.FlowRunScanLimit:N0} runs were read - these counts are a lower bound._");

            Metric("Execution volume", $"Cloud flow runs - last {ExecutionWindowDays} days", byFlow.Sum(o => o.Runs),
                $"{byFlow.Count:N0} flows ran{(truncated ? "; a lower bound" : string.Empty)}");
        }

        if (_environmentWide)
        {
            var jobs = await _client.AggregateAsync("asyncoperation", "asyncoperations", "asyncoperationid", ["operationtype", "statuscode"],
                from: since, ct: ct).ConfigureAwait(false);
            var byType = jobs.GroupBy(j => j.Labels[0])
                .Select(g => Outcomes(g.Key, g, succeeded: r => r.Keys[1] == "30", failed: r => r.Keys[1] == "31"))
                .OrderByDescending(o => o.Runs).ToList();

            md.AppendLine().AppendLine("## Environment-wide").AppendLine()
                .AppendLine("**System jobs by type** - a lower bound, since successful jobs are often deleted automatically").AppendLine();
            Markdown.Table(md, ["Operation type", "Jobs", "Succeeded", "Failed", "Other"], byType.Select(o => o.Cells), numeric: [1, 2, 3, 4]);
            Metric("Execution volume", $"System jobs - last {ExecutionWindowDays} days (environment)", byType.Sum(o => o.Runs),
                string.Join(", ", byType.Take(6).Select(o => $"{o.Cells[0]}: {o.Runs:N0}")));
        }

        pages.Add(new DocPage("Inventory", "Execution volumes", md.ToString()));
    }

    private sealed record Outcome(long Runs, string[] Cells);

    private static Outcome Outcomes(string name, IEnumerable<AggregateRow> rows, Func<AggregateRow, bool> succeeded, Func<AggregateRow, bool> failed)
    {
        var list = rows.ToList();
        long total = list.Sum(r => r.Count), ok = list.Where(succeeded).Sum(r => r.Count), bad = list.Where(failed).Sum(r => r.Count);
        return new Outcome(total, [name, $"{total:N0}", $"{ok:N0}", $"{bad:N0}", $"{total - ok - bad:N0}"]);
    }
}

/// <summary>Markdown tables, with cells escaped.</summary>
internal static class Markdown
{
    /// <summary>A table, or "None." when there are no rows. Columns named in <paramref name="numeric"/> align right.</summary>
    public static void Table(StringBuilder md, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, IReadOnlyCollection<int>? numeric = null)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            md.AppendLine("None.");
            return;
        }

        md.AppendLine("| " + string.Join(" | ", headers.Select(SolutionDocumenter.Cell)) + " |");
        md.AppendLine("| " + string.Join(" | ", headers.Select((_, i) => numeric?.Contains(i) == true ? "---:" : "---")) + " |");
        foreach (var row in list) md.AppendLine("| " + string.Join(" | ", row.Select(c => c.StartsWith('`') ? c : SolutionDocumenter.Cell(c))) + " |");
    }
}
