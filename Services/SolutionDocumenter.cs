using System.Text;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>Which sections the documentation includes, and how it is split into files.</summary>
public sealed class DocumentationOptions
{
    public bool Tables { get; set; } = true;
    public bool Flows { get; set; } = true;
    public bool EnvironmentVariables { get; set; } = true;
    public bool PluginSteps { get; set; } = true;
    public bool SecurityRoles { get; set; } = true;
    public bool Dependencies { get; set; } = true;

    /// <summary>One Markdown file per component under a folder, with an index - for a docs repository.</summary>
    public bool FilePerComponent { get; set; }
}

/// <summary>One page of the documentation: a component, or the solution's summary.</summary>
public sealed record DocPage(string Section, string Title, string Markdown)
{
    /// <summary>A file name that survives every file system: letters, digits, dash and underscore.</summary>
    public string FileName =>
        $"{Slug(Section)}/{Slug(Title)}.md";

    internal static string Slug(string text)
    {
        var slug = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            slug.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? char.ToLowerInvariant(c) : '-');
        }

        var result = slug.ToString().Trim('-');
        while (result.Contains("--", StringComparison.Ordinal)) result = result.Replace("--", "-");
        return result.Length == 0 ? "untitled" : result;
    }
}

/// <summary>
/// A readable inventory of a solution - for a handover, an audit or a wiki: its tables, flows,
/// environment variables, plug-in steps, security roles and dependencies. Read-only, and each
/// section is read on its own, so one that cannot be read is said rather than failing the rest.
/// </summary>
public sealed class SolutionDocumenter
{
    private const int TableType = 1;
    private const int RoleType = 20;
    private const int PluginStepType = 92;
    private const int EnvironmentVariableType = 380;

    private readonly DataverseClient _client;

    public SolutionDocumenter(DataverseClient client) => _client = client;

    public async Task<IReadOnlyList<DocPage>> BuildAsync(
        SolutionInfo solution,
        IReadOnlyList<SolutionComponentItem> items,
        string environment,
        DocumentationOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var pages = new List<DocPage>();
        var problems = new List<string>();

        async Task Section(string name, Func<Task> build)
        {
            progress?.Report($"Documenting {name}...");
            try
            {
                await build().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                problems.Add($"{name}: {ex.Message}");
            }
        }

        var tables = items.Where(i => i.ComponentType == TableType).OrderBy(i => i.PrimaryLabel, StringComparer.CurrentCultureIgnoreCase).ToList();

        if (options.Tables) await Section("tables", () => TablesAsync(tables, pages, ct)).ConfigureAwait(false);
        if (options.Flows) await Section("cloud flows", () => FlowsAsync(items, pages, ct)).ConfigureAwait(false);
        if (options.EnvironmentVariables) await Section("environment variables", () => VariablesAsync(items, pages, ct)).ConfigureAwait(false);
        if (options.PluginSteps) await Section("plug-in steps", () => StepsAsync(items, pages, ct)).ConfigureAwait(false);
        if (options.SecurityRoles) await Section("security roles", () => RolesAsync(items, tables, pages, ct)).ConfigureAwait(false);

        string? dependencies = null;
        if (options.Dependencies)
        {
            await Section("dependencies", async () => dependencies = await DependenciesAsync(solution, ct).ConfigureAwait(false))
                .ConfigureAwait(false);
        }

        pages.Insert(0, new DocPage("index", solution.FriendlyName, Summary(solution, items, environment, dependencies, problems)));
        return pages;
    }

    /// <summary>The pages as files: one README.md holding everything, or an index plus a file per component.</summary>
    public static IReadOnlyDictionary<string, string> Render(IReadOnlyList<DocPage> pages, bool filePerComponent)
    {
        if (!filePerComponent)
        {
            var all = new StringBuilder(pages[0].Markdown);
            foreach (var group in pages.Skip(1).GroupBy(p => p.Section))
            {
                all.AppendLine().AppendLine($"## {group.Key}").AppendLine();
                foreach (var page in group) all.AppendLine(Demote(page.Markdown)).AppendLine();
            }

            return new Dictionary<string, string> { ["README.md"] = all.ToString() };
        }

        var files = new Dictionary<string, string>();
        var index = new StringBuilder(pages[0].Markdown);

        foreach (var group in pages.Skip(1).GroupBy(p => p.Section))
        {
            index.AppendLine().AppendLine($"## {group.Key}").AppendLine();
            foreach (var page in group)
            {
                var name = page.FileName;
                var n = 2;
                while (files.ContainsKey(name))
                {
                    name = page.FileName[..^3] + $"-{n}.md";
                    n++;
                }

                files[name] = page.Markdown;
                index.AppendLine($"- [{page.Title}]({name})");
            }
        }

        files["README.md"] = index.ToString();
        return files;
    }

    /// <summary>A page's own "# Title" becomes "### Title" inside the single file.</summary>
    private static string Demote(string markdown) =>
        string.Join("\n", markdown.Replace("\r\n", "\n").Split('\n').Select(l => l.StartsWith('#') ? "##" + l : l));

    private static string Summary(
        SolutionInfo solution, IReadOnlyList<SolutionComponentItem> items, string environment,
        string? dependencies, IReadOnlyList<string> problems)
    {
        var md = new StringBuilder()
            .AppendLine($"# {solution.FriendlyName}")
            .AppendLine()
            .AppendLine("| | |")
            .AppendLine("| --- | --- |")
            .AppendLine($"| Unique name | `{solution.UniqueName}` |")
            .AppendLine($"| Version | {solution.Version} |")
            .AppendLine($"| Publisher | {Cell(solution.PublisherName)} |")
            .AppendLine($"| Managed | {(solution.IsManaged ? "Yes" : "No")} |")
            .AppendLine($"| Read from | {Cell(environment)} |")
            .AppendLine($"| Generated | {DateTimeOffset.Now:yyyy-MM-dd HH:mm} |")
            .AppendLine()
            .AppendLine("## Components")
            .AppendLine()
            .AppendLine("| Type | Count |")
            .AppendLine("| --- | ---: |");

        foreach (var type in items.GroupBy(i => i.ComponentTypeName).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            md.AppendLine($"| {Cell(type.Key)} | {type.Count():N0} |");
        }

        if (dependencies is not null) md.AppendLine().Append(dependencies);

        if (problems.Count > 0)
        {
            md.AppendLine().AppendLine("## Not documented").AppendLine();
            foreach (var problem in problems) md.AppendLine($"- {problem}");
        }

        return md.ToString();
    }

    internal static string Cell(string? text) =>
        (text ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private async Task TablesAsync(IReadOnlyList<SolutionComponentItem> tables, List<DocPage> pages, CancellationToken ct)
    {
        foreach (var item in tables)
        {
            var table = await _client.GetTableIdentityAsync(item.ObjectId, item.SchemaName ?? item.Name, ct).ConfigureAwait(false);
            if (table is null) continue;

            var md = new StringBuilder().AppendLine($"# {item.PrimaryLabel}").AppendLine().AppendLine($"`{table.LogicalName}`").AppendLine();

            // Custom columns only - those carry a publisher prefix; system columns are the same on
            // every table and say nothing about this one.
            var columns = (await _client.GetTableColumnsAsync(table, ct).ConfigureAwait(false))
                .Where(c => c.Name.Contains('_'))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            md.AppendLine("**Columns**").AppendLine().AppendLine("| Column | Display name | Type and requirement |").AppendLine("| --- | --- | --- |");
            foreach (var column in columns) md.AppendLine($"| `{column.Name}` | {Cell(column.DisplayName)} | {Cell(column.Detail)} |");

            var relationships = await _client.GetTableRelationshipsAsync(table, ct).ConfigureAwait(false);
            if (relationships.Count > 0)
            {
                md.AppendLine().AppendLine("**Relationships**").AppendLine().AppendLine("| Relationship | Detail |").AppendLine("| --- | --- |");
                foreach (var r in relationships) md.AppendLine($"| `{r.Name}` | {Cell(r.Detail)} |");
            }

            var keys = await _client.GetTableKeysAsync(table, ct).ConfigureAwait(false);
            if (keys.Count > 0)
            {
                md.AppendLine().AppendLine("**Alternate keys**").AppendLine().AppendLine("| Key | Columns |").AppendLine("| --- | --- |");
                foreach (var k in keys) md.AppendLine($"| `{k.Name}` | {Cell(k.Detail)} |");
            }

            pages.Add(new DocPage("Tables", item.PrimaryLabel, md.ToString()));
        }
    }

    private async Task FlowsAsync(IReadOnlyList<SolutionComponentItem> items, List<DocPage> pages, CancellationToken ct)
    {
        foreach (var item in items.Where(i => Switchable.KindOf(i) == SwitchableKind.CloudFlow)
                                  .OrderBy(i => i.PrimaryLabel, StringComparer.CurrentCultureIgnoreCase))
        {
            var flow = await _client.GetCloudFlowAsync(item.ObjectId, ct).ConfigureAwait(false);
            var md = new StringBuilder().AppendLine($"# {item.PrimaryLabel}").AppendLine();

            var state = flow.IsOn switch
            {
                true => "on",
                false => "off",
                null => "unknown"
            };
            md.AppendLine($"State: {state}").AppendLine();

            if (string.IsNullOrWhiteSpace(flow.Definition))
            {
                md.AppendLine("_No definition is stored in Dataverse._");
                pages.Add(new DocPage("Cloud flows", item.PrimaryLabel, md.ToString()));
                continue;
            }

            var design = FlowDesignParser.Parse(flow.Definition);
            var trigger = design.Triggers.FirstOrDefault();
            md.AppendLine($"**Trigger:** {Cell(trigger?.Summary ?? "none")}").AppendLine();

            var connectors = Nodes(design).Select(n => n.Connector).Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            md.AppendLine($"**Connectors:** {(connectors.Count == 0 ? "none" : string.Join(", ", connectors))}").AppendLine();
            md.AppendLine($"**Actions:** {design.ActionCount:N0}").AppendLine();

            md.AppendLine("```mermaid").AppendLine(FlowMermaidExporter.ToMermaid(design, item.PrimaryLabel).TrimEnd()).AppendLine("```");
            pages.Add(new DocPage("Cloud flows", item.PrimaryLabel, md.ToString()));
        }
    }

    /// <summary>Every node in a flow - triggers, actions, and the actions inside branches and loops.</summary>
    internal static IEnumerable<FlowNode> Nodes(FlowDesign design)
    {
        foreach (var trigger in design.Triggers) yield return trigger;
        foreach (var node in Nodes(design.Actions)) yield return node;
    }

    private static IEnumerable<FlowNode> Nodes(FlowSequence sequence)
    {
        foreach (var step in sequence.Steps)
        {
            switch (step)
            {
                case FlowActionStep action:
                    yield return action.Node;
                    foreach (var branch in action.Node.Branches)
                    {
                        foreach (var inner in Nodes(branch.Steps)) yield return inner;
                    }
                    break;

                case FlowParallelStep parallel:
                    foreach (var branch in parallel.Branches)
                    {
                        foreach (var inner in Nodes(branch)) yield return inner;
                    }
                    break;
            }
        }
    }

    private async Task VariablesAsync(IReadOnlyList<SolutionComponentItem> items, List<DocPage> pages, CancellationToken ct)
    {
        var variables = items.Where(i => i.ComponentType == EnvironmentVariableType && i.ObjectId != Guid.Empty).ToList();
        if (variables.Count == 0) return;

        var md = new StringBuilder()
            .AppendLine("# Environment variables").AppendLine()
            .AppendLine("| Schema name | Type | Default | Current value here |")
            .AppendLine("| --- | --- | --- | --- |");

        foreach (var item in variables.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var v = await _client.GetEnvironmentVariableAsync(item.ObjectId, isValueRecord: false, ct).ConfigureAwait(false);

            // A secret's value is a Key Vault reference, and still not something to publish.
            string Shown(string? value) => v.IsSecret && !string.IsNullOrEmpty(value) ? "_(secret - redacted)_" : Code(value);

            md.AppendLine($"| `{v.SchemaName}` | {Cell(v.TypeLabel)} | {Shown(v.DefaultValue)} | {(v.HasCurrentValue ? Shown(v.CurrentValue) : "_not set_")} |");
        }

        pages.Add(new DocPage("Environment variables", "Environment variables", md.ToString()));
    }

    private static string Code(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var oneLine = value.Replace("\r", " ").Replace("\n", " ");
        if (oneLine.Length > 120) oneLine = oneLine[..117] + "...";
        return $"`{oneLine.Replace("`", "'").Replace("|", "\\|")}`";
    }

    private async Task StepsAsync(IReadOnlyList<SolutionComponentItem> items, List<DocPage> pages, CancellationToken ct)
    {
        var ids = items.Where(i => i.ComponentType == PluginStepType && i.ObjectId != Guid.Empty).Select(i => i.ObjectId).ToList();
        if (ids.Count == 0) return;

        var steps = await _client.GetPluginStepsAsync(ids, ct).ConfigureAwait(false);
        var md = new StringBuilder()
            .AppendLine("# Plug-in steps").AppendLine()
            .AppendLine("| Step | Message | Table | Stage | Mode | Rank | Filtering attributes |")
            .AppendLine("| --- | --- | --- | --- | --- | ---: | --- |");

        foreach (var s in steps.OrderBy(s => s.Table).ThenBy(s => s.Message).ThenBy(s => s.Rank))
        {
            md.AppendLine($"| {Cell(s.Name)} | {Cell(s.Message)} | {Cell(s.Table)} | {Cell(s.Stage)} | {Cell(s.Mode)} | {s.Rank} | {Cell(s.FilteringAttributes)} |");
        }

        pages.Add(new DocPage("Plug-in steps", "Plug-in steps", md.ToString()));
    }

    private async Task RolesAsync(
        IReadOnlyList<SolutionComponentItem> items, IReadOnlyList<SolutionComponentItem> tables, List<DocPage> pages, CancellationToken ct)
    {
        // Privileges are named after the table's schema name, so the solution's tables are matched by it.
        var solutionTables = tables.Select(t => t.SchemaName ?? t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var role in items.Where(i => i.ComponentType == RoleType).OrderBy(i => i.PrimaryLabel, StringComparer.CurrentCultureIgnoreCase))
        {
            var (rows, _) = PrivilegeMatrix.Build(await _client.GetRolePrivilegesAsync(role.ObjectId, ct).ConfigureAwait(false));
            var shown = solutionTables.Count == 0 ? rows : rows.Where(r => solutionTables.Contains(r.Table)).ToList();

            static string D(PrivilegeDepth d) => d == PrivilegeDepth.None ? string.Empty : PrivilegeMatrix.DepthLabel(d);

            var md = new StringBuilder()
                .AppendLine($"# {role.PrimaryLabel}").AppendLine()
                .AppendLine(solutionTables.Count == 0 ? "Every table the role reaches:" : "Privileges on this solution's tables:").AppendLine()
                .AppendLine("| Table | Create | Read | Write | Delete | Append | Append to | Assign | Share |")
                .AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

            foreach (var r in shown)
            {
                md.AppendLine($"| {r.Table} | {D(r.Create)} | {D(r.Read)} | {D(r.Write)} | {D(r.Delete)} | {D(r.Append)} | {D(r.AppendTo)} | {D(r.Assign)} | {D(r.Share)} |");
            }

            pages.Add(new DocPage("Security roles", role.PrimaryLabel, md.ToString()));
        }
    }

    private async Task<string> DependenciesAsync(SolutionInfo solution, CancellationToken ct)
    {
        var missing = await _client.GetMissingDependenciesAsync(solution.UniqueName, ct).ConfigureAwait(false);

        var md = new StringBuilder().AppendLine("## Depends on").AppendLine();
        if (missing.Count == 0)
        {
            md.AppendLine("Nothing outside the solution beyond what every environment has.");
            return md.ToString();
        }

        md.AppendLine("Components the solution needs but does not contain - they must already be in an environment it is imported into:")
          .AppendLine()
          .AppendLine("| Type | Component id | Needed by (type) |")
          .AppendLine("| --- | --- | --- |");

        foreach (var group in missing.GroupBy(m => m.RequiredId))
        {
            var first = group.First();
            md.AppendLine($"| {ComponentTypes.GetName(first.RequiredType)} | `{first.RequiredId}` | " +
                          $"{string.Join(", ", group.Select(m => ComponentTypes.GetName(m.DependentType)).Distinct())} |");
        }

        return md.ToString();
    }
}
