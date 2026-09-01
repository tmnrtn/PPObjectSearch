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

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        // Excel treats a leading =, +, - or @ as a formula; prefix with a quote to neutralise it.
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;

        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
    }
}
