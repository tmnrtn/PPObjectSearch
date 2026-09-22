using System.Globalization;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

public enum RecordCompareStatus
{
    /// <summary>Kept in this order so sorting puts the actionable rows above the matching ones.</summary>
    OnlyInSource,
    OnlyInTarget,
    Different,
    Same
}

/// <summary>One column of one row, as it stands in each environment.</summary>
public sealed class ColumnComparison
{
    public required EntityColumn Column { get; init; }
    public string? SourceValue { get; init; }
    public string? TargetValue { get; init; }
    public required bool IsDifferent { get; init; }

    public string ColumnLabel => Column.Label;
    public string TypeLabel => Column.TypeLabel;
}

/// <summary>
/// Everything needed to read and compare one table: which rows to fetch, which columns count as
/// the key, and which columns take part in the value comparison. Resolved once from the saved
/// configuration against the source environment's metadata, then used for both environments.
/// </summary>
public sealed class EntityComparePlan
{
    public required EntitySummary Entity { get; init; }
    public required IReadOnlyList<EntityColumn> KeyColumns { get; init; }
    public required IReadOnlyList<EntityColumn> ValueColumns { get; init; }
    public required string KeyLabel { get; init; }
    public string? Filter { get; init; }

    /// <summary>Settable so the window can re-judge lookups on rows it has already read,
    /// rather than fetching both environments again to answer the same question.</summary>
    public bool MatchLookupsByName { get; set; } = true;

    public string EntityLabel => Entity.Label;

    /// <summary>Everything the row query has to ask for - both halves of the comparison.</summary>
    public IEnumerable<string> SelectNames =>
        KeyColumns.Concat(ValueColumns).Select(c => c.SelectName).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One row of the result grid: the same logical record in both environments, or its absence.</summary>
public sealed class RecordComparison
{
    public required EntityComparePlan Plan { get; init; }
    public required string Key { get; init; }
    public required RecordCompareStatus Status { get; init; }
    public DataRecord? Source { get; init; }
    public DataRecord? Target { get; init; }
    public required IReadOnlyList<ColumnComparison> Differences { get; init; }

    public string EntityLabel => Plan.EntityLabel;
    public string EntityLogicalName => Plan.Entity.LogicalName;
    public string KeyLabel => Plan.KeyLabel;

    /// <summary>The primary name value, which is what a person recognises the row by.</summary>
    public string? Name => Source?.PrimaryName ?? Target?.PrimaryName;

    public string StatusLabel => Status switch
    {
        RecordCompareStatus.OnlyInSource => "Only in source",
        RecordCompareStatus.OnlyInTarget => "Only in target",
        RecordCompareStatus.Different => "Values differ",
        _ => "Match"
    };

    public string DifferenceSummary => Status switch
    {
        RecordCompareStatus.Different => string.Join(", ", Differences.Select(d => d.Column.LogicalName)),
        RecordCompareStatus.OnlyInSource => "Missing from target",
        RecordCompareStatus.OnlyInTarget => "Missing from source",
        _ => string.Empty
    };

    public int DifferenceCount => Differences.Count;

    public string SourceId => Source is null ? string.Empty : Source.Id.ToString();
    public string TargetId => Target is null ? string.Empty : Target.Id.ToString();

    /// <summary>
    /// Every compared column side by side, built on demand. Only the differing ones are kept with
    /// the row - a matching table would otherwise carry a column comparison per column per row for
    /// no reason.
    /// </summary>
    public IReadOnlyList<ColumnComparison> AllColumns() =>
        ReferenceDataComparer.CompareColumns(Plan, Source, Target);
}

/// <summary>What one table's comparison produced, including the parts that did not go to plan.</summary>
public sealed class EntityCompareResult
{
    public required EntityComparePlan Plan { get; init; }
    public required IReadOnlyList<RecordComparison> Rows { get; init; }
    public required int SourceRowCount { get; init; }
    public required int TargetRowCount { get; init; }

    /// <summary>Row caps hit and duplicate keys found - things that make the result partial.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Matches rows from two environments on a chosen key and reports what differs.
///
/// Values are compared after normalising them per column type, so that a decimal written as
/// <c>1.0</c> in one environment and <c>1.0000</c> in the other, or a date carrying a different
/// offset, does not read as a difference. Lookups compare on their label by default, because the
/// id behind a lookup only agrees between environments where the target row was deployed.
/// </summary>
public static class ReferenceDataComparer
{
    /// <summary>Stands in for a key whose columns are all empty, so such rows stay visible
    /// instead of colliding with each other.</summary>
    private const string NoKeyMarker = "(no key)";

    public static EntityCompareResult Compare(
        EntityComparePlan plan,
        IReadOnlyList<DataRecord> source,
        IReadOnlyList<DataRecord> target)
    {
        var warnings = new List<string>();

        var sourceByKey = Index(plan, source, "source", warnings);
        var targetByKey = Index(plan, target, "target", warnings);

        var rows = new List<RecordComparison>(Math.Max(sourceByKey.Count, targetByKey.Count));

        foreach (var (key, sourceRecord) in sourceByKey)
        {
            if (!targetByKey.TryGetValue(key, out var targetRecord))
            {
                rows.Add(new RecordComparison
                {
                    Plan = plan,
                    Key = key,
                    Status = RecordCompareStatus.OnlyInSource,
                    Source = sourceRecord,
                    Differences = Array.Empty<ColumnComparison>()
                });

                continue;
            }

            var differences = CompareColumns(plan, sourceRecord, targetRecord)
                .Where(c => c.IsDifferent)
                .ToList();

            rows.Add(new RecordComparison
            {
                Plan = plan,
                Key = key,
                Status = differences.Count == 0 ? RecordCompareStatus.Same : RecordCompareStatus.Different,
                Source = sourceRecord,
                Target = targetRecord,
                Differences = differences
            });
        }

        foreach (var (key, targetRecord) in targetByKey.Where(t => !sourceByKey.ContainsKey(t.Key)))
        {
            rows.Add(new RecordComparison
            {
                Plan = plan,
                Key = key,
                Status = RecordCompareStatus.OnlyInTarget,
                Target = targetRecord,
                Differences = Array.Empty<ColumnComparison>()
            });
        }

        rows.Sort((a, b) =>
        {
            var byStatus = a.Status.CompareTo(b.Status);
            if (byStatus != 0) return byStatus;

            return string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase);
        });

        return new EntityCompareResult
        {
            Plan = plan,
            Rows = rows,
            SourceRowCount = source.Count,
            TargetRowCount = target.Count,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Every value column side by side. Used both to find the differences and to fill the detail
    /// pane, so that what the pane shows and what the grid counts can never disagree.
    /// </summary>
    public static IReadOnlyList<ColumnComparison> CompareColumns(
        EntityComparePlan plan,
        DataRecord? source,
        DataRecord? target)
    {
        var results = new List<ColumnComparison>(plan.ValueColumns.Count);

        foreach (var column in plan.ValueColumns)
        {
            var sourceDisplay = source?.Display(column.SelectName);
            var targetDisplay = target?.Display(column.SelectName);

            // A row present on one side only has nothing to differ from; its values are shown as
            // they stand rather than flagged column by column.
            var isDifferent = source is not null && target is not null &&
                              !string.Equals(
                                  Comparable(source, column, plan.MatchLookupsByName),
                                  Comparable(target, column, plan.MatchLookupsByName),
                                  StringComparison.Ordinal);

            results.Add(new ColumnComparison
            {
                Column = column,
                SourceValue = sourceDisplay,
                TargetValue = targetDisplay,
                IsDifferent = isDifferent
            });
        }

        return results;
    }

    private static Dictionary<string, DataRecord> Index(
        EntityComparePlan plan,
        IReadOnlyList<DataRecord> records,
        string side,
        List<string> warnings)
    {
        var map = new Dictionary<string, DataRecord>(records.Count, StringComparer.OrdinalIgnoreCase);
        var duplicates = 0;

        foreach (var record in records)
        {
            var key = BuildKey(plan, record);

            // A duplicate key means the chosen key is not unique in that environment, which is
            // worth saying out loud rather than quietly comparing against whichever row won.
            if (!map.TryAdd(key, record)) duplicates++;
        }

        if (duplicates > 0)
        {
            warnings.Add($"{plan.Entity.LogicalName}: {duplicates:N0} {side} row(s) share a key on " +
                         $"{plan.KeyLabel} and were skipped - the key is not unique there.");
        }

        return map;
    }

    private static string BuildKey(EntityComparePlan plan, DataRecord record)
    {
        var parts = plan.KeyColumns
            .Select(c => Comparable(record, c, plan.MatchLookupsByName))
            .ToList();

        return parts.All(string.IsNullOrEmpty)
            ? $"{NoKeyMarker} {record.Id}"
            : string.Join(" | ", parts);
    }

    /// <summary>
    /// The form of a value that two environments can be compared on: normalised by type, and for a
    /// lookup the label rather than the id unless there is no label to use.
    /// </summary>
    private static string Comparable(DataRecord record, EntityColumn column, bool matchLookupsByName)
    {
        if (column.IsLookup && matchLookupsByName)
        {
            var label = record.Label(column.SelectName);
            if (!string.IsNullOrWhiteSpace(label)) return label.Trim();
        }

        return Normalise(record.Raw(column.SelectName), column) ?? string.Empty;
    }

    private static string? Normalise(string? raw, EntityColumn column)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();

        if (column.IsNumeric && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            // Trailing zeros are a scale artefact, not a value: 1.0000 and 1.0 are the same money.
            return number.ToString("0.############################", CultureInfo.InvariantCulture);
        }

        if (column.IsBoolean)
        {
            return value switch
            {
                "1" or "true" or "True" => "true",
                "0" or "false" or "False" => "false",
                _ => value
            };
        }

        if (column.IsDateTime && DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
        {
            return moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        if ((column.IsUniqueIdentifier || column.IsLookup) && Guid.TryParse(value, out var id))
        {
            return id.ToString("D");
        }

        return value;
    }
}
