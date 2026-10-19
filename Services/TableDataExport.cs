using System.Text;

namespace PPObjectSearch.Services;

/// <summary>The rows and columns on screen in the table data window, as CSV lines or a Markdown table.</summary>
public static class TableDataExport
{
    public static IEnumerable<string> CsvLines(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows) =>
        rows.Select(r => CsvExporter.Line(r)).Prepend(CsvExporter.Line(headers));

    /// <param name="notes">Lines said under the heading: the environment, the filter, a read cut short.</param>
    public static string Markdown(
        string heading,
        IEnumerable<string> notes,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# " + heading).AppendLine();

        foreach (var note in notes) builder.AppendLine(note + "  ");
        builder.AppendLine();

        builder.AppendLine("| " + string.Join(" | ", headers.Select(Cell)) + " |");
        builder.AppendLine("|" + string.Concat(headers.Select(_ => " --- |")));
        foreach (var row in rows) builder.AppendLine("| " + string.Join(" | ", row.Select(Cell)) + " |");

        return builder.ToString();
    }

    /// <summary>A pipe would end the cell and a line break the row, so neither survives as itself.</summary>
    internal static string Cell(string? value) => string.IsNullOrEmpty(value)
        ? string.Empty
        : value.Replace("\\", "\\\\", StringComparison.Ordinal)
               .Replace("|", "\\|", StringComparison.Ordinal)
               .Replace("\r\n", "<br>", StringComparison.Ordinal)
               .Replace("\n", "<br>", StringComparison.Ordinal)
               .Replace("\r", "<br>", StringComparison.Ordinal);
}
