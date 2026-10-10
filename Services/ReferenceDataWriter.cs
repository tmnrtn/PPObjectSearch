using System.Globalization;
using System.Text.Json.Nodes;
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

public sealed record ReconcileOutcome(ReconcilePlanItem Item, bool Succeeded, string Message)
{
    /// <summary>The target row written, where one was.</summary>
    public Guid? Id { get; init; }

    /// <summary>What was sent to the target.</summary>
    public IReadOnlyDictionary<string, object?>? Written { get; init; }

    /// <summary>The target row just before an update or delete, when snapshots are on.</summary>
    public JsonObject? Before { get; init; }

    /// <summary>The write that would reverse this one. Set on a row that was changed even where a
    /// later step for it failed, since the change is still there to undo.</summary>
    public UndoStep? Undo { get; init; }
}

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

    /// <summary>The target's columns per table, for putting a deleted row back.</summary>
    private readonly Dictionary<string, IReadOnlyList<EntityColumn>> _targetColumns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Read the whole target row before updating or deleting it, so the write can be logged in
    /// full and undone. A row that cannot be read first is not written.
    /// </summary>
    public bool SaveSnapshots { get; init; }

    /// <summary>
    /// Columns left out of this run only, as "table/column". They are neither created nor updated,
    /// whatever the comparison says - the saved configuration is not touched.
    /// </summary>
    public IReadOnlySet<string> ExcludedColumns { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static string ColumnKey(string table, string column) => $"{table}/{column}";

    private bool IsExcluded(EntityComparePlan plan, EntityColumn column) =>
        ExcludedColumns.Count > 0 && ExcludedColumns.Contains(ColumnKey(plan.Entity.LogicalName, column.LogicalName));

    /// <summary>The compared value columns this run writes.</summary>
    private IReadOnlyList<EntityColumn> ValueColumns(EntityComparePlan plan) =>
        ExcludedColumns.Count == 0 ? plan.ValueColumns : plan.ValueColumns.Where(c => !IsExcluded(plan, c)).ToList();

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

            if (item.Action != ReconcileAction.Delete && MoneyWithoutCurrency(plan) is { } money)
            {
                return Fail(item, $"money column(s) {money} would be written without {SystemColumns.Currency}, " +
                                  "so the amount could land in the wrong currency. Compare the currency column too.");
            }

            switch (item.Action)
            {
                case ReconcileAction.Delete:
                {
                    if (item.Row.Target is not { } target) return Fail(item, "no target row to delete.");

                    JsonObject? before = null;
                    UndoStep? undo = null;

                    if (SaveSnapshots)
                    {
                        before = await SnapshotAsync(entitySet, target.Id, ct).ConfigureAwait(false);
                        if (before is null) return Fail(item, "the row is no longer in the target.");

                        var columns = await TargetColumnsAsync(plan.Entity.LogicalName, ct).ConfigureAwait(false);
                        undo = WriteUndo.ForDelete(
                            entitySet, target.Id, TargetPrimaryId(plan.Entity), before, columns, EntitySetOf);
                    }

                    await _target.DeleteRecordAsync(entitySet, target.Id, ct, target.ETag).ConfigureAwait(false);
                    return new ReconcileOutcome(item, true, "Deleted.") { Id = target.Id, Before = before, Undo = undo };
                }

                case ReconcileAction.Create:
                {
                    if (item.Row.Source is not { } source) return Fail(item, "no source row to copy.");

                    var skipped = new List<string>();

                    var body = await BuildBodyAsync(source, null, ValueColumns(plan), true, plan.MatchLookupsByName, skipped, ct)
                        .ConfigureAwait(false);

                    // The source id travels with the row so the two environments converge on one
                    // id instead of drifting into two rows only an alternate key can tie together.
                    body[plan.Entity.PrimaryIdAttribute] = source.Id.ToString();

                    // A row is always created in its default state. An inactive source row's status
                    // would be refused against that state, so state and status are set afterwards.
                    var state = DeferredState(plan, source, body, skipped);

                    await _target.CreateRecordAsync(entitySet, source.Id, body, ct).ConfigureAwait(false);
                    Remember(plan.Entity, source.PrimaryName, source.Id);

                    var undoCreate = WriteUndo.ForCreate(entitySet, source.Id);

                    if (state is not null)
                    {
                        try
                        {
                            await _target.UpdateRecordAsync(entitySet, source.Id, state, ct).ConfigureAwait(false);
                        }
                        catch (DataverseException ex)
                        {
                            return Fail(item, $"Created with id {source.Id}, but its status could not be set to match " +
                                              $"the source: {ex.Message}") with { Id = source.Id, Written = body, Undo = undoCreate };
                        }
                    }

                    if (state is not null) foreach (var (k, v) in state) body[k] = v;

                    return new ReconcileOutcome(item, true,
                        $"Created with id {source.Id}{(state is null ? string.Empty : ", then set to the source's status")}." +
                        Note(skipped)) { Id = source.Id, Written = body, Undo = undoCreate };
                }

                default:
                {
                    if (item.Row.Source is not { } source) return Fail(item, "no source row to copy from.");
                    if (item.Row.Target is not { } target) return Fail(item, "no target row to update.");

                    var columns = item.Row.Differences.Select(d => d.Column).Where(c => !IsExcluded(plan, c)).ToList();
                    if (columns.Count == 0)
                    {
                        return new ReconcileOutcome(item, true, item.Row.Differences.Count == 0
                            ? "Nothing to change."
                            : "Nothing written - every differing column is left out of this run.");
                    }

                    var skipped = new List<string>();
                    var body = await BuildBodyAsync(source, target, columns, false, plan.MatchLookupsByName, skipped, ct).ConfigureAwait(false);

                    // A change of state needs its status alongside, or Dataverse pairs the new state
                    // with a status that belongs to the old one and refuses it.
                    PairStateWithStatus(plan, source, body);

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

                    JsonObject? before = null;
                    UndoStep? undo = null;

                    if (SaveSnapshots)
                    {
                        before = await SnapshotAsync(entitySet, target.Id, ct).ConfigureAwait(false);
                        if (before is null) return Fail(item, "the row is no longer in the target.");
                        undo = WriteUndo.ForUpdate(entitySet, target.Id, body, before, EntitySetOf);
                    }

                    await _target.UpdateRecordAsync(entitySet, target.Id, body, ct, target.ETag).ConfigureAwait(false);
                    Remember(plan.Entity, source.PrimaryName, target.Id);
                    return new ReconcileOutcome(item, true,
                        $"Updated {body.Count} column(s).{Note(skipped)}" +
                        (idDiffers ? $" Not changed: {PrimaryIdNote(plan.Entity.PrimaryIdAttribute)}." : string.Empty))
                    {
                        Id = target.Id, Written = body, Before = before, Undo = undo
                    };
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(item, ex.Message);
        }
    }

    private async Task<JsonObject?> SnapshotAsync(string entitySet, Guid id, CancellationToken ct)
    {
        try
        {
            return await _target.GetRecordSnapshotAsync(entitySet, id, ct).ConfigureAwait(false);
        }
        catch (DataverseException ex)
        {
            // No copy, no write: the point of the snapshot is that the write can be taken back.
            throw new DataverseException("Could not keep a copy of the row before writing it: " + ex.Message, ex.StatusCode);
        }
    }

    private async Task<IReadOnlyList<EntityColumn>> TargetColumnsAsync(string logicalName, CancellationToken ct)
    {
        if (_targetColumns.TryGetValue(logicalName, out var known)) return known;

        var columns = await _target.GetEntityColumnsAsync(logicalName, ct).ConfigureAwait(false);
        _targetColumns[logicalName] = columns;
        return columns;
    }

    private string TargetPrimaryId(EntitySummary sourceEntity) =>
        _targetEntities.TryGetValue(sourceEntity.LogicalName, out var target)
            ? target.PrimaryIdAttribute
            : sourceEntity.PrimaryIdAttribute;

    private string? EntitySetOf(string logicalName) =>
        _targetEntities.TryGetValue(logicalName, out var entity) ? entity.EntitySetName : null;

    private string? MoneyWithoutCurrency(EntityComparePlan plan)
    {
        var columns = ValueColumns(plan);
        var money = columns.Where(c => c.IsMoney && (c.IsValidForCreate || c.IsValidForUpdate)).ToList();
        if (money.Count == 0) return null;

        return columns.Any(c => c.LogicalName.Equals(SystemColumns.Currency, StringComparison.OrdinalIgnoreCase))
            ? null
            : string.Join(", ", money.Select(c => c.LogicalName));
    }

    private static ReconcileOutcome Fail(ReconcilePlanItem item, string message) =>
        new(item, false, message);

    private static string PrimaryIdNote(string primaryIdAttribute) =>
        $"the primary id ({primaryIdAttribute}) differs, and an update cannot change a row's id";

    /// <summary>Read-only columns are left out rather than failing the row, but never silently.</summary>
    private EntityColumn? Column(EntityComparePlan plan, string typeName) =>
        ValueColumns(plan).FirstOrDefault(c => c.TypeName == typeName);

    /// <summary>
    /// Takes state and status out of a create when the source row is not in the default state (0),
    /// and returns them to be written once the row exists. Null when the create can carry them.
    /// </summary>
    private Dictionary<string, object?>? DeferredState(
        EntityComparePlan plan, DataRecord source, Dictionary<string, object?> body, List<string> skipped)
    {
        var stateColumn = Column(plan, "StateType");
        var statusColumn = Column(plan, "StatusType");
        if (stateColumn is null && statusColumn is null) return null;

        var state = stateColumn is null ? null : ToWriteValue(source.Raw(stateColumn.SelectName), stateColumn);
        var status = statusColumn is null ? null : ToWriteValue(source.Raw(statusColumn.SelectName), statusColumn);

        // A row known to be in the default state is created as it is. Where the state is not being
        // compared, the status is set afterwards, since nothing says it belongs to the default state.
        var needsDeferring = state is not null ? state is not 0L : status is not null;
        if (!needsDeferring) return null;

        var deferred = new Dictionary<string, object?>();

        if (stateColumn is not null && state is not null)
        {
            deferred[stateColumn.LogicalName] = state;
            body.Remove(stateColumn.LogicalName);
            skipped.Remove(stateColumn.LogicalName);
        }

        if (statusColumn is not null && status is not null)
        {
            deferred[statusColumn.LogicalName] = status;
            body.Remove(statusColumn.LogicalName);
        }

        return deferred;
    }

    private void PairStateWithStatus(EntityComparePlan plan, DataRecord source, Dictionary<string, object?> body)
    {
        if (Column(plan, "StateType") is not { } stateColumn || !body.ContainsKey(stateColumn.LogicalName)) return;
        if (Column(plan, "StatusType") is not { } statusColumn || body.ContainsKey(statusColumn.LogicalName)) return;

        if (ToWriteValue(source.Raw(statusColumn.SelectName), statusColumn) is { } status)
        {
            body[statusColumn.LogicalName] = status;
        }
    }

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

        if (!_targetEntities.TryGetValue(targetTable, out var related))
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

        var id = await ResolveAsync(related, label, ct).ConfigureAwait(false);

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

            foreach (var target in source.LookupTargets.Values.OfType<string>().Where(target =>
                         dependsOn.ContainsKey(target) && !string.Equals(target, item.Table, StringComparison.OrdinalIgnoreCase)))
            {
                dependsOn[item.Table].Add(target);
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
        if (string.IsNullOrEmpty(raw)) return null;

        // Text is written exactly as the source holds it: trimming would change a code or strip a
        // memo's leading and trailing lines without anyone asking. Only values parsed into a
        // number, flag or date are trimmed first.
        if (ReferenceDataComparer.IsText(column)) return raw;

        var value = raw.Trim();
        if (value.Length == 0) return null;

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
