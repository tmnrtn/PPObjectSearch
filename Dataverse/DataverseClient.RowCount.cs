using System.Diagnostics;
using System.Text.Json;

namespace PPObjectSearch.Dataverse;

/// <summary>An exact row count, and what it cost to get.</summary>
public sealed record RowCountResult(long Rows, int Requests, TimeSpan Elapsed);

/// <summary>
/// Counting a table's rows exactly without reading them all. Dataverse's aggregate count stops at
/// 50,000, and the RetrieveTotalRecordCount snapshot is only refreshed daily, so this finds the
/// last page instead: pages of 5,000 carrying nothing but the primary key, probed by page number -
/// doubling until a page comes back short or empty, then halving between the last full page and
/// the first empty one. A table of millions of rows is counted in a couple of dozen requests.
/// </summary>
public sealed partial class DataverseClient
{
    private const int CountPageSize = 5000;

    /// <summary>A billion rows - a ceiling no real table reaches, so the doubling cannot run away.</summary>
    private const int MaxCountPage = 200_000;

    /// <summary>Counts the rows of the table with this metadata id.</summary>
    public async Task<RowCountResult> CountRowsAsync(Guid metadataId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        string entitySet;
        string logicalName;
        string primaryId;

        using (var definition = await GetJsonAsync(
                   EnvironmentUrl + ApiPath +
                   $"EntityDefinitions({metadataId})?$select=LogicalName,EntitySetName,PrimaryIdAttribute", ct).ConfigureAwait(false))
        {
            var root = definition.RootElement;
            logicalName = JsonHelper.GetString(root, "LogicalName") ?? throw new DataverseException("The table's metadata has no logical name.");
            entitySet = JsonHelper.GetString(root, "EntitySetName")
                        ?? throw new DataverseException($"{logicalName} has no entity set, so its rows cannot be read.");
            primaryId = JsonHelper.GetString(root, "PrimaryIdAttribute") ?? logicalName + "id";
        }

        var timer = Stopwatch.StartNew();
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
            progress?.Report($"Counting {logicalName}... page {page:N0}");

            // Only the key, ordered on it so a page number means the same rows on every request.
            var fetch = $"<fetch page='{page}' count='{CountPageSize}' no-lock='true'>" +
                        $"<entity name='{logicalName}'><attribute name='{primaryId}' />" +
                        $"<order attribute='{primaryId}' /></entity></fetch>";

            // Without odata.maxpagesize: with it, the Web API pages by itself and ignores page='n'.
            using var doc = await GetJsonAsync(
                EnvironmentUrl + ApiPath + $"{entitySet}?fetchXml={Uri.EscapeDataString(fetch)}",
                ct, maxPageSize: false).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) return 0;

            var count = value.GetArrayLength();
            var firstKey = count > 0 ? JsonHelper.GetString(value[0], primaryId) : null;

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

        // Page 1 settles every table that fits in a single page.
        var first = await Probe(1).ConfigureAwait(false);
        if (first < CountPageSize) return new RowCountResult(first, requests, timer.Elapsed);

        // Double until a page is not full: lo is the last page known full, hi the first known not.
        var lo = 1;
        var hi = 2;
        int atHi;

        while ((atHi = await Probe(hi).ConfigureAwait(false)) == CountPageSize)
        {
            lo = hi;
            hi = Math.Min(hi * 2, MaxCountPage + 1);
        }

        if (atHi > 0) return new RowCountResult((long)(hi - 1) * CountPageSize + atHi, requests, timer.Elapsed);

        // hi is empty: halve between the last full page and it until they are neighbours, stopping
        // early on a page that is part full, which is the last page.
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            var count = await Probe(mid).ConfigureAwait(false);

            if (count == CountPageSize) lo = mid;
            else if (count == 0) hi = mid;
            else return new RowCountResult((long)(mid - 1) * CountPageSize + count, requests, timer.Elapsed);
        }

        // Every page up to lo is full and the next is empty.
        return new RowCountResult((long)lo * CountPageSize, requests, timer.Elapsed);
    }

    /// <summary>
    /// Dataverse's own row count snapshot (RetrieveTotalRecordCount). Cheap, but refreshed
    /// roughly daily, so it is a cross-check on the exact count rather than a replacement.
    /// </summary>
    public async Task<long?> GetRowCountSnapshotAsync(string logicalName, CancellationToken ct = default)
    {
        var names = Uri.EscapeDataString($"[\"{logicalName}\"]");

        using var doc = await GetJsonAsync(
            EnvironmentUrl + ApiPath + $"RetrieveTotalRecordCount(EntityNames=@p1)?@p1={names}", ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("EntityRecordCountCollection", out var collection)) return null;
        if (!collection.TryGetProperty("Keys", out var keys) || !collection.TryGetProperty("Values", out var values)) return null;

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

    /// <summary>The table's logical name from its metadata id.</summary>
    public async Task<string?> GetTableLogicalNameAsync(Guid metadataId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(
            EnvironmentUrl + ApiPath + $"EntityDefinitions({metadataId})?$select=LogicalName", ct).ConfigureAwait(false);
        return JsonHelper.GetString(doc.RootElement, "LogicalName");
    }
}
