using System.IO;
using System.Text;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Cli;

/// <summary>
/// The command line: the read-only checks a pipeline wants before promoting a release. Nothing
/// here writes to an environment - reconciling is never offered outside the window.
/// </summary>
public sealed class CliRunner
{
    public const int Clean = 0;
    public const int Found = 1;
    public const int Usage = 2;
    public const int Failed = 3;

    public const string Help = """
        ppos - Power Platform Object Search, from the command line. Read-only.

          ppos diff --solution <name> --left <url> --right <url> [--format md|csv|json] [--out <file>]
              Components of a solution missing from one environment or the other.

          ppos compare-data --config <name> --source <url> --target <url> [--out <file.csv>]
              A saved reference-data comparison (from settings.json).

          ppos readiness --solution <name> --source <url> --target <url> [--out <file.md>] [--fail-on blocker|warning]
              Will the solution import cleanly into the target, and work there?

        Exit codes: 0 nothing found, 1 differences or blockers found, 2 usage error, 3 the run failed.

        Sign-in: set PPOS_TENANT_ID, PPOS_CLIENT_ID and PPOS_CLIENT_SECRET (or PPOS_CLIENT_CERTIFICATE,
        a .pfx path, with PPOS_CLIENT_CERTIFICATE_PASSWORD) to run as a service principal; otherwise the
        account signed in to the app is used, and a browser opens if it has to.
        """;

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly Func<string, CancellationToken, Task<DataverseClient>> _connect;
    private readonly Func<IReadOnlyList<ReferenceDataConfig>> _configurations;

    public CliRunner(
        TextWriter output,
        TextWriter error,
        Func<string, CancellationToken, Task<DataverseClient>> connect,
        Func<IReadOnlyList<ReferenceDataConfig>> configurations)
    {
        _out = output;
        _err = error;
        _connect = connect;
        _configurations = configurations;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            await _out.WriteLineAsync(Help);
            return args.Length == 0 ? Usage : Clean;
        }

        if (!TryParse(args.Skip(1).ToArray(), out var options, out var problem))
        {
            await _err.WriteLineAsync(problem);
            return Usage;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "diff" => await DiffAsync(options, ct),
                "compare-data" => await CompareDataAsync(options, ct),
                "readiness" => await ReadinessAsync(options, ct),
                _ => await UnknownAsync(args[0])
            };
        }
        catch (UsageException ex)
        {
            await _err.WriteLineAsync(ex.Message);
            return Usage;
        }
        catch (OperationCanceledException)
        {
            await _err.WriteLineAsync("Cancelled.");
            return Failed;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync("Failed: " + ex.Message);
            return Failed;
        }
    }

    private async Task<int> UnknownAsync(string verb)
    {
        await _err.WriteLineAsync($"Unknown command '{verb}'. Run 'ppos help' for the commands.");
        return Usage;
    }

    private sealed class UsageException(string message) : Exception(message);

    /// <summary>"--name value" pairs. Every option takes a value.</summary>
    internal static bool TryParse(string[] args, out Dictionary<string, string> options, out string problem)
    {
        options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        problem = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Length < 3)
            {
                problem = $"Unexpected '{args[i]}' - options are written --name value.";
                return false;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                problem = $"{args[i]} needs a value.";
                return false;
            }

            options[args[i][2..]] = args[++i];
        }

        return true;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new UsageException($"--{name} is required. Run 'ppos help' for the commands.");

    private async Task<SolutionInfo> FindSolutionAsync(DataverseClient client, string name, CancellationToken ct) =>
        (await client.GetSolutionsAsync(ct)).FirstOrDefault(s =>
            string.Equals(s.UniqueName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.FriendlyName, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new UsageException($"No solution '{name}' in {client.EnvironmentUrl}.");

    private async Task Write(Dictionary<string, string> options, string content)
    {
        if (options.TryGetValue("out", out var path))
        {
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
            await _err.WriteLineAsync($"Wrote {path}.");
        }
        else
        {
            await _out.WriteAsync(content);
        }
    }

    // ---------------------------------------------------------------- diff

    private async Task<int> DiffAsync(Dictionary<string, string> options, CancellationToken ct)
    {
        var name = Required(options, "solution");
        var leftUrl = Required(options, "left");
        var rightUrl = Required(options, "right");
        var format = options.GetValueOrDefault("format", "md").ToLowerInvariant();
        if (format is not ("md" or "csv" or "json")) throw new UsageException("--format is md, csv or json.");

        var left = await _connect(leftUrl, ct);
        var right = await _connect(rightUrl, ct);

        var leftItems = await left.GetSolutionComponentsAsync((await FindSolutionAsync(left, name, ct)).SolutionId, ct: ct);

        // A solution not yet in the right environment is a diff in its own right: everything is missing there.
        IReadOnlyList<SolutionComponentItem> rightItems;
        try
        {
            rightItems = await right.GetSolutionComponentsAsync((await FindSolutionAsync(right, name, ct)).SolutionId, ct: ct);
        }
        catch (UsageException ex)
        {
            await _err.WriteLineAsync(ex.Message + " Treating it as empty.");
            rightItems = Array.Empty<SolutionComponentItem>();
        }

        var rows = ComponentDiff.Diff(leftItems, rightItems);
        var differing = rows.Where(r => r.Status != CompareStatus.Same).ToList();

        await Write(options, format switch
        {
            "csv" => string.Join(Environment.NewLine,
                rows.Select(r => CsvExporter.Line(r.StatusLabel, r.Name, r.ComponentTypeName, r.SubType,
                        r.LeftModified?.ToString("o"), r.RightModified?.ToString("o")))
                    .Prepend(CsvExporter.Line("Status", "Name", "Type", "Sub type", "Left modified", "Right modified"))) + Environment.NewLine,
            "json" => JsonSerializer.Serialize(differing.Select(r => new
            {
                status = r.Status.ToString(), name = r.Name, type = r.ComponentTypeName, subType = r.SubType,
                objectId = (r.Left ?? r.Right)?.ObjectId
            }), new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            _ => DiffMarkdown(name, leftUrl, rightUrl, rows)
        });

        await _err.WriteLineAsync($"{differing.Count(r => r.Status == CompareStatus.OnlyInLeft):N0} only in left, " +
                                  $"{differing.Count(r => r.Status == CompareStatus.OnlyInRight):N0} only in right, " +
                                  $"{rows.Count - differing.Count:N0} in both.");
        return differing.Count == 0 ? Clean : Found;
    }

    private static string DiffMarkdown(string solution, string left, string right, IReadOnlyList<CompareRow> rows)
    {
        var md = new StringBuilder()
            .AppendLine($"# {solution}: {left} vs {right}")
            .AppendLine();

        foreach (var (status, heading) in new[] { (CompareStatus.OnlyInLeft, $"Only in {left}"), (CompareStatus.OnlyInRight, $"Only in {right}") })
        {
            var group = rows.Where(r => r.Status == status).ToList();
            md.AppendLine($"## {heading} ({group.Count:N0})").AppendLine();
            if (group.Count == 0) { md.AppendLine("None.").AppendLine(); continue; }

            md.AppendLine("| Type | Name |").AppendLine("| --- | --- |");
            foreach (var r in group) md.AppendLine($"| {SolutionDocumenter.Cell(r.ComponentTypeName)} | {SolutionDocumenter.Cell(r.Name)} |");
            md.AppendLine();
        }

        md.AppendLine($"{rows.Count(r => r.Status == CompareStatus.Same):N0} component(s) are in both.");
        return md.ToString();
    }

    // ---------------------------------------------------------------- compare-data

    private async Task<int> CompareDataAsync(Dictionary<string, string> options, CancellationToken ct)
    {
        var name = Required(options, "config");
        var config = _configurations().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                     ?? throw new UsageException($"No saved comparison named '{name}' in settings.json. Saved ones: " +
                                                 string.Join(", ", _configurations().Select(c => c.Name)));

        var source = await _connect(Required(options, "source"), ct);
        var target = await _connect(Required(options, "target"), ct);

        var result = await ReferenceDataRun.RunAsync(config, source, target, new Progress<string>(m => _err.WriteLine(m)), ct);
        var differences = result.Differences.ToList();

        var lines = new List<string> { CsvExporter.Line("Table", "Key", "Name", "Status", "Column", "Source value", "Target value") };
        foreach (var row in differences)
        {
            if (row.Differences.Count == 0)
            {
                lines.Add(CsvExporter.Line(row.EntityLogicalName, row.Key, row.Name, row.StatusLabel, null, null, null));
                continue;
            }

            foreach (var d in row.Differences)
            {
                lines.Add(CsvExporter.Line(row.EntityLogicalName, row.Key, row.Name, row.StatusLabel, d.Column.LogicalName, d.SourceValue, d.TargetValue));
            }
        }

        await Write(options, string.Join(Environment.NewLine, lines) + Environment.NewLine);

        foreach (var warning in result.Warnings) await _err.WriteLineAsync("Warning: " + warning);
        await _err.WriteLineAsync($"{result.Tables:N0} table(s), {result.Rows.Count:N0} row(s) compared, {differences.Count:N0} differ.");
        return differences.Count == 0 ? Clean : Found;
    }

    // ---------------------------------------------------------------- readiness

    private async Task<int> ReadinessAsync(Dictionary<string, string> options, CancellationToken ct)
    {
        var name = Required(options, "solution");
        var failOn = options.GetValueOrDefault("fail-on", "blocker").ToLowerInvariant();
        if (failOn is not ("blocker" or "warning")) throw new UsageException("--fail-on is blocker or warning.");

        var sourceUrl = Required(options, "source");
        var targetUrl = Required(options, "target");
        var source = await _connect(sourceUrl, ct);
        var target = await _connect(targetUrl, ct);

        var solution = await FindSolutionAsync(source, name, ct);
        var components = await source.GetSolutionComponentsAsync(solution.SolutionId, ct: ct);

        string? targetEnvironmentId = null;
        try
        {
            targetEnvironmentId = await target.GetEnvironmentIdFromDiscoveryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _err.WriteLineAsync("Connection status will not be checked: " + ex.Message);
        }

        var report = await new ReadinessCheck(source, target, target.CreatePowerAutomateClient(), targetEnvironmentId)
            .RunAsync(solution, components, sourceUrl, targetUrl, new Progress<string>(m => _err.WriteLine(m)), ct);

        await Write(options, report.ToMarkdown());
        await _err.WriteLineAsync(report.Summary);

        var failing = report.Count(ReadinessSeverity.Blocker) + (failOn == "warning" ? report.Count(ReadinessSeverity.Warning) : 0);
        return failing == 0 ? Clean : Found;
    }
}
