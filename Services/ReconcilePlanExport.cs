namespace PPObjectSearch.Services;

/// <summary>
/// A reconcile plan as CSV - one line per column that would change, or one per row for a create or
/// delete - so a change can be reviewed or approved before it is applied.
/// </summary>
public static class ReconcilePlanExport
{
    public static IReadOnlyList<string> Lines(
        IEnumerable<(ReconcilePlanItem Item, bool Included, string Result)> rows,
        string sourceName,
        string targetName)
    {
        var lines = new List<string>
        {
            CsvExporter.Line(
                "Table", "Action", "Included", "Key", "Name", "Column",
                $"{sourceName} value", $"{targetName} value", "Source id", "Target id", "Result")
        };

        foreach (var (item, included, result) in rows)
        {
            var row = item.Row;

            string Line(string? column, string? source, string? target) => CsvExporter.Line(
                item.Table, item.Action.ToString(), included ? "Yes" : "No", item.Key, item.Name, column,
                source, target, row.SourceId, row.TargetId, result);

            if (item.Action == ReconcileAction.Update && row.Differences.Count > 0)
            {
                foreach (var d in row.Differences) lines.Add(Line(d.Column.LogicalName, d.SourceValue, d.TargetValue));
                continue;
            }

            lines.Add(Line(null, null, null));
        }

        return lines;
    }
}
