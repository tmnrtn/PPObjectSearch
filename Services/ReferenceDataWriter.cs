using System.Globalization;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

public enum ReconcileAction
{
    Create,
    Update,
    Delete
}

/// <summary>Which of the three reconciliations the user has turned on for this run.</summary>
public sealed record ReconcileOptions(bool Create, bool Update, bool Delete)
{
    public bool Allows(ReconcileAction action) => action switch
    {
        ReconcileAction.Create => Create,
        ReconcileAction.Update => Update,
        _ => Delete
    };
}

/// <summary>One row and what would be done to it, decided before anything is written.</summary>
public sealed class ReconcilePlanItem
{
    public required RecordComparison Row { get; init; }
    public required ReconcileAction Action { get; init; }

    public string Table => Row.EntityLogicalName;
    public string Key => Row.Key;

    /// <summary>Set when the comparison cannot vouch for this row; such an item is never written.</summary>
    public string? BlockedReason => Row.WriteBlockedReason;
    public bool IsBlocked => BlockedReason is not null;
    public string? Name => Row.Name;

    public string ActionLabel => Action switch
    {
        ReconcileAction.Create => "Create in target",
        ReconcileAction.Update => "Update in target",
        _ => "Delete from target"
    };

    /// <summary>What the write will actually touch, so the confirmation is specific rather than
    /// a count of rows.</summary>
    public string Detail => Action switch
    {
        ReconcileAction.Create => $"{Row.Plan.ValueColumns.Count} column(s), id {Row.SourceId}",
        ReconcileAction.Update => string.Join(", ", Row.Differences.Select(d => d.Column.LogicalName)),
        _ => $"id {Row.TargetId}"
    };
}

public sealed record ReconcileOutcome(ReconcilePlanItem Item, bool Succeeded, string Message);

/// <summary>
/// Applies a reconciliation to the target environment, one row at a time.
///
/// Only the columns the comparison covered are ever written: what you compared is what you change,
/// so a column excluded from the comparison cannot be altered by reconciling it. An update writes
/// only the columns that actually differ.
///
/// A lookup is written by resolving the label the source showed against the target table and
/// binding to whatever single row matches. Where nothing matches, or several do, the whole record
/// is abandoned rather than written with the reference missing or guessed - a half-written
/// reference row is worse than an unwritten one.
/// </summary>
public sealed class ReferenceDataWriter
{
    private readonly DataverseClient _target;
    private readonly IReadOnlyDictionary<string, EntitySummary> _targetEntities;

    /// <summary>Resolved lookups, keyed by table and label. Reference data repeats its references
    /// heavily, so this saves a query per row rather than per run.</summary>
    private readonly Dictionary<(string Table, string Label), Guid?> _resolved = new();

    public ReferenceDataWriter(DataverseClient target, IReadOnlyDictionary<string, EntitySummary> targetEntities)
    {
        _target = target;
        _targetEntities = targetEntities;
    }

    /// <summary>
    /// Turns selected rows into the actions that would reconcile them. Rows that already match, and
    /// actions the user has not turned on, produce nothing at all rather than a silent no-op.
    /// </summary>
    public static IReadOnlyList<ReconcilePlanItem> Plan(
        IEnumerable<RecordComparison> rows,
        ReconcileOptions options)
    {
        var items = new List<ReconcilePlanItem>();

        foreach (var row in rows)
        {
            var action = row.Status switch
            {
                RecordCompareStatus.OnlyInSource => (ReconcileAction?)ReconcileAction.Create,
                RecordCompareStatus.Different => ReconcileAction.Update,
                RecordCompareStatus.OnlyInTarget => ReconcileAction.Delete,
                _ => null
            };

            if (action is null || !options.Allows(action.Value)) continue;

            items.Add(new ReconcilePlanItem { Row = row, Action = action.Value });
        }

        return items;
    }

    public async Task<ReconcileOutcome> ApplyAsync(ReconcilePlanItem item, CancellationToken ct = default)
    {
        // The window never offers a blocked row, but the guarantee belongs here, beside the write.
        if (item.BlockedReason is { } blocked) return Fail(item, blocked);

        try
        {
            var plan = item.Row.Plan;
            var entitySet = TargetEntitySet(plan.Entity);

            switch (item.Action)
            {
                case ReconcileAction.Delete:
                {
                    if (item.Row.Target is not { } target) return Fail(item, "no target row to delete.");

                    await _target.DeleteRecordAsync(entitySet, target.Id, ct, target.ETag).ConfigureAwait(false);
                    return new ReconcileOutcome(item, true, "Deleted.");
                }

                case ReconcileAction.Create:
                {
                    if (item.Row.Source is not { } source) return Fail(item, "no source row to copy.");

                    var skipped = new List<string>();

                    var body = await BuildBodyAsync(source, null, plan.ValueColumns, true, plan.MatchLookupsByName, skipped, ct)
                        .ConfigureAwait(false);

                    // The source id travels with the row so the two environments converge on one
                    // id instead of drifting into two rows only an alternate key can tie together.
                    body[plan.Entity.PrimaryIdAttribute] = source.Id.ToString();

                    await _target.CreateRecordAsync(entitySet, source.Id, body, ct).ConfigureAwait(false);
                    Remember(plan.Entity, source.PrimaryName, source.Id);
                    return new ReconcileOutcome(item, true, $"Created with id {source.Id}.{Note(skipped)}");
                }

                default:
                {
                    if (item.Row.Source is not { } source) return Fail(item, "no source row to copy from.");
                    if (item.Row.Target is not { } target) return Fail(item, "no target row to update.");

                    var columns = item.Row.Differences.Select(d => d.Column).ToList();
                    if (columns.Count == 0) return new ReconcileOutcome(item, true, "Nothing to change.");

                    var skipped = new List<string>();
                    var body = await BuildBodyAsync(source, target, columns, false, plan.MatchLookupsByName, skipped, ct).ConfigureAwait(false);

                    // Rows matched on another key can still carry different primary ids. An update
                    // cannot change a row's id, so that difference is reported rather than written.
                    var idDiffers = columns.Any(c => c.IsPrimaryId);

                    if (body.Count == 0)
                    {
                        var reasons = new List<string>();
                        if (idDiffers) reasons.Add(PrimaryIdNote(plan.Entity.PrimaryIdAttribute));
                        if (skipped.Count > 0) reasons.Add("read-only in Dataverse: " + string.Join(", ", skipped));

                        return Fail(item, "nothing could be written - " + string.Join("; ", reasons) + ".");
                    }

                    await _target.UpdateRecordAsync(entitySet, target.Id, body, ct, target.ETag).ConfigureAwait(false);
                    Remember(plan.Entity, source.PrimaryName, target.Id);
                    return new ReconcileOutcome(item, true,
                        $"Updated {body.Count} column(s).{Note(skipped)}" +
                        (idDiffers ? $" Not changed: {PrimaryIdNote(plan.Entity.PrimaryIdAttribute)}." : string.Empty));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(item, ex.Message);
        }
    }

    private static ReconcileOutcome Fail(ReconcilePlanItem item, string message) =>
        new(item, false, message);

    private static string PrimaryIdNote(string primaryIdAttribute) =>
        $"the primary id ({primaryIdAttribute}) differs, and an update cannot change a row's id";

    /// <summary>Read-only columns are left out rather than failing the row, but never silently.</summary>
    private static string Note(IReadOnlyList<string> skipped) =>
        skipped.Count == 0
            ? string.Empty
            : $" Left out as read-only: {string.Join(", ", skipped)}.";

    /// <summary>The target's own entity set name where it is known, since only the target is
    /// being written to.</summary>
    private string TargetEntitySet(EntitySummary sourceEntity) =>
        _targetEntities.TryGetValue(sourceEntity.LogicalName, out var target) && target.EntitySetName is { Length: > 0 }
            ? target.EntitySetName
            : sourceEntity.EntitySetName ?? sourceEntity.LogicalName;

    private async Task<Dictionary<string, object?>> BuildBodyAsync(
        DataRecord source,
        DataRecord? target,
        IReadOnlyList<EntityColumn> columns,
        bool isCreate,
        bool matchLookupsByName,
        List<string> skipped,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>();

        foreach (var column in columns)
        {
            // The primary id is identity, not a value; it is set explicitly on create and must
            // never be written by an update.
            if (column.IsPrimaryId) continue;

            // A calculated or rollup column reads like any other and is refused on write. Dropping
            // it costs nothing real - Dataverse recomputes it - whereas sending it fails the row.
            if (!column.IsWritable(isCreate))
            {
                skipped.Add(column.LogicalName);
                continue;
            }

            var raw = source.Raw(column.SelectName);

            if (column.IsLookup)
            {
                await AddLookupAsync(body, source, target, column, raw, isCreate, matchLookupsByName, ct).ConfigureAwait(false);
                continue;
            }

            body[column.LogicalName] = ToWriteValue(raw, column);
        }

        return body;
    }

    private async Task AddLookupAsync(
        Dictionary<string, object?> body,
        DataRecord source,
        DataRecord? target,
        EntityColumn column,
        string? raw,
        bool isCreate,
        bool matchLookupsByName,
        CancellationToken ct)
    {
        // Dataverse only annotates a lookup that has a value, so a source that is empty here says
        // nothing about how to bind it. The target row, which does have a value to clear, does.
        var navigation = NavigationOf(source, column) ?? NavigationOf(target, column);

        if (string.IsNullOrWhiteSpace(raw))
        {
            // A new row simply has no reference; an existing one has to be cleared to match.
            if (isCreate) return;

            if (string.IsNullOrWhiteSpace(navigation))
            {
                throw new DataverseException(
                    $"'{column.LogicalName}' is empty in the source but the navigation property for it is " +
                    "not known, so it cannot be cleared in the target.");
            }

            body[$"{navigation}@odata.bind"] = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(navigation))
        {
            throw new DataverseException(
                $"The navigation property for lookup '{column.LogicalName}' was not returned by the source, " +
                "so the reference cannot be written.");
        }

        var targetTable = source.LookupTargets.TryGetValue(column.SelectName, out var t) ? t : null;

        if (string.IsNullOrWhiteSpace(targetTable))
        {
            throw new DataverseException(
                $"The source did not say which table lookup '{column.LogicalName}' points at, " +
                "so the reference cannot be written.");
        }

        if (!_targetEntities.TryGetValue(targetTable!, out var related))
        {
            throw new DataverseException(
                $"Lookup '{column.LogicalName}' points at '{targetTable}', which is not in the target environment.");
        }

        // Compared by id, the ids agree between the environments, so the reference is bound by id -
        // once it is known to exist there. Resolving by name instead would fail on names that are
        // not unique even though the very row the source points at is in the target.
        if (!matchLookupsByName && Guid.TryParse(raw, out var sourceId))
        {
            if (!await _target.RecordExistsAsync(related, sourceId, ct).ConfigureAwait(false))
            {
                throw new DataverseException(
                    $"Lookup '{column.LogicalName}' points at {related.LogicalName} {sourceId}, which does not exist " +
                    "in the target environment.");
            }

            body[$"{navigation}@odata.bind"] = $"/{related.EntitySetName}({sourceId})";
            return;
        }

        var label = source.Label(column.SelectName);

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new DataverseException(
                $"Lookup '{column.LogicalName}' has no name in the source, so the matching row in the " +
                "target cannot be identified.");
        }

        var id = await ResolveAsync(related, label!, ct).ConfigureAwait(false);

        if (id is null)
        {
            throw new DataverseException(
                $"Lookup '{column.LogicalName}' points at '{label}', which does not exist in " +
                $"{related.LogicalName} in the target environment.");
        }

        body[$"{navigation}@odata.bind"] = $"/{related.EntitySetName}({id})";
    }

    private static string? NavigationOf(DataRecord? record, EntityColumn column) =>
        record is not null && record.NavigationProperties.TryGetValue(column.SelectName, out var nav) &&
        !string.IsNullOrWhiteSpace(nav)
            ? nav
            : null;

    private async Task<Guid?> ResolveAsync(EntitySummary related, string label, CancellationToken ct)
    {
        var key = (related.LogicalName, label);

        if (_resolved.TryGetValue(key, out var cached)) return cached;

        // An ambiguous name throws out of here rather than being cached, so the failure is
        // reported against every row that depends on it instead of only the first. Nor is "not
        // found" cached: the row may be created later in this same run.
        var id = await _target.ResolveByNameAsync(related, label, ct).ConfigureAwait(false);

        if (id is not null) _resolved[key] = id;
        return id;
    }

    /// <summary>A row this run has just written is a lookup target for the rows after it.</summary>
    private void Remember(EntitySummary entity, string? name, Guid id)
    {
        if (!string.IsNullOrWhiteSpace(name)) _resolved[(entity.LogicalName, name)] = id;
    }

    /// <summary>
    /// The order to write a run in. Creates and updates go first, tables before the tables whose
    /// lookups point at them, and within a table that points at itself, parents before children -
    /// so a row is in the target by the time anything referring to it is written. Deletes follow,
    /// in the reverse order, so a child goes before the parent it depends on.
    /// </summary>
    public static IReadOnlyList<T> OrderForWriting<T>(IEnumerable<T> items, Func<T, ReconcilePlanItem> itemOf)
    {
        var list = items.ToList();
        var tables = list.Select(i => itemOf(i).Table).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Table -> tables in this run that its rows point at.
        var dependsOn = tables.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in list.Select(itemOf))
        {
            if (item.Row.Source is not { } source) continue;

            foreach (var target in source.LookupTargets.Values)
            {
                if (target is not null && dependsOn.ContainsKey(target) &&
                    !string.Equals(target, item.Table, StringComparison.OrdinalIgnoreCase))
                {
                    dependsOn[item.Table].Add(target);
                }
            }
        }

        // Depth-first topological order; a cycle between tables keeps the order it was found in.
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string table)
        {
            if (rank.ContainsKey(table) || !visiting.Add(table)) return;
            foreach (var parent in dependsOn[table]) Visit(parent);
            visiting.Remove(table);
            rank[table] = rank.Count;
        }

        foreach (var table in tables) Visit(table);

        var depth = SelfReferenceDepths(list.Select(itemOf).ToList());

        var writes = list.Where(i => itemOf(i).Action != ReconcileAction.Delete)
            .OrderBy(i => rank[itemOf(i).Table])
            .ThenBy(i => depth.TryGetValue(itemOf(i), out var d) ? d : 0);

        var deletes = list.Where(i => itemOf(i).Action == ReconcileAction.Delete)
            .OrderByDescending(i => rank[itemOf(i).Table]);

        return writes.Concat(deletes).ToList();
    }

    /// <summary>
    /// For rows of a table that points at itself: how many ancestors in this run each has, found by
    /// following the lookup's label to another row of the run with that name.
    /// </summary>
    private static Dictionary<ReconcilePlanItem, int> SelfReferenceDepths(List<ReconcilePlanItem> items)
    {
        var depths = new Dictionary<ReconcilePlanItem, int>();

        foreach (var table in items.GroupBy(i => i.Table, StringComparer.OrdinalIgnoreCase))
        {
            var byName = table
                .Where(i => i.Row.Source?.PrimaryName is { Length: > 0 })
                .GroupBy(i => i.Row.Source!.PrimaryName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            ReconcilePlanItem? ParentOf(ReconcilePlanItem item)
            {
                if (item.Row.Source is not { } source) return null;

                foreach (var (column, target) in source.LookupTargets)
                {
                    if (!string.Equals(target, item.Table, StringComparison.OrdinalIgnoreCase)) continue;
                    if (source.Label(column) is { Length: > 0 } label && byName.TryGetValue(label, out var parent) &&
                        !ReferenceEquals(parent, item))
                    {
                        return parent;
                    }
                }

                return null;
            }

            foreach (var item in table)
            {
                var depth = 0;
                var seen = new HashSet<ReconcilePlanItem> { item };

                for (var parent = ParentOf(item); parent is not null && seen.Add(parent); parent = ParentOf(parent))
                {
                    depth++;
                }

                depths[item] = depth;
            }
        }

        return depths;
    }

    /// <summary>
    /// Sends a value as the JSON type its column expects. Dataverse rejects a number offered as a
    /// string on a numeric column, and the values arrived here as text.
    /// </summary>
    public static object? ToWriteValue(string? raw, EntityColumn column)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();

        if (column.IsBoolean)
        {
            return value is "1" or "true" or "True" || (bool.TryParse(value, out var b) && b);
        }

        // A choice is an integer column however its label reads, so it goes as a number. The
        // multi-select kind is the exception: it travels as its comma-separated list of values.
        if (column.TypeName is "IntegerType" or "BigIntType" or "PicklistType" or "StateType" or "StatusType" &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        if (column.IsNumeric &&
            decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        // Dates, strings, guids and multi-select choices all travel correctly as text.
        return value;
    }
}
