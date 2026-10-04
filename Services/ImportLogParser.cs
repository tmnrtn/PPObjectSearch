using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace PPObjectSearch.Services;

/// <summary>One component's line in an import log.</summary>
public sealed class ImportLogRow
{
    /// <summary>What kind of component, from the section it is in: "entities", "optionSets", "nodes"...</summary>
    public required string Section { get; init; }
    public required string Name { get; init; }
    public Guid? Id { get; init; }

    /// <summary>"success", "warning" or "failure", as the log says.</summary>
    public required string Result { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorText { get; init; }

    /// <summary>When the component finished, and how long it took since the one before it.</summary>
    public DateTimeOffset? At { get; init; }
    public TimeSpan? Duration { get; init; }

    public bool IsFailure => Result.Equals("failure", StringComparison.OrdinalIgnoreCase);
    public bool IsWarning => Result.Equals("warning", StringComparison.OrdinalIgnoreCase);
    public bool IsProblem => IsFailure || IsWarning;

    public string ResultLabel => IsFailure ? "Failure" : IsWarning ? "Warning" : "Success";
    public string DurationLabel => Duration is { } d ? d.TotalSeconds < 1 ? "<1 s" : $"{d.TotalSeconds:N0} s" : string.Empty;
}

/// <summary>An import log as a whole.</summary>
public sealed class ImportLog
{
    public List<ImportLogRow> Rows { get; } = new();
    public double? Progress { get; init; }
    public bool Processed { get; init; }

    public int Failures => Rows.Count(r => r.IsFailure);
    public int Warnings => Rows.Count(r => r.IsWarning);

    /// <summary>Failures and warnings as Markdown, for a ticket or a chat message.</summary>
    public string ProblemsAsMarkdown(string solution)
    {
        static string Cell(string? text) => (text ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

        var md = new StringBuilder()
            .AppendLine($"### Import of {solution}: {Failures:N0} failure(s), {Warnings:N0} warning(s)")
            .AppendLine()
            .AppendLine("| Result | Type | Component | Error |")
            .AppendLine("| --- | --- | --- | --- |");

        foreach (var row in Rows.Where(r => r.IsProblem).OrderBy(r => r.IsWarning))
        {
            md.AppendLine($"| {row.ResultLabel} | {Cell(row.Section)} | {Cell(row.Name)} | {Cell(row.ErrorText)} |");
        }

        return md.ToString();
    }
}

/// <summary>
/// Reads importjob.data. The log is a tree of sections ("entities", "optionSets", "nodes"...),
/// each holding components that carry a &lt;result&gt;; this reads every component with a result,
/// whatever its section, so sections the log adds later still appear.
/// </summary>
public static class ImportLogParser
{
    public static ImportLog Parse(string xml)
    {
        var root = XElement.Parse(xml);

        var log = new ImportLog
        {
            Progress = double.TryParse((string?)root.Attribute("progress"), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
            Processed = string.Equals((string?)root.Attribute("processed"), "true", StringComparison.OrdinalIgnoreCase)
        };

        DateTimeOffset? previous = Date((string?)root.Attribute("start"));

        foreach (var result in root.Descendants("result"))
        {
            var component = result.Parent;
            if (component is null || component == root) continue;

            var at = Ticks((string?)result.Attribute("datetimeticks")) ?? Date((string?)result.Attribute("datetime"));
            var id = (string?)component.Attribute("id");

            log.Rows.Add(new ImportLogRow
            {
                Section = component.Parent?.Name.LocalName ?? component.Name.LocalName,
                Name = (string?)component.Attribute("LocalizedName") ?? (string?)component.Attribute("name")
                       ?? (string?)component.Attribute("OriginalName") ?? id ?? component.Name.LocalName,
                Id = Guid.TryParse(id?.Trim('{', '}'), out var guid) ? guid : null,
                Result = (string?)result.Attribute("result") ?? "success",
                ErrorCode = (string?)result.Attribute("errorcode") is { Length: > 0 } code && code != "0" ? code : null,
                ErrorText = (string?)result.Attribute("errortext") is { Length: > 0 } text ? text : null,
                At = at,
                Duration = at is { } now && previous is { } before && now >= before ? now - before : null
            });

            if (at is not null) previous = at;
        }

        return log;
    }

    private static DateTimeOffset? Ticks(string? value) =>
        long.TryParse(value, out var ticks) && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    private static DateTimeOffset? Date(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}
