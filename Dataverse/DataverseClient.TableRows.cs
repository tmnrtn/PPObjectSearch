using System.Xml.Linq;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>Rows read for looking at, and whether the cap stopped the read before the table ran out.</summary>
public sealed record TableRowsResult(IReadOnlyList<DataRecord> Rows, bool IsTruncated);

/// <summary>
/// Reading a table's rows to look at them - reference and configuration data, a lookup table, an
/// option list. Read-only: nothing here sends anything but a GET.
/// </summary>
public sealed partial class DataverseClient
{
    /// <summary>The main (public) view's query type in savedquery.querytype.</summary>
    private const int PublicViewQueryType = 0;

    /// <summary>
    /// The columns of the table's default public view, in the order the view shows them - what a
    /// maker chose as the useful ones. Empty where there is no such view, or it cannot be read;
    /// the caller falls back to the primary name. Columns from a related table ("alias.name") are
    /// left out, since they are not this table's to select.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDefaultViewColumnsAsync(string logicalName, CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  "savedqueries?$select=savedqueryid,isdefault,layoutxml" +
                  $"&$filter=returnedtypecode eq '{Uri.EscapeDataString(logicalName.ToLowerInvariant())}' and querytype eq {PublicViewQueryType}";

        string? chosen = null;
        var chosenIsDefault = false;

        try
        {
            await ForEachRowAsync(url, null, row =>
            {
                var layout = JsonHelper.GetString(row, "layoutxml");
                if (string.IsNullOrWhiteSpace(layout)) return;

                var isDefault = JsonHelper.GetBool(row, "isdefault") ?? false;
                if (chosen is null || (isDefault && !chosenIsDefault))
                {
                    chosen = layout;
                    chosenIsDefault = isDefault;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (DataverseException)
        {
            // A virtual table, or one without views, answers with an error - no view, then.
            return [];
        }

        return chosen is null ? [] : ReadLayoutColumns(chosen);
    }

    /// <summary>The cell names of a view's layoutxml, in order, without related-table cells or repeats.</summary>
    internal static IReadOnlyList<string> ReadLayoutColumns(string layoutXml)
    {
        try
        {
            return XDocument.Parse(layoutXml)
                .Descendants("cell")
                .Select(c => (string?)c.Attribute("name"))
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Contains('.', StringComparison.Ordinal))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads up to <paramref name="maxRows"/> rows, sorted by the primary name then the id so that
    /// a read the cap cuts short stops at a predictable place. A table that refuses the sort - some
    /// virtual tables do - is read again unsorted rather than not at all.
    /// </summary>
    public async Task<TableRowsResult> GetTableRowsAsync(
        EntitySummary entity,
        IEnumerable<string> selectNames,
        string? filter,
        int maxRows,
        Action<int>? onRowsRead = null,
        CancellationToken ct = default)
    {
        var select = new List<string> { entity.PrimaryIdAttribute };
        select.AddRange(selectNames);

        var baseUrl = EnvironmentUrl + ApiPath + entity.EntitySetName +
                      "?$select=" + string.Join(",", select.Distinct(StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(filter)) baseUrl += "&$filter=" + EscapeFilter(filter);

        var orderBy = entity.PrimaryNameAttribute is { Length: > 0 } name
            ? $"{name},{entity.PrimaryIdAttribute}"
            : entity.PrimaryIdAttribute;

        try
        {
            return await ReadTableRowsAsync(baseUrl + "&$orderby=" + orderBy, entity, maxRows, onRowsRead, ct).ConfigureAwait(false);
        }
        catch (DataverseException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotImplemented)
        {
            return await ReadTableRowsAsync(baseUrl, entity, maxRows, onRowsRead, ct).ConfigureAwait(false);
        }
    }

    private async Task<TableRowsResult> ReadTableRowsAsync(
        string url, EntitySummary entity, int maxRows, Action<int>? onRowsRead, CancellationToken ct)
    {
        var results = new List<DataRecord>();
        var truncated = false;

        while (url.Length > 0)
        {
            ct.ThrowIfCancellationRequested();

            using var doc = await GetJsonAsync(url, ct, Annotations.Formatted).ConfigureAwait(false);

            foreach (var row in JsonHelper.Rows(doc.RootElement))
            {
                if (results.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }
                results.Add(ReadRecord(row, entity));
            }

            onRowsRead?.Invoke(results.Count);
            url = JsonHelper.NextLink(doc.RootElement);

            // Full, with more pages to come: the table did not run out, the cap stopped it.
            if (results.Count >= maxRows)
            {
                truncated |= url.Length > 0;
                break;
            }
        }

        return new TableRowsResult(results, truncated);
    }
}
