using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    private const string ConnectionReferenceSelect =
        "connectionreferences?$select=connectionreferenceid,connectionreferencelogicalname,connectionreferencedisplayname," +
        "connectorid,connectionid";

    public static bool IsConnectionReference(SolutionComponentItem item) =>
        string.Equals(item.ComponentLogicalName, "connectionreference", StringComparison.OrdinalIgnoreCase);

    /// <summary>Connection reference rows by id - fifty to a request.</summary>
    public async Task<IReadOnlyList<ConnectionReferenceInfo>> GetConnectionReferencesAsync(
        IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var results = new List<ConnectionReferenceInfo>();

        foreach (var chunk in ids.Distinct().Chunk(50))
        {
            await ReadConnectionReferencesAsync(
                ConnectionReferenceSelect + "&$filter=" + InFilter("connectionreferenceid", chunk.Select(id => id.ToString())),
                results, ct).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>Connection reference rows by logical name - how a flow's definition names them.</summary>
    public async Task<IReadOnlyList<ConnectionReferenceInfo>> GetConnectionReferencesByNameAsync(
        IEnumerable<string> logicalNames, CancellationToken ct = default)
    {
        var results = new List<ConnectionReferenceInfo>();

        foreach (var chunk in logicalNames.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(25))
        {
            await ReadConnectionReferencesAsync(
                ConnectionReferenceSelect + "&$filter=" +
                Uri.EscapeDataString(string.Join(" or ",
                    chunk.Select(n => $"connectionreferencelogicalname eq '{n.Replace("'", "''")}'"))),
                results, ct).ConfigureAwait(false);
        }

        return results;
    }

    private async Task ReadConnectionReferencesAsync(string query, List<ConnectionReferenceInfo> into, CancellationToken ct)
    {
        var url = EnvironmentUrl + ApiPath + query;

        while (url.Length > 0)
        {
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    if (ReadConnectionReference(row) is { } reference) into.Add(reference);
                }
            }

            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }
    }

    private static ConnectionReferenceInfo? ReadConnectionReference(JsonElement row)
    {
        if (!Guid.TryParse(JsonHelper.GetString(row, "connectionreferenceid"), out var id)) return null;

        return new ConnectionReferenceInfo
        {
            Id = id,
            LogicalName = JsonHelper.GetString(row, "connectionreferencelogicalname") ?? string.Empty,
            DisplayName = JsonHelper.GetString(row, "connectionreferencedisplayname"),
            ConnectorId = JsonHelper.GetString(row, "connectorid"),
            ConnectionId = JsonHelper.GetString(row, "connectionid")
        };
    }

    /// <summary>
    /// Marks each connection reference in a component list "Has connection" or "No connection",
    /// as its sub type - which is what the grid's sub-type filter offers, so the unbound ones can
    /// be listed on their own. Best effort: a failure leaves them unmarked.
    /// </summary>
    private async Task ApplyConnectionReferenceStatesAsync(IReadOnlyList<SolutionComponentItem> items, CancellationToken ct)
    {
        var references = items.Where(i => IsConnectionReference(i) && i.ObjectId != Guid.Empty).ToList();
        if (references.Count == 0) return;

        try
        {
            var rows = (await GetConnectionReferencesAsync(references.Select(r => r.ObjectId), ct).ConfigureAwait(false))
                .ToDictionary(r => r.Id);

            foreach (var item in references)
            {
                if (!rows.TryGetValue(item.ObjectId, out var row)) continue;

                item.SubType = row.HasConnection ? "Has connection" : "No connection";
                item.BuildSearchIndex();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.Log.Warn("Connection references could not be read", ex);
        }
    }

    /// <summary>
    /// The connection references a cloud flow's definition (its clientdata) names, by logical
    /// name. A flow that embeds its connections rather than referencing them names none.
    /// </summary>
    public static IReadOnlyList<string> ConnectionReferenceNames(string? clientData)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(clientData)) return names;

        try
        {
            using var doc = JsonDocument.Parse(clientData);
            var root = doc.RootElement;
            var properties = root.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : root;

            if (!properties.TryGetProperty("connectionReferences", out var references) ||
                references.ValueKind != JsonValueKind.Object)
            {
                return names;
            }

            foreach (var reference in references.EnumerateObject().Select(r => r.Value))
            {
                if (reference.ValueKind != JsonValueKind.Object) continue;
                if (!reference.TryGetProperty("connection", out var connection) ||
                    connection.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (JsonHelper.GetString(connection, "connectionReferenceLogicalName") is { Length: > 0 } name &&
                    !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        catch (JsonException)
        {
            // A definition that does not parse has nothing to say here.
        }

        return names;
    }
}
