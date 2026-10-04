using System.Globalization;
using System.IO;
using System.Text;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

public static class CsvExporter
{
    private static readonly string[] Headers =
    {
        "Name", "Display name", "Schema name", "Object type", "Sub type", "Related table",
        "State", "Customizable", "Owner", "Created", "Modified", "Object id", "Maker portal link"
    };

    public static void Write(string path, IEnumerable<SolutionComponentItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", Headers.Select(Escape)));

        foreach (var item in items)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                item.Name,
                item.DisplayName,
                item.SchemaName,
                item.ComponentTypeName,
                item.SubType,
                item.PrimaryEntityName,
                item.ManagedLabel,
                item.IsCustomizable ? "Yes" : "No",
                item.Owner,
                Format(item.CreatedOn),
                Format(item.ModifiedOn),
                item.ObjectId == Guid.Empty ? null : item.ObjectId.ToString(),
                item.MakerUrl
            }.Select(Escape)));
        }

        // UTF-8 with BOM so Excel opens non-ASCII names correctly.
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Format(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Writes lines already made with <see cref="Line"/>, as UTF-8 with a BOM so Excel opens
    /// non-ASCII names correctly.
    /// </summary>
    public static void WriteLines(string path, IEnumerable<string> lines) =>
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    /// <summary>One CSV line, every field escaped by <see cref="Escape"/>.</summary>
    public static string Line(IEnumerable<string?> values) => string.Join(",", values.Select(Escape));

    public static string Line(params string?[] values) => Line((IEnumerable<string?>)values);

    /// <summary>
    /// One field. Every export goes through here so they cannot drift apart again.
    ///
    /// Component names, row values and error text are written by other people, and a spreadsheet
    /// reads a field that starts with = + - @ (or a tab or carriage return) as a formula - so
    /// =HYPERLINK(...) in a display name would run when the file is opened. Such a field is
    /// prefixed with a quote - leading spaces do not hide it. Fields holding a comma, quote or line
    /// break are quoted, so a multi-line value stays in its own cell.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var first = value.TrimStart(' ');
        if (first.Length > 0 && first[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;

        return value.IndexOfAny(QuoteTriggers) >= 0
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
    }

    private static readonly char[] QuoteTriggers = { ',', '"', '\n', '\r' };
}
