using System.Text;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.PowerAutomate;

namespace PPObjectSearch.Services;

public enum ReadinessSeverity
{
    /// <summary>The import will fail, or what it brings will not work.</summary>
    Blocker,

    /// <summary>Likely to cause trouble after import; worth checking first.</summary>
    Warning,

    /// <summary>Worth knowing; nothing to fix.</summary>
    Info
}

/// <summary>One thing the readiness check found.</summary>
public sealed class ReadinessFinding
{
    public required ReadinessSeverity Severity { get; init; }
    public required string Area { get; init; }
    public required string Component { get; init; }
    public required string Message { get; init; }

    /// <summary>The solution's own row for the component, for opening its details.</summary>
    public SolutionComponentItem? Item { get; init; }

    public string SeverityLabel => Severity switch
    {
        ReadinessSeverity.Blocker => "Blocker",
        ReadinessSeverity.Warning => "Warning",
        _ => "Info"
    };
}

public sealed class ReadinessReport
{
    public required string Solution { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    public List<ReadinessFinding> Findings { get; } = new();

    /// <summary>Checks that could not run, so their silence is not read as a pass.</summary>
    public List<string> NotChecked { get; } = new();

    public int Count(ReadinessSeverity severity) => Findings.Count(f => f.Severity == severity);

    public string Summary =>
        $"{Count(ReadinessSeverity.Blocker):N0} blocker(s), {Count(ReadinessSeverity.Warning):N0} warning(s), " +
        $"{Count(ReadinessSeverity.Info):N0} note(s)" +
        (NotChecked.Count > 0 ? $"; {NotChecked.Count:N0} check(s) could not run" : string.Empty) + ".";

    /// <summary>The report as Markdown, for a pull request or a change ticket.</summary>
    public string ToMarkdown()
    {
        var md = new StringBuilder()
            .AppendLine($"# Readiness: {Solution}")
            .AppendLine()
            .AppendLine($"**{Source}** → **{Target}**, checked {CheckedAt:yyyy-MM-dd HH:mm}.")
            .AppendLine()
            .AppendLine(Summary)
            .AppendLine();

        foreach (var severity in new[] { ReadinessSeverity.Blocker, ReadinessSeverity.Warning, ReadinessSeverity.Info })
        {
            var findings = Findings.Where(f => f.Severity == severity).ToList();
            if (findings.Count == 0) continue;

            md.AppendLine($"## {severity switch { ReadinessSeverity.Blocker => "Blockers", ReadinessSeverity.Warning => "Warnings", _ => "Notes" }}")
              .AppendLine()
              .AppendLine("| Area | Component | Finding |")
              .AppendLine("| --- | --- | --- |");

            foreach (var f in findings) md.AppendLine($"| {Cell(f.Area)} | {Cell(f.Component)} | {Cell(f.Message)} |");
            md.AppendLine();
        }

        if (NotChecked.Count > 0)
        {
            md.AppendLine("## Not checked").AppendLine();
            foreach (var reason in NotChecked) md.AppendLine($"- {reason}");
            md.AppendLine();
        }

        return md.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}

/// <summary>
/// Will this solution import cleanly into the target, and work once it is there? Each area is
/// checked on its own; one that cannot be checked is reported as such and the rest still run.
/// </summary>
public sealed class ReadinessCheck
{
    /// <summary>Layers are one request per component; past this the rest are left unchecked, and the report says so.</summary>
    public const int MaxLayerChecks = 400;

    private const int PluginAssemblyType = 91;
    private const int EnvironmentVariableType = 380;

    private readonly DataverseClient _source;
    private readonly DataverseClient _target;
    private readonly PowerAutomateClient? _targetFlows;
    private readonly string? _targetEnvironmentId;

    public ReadinessCheck(DataverseClient source, DataverseClient target, PowerAutomateClient? targetFlows, string? targetEnvironmentId)
    {
        _source = source;
        _target = target;
        _targetFlows = targetFlows;
        _targetEnvironmentId = targetEnvironmentId;
    }

    public async Task<ReadinessReport> RunAsync(
        SolutionInfo solution,
        IReadOnlyList<SolutionComponentItem> components,
        string sourceName,
        string targetName,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var report = new ReadinessReport { Solution = solution.DisplayLabel, Source = sourceName, Target = targetName };
        var byId = components.Where(c => c.ObjectId != Guid.Empty)
            .GroupBy(c => c.ObjectId).ToDictionary(g => g.Key, g => g.First());

        async Task Step(string what, Func<Task> check)
        {
            progress?.Report($"Checking {what}...");
            try
            {
                await check().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.NotChecked.Add($"{what}: {ex.Message}");
            }
        }

        await Step("the solution version", () => CheckVersionAsync(solution, report, ct)).ConfigureAwait(false);
        await Step("missing dependencies", () => CheckDependenciesAsync(solution, byId, report, ct)).ConfigureAwait(false);
        await Step("environment variables", () => CheckEnvironmentVariablesAsync(components, report, ct)).ConfigureAwait(false);
        await Step("connection references", () => CheckConnectionReferencesAsync(components, report, ct)).ConfigureAwait(false);
        await Step("flows", () => CheckFlowsAsync(components, report, ct)).ConfigureAwait(false);
        await Step("plug-in assemblies", () => CheckPluginAssembliesAsync(components, report, ct)).ConfigureAwait(false);
        await Step("unmanaged layers in the target", () => CheckLayersAsync(components, report, ct)).ConfigureAwait(false);

        return report;
    }

    private async Task CheckVersionAsync(SolutionInfo solution, ReadinessReport report, CancellationToken ct)
    {
        var installed = (await _target.GetSolutionsAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(s => string.Equals(s.UniqueName, solution.UniqueName, StringComparison.OrdinalIgnoreCase));

        if (installed is null)
        {
            report.Findings.Add(Finding(ReadinessSeverity.Info, "Version", solution.UniqueName,
                $"Not in the target yet - this would be its first install (version {solution.Version ?? "unknown"})."));
            return;
        }

        if (!installed.IsManaged)
        {
            report.Findings.Add(Finding(ReadinessSeverity.Warning, "Version", solution.UniqueName,
                "The target has this solution unmanaged - a managed import of it is refused. Import it unmanaged, or remove it first."));
        }

        var compared = CompareVersions(solution.Version, installed.Version);
        if (compared < 0)
        {
            report.Findings.Add(Finding(ReadinessSeverity.Blocker, "Version", solution.UniqueName,
                $"The target already has version {installed.Version}, newer than {solution.Version}. Importing an older version is refused."));
        }
        else if (compared == 0)
        {
            report.Findings.Add(Finding(ReadinessSeverity.Info, "Version", solution.UniqueName,
                $"The target already has version {installed.Version} - the same version."));
        }
        else
        {
            report.Findings.Add(Finding(ReadinessSeverity.Info, "Version", solution.UniqueName,
                $"Upgrades the target from {installed.Version} to {solution.Version}."));
        }
    }

    /// <summary>Negative when <paramref name="a"/> is older, zero when the same, null-safe.</summary>
    public static int CompareVersions(string? a, string? b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)
            ? va.CompareTo(vb)
            : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

    private async Task CheckDependenciesAsync(
        SolutionInfo solution, IReadOnlyDictionary<Guid, SolutionComponentItem> byId, ReadinessReport report, CancellationToken ct)
    {
        var missing = await _source.GetMissingDependenciesAsync(solution.UniqueName, ct).ConfigureAwait(false);
        if (missing.Count == 0) return;

        var present = await _target.GetExistingComponentIdsAsync(missing.Select(m => m.RequiredId), ct).ConfigureAwait(false);

        foreach (var group in missing.Where(m => !present.Contains(m.RequiredId)).GroupBy(m => m.RequiredId))
        {
            var first = group.First();
            var neededBy = group
                .Select(m => byId.TryGetValue(m.DependentId, out var item) ? item.PrimaryLabel : $"{ComponentTypes.GetName(m.DependentType)} {m.DependentId}")
                .Distinct()
                .ToList();

            report.Findings.Add(new ReadinessFinding
            {
                Severity = ReadinessSeverity.Blocker,
                Area = "Dependencies",
                Component = $"{ComponentTypes.GetName(first.RequiredType)} {first.RequiredId}",
                Message = $"Needed by {string.Join(", ", neededBy.Take(5))}{(neededBy.Count > 5 ? $" and {neededBy.Count - 5} more" : string.Empty)}, " +
                          "but neither in the solution nor in the target. Import will fail until it is there.",
                Item = byId.TryGetValue(first.DependentId, out var dependent) ? dependent : null
            });
        }
    }

    private async Task CheckEnvironmentVariablesAsync(
        IReadOnlyList<SolutionComponentItem> components, ReadinessReport report, CancellationToken ct)
    {
        foreach (var item in components.Where(c => c.ComponentType == EnvironmentVariableType && c.ObjectId != Guid.Empty))
        {
            var source = await _source.GetEnvironmentVariableAsync(item.ObjectId, isValueRecord: false, ct).ConfigureAwait(false);
            var target = await _target.GetEnvironmentVariableBySchemaNameAsync(source.SchemaName, ct).ConfigureAwait(false);

            foreach (var finding in EvaluateVariable(source, target)) report.Findings.Add(WithItem(finding, item));
        }
    }

    /// <summary>What to say about one variable, given it in the source and (if there) in the target.</summary>
    public static IEnumerable<ReadinessFinding> EvaluateVariable(EnvironmentVariableInfo source, EnvironmentVariableInfo? target)
    {
        // The default travels with the solution; the current value does not.
        var defaultAfterImport = source.DefaultValue;
        var valueInTarget = target is { HasCurrentValue: true } ? target.CurrentValue : null;

        if (string.IsNullOrEmpty(valueInTarget) && string.IsNullOrEmpty(defaultAfterImport))
        {
            yield return Finding(ReadinessSeverity.Warning, "Environment variables", source.SchemaName,
                target is null
                    ? "New to the target, with no default value - it will have no value until one is set (the deployment settings file can set it)."
                    : "No value in the target and no default - anything that reads it gets nothing.");
        }

        if (source is { HasCurrentValue: true, IsSecret: false } && valueInTarget is not null &&
            string.Equals(source.CurrentValue, valueInTarget, StringComparison.Ordinal))
        {
            yield return Finding(ReadinessSeverity.Warning, "Environment variables", source.SchemaName,
                "The target's value is the same as the source's - check it is not a source-specific value, such as a dev URL.");
        }
    }

    private async Task CheckConnectionReferencesAsync(
        IReadOnlyList<SolutionComponentItem> components, ReadinessReport report, CancellationToken ct)
    {
        var items = components.Where(DataverseClient.IsConnectionReference).ToList();
        if (items.Count == 0) return;

        var source = await _source.GetConnectionReferencesAsync(items.Select(i => i.ObjectId), ct).ConfigureAwait(false);
        var target = (await _target.GetConnectionReferencesByNameAsync(source.Select(s => s.LogicalName), ct).ConfigureAwait(false))
            .GroupBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<ConnectionInfo>? connections = null;
        if (target.Values.Any(t => t.HasConnection) && _targetFlows is not null && !string.IsNullOrWhiteSpace(_targetEnvironmentId))
        {
            try
            {
                connections = await _targetFlows.GetConnectionsAsync(_targetEnvironmentId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.NotChecked.Add("connection status in the target: " + ex.Message);
            }
        }

        foreach (var reference in source)
        {
            var item = items.FirstOrDefault(i => i.ObjectId == reference.Id);
            target.TryGetValue(reference.LogicalName, out var there);
            var connection = there?.ConnectionId is { Length: > 0 } id
                ? connections?.FirstOrDefault(c => string.Equals(c.Name, id, StringComparison.OrdinalIgnoreCase))
                : null;

            if (EvaluateReference(reference, there, connection, connections is not null) is { } finding)
            {
                report.Findings.Add(WithItem(finding, item));
            }
        }
    }

    public static ReadinessFinding? EvaluateReference(
        ConnectionReferenceInfo source, ConnectionReferenceInfo? target, ConnectionInfo? connection, bool connectionsKnown)
    {
        const string area = "Connection references";

        if (target is null)
        {
            return Finding(ReadinessSeverity.Warning, area, source.Label,
                $"New to the target ({source.Connector}) - it arrives with no connection, so flows using it stay off until one is bound.");
        }

        if (!target.HasConnection)
        {
            return Finding(ReadinessSeverity.Warning, area, source.Label,
                $"No connection bound in the target ({source.Connector}) - flows using it cannot turn on.");
        }

        if (connection is { IsConnected: false })
        {
            return Finding(ReadinessSeverity.Blocker, area, source.Label,
                $"Bound in the target to a connection that is in error: {connection.Status}" +
                (string.IsNullOrWhiteSpace(connection.StatusMessage) ? "." : $" - {connection.StatusMessage}"));
        }

        if (connection is null && connectionsKnown)
        {
            return Finding(ReadinessSeverity.Info, area, source.Label,
                "Bound in the target to a connection not shared with you, so its status was not checked.");
        }

        return null;
    }

    private async Task CheckFlowsAsync(IReadOnlyList<SolutionComponentItem> components, ReadinessReport report, CancellationToken ct)
    {
        var flows = components.Where(c => Switchable.KindOf(c) == SwitchableKind.CloudFlow).ToList();
        if (flows.Count == 0) return;

        var ids = flows.Select(f => f.ObjectId).ToList();
        var source = await _source.GetFlowOwnershipAsync(ids, ct).ConfigureAwait(false);
        var target = await _target.GetFlowOwnershipAsync(ids, ct).ConfigureAwait(false);

        foreach (var flow in flows)
        {
            source.TryGetValue(flow.ObjectId, out var inSource);
            target.TryGetValue(flow.ObjectId, out var inTarget);

            if (inSource is { IsOn: false })
            {
                report.Findings.Add(WithItem(Finding(ReadinessSeverity.Info, "Flows", flow.PrimaryLabel,
                    "Off in the source."), flow));
            }

            if (inTarget is { OwnerDisabled: true })
            {
                report.Findings.Add(WithItem(Finding(ReadinessSeverity.Warning, "Flows", flow.PrimaryLabel,
                    $"Owned in the target by {inTarget.OwnerName ?? "a user"}, who is disabled - the flow cannot be turned on or run as them. " +
                    "Change its owner before or after import."), flow));
            }
        }
    }

    private async Task CheckPluginAssembliesAsync(
        IReadOnlyList<SolutionComponentItem> components, ReadinessReport report, CancellationToken ct)
    {
        var assemblies = components.Where(c => c.ComponentType == PluginAssemblyType && c.ObjectId != Guid.Empty).ToList();
        if (assemblies.Count == 0) return;

        var ids = assemblies.Select(a => a.ObjectId).ToList();
        var source = await _source.GetPluginAssemblyVersionsAsync(ids, ct).ConfigureAwait(false);
        var target = await _target.GetPluginAssemblyVersionsAsync(ids, ct).ConfigureAwait(false);

        foreach (var assembly in assemblies)
        {
            if (!source.TryGetValue(assembly.ObjectId, out var inSource) || !target.TryGetValue(assembly.ObjectId, out var inTarget)) continue;

            var compared = CompareVersions(inSource.Version, inTarget.Version);
            if (compared > 0)
            {
                report.Findings.Add(WithItem(Finding(ReadinessSeverity.Info, "Plug-ins", inSource.Name,
                    $"Older in the target ({inTarget.Version}); the import updates it to {inSource.Version}."), assembly));
            }
            else if (compared < 0)
            {
                report.Findings.Add(WithItem(Finding(ReadinessSeverity.Warning, "Plug-ins", inSource.Name,
                    $"Newer in the target ({inTarget.Version}) than in the source ({inSource.Version}) - the import would take it back."), assembly));
            }
        }
    }

    private async Task CheckLayersAsync(IReadOnlyList<SolutionComponentItem> components, ReadinessReport report, CancellationToken ct)
    {
        var present = await _target.GetExistingComponentIdsAsync(components.Select(c => c.ObjectId), ct).ConfigureAwait(false);
        var toCheck = components.Where(c => present.Contains(c.ObjectId)).ToList();
        if (toCheck.Count == 0) return;

        if (toCheck.Count > MaxLayerChecks)
        {
            report.NotChecked.Add($"unmanaged layers: only the first {MaxLayerChecks:N0} of {toCheck.Count:N0} components in the target were checked");
            toCheck = toCheck.Take(MaxLayerChecks).ToList();
        }

        var layers = await _target.GetComponentLayersBulkAsync(
            toCheck.Select(c => (c.ObjectId, c.ComponentType)).ToList(), ct: ct).ConfigureAwait(false);

        foreach (var item in toCheck)
        {
            if (!layers.TryGetValue(item.ObjectId, out var stack) || stack is null) continue;
            if (!stack.Any(l => l.IsUnmanagedLayer)) continue;

            report.Findings.Add(WithItem(Finding(ReadinessSeverity.Warning, "Unmanaged layers", item.PrimaryLabel,
                "Has unmanaged changes in the target. They sit above the managed layer, so the import's changes to it will not show."),
                item));
        }
    }

    private static ReadinessFinding Finding(ReadinessSeverity severity, string area, string component, string message) =>
        new() { Severity = severity, Area = area, Component = component, Message = message };

    private static ReadinessFinding WithItem(ReadinessFinding finding, SolutionComponentItem? item) =>
        new()
        {
            Severity = finding.Severity, Area = finding.Area, Component = finding.Component, Message = finding.Message, Item = item
        };
}
