using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>A plan that could not be built, and why - reported per table rather than failing the run.</summary>
public sealed record PlanFailure(string EntityLogicalName, string Reason);

public sealed record PlanResult(EntityComparePlan? Plan, IReadOnlyList<string> Warnings, PlanFailure? Failure);

/// <summary>
/// Turns a saved table configuration into something both environments can actually answer.
///
/// Columns are resolved against both sides and intersected: a column that exists only in the
/// source is not merely a difference to report, it would make the target's row query fail outright,
/// so it is dropped and called out instead.
/// </summary>
public sealed class ReferenceDataPlanner
{
    private readonly DataverseClient _source;
    private readonly DataverseClient _target;

    private readonly Dictionary<string, IReadOnlyList<EntityColumn>> _sourceColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<EntityColumn>> _targetColumns = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, EntitySummary>? _sourceEntities;
    private IReadOnlyDictionary<string, EntitySummary>? _targetEntities;

    public ReferenceDataPlanner(DataverseClient source, DataverseClient target)
    {
        _source = source;
        _target = target;
    }

    public async Task<PlanResult> BuildAsync(
        ReferenceEntityConfig config,
        bool matchLookupsByName,
        CancellationToken ct = default)
    {
        var logicalName = config.LogicalName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            return new PlanResult(null, Array.Empty<string>(), new PlanFailure("(unnamed)", "No table name."));
        }

        _sourceEntities ??= await LoadEntitiesAsync(_source, ct).ConfigureAwait(false);
        _targetEntities ??= await LoadEntitiesAsync(_target, ct).ConfigureAwait(false);

        if (!_sourceEntities.TryGetValue(logicalName, out var entity))
        {
            return new PlanResult(null, Array.Empty<string>(),
                new PlanFailure(logicalName, "Not in the source environment."));
        }

        if (!_targetEntities.ContainsKey(logicalName))
        {
            return new PlanResult(null, Array.Empty<string>(),
                new PlanFailure(logicalName, "Not in the target environment."));
        }

        var warnings = new List<string>();

        var sourceColumns = await ColumnsAsync(_source, _sourceColumns, logicalName, ct).ConfigureAwait(false);
        var targetNames = (await ColumnsAsync(_target, _targetColumns, logicalName, ct).ConfigureAwait(false))
            .Select(c => c.LogicalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var shared = sourceColumns.Where(c => targetNames.Contains(c.LogicalName)).ToList();

        var missing = sourceColumns.Count - shared.Count;
        if (missing > 0)
        {
            var names = sourceColumns.Where(c => !targetNames.Contains(c.LogicalName))
                                     .Select(c => c.LogicalName)
                                     .Take(6);

            warnings.Add($"{logicalName}: {missing} column(s) exist only in the source and were left out of " +
                         $"the comparison ({string.Join(", ", names)}{(missing > 6 ? ", ..." : string.Empty)}).");
        }

        var keyResult = ResolveKeyColumns(config, entity, shared);
        if (keyResult.Failure is not null) return new PlanResult(null, warnings, keyResult.Failure);

        var excluded = config.ExcludedColumns is null
            ? SystemColumns.DefaultExclusions(shared)
            : config.ExcludedColumns;

        var excludedSet = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var valueColumns = shared.Where(c => !excludedSet.Contains(c.LogicalName)).ToList();

        if (valueColumns.Count == 0)
        {
            warnings.Add($"{logicalName}: every column is excluded, so only presence is compared.");
        }

        return new PlanResult(
            new EntityComparePlan
            {
                Entity = entity,
                KeyColumns = keyResult.Columns,
                ValueColumns = valueColumns,
                KeyLabel = keyResult.Label,
                Filter = config.Filter,
                MatchLookupsByName = matchLookupsByName
            },
            warnings,
            null);
    }

    /// <summary>The alternate keys the source environment defines for a table, for the key picker.</summary>
    public Task<IReadOnlyList<AlternateKeyInfo>> GetAlternateKeysAsync(string logicalName, CancellationToken ct = default)
        => _source.GetAlternateKeysAsync(logicalName, ct);

    private sealed record KeyResult(IReadOnlyList<EntityColumn> Columns, string Label, PlanFailure? Failure);

    private KeyResult ResolveKeyColumns(
        ReferenceEntityConfig config,
        EntitySummary entity,
        IReadOnlyList<EntityColumn> available)
    {
        var byName = available.ToDictionary(c => c.LogicalName, StringComparer.OrdinalIgnoreCase);
        var logicalName = entity.LogicalName;

        switch (config.KeySource)
        {
            case RecordKeySource.AlternateKey:
            {
                // The key's columns were stored alongside its name when it was chosen, so a key
                // that has since been dropped from the table still compares on the same columns.
                var names = config.KeyColumns;
                if (names is not { Count: > 0 })
                {
                    return new KeyResult(Array.Empty<EntityColumn>(), string.Empty,
                        new PlanFailure(logicalName, $"Alternate key '{config.AlternateKeyName}' has no columns recorded."));
                }

                return Resolve(names, config.AlternateKeyName ?? "alternate key");
            }

            case RecordKeySource.Columns:
            {
                var names = config.KeyColumns;
                if (names is not { Count: > 0 })
                {
                    return new KeyResult(Array.Empty<EntityColumn>(), string.Empty,
                        new PlanFailure(logicalName, "No key columns chosen."));
                }

                return Resolve(names, string.Join(" + ", names));
            }

            default:
            {
                if (!byName.TryGetValue(entity.PrimaryIdAttribute, out var primary))
                {
                    return new KeyResult(Array.Empty<EntityColumn>(), string.Empty,
                        new PlanFailure(logicalName, $"Primary id '{entity.PrimaryIdAttribute}' is not readable in both environments."));
                }

                return new KeyResult(new[] { primary }, entity.PrimaryIdAttribute, null);
            }
        }

        KeyResult Resolve(IReadOnlyList<string> names, string label)
        {
            var columns = new List<EntityColumn>(names.Count);

            foreach (var name in names)
            {
                if (!byName.TryGetValue(name, out var column))
                {
                    return new KeyResult(Array.Empty<EntityColumn>(), label,
                        new PlanFailure(logicalName, $"Key column '{name}' is not readable in both environments."));
                }

                columns.Add(column);
            }

            return new KeyResult(columns, label, null);
        }
    }

    private static async Task<IReadOnlyDictionary<string, EntitySummary>> LoadEntitiesAsync(
        DataverseClient client,
        CancellationToken ct)
    {
        var entities = await client.GetEntitiesAsync(ct).ConfigureAwait(false);
        return entities.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<EntityColumn>> ColumnsAsync(
        DataverseClient client,
        Dictionary<string, IReadOnlyList<EntityColumn>> cache,
        string logicalName,
        CancellationToken ct)
    {
        if (cache.TryGetValue(logicalName, out var cached)) return cached;

        var columns = await client.GetEntityColumnsAsync(logicalName, ct).ConfigureAwait(false);
        cache[logicalName] = columns;
        return columns;
    }
}
