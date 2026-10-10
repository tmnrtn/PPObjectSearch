using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace PPObjectSearch.Dataverse;

/// <summary>An exact row count, how it was reached, and what it cost.</summary>
public sealed record RowCountResult(long Rows, int Requests, TimeSpan Elapsed, string Method);

/// <summary>Dataverse's stored row count for a table, and when it was last refreshed.</summary>
public sealed record RowCountSnapshot(long Rows, DateTimeOffset? LastUpdated);

/// <summary>
/// Counting a table's rows. Two answers, with different trade-offs:
///
/// The snapshot (RetrieveTotalRecordCount) is instant whatever the size - it is a number Dataverse
/// keeps rather than a count - but it is refreshed roughly daily, so it can be a day behind.
///
/// The exact count is live. Up to 50,000 rows an aggregate count answers in one request; past that
/// Dataverse refuses the aggregate, so the last page is found instead: pages of 5,000 carrying
/// nothing but the primary key, probed by page number - doubling until a page comes back short or
/// empty, then halving between the last full page and the first empty one.
/// </summary>
public sealed partial class DataverseClient
{
    private const int CountPageSize = 5000;

    /// <summary>Dataverse's AggregateQueryRecordLimit: an aggregate over more rows than this fails.</summary>
    public const long AggregateCountLimit = 50_000;

    /// <summary>A billion rows - a ceiling no real table reaches, so the doubling cannot run away.</summary>
    private const int MaxCountPage = 200_000;

    private sealed record TableDefinition(string LogicalName, string EntitySet, string PrimaryId, int? ObjectTypeCode);

    private async Task<TableDefinition> GetTableDefinitionAsync(Guid metadataId, CancellationToken ct)
    {
        using var definition = await GetJsonAsync(
            EnvironmentUrl + ApiPath +
            $"EntityDefinitions({metadataId})?$select=LogicalName,EntitySetName,PrimaryIdAttribute,ObjectTypeCode", ct).ConfigureAwait(false);

        var root = definition.RootElement;
        var logicalName = JsonHelper.GetString(root, "LogicalName")
                          ?? throw new DataverseException("The table's metadata has no logical name.");

        return new TableDefinition(
            logicalName,
            JsonHelper.GetString(root, "EntitySetName")
                ?? throw new DataverseException($"{logicalName} has no entity set, so its rows cannot be read."),
            JsonHelper.GetString(root, "PrimaryIdAttribute") ?? logicalName + "id",
            JsonHelper.GetInt(root, "ObjectTypeCode"));
    }

    /// <summary>
    /// Counts the rows of the table with this metadata id exactly. <paramref name="expectedRows"/>
    /// (the snapshot, if known) skips the aggregate attempt when the table is plainly too big for it.
    /// </summary>
    public async Task<RowCountResult> CountRowsAsync(
        Guid metadataId, long? expectedRows = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var table = await GetTableDefinitionAsync(metadataId, ct).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();

        if (expectedRows is null || expectedRows <= AggregateCountLimit)
        {
            progress?.Report($"Counting {table.LogicalName}...");

            var aggregate = await TryAggregateCountAsync(table, ct).ConfigureAwait(false);
            if (aggregate is { } rows) return new RowCountResult(rows, 1, timer.Elapsed, "aggregate count");
        }

        var (pageRows, requests) = await CountByPagesAsync(table, progress, ct).ConfigureAwait(false);

        // The aggregate attempt, where it was made and refused, was a request too.
        if (expectedRows is null || expectedRows <= AggregateCountLimit) requests++;

        return new RowCountResult(pageRows, requests, timer.Elapsed, "page search");
    }

    /// <summary>
    /// One FetchXML aggregate count. Null when Dataverse refuses it, which it does - rather than
    /// returning a capped number - once the table has more than 50,000 rows.
    /// </summary>
    private async Task<long?> TryAggregateCountAsync(TableDefinition table, CancellationToken ct)
    {
        var fetch = $"<fetch aggregate='true'><entity name='{table.LogicalName}'>" +
                    $"<attribute name='{table.PrimaryId}' alias='rowcount' aggregate='count' /></entity></fetch>";

        try
        {
            using var doc = await GetJsonAsync(
                EnvironmentUrl + ApiPath + $"{table.EntitySet}?fetchXml={Uri.EscapeDataString(fetch)}",
                ct, maxPageSize: false).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() == 0) return null;

            return long.TryParse(JsonHelper.GetString(value[0], "rowcount"), out var rows) ? rows : null;
        }
        catch (DataverseException ex) when (ex.StatusCode is HttpStatusCode.BadRequest)
        {
            // Over the aggregate limit (or aggregates not allowed on this table): the page search
            // follows. Anything else - throttling that outlasted the retries, a lost connection - is
            // a failure to report, not a reason to start hundreds of page requests instead.
            return null;
        }
    }

    private async Task<(long Rows, int Requests)> CountByPagesAsync(
        TableDefinition table, IProgress<string>? progress, CancellationToken ct)
    {
        var requests = 0;
        string? firstKeyOnPageOne = null;

        async Task<int> Probe(int page)
        {
            // Far beyond any real table; a page number this high means the count has gone wrong.
            if (page > MaxCountPage)
            {
                throw new DataverseException(
                    $"Stopped counting at page {MaxCountPage:N0} - Dataverse kept returning full pages, which no real table does.");
            }

            requests++;
            progress?.Report($"Counting {table.LogicalName}... page {page:N0}");

            // Only the key, ordered on it so a page number means the same rows on every request.
            var fetch = $"<fetch page='{page}' count='{CountPageSize}' no-lock='true'>" +
                        $"<entity name='{table.LogicalName}'><attribute name='{table.PrimaryId}' />" +
                        $"<order attribute='{table.PrimaryId}' /></entity></fetch>";

            // Without odata.maxpagesize: with it, the Web API pages by itself and ignores page='n'.
            using var doc = await GetJsonAsync(
                EnvironmentUrl + ApiPath + $"{table.EntitySet}?fetchXml={Uri.EscapeDataString(fetch)}",
                ct, maxPageSize: false).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) return 0;

            var count = value.GetArrayLength();
            var firstKey = count > 0 ? JsonHelper.GetString(value[0], table.PrimaryId) : null;

            // A later page that starts where page 1 did means the page number is being ignored,
            // and every probe would come back "full" forever.
            if (page == 1) firstKeyOnPageOne = firstKey;
            else if (firstKey is not null && string.Equals(firstKey, firstKeyOnPageOne, StringComparison.OrdinalIgnoreCase))
            {
                throw new DataverseException(
                    $"Dataverse returned page 1 again when asked for page {page}, so the page number is being ignored and the rows cannot be counted this way.");
            }

            return count;
        }

        var rows = await CountFromPageProbesAsync(Probe).ConfigureAwait(false);
        return (rows, requests);
    }

    /// <summary>
    /// The row count, from as few pages as it takes to find the last one: <paramref name="probe"/>
    /// reads one page and says how many rows it held.
    /// </summary>
    private static async Task<long> CountFromPageProbesAsync(Func<int, Task<int>> probe)
    {
        // Page 1 settles every table that fits in a single page.
        var first = await probe(1).ConfigureAwait(false);
        if (first < CountPageSize) return first;

        // Double until a page is not full: lo is the last page known full, hi the first known not.
        var lo = 1;
        var hi = 2;
        int atHi;

        while ((atHi = await probe(hi).ConfigureAwait(false)) == CountPageSize)
        {
            lo = hi;
            hi = Math.Min(hi * 2, MaxCountPage + 1);
        }

        if (atHi > 0) return (long)(hi - 1) * CountPageSize + atHi;

        // hi is empty: halve between the last full page and it until they are neighbours, stopping
        // early on a page that is part full, which is the last page.
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            var count = await probe(mid).ConfigureAwait(false);

            if (count == CountPageSize) lo = mid;
            else if (count == 0) hi = mid;
            else return (long)(mid - 1) * CountPageSize + count;
        }

        // Every page up to lo is full and the next is empty.
        return (long)lo * CountPageSize;
    }

    /// <summary>
    /// Dataverse's stored row count (RetrieveTotalRecordCount) and, where the account can read the
    /// recordcountsnapshot table, when it was last refreshed. Null when there is no snapshot.
    /// </summary>
    public async Task<RowCountSnapshot?> GetRowCountSnapshotAsync(Guid metadataId, CancellationToken ct = default)
    {
        var table = await GetTableDefinitionAsync(metadataId, ct).ConfigureAwait(false);
        var names = Uri.EscapeDataString($"[\"{table.LogicalName}\"]");

        long? rows;
        using (var doc = await GetJsonAsync(
                   EnvironmentUrl + ApiPath + $"RetrieveTotalRecordCount(EntityNames=@p1)?@p1={names}", ct).ConfigureAwait(false))
        {
            rows = TotalRecordCount(doc.RootElement, table.LogicalName);
        }

        if (rows is null) return null;

        DateTimeOffset? lastUpdated = null;
        if (table.ObjectTypeCode is { } code)
        {
            try
            {
                using var snapshot = await GetJsonAsync(
                    EnvironmentUrl + ApiPath +
                    $"recordcountsnapshots?$select=lastupdated&$filter=objecttypecode eq {code}", ct).ConfigureAwait(false);

                if (snapshot.RootElement.TryGetProperty("value", out var value) && value.GetArrayLength() > 0)
                {
                    lastUpdated = JsonHelper.GetDate(value[0], "lastupdated");
                }
            }
            catch (DataverseException)
            {
                // When it was refreshed is a nicety; the count stands without it.
            }
        }

        return new RowCountSnapshot(rows.Value, lastUpdated);
    }

    /// <summary>One table's count from a RetrieveTotalRecordCount response, which pairs table names with counts by position.</summary>
    private static long? TotalRecordCount(JsonElement response, string logicalName)
    {
        if (!response.TryGetProperty("EntityRecordCountCollection", out var collection) ||
            !collection.TryGetProperty("Keys", out var keys) || !collection.TryGetProperty("Values", out var values))
        {
            return null;
        }

        for (var i = 0; i < keys.GetArrayLength() && i < values.GetArrayLength(); i++)
        {
            if (string.Equals(keys[i].GetString(), logicalName, StringComparison.OrdinalIgnoreCase) &&
                values[i].TryGetInt64(out var count))
            {
                return count;
            }
        }

        return null;
    }
}
