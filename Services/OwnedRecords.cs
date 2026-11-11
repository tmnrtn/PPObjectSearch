using System.Collections.Concurrent;
using PPObjectSearch.Dataverse;

namespace PPObjectSearch.Services;

/// <summary>How many rows of one table an owner has - or, with <see cref="Error"/>, why they could not be counted.</summary>
public sealed record OwnedRecordCount(OwnedTable Table, long Rows, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>The tables an owner has rows in, largest first, and the tables that could not be counted.</summary>
public sealed record OwnedRecordsResult(IReadOnlyList<OwnedRecordCount> Owned, IReadOnlyList<OwnedRecordCount> Failed, int TablesCounted)
{
    public long TotalRows => Owned.Sum(o => o.Rows);
}

/// <summary>
/// Counts the rows a user or a team owns, in every table that has an owner. One count per table,
/// several at once: most tables hold nothing for any one owner, and each answers in a single
/// request. A table that cannot be counted - no read privilege, say - is kept with its reason, so
/// a short list is never mistaken for a complete one.
/// </summary>
public static class OwnedRecords
{
    /// <summary>Counts in flight together - well inside Dataverse's limit on concurrent requests.</summary>
    public const int CountsAtOnce = 8;

    /// <param name="progress">Tables counted so far, and how many there are.</param>
    public static async Task<OwnedRecordsResult> CountAsync(
        DataverseClient client, Guid ownerId, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var tables = await client.GetOwnedTablesAsync(ct).ConfigureAwait(false);
        progress?.Report((0, tables.Count));

        var counts = new ConcurrentBag<OwnedRecordCount>();
        var done = 0;

        await Parallel.ForEachAsync(tables, new ParallelOptions { MaxDegreeOfParallelism = CountsAtOnce, CancellationToken = ct },
            async (table, token) =>
            {
                try
                {
                    var rows = await client.CountOwnedRowsAsync(table, ownerId, token).ConfigureAwait(false);
                    if (rows > 0) counts.Add(new OwnedRecordCount(table, rows, null));
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    counts.Add(new OwnedRecordCount(table, 0, ex.Message));
                }

                progress?.Report((Interlocked.Increment(ref done), tables.Count));
            }).ConfigureAwait(false);

        var owned = counts.Where(c => !c.Failed)
            .OrderByDescending(c => c.Rows).ThenBy(c => c.Table.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var failed = counts.Where(c => c.Failed)
            .OrderBy(c => c.Table.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new OwnedRecordsResult(owned, failed, tables.Count);
    }
}
