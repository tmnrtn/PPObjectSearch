using PPObjectSearch.Dataverse;

namespace PPObjectSearch.Services;

/// <summary>The outcome of running a saved comparison without the window.</summary>
public sealed class ReferenceDataRunResult
{
    public List<RecordComparison> Rows { get; } = new();
    public List<string> Warnings { get; } = new();
    public int Tables { get; set; }

    public IEnumerable<RecordComparison> Differences => Rows.Where(r => r.Status != RecordCompareStatus.Same);
}

/// <summary>
/// A saved reference-data comparison run end to end - plan each table, read both sides, compare -
/// as the window does, for the command line.
/// </summary>
public static class ReferenceDataRun
{
    public static async Task<ReferenceDataRunResult> RunAsync(
        ReferenceDataConfig config,
        DataverseClient source,
        DataverseClient target,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ReferenceDataRunResult();
        var planner = new ReferenceDataPlanner(source, target);
        var max = config.MaxRowsPerEntity;

        foreach (var entity in (config.Entities ?? new List<ReferenceEntityConfig>()).Where(e => e.IsEnabled))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{entity.LogicalName}: reading metadata...");

            var plan = await planner.BuildAsync(entity, config.MatchLookupsByName, ct).ConfigureAwait(false);
            result.Warnings.AddRange(plan.Warnings);

            if (plan.Failure is not null)
            {
                result.Warnings.Add($"{plan.Failure.EntityLogicalName}: skipped - {plan.Failure.Reason}");
                continue;
            }

            if (plan.Plan is null) continue;

            progress?.Report($"{entity.LogicalName}: reading rows...");
            var sourceRows = source.GetRecordsAsync(plan.Plan.Entity, plan.Plan.SelectNames, plan.Plan.Filter, max, ct: ct);
            var targetRows = target.GetRecordsAsync(plan.Plan.Entity, plan.Plan.SelectNames, plan.Plan.Filter, max, ct: ct);
            await Task.WhenAll(sourceRows, targetRows).ConfigureAwait(false);

            var sourceTruncated = sourceRows.Result.Count >= max;
            var targetTruncated = targetRows.Result.Count >= max;
            if (sourceTruncated || targetTruncated)
            {
                result.Warnings.Add($"{entity.LogicalName}: hit the {max:N0} row cap, so the result is partial.");
            }

            var compared = ReferenceDataComparer.Compare(plan.Plan, sourceRows.Result, targetRows.Result, sourceTruncated, targetTruncated);
            result.Warnings.AddRange(compared.Warnings);
            result.Rows.AddRange(compared.Rows);
            result.Tables++;
        }

        return result;
    }
}
