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

    /// <summary>Writes the given columns over an existing row. Columns not named are left alone.</summary>
    public async Task UpdateRecordAsync(
        string entitySetName,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, EnvironmentUrl + ApiPath + $"{entitySetName}({id})")
        {
            Content = JsonContent(values)
        };

        // Without this a PATCH at a missing id would create the row - an update that silently
        // becomes an insert is not an update.
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Any);

        await SendAsync(request, $"update {entitySetName}({id})", ct).ConfigureAwait(false);
    }

    public async Task DeleteRecordAsync(string entitySetName, Guid id, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, EnvironmentUrl + ApiPath + $"{entitySetName}({id})");

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
        // The label is data, not syntax: the quote is doubled so it cannot end the literal, and
        // the three characters that would otherwise end the query string are percent-encoded.
        var literal = EscapeFilter(label.Replace("'", "''"));

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
            throw new DataverseException(
                $"'{label}' matches {matches.Count} rows of {entity.LogicalName}, so the reference is ambiguous.");
        }

        return matches.Count == 1 ? matches[0] : null;
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
        var detail = response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound
            ? $"{ExtractError(body)} (the row may have been changed or removed since the comparison ran)"
            : ExtractError(body);

        throw new DataverseException($"Could not {what}: {detail}", response.StatusCode);
    }
}
