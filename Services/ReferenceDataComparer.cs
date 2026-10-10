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

    /// <summary>
    /// Why this row must not be written, when the comparison cannot vouch for it: a side was read
    /// only up to the row cap, so "missing" may just mean "not read", or the key was not unique,
    /// so the row was paired with whichever duplicate happened to win.
    /// </summary>
    public string? WriteBlockedReason { get; init; }

    public bool IsWriteBlocked => WriteBlockedReason is not null;

    /// <summary>
    /// The target's key, when the same row (by primary id) carries a different key on each side.
    /// Such a row is one record whose key was edited, not a new row plus an old one: it is
    /// reconciled as an update, never as a create and a delete of the same id.
    /// </summary>
    public string? TargetKey { get; init; }

    public bool IsKeyChanged => TargetKey is not null;

    public string EntityLabel => Plan.EntityLabel;
    public string EntityLogicalName => Plan.Entity.LogicalName;
    public string KeyLabel => Plan.KeyLabel;

    /// <summary>The primary name value, which is what a person recognises the row by.</summary>
    public string? Name => Source?.PrimaryName ?? Target?.PrimaryName;

    public string StatusLabel => Status switch
    {
        RecordCompareStatus.OnlyInSource => "Only in source",
        RecordCompareStatus.OnlyInTarget => "Only in target",
        RecordCompareStatus.Different when IsKeyChanged => "Key changed",
        RecordCompareStatus.Different => "Values differ",
        _ => "Match"
    };

    public string DifferenceSummary => Status switch
    {
        RecordCompareStatus.Different when IsKeyChanged =>
            $"Key was {TargetKey} in target: " + string.Join(", ", Differences.Select(d => d.Column.LogicalName)),
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

    public bool SourceTruncated { get; init; }
    public bool TargetTruncated { get; init; }

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

    /// <param name="sourceTruncated">The source read stopped at the row cap, so a row missing from
    /// it may only be unread.</param>
    /// <param name="targetTruncated">The same for the target.</param>
    public static EntityCompareResult Compare(
        EntityComparePlan plan,
        IReadOnlyList<DataRecord> source,
        IReadOnlyList<DataRecord> target,
        bool sourceTruncated = false,
        bool targetTruncated = false)
    {
        var warnings = new List<string>();
        var duplicated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sourceByKey = Index(plan, source, "source", warnings, duplicated);
        var targetByKey = Index(plan, target, "target", warnings, duplicated);

        string? Blocked(string key, RecordCompareStatus status)
        {
            if (duplicated.Contains(key))
            {
                return $"Not written: the key {plan.KeyLabel} is not unique, so this row was paired with " +
                       "one of several and could be the wrong one.";
            }

            return status switch
            {
                RecordCompareStatus.OnlyInTarget when sourceTruncated =>
                    "Not written: the source was only read up to the row cap, so this row may exist there unread.",
                RecordCompareStatus.OnlyInSource when targetTruncated =>
                    "Not written: the target was only read up to the row cap, so this row may already exist there.",
                _ => null
            };
        }

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
                    Differences = Array.Empty<ColumnComparison>(),
                    WriteBlockedReason = Blocked(key, RecordCompareStatus.OnlyInSource)
                });

                continue;
            }

            var differences = CompareColumns(plan, sourceRecord, targetRecord)
                .Where(c => c.IsDifferent)
                .ToList();

            var status = differences.Count == 0 ? RecordCompareStatus.Same : RecordCompareStatus.Different;

            rows.Add(new RecordComparison
            {
                Plan = plan,
                Key = key,
                Status = status,
                Source = sourceRecord,
                Target = targetRecord,
                Differences = differences,
                WriteBlockedReason = Blocked(key, status)
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
                Differences = Array.Empty<ColumnComparison>(),
                WriteBlockedReason = Blocked(key, RecordCompareStatus.OnlyInTarget)
            });
        }

        PairKeyChanges(plan, rows, Blocked);

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
            SourceTruncated = sourceTruncated,
            TargetTruncated = targetTruncated,
            Warnings = warnings
        };
    }

    /// <summary>
    /// A row present on both sides under the same primary id but a different key shows up as
    /// "only in source" under its new key and "only in target" under its old one. Planned like that,
    /// it would become a create and a delete of one id: the create fails on the duplicate id and the
    /// delete then removes the row, or the delete goes first and the row comes back without its
    /// children, owner and every uncompared column. So the two halves are joined into one update
    /// that writes the key columns as well as any value that differs.
    /// </summary>
    private static void PairKeyChanges(
        EntityComparePlan plan,
        List<RecordComparison> rows,
        Func<string, RecordCompareStatus, string?> blocked)
    {
        var onlyInTarget = rows
            .Where(r => r.Status == RecordCompareStatus.OnlyInTarget && r.Target is not null)
            .GroupBy(r => r.Target!.Id)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());

        if (onlyInTarget.Count == 0) return;

        var joined = new HashSet<RecordComparison>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Status != RecordCompareStatus.OnlyInSource || row.Source is not { } source) continue;
            if (!onlyInTarget.Remove(source.Id, out var other) || other.Target is not { } target) continue;

            var differences = CompareColumns(plan, source, target).Where(c => c.IsDifferent).ToList();
            AddChangedKeys(plan, source, target, differences);

            rows[i] = new RecordComparison
            {
                Plan = plan,
                Key = row.Key,
                TargetKey = other.Key,
                Status = RecordCompareStatus.Different,
                Source = source,
                Target = target,
                Differences = differences,
                WriteBlockedReason = blocked(row.Key, RecordCompareStatus.Different) ??
                                     blocked(other.Key, RecordCompareStatus.Different)
            };

            joined.Add(other);
        }

        rows.RemoveAll(joined.Contains);
    }

    /// <summary>The key columns that changed, so the joined update writes the new key too.</summary>
    private static void AddChangedKeys(
        EntityComparePlan plan, DataRecord source, DataRecord target, List<ColumnComparison> differences)
    {
        foreach (var key in plan.KeyColumns)
        {
            if (key.IsPrimaryId || differences.Any(d => d.Column.SelectName.Equals(key.SelectName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var sourceValue = Comparable(source, key, plan.MatchLookupsByName);
            var targetValue = Comparable(target, key, plan.MatchLookupsByName);
            if (string.Equals(sourceValue, targetValue, StringComparison.Ordinal)) continue;

            differences.Add(new ColumnComparison
            {
                Column = key,
                SourceValue = source.Display(key.SelectName),
                TargetValue = target.Display(key.SelectName),
                IsDifferent = true
            });
        }
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
        List<string> warnings,
        HashSet<string> duplicated)
    {
        var map = new Dictionary<string, DataRecord>(records.Count, StringComparer.OrdinalIgnoreCase);
        var duplicates = 0;

        foreach (var record in records)
        {
            var key = BuildKey(plan, record);

            // A duplicate key means the chosen key is not unique in that environment, which is
            // worth saying out loud rather than quietly comparing against whichever row won.
            if (!map.TryAdd(key, record))
            {
                duplicates++;
                duplicated.Add(key);
            }
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

        // A key of only whitespace identifies nothing, so it is as good as no key.
        return parts.All(string.IsNullOrWhiteSpace)
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
        // Empty and absent are the same value; anything else in a text column is compared exactly,
        // surrounding whitespace included - "Code " and "Code" are different keys to an integration.
        if (string.IsNullOrEmpty(raw)) return null;

        var value = raw.Trim();
        if (value.Length == 0 && !IsText(column)) return null;

        if (column.IsNumeric && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            // Trailing zeros are a scale artefact, not a value: 1.0000 and 1.0 are the same money.
            return number.ToString("0.############################", CultureInfo.InvariantCulture);
        }

        if (column.IsBoolean) return NormalisedBoolean(value);

        if (column.IsDateTime && DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
        {
            return moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        if ((column.IsUniqueIdentifier || column.IsLookup) && Guid.TryParse(value, out var id))
        {
            return id.ToString("D");
        }

        return IsText(column) ? raw : value;
    }

    private static string NormalisedBoolean(string value) => value switch
    {
        "1" or "true" or "True" => "true",
        "0" or "false" or "False" => "false",
        _ => value
    };

    /// <summary>Columns whose value is free text, where whitespace is part of the value.</summary>
    internal static bool IsText(EntityColumn column) =>
        column.TypeName is "StringType" or "MemoType";
}
