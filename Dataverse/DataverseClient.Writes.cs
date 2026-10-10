using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

/// <summary>
/// The only part of this app that changes anything. Every method here is called from a path that
/// has already passed the write guard and an explicit confirmation; none of them is called while
/// merely reading or comparing.
/// </summary>
public sealed partial class DataverseClient
{
    /// <summary>What type of environment this is, for the write guard. Never prompts.</summary>
    public Task<EnvironmentTypeInfo> GetEnvironmentTypeAsync(string? environmentId, CancellationToken ct = default) =>
        EnvironmentTypeProbe.ProbeAsync(_auth, _http, EnvironmentUrl, environmentId, ct);

    /// <summary>
    /// Creates a row, carrying the id it had in the source environment so that the two
    /// environments converge on one id rather than drifting into two rows that only an alternate
    /// key can tie together. A taken id fails the create rather than overwriting the row that
    /// holds it - this method never upserts.
    /// </summary>
    public async Task CreateRecordAsync(
        string entitySetName,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>(values);

        // POST to the collection, never PATCH to the id: a POST cannot turn into an upsert, so an
        // id that is already taken fails here rather than overwriting the row holding it.
        using var request = new HttpRequestMessage(HttpMethod.Post, EnvironmentUrl + ApiPath + entitySetName)
        {
            Content = JsonContent(body)
        };

        await SendAsync(request, $"create {entitySetName}({id})", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the given columns over an existing row. Columns not named are left alone. With an
    /// <paramref name="etag"/>, the write only happens if the row is still the version that was
    /// read - an edit made since the comparison is not overwritten.
    /// </summary>
    public async Task UpdateRecordAsync(
        string entitySetName,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        string? etag = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, EnvironmentUrl + ApiPath + $"{entitySetName}({id})")
        {
            Content = JsonContent(values)
        };

        // Without an If-Match a PATCH at a missing id would create the row - an update that
        // silently becomes an insert is not an update. Either form refuses a missing row.
        request.Headers.IfMatch.Add(IfMatch(etag));

        await SendAsync(request, $"update {entitySetName}({id})", ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a row - with an <paramref name="etag"/>, only if it is still the version read.</summary>
    public async Task DeleteRecordAsync(string entitySetName, Guid id, string? etag = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, EnvironmentUrl + ApiPath + $"{entitySetName}({id})");

        if (etag is not null) request.Headers.IfMatch.Add(IfMatch(etag));

        await SendAsync(request, $"delete {entitySetName}({id})", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the one row of a table whose primary name is <paramref name="label"/>. Returns null
    /// where nothing matches and throws where several do - a lookup that cannot be pinned to a
    /// single row is a reason to abandon the write, not to pick one.
    /// </summary>
    public async Task<Guid?> ResolveByNameAsync(
        EntitySummary entity,
        string label,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entity.PrimaryNameAttribute))
        {
            throw new DataverseException(
                $"{entity.LogicalName} has no primary name column, so '{label}' cannot be resolved by name.");
        }

        if (string.IsNullOrWhiteSpace(entity.EntitySetName))
        {
            throw new DataverseException(
                $"{entity.LogicalName} has no entity set name, so its rows cannot be queried.");
        }

        // Three would already be too many; the third is only fetched to say "several" honestly.
        // The label is data, not syntax: the quote is doubled so it cannot end the literal, and the
        // whole literal is percent-encoded - including '%', which the server decodes after the
        // quotes are checked, so "%27" would otherwise become a quote that ends the literal.
        // It is not trimmed: a label with surrounding spaces has to match itself.
        var literal = Escape(label);

        var url = EnvironmentUrl + ApiPath + entity.EntitySetName +
                  $"?$select={entity.PrimaryIdAttribute}&$top=3" +
                  $"&$filter={entity.PrimaryNameAttribute} eq '{literal}'";

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("value", out var value)) return null;

        var matches = new List<Guid>();

        foreach (var row in value.EnumerateArray())
        {
            if (Guid.TryParse(JsonHelper.GetString(row, entity.PrimaryIdAttribute), out var id)) matches.Add(id);
        }

        if (matches.Count > 1)
        {
            // An inactive row that shares the name is usually a retired copy; if exactly one active
            // row carries it, that is the one meant. Not every table has statecode, so a query that
            // fails here simply leaves the name ambiguous.
            if (await ResolveActiveByNameAsync(entity, literal, ct).ConfigureAwait(false) is { } active) return active;

            throw new DataverseException(
                $"'{label}' matches {matches.Count} rows of {entity.LogicalName}, so the reference is ambiguous.");
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<Guid?> ResolveActiveByNameAsync(EntitySummary entity, string literal, CancellationToken ct)
    {
        var url = EnvironmentUrl + ApiPath + entity.EntitySetName +
                  $"?$select={entity.PrimaryIdAttribute}&$top=2" +
                  $"&$filter={entity.PrimaryNameAttribute} eq '{literal}' and statecode eq 0";

        try
        {
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("value", out var value) || value.GetArrayLength() != 1) return null;

            return Guid.TryParse(JsonHelper.GetString(value[0], entity.PrimaryIdAttribute), out var id) ? id : null;
        }
        catch (DataverseException)
        {
            return null;
        }
    }

    /// <summary>Whether a row with this id exists in the table - for binding a lookup by id.</summary>
    public async Task<bool> RecordExistsAsync(EntitySummary entity, Guid id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entity.EntitySetName))
        {
            throw new DataverseException(
                $"{entity.LogicalName} has no entity set name, so its rows cannot be queried.");
        }

        try
        {
            using var doc = await GetJsonAsync(
                EnvironmentUrl + ApiPath + $"{entity.EntitySetName}({id})?$select={entity.PrimaryIdAttribute}", ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (DataverseException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// The whole row as it stands, kept before an update or delete so the write can be undone. Each
    /// lookup carries the table it points at and the navigation property that binds it, which is
    /// what writing it back needs. Null when the row is not there.
    /// </summary>
    public async Task<System.Text.Json.Nodes.JsonObject?> GetRecordSnapshotAsync(
        string entitySetName, Guid id, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync(
                EnvironmentUrl + ApiPath + $"{entitySetName}({id})", ct,
                annotations: "Microsoft.Dynamics.CRM.lookuplogicalname,Microsoft.Dynamics.CRM.associatednavigationproperty",
                maxPageSize: false).ConfigureAwait(false);

            var row = System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText())?.AsObject();
            row?.Remove("@odata.context");
            return row;
        }
        catch (DataverseException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static EntityTagHeaderValue IfMatch(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag)) return EntityTagHeaderValue.Any;

        // Dataverse sends weak tags (W/"123"); the header wants the quoted tag and the flag apart.
        var weak = etag.StartsWith("W/", StringComparison.Ordinal);
        var tag = weak ? etag[2..] : etag;
        if (!tag.StartsWith('"')) tag = $"\"{tag}\"";

        return new EntityTagHeaderValue(tag, weak);
    }

    private static StringContent JsonContent(IReadOnlyDictionary<string, object?> values)
    {
        var json = JsonSerializer.Serialize(values);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private async Task SendAsync(HttpRequestMessage request, string what, CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync(EnvironmentUrl, ct).ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // If-Match on a row that is not there comes back as a precondition failure rather than
        // anything that names the row, so it is spelled out.
        // A create answers 412 for a key that is already taken, and says so; an update or delete
        // answers 412 when its If-Match no longer holds, which needs spelling out.
        var detail = (response.StatusCode, request.Method.Method) switch
        {
            (HttpStatusCode.PreconditionFailed, "POST") => ExtractError(body),
            (HttpStatusCode.PreconditionFailed, _) =>
                $"{ExtractError(body)} (the row has been changed or removed in the target since the comparison " +
                "ran - compare again before writing it)",
            (HttpStatusCode.NotFound, _) =>
                $"{ExtractError(body)} (the row may have been removed since the comparison ran)",
            _ => ExtractError(body)
        };

        throw new DataverseException($"Could not {what}: {detail}", response.StatusCode);
    }
}
