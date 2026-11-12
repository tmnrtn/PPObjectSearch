using System.Globalization;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>
/// An audit history as a table - one line per column changed, with its old and new values whole -
/// for <see cref="TableDataExport"/> to write as CSV or Markdown.
/// </summary>
public static class AuditHistoryExport
{
    /// <param name="withRow">Whether the history covers more than one row, so each line says which.</param>
    public static IReadOnlyList<string> Headers(bool withRow) => withRow
        ? ["When", "By", "Action", "Row", "Column", "Logical name", "Old value", "New value"]
        : ["When", "By", "Action", "Column", "Logical name", "Old value", "New value"];

    /// <summary>A change with no columns - a delete, a share - is still a line, with the column cells empty.</summary>
    public static List<IReadOnlyList<string?>> Rows(IEnumerable<AuditRecord> records, bool withRow)
    {
        var rows = new List<IReadOnlyList<string?>>();

        foreach (var record in records)
        {
            var when = record.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            IEnumerable<AuditChange?> changes = record.Changes.Count > 0 ? record.Changes.Cast<AuditChange?>() : [null];

            foreach (var change in changes)
            {
                var line = new List<string?> { when, record.By, record.Action };
                if (withRow) line.Add(record.Row);
                line.AddRange([change?.ColumnLabel, change?.Column, change?.OldValue, change?.NewValue]);
                rows.Add(line);
            }
        }

        return rows;
    }
}
