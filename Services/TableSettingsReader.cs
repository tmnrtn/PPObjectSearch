using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>
/// Reads one environment for the table settings review. Each part is read on its own and a part
/// that fails is recorded as a problem rather than failing the environment - an app user who
/// cannot read Synapse Link profiles still has audit settings worth comparing.
/// </summary>
public static class TableSettingsReader
{
    /// <summary>Column reads run this many at a time per environment, well inside the service protection limits.</summary>
    private const int ColumnConcurrency = 4;

    public static async Task<TableSettingsSnapshot> ReadAsync(string environment, DataverseClient client, CancellationToken ct)
    {
        var problems = new List<string>();

        async Task<T?> Part<T>(string what, Func<Task<T>> read) where T : class
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"{what} could not be read in {environment}", ex);
                lock (problems) problems.Add($"{what} could not be read - {ex.Message}");
                return null;
            }
        }

        // Independent reads, so they go together.
        var tables = Part("Tables", () => client.GetTableSettingsAsync(ct));
        var settings = Part("Organization settings", () => client.GetEnvironmentDataSettingsAsync(ct));
        var synapse = Part("Synapse Link", () => client.GetSynapseLinkAsync(ct));
        var search = Part("Dataverse search status", () => client.GetSearchStatusAsync(ct));

        await Task.WhenAll(tables, settings, synapse, search).ConfigureAwait(false);

        var snapshot = new TableSettingsSnapshot
        {
            Environment = environment,
            Tables = ((await tables.ConfigureAwait(false)) ?? [])
                .GroupBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            TablesRead = await tables.ConfigureAwait(false) is not null,
            Settings = await settings.ConfigureAwait(false),
            Synapse = await synapse.ConfigureAwait(false),
            Search = await search.ConfigureAwait(false)
        };

        snapshot.Problems.AddRange(problems);
        return snapshot;
    }

    /// <summary>
    /// The column audit settings of the given tables, into the snapshot. Only tables this
    /// environment has are asked for; one that fails is counted, not fatal.
    /// </summary>
    public static async Task ReadColumnsAsync(
        TableSettingsSnapshot snapshot, DataverseClient client, IEnumerable<string> tables, CancellationToken ct)
    {
        var wanted = tables.Where(snapshot.Tables.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) return;

        using var gate = new SemaphoreSlim(ColumnConcurrency);
        var failed = new List<string>();
        string? firstError = null;

        var reads = wanted.Select(async table =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var columns = await client.GetColumnAuditAsync(table, ct).ConfigureAwait(false);
                lock (snapshot.Columns) snapshot.Columns[table] = columns;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Columns of {table} could not be read in {snapshot.Environment}", ex);
                lock (failed)
                {
                    failed.Add(table);
                    firstError ??= ex.Message;
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(reads).ConfigureAwait(false);

        if (failed.Count > 0)
        {
            snapshot.Problems.Add($"Columns of {failed.Count:N0} table{(failed.Count == 1 ? string.Empty : "s")} could not be read " +
                                  $"({string.Join(", ", failed.Order().Take(5))}{(failed.Count > 5 ? ", …" : string.Empty)}) - {firstError}");
        }
    }

    /// <summary>
    /// The tables whose columns are worth comparing: in scope, and audited in at least one
    /// environment - a column's audit flag has no effect while its table is not audited.
    /// </summary>
    public static IReadOnlyList<string> AuditedTables(IEnumerable<TableSettingsSnapshot> snapshots, IEnumerable<string> scope)
    {
        var inScope = scope.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return snapshots
            .SelectMany(s => s.Tables.Values)
            .Where(t => t.IsAuditEnabled == true && inScope.Contains(t.LogicalName))
            .Select(t => t.LogicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
