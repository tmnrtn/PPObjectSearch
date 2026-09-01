using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed class DataverseException : Exception
{
    public DataverseException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Thin Dataverse Web API client covering the three calls this app needs:
/// who am I, list solutions, and list a solution's components.
/// </summary>
public sealed class DataverseClient : IDisposable
{
    private const string ApiPath = "/api/data/v9.2/";
    private const int PageSize = 5000;
    private const int MaxRows = 200_000;

    private readonly HttpClient _http;
    private readonly EnvironmentAuthContext _auth;

    public DataverseClient(EnvironmentAuthContext auth, string environmentUrl)
    {
        _auth = auth;
        EnvironmentUrl = NormalizeEnvironmentUrl(environmentUrl);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        _http.DefaultRequestHeaders.Add("OData-Version", "4.0");
    }

    public string EnvironmentUrl { get; }

    public static string NormalizeEnvironmentUrl(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new DataverseException("Enter an environment URL, e.g. https://contoso.crm11.dynamics.com");

        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new DataverseException($"'{url}' is not a valid environment URL.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct, bool includeFormattedValues = false)
    {
        var token = await _auth.GetTokenAsync(EnvironmentUrl, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var prefer = new List<string> { $"odata.maxpagesize={PageSize}" };
        if (includeFormattedValues) prefer.Add("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");
        request.Headers.Add("Prefer", string.Join(",", prefer));

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw new DataverseException($"{(int)response.StatusCode} {response.ReasonPhrase}: {ExtractError(body)}");
        }

        return response;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, bool includeFormattedValues = false)
    {
        using var response = await SendAsync(url, ct, includeFormattedValues).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? body;
            }
        }
        catch
        {
            // not JSON - fall through
        }

        return body.Length > 500 ? body[..500] + "..." : body;
    }

    public async Task<string> WhoAmIAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(EnvironmentUrl + ApiPath + "WhoAmI", ct).ConfigureAwait(false);
        return JsonHelper.GetString(doc.RootElement, "UserId") ?? string.Empty;
    }

    /// <summary>Friendly organisation name, used as the tab title.</summary>
    public async Task<string?> GetOrganizationNameAsync(CancellationToken ct = default)
    {
        try
        {
            var url = EnvironmentUrl + ApiPath + "organizations?$select=name&$top=1";
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    var name = JsonHelper.GetString(row, "name");
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Falls back to the host name in the tab title.
        }

        return null;
    }

    /// <summary>
    /// Resolves the Power Platform environment id (needed for maker portal links). Tries the
    /// organization's own metadata first, then the Global Discovery Service.
    /// </summary>
    public async Task<string?> GetEnvironmentIdAsync(CancellationToken ct = default)
    {
        try
        {
            var url = EnvironmentUrl + ApiPath +
                      "RetrieveCurrentOrganization(AccessType=Microsoft.Dynamics.CRM.EndpointAccessType'Default')";
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

            var id = JsonHelper.FindStringDeep(doc.RootElement, "EnvironmentId");
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Not exposed on every version - fall through to discovery.
        }

        return await GetEnvironmentIdFromDiscoveryAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> GetEnvironmentIdFromDiscoveryAsync(CancellationToken ct)
    {
        const string discoveryResource = "https://globaldisco.crm.dynamics.com";

        try
        {
            var token = await _auth.GetTokenAsync(discoveryResource, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(
                HttpMethod.Get, discoveryResource + "/api/discovery/v2.0/Instances");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("value", out var value)) return null;

            foreach (var instance in value.EnumerateArray())
            {
                var apiUrl = JsonHelper.GetString(instance, "ApiUrl") ?? JsonHelper.GetString(instance, "Url");
                if (apiUrl is null) continue;

                if (apiUrl.TrimEnd('/').Equals(EnvironmentUrl, StringComparison.OrdinalIgnoreCase) ||
                    apiUrl.Contains(new Uri(EnvironmentUrl).Host, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonHelper.GetString(instance, "EnvironmentId");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Discovery is optional - links are simply disabled when it fails.
        }

        return null;
    }

    public async Task<IReadOnlyList<SolutionInfo>> GetSolutionsAsync(CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  "solutions?$select=solutionid,uniquename,friendlyname,ismanaged,version" +
                  "&$expand=publisherid($select=friendlyname)" +
                  "&$filter=isvisible eq true" +
                  "&$orderby=friendlyname asc";

        var solutions = new List<SolutionInfo>();

        while (url.Length > 0)
        {
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    var idText = JsonHelper.GetString(row, "solutionid");
                    if (!Guid.TryParse(idText, out var id)) continue;

                    var uniqueName = JsonHelper.GetString(row, "uniquename") ?? id.ToString();

                    solutions.Add(new SolutionInfo
                    {
                        SolutionId = id,
                        UniqueName = uniqueName,
                        FriendlyName = JsonHelper.GetString(row, "friendlyname") ?? uniqueName,
                        IsManaged = JsonHelper.GetBool(row, "ismanaged") ?? false,
                        Version = JsonHelper.GetString(row, "version"),
                        PublisherName = row.TryGetProperty("publisherid", out var pub) && pub.ValueKind == JsonValueKind.Object
                            ? JsonHelper.GetString(pub, "friendlyname")
                            : null
                    });
                }
            }

            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        return solutions;
    }

    /// <summary>
    /// Reads every component of a solution from msdyn_solutioncomponentsummary - the same virtual
    /// table the maker portal's solution object list is built on, so names, display names and
    /// component type labels all arrive in one pass.
    /// </summary>
    public async Task<IReadOnlyList<SolutionComponentItem>> GetSolutionComponentsAsync(
        Guid solutionId,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  $"msdyn_solutioncomponentsummaries?$filter=(msdyn_solutionid eq {solutionId})";

        var items = new List<SolutionComponentItem>();

        while (url.Length > 0 && items.Count < MaxRows)
        {
            ct.ThrowIfCancellationRequested();

            using var doc = await GetJsonAsync(url, ct, includeFormattedValues: true).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    items.Add(ReadComponent(row));
                }
            }

            progress?.Report(items.Count);
            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        return items;
    }

    private static SolutionComponentItem ReadComponent(JsonElement row)
    {
        var componentType = JsonHelper.GetInt(row, "msdyn_componenttype") ?? 0;

        // Prefer the server's own label, then the formatted value annotation, then our map.
        var typeName = JsonHelper.GetString(row, "msdyn_componenttypename")
                       ?? JsonHelper.GetString(row, "msdyn_componenttype@OData.Community.Display.V1.FormattedValue")
                       ?? ComponentTypes.GetName(componentType);

        // Processes all report as "Process" - the subtype tells flows from business rules.
        var subType = JsonHelper.GetString(row, "msdyn_subtypename")
                      ?? JsonHelper.GetString(row, "msdyn_subtype@OData.Community.Display.V1.FormattedValue");
        if (componentType == 29 && !string.IsNullOrWhiteSpace(subType)) typeName = subType!;

        var name = JsonHelper.GetString(row, "msdyn_name");
        var displayName = JsonHelper.GetString(row, "msdyn_displayname");

        var item = new SolutionComponentItem
        {
            Name = name ?? displayName ?? "(unnamed)",
            DisplayName = displayName,
            ComponentType = componentType,
            ComponentTypeName = typeName,
            ComponentLogicalName = JsonHelper.GetString(row, "msdyn_componentlogicalname"),
            ObjectId = Guid.TryParse(JsonHelper.GetString(row, "msdyn_objectid"), out var objectId) ? objectId : Guid.Empty,
            SchemaName = JsonHelper.GetString(row, "msdyn_schemaname"),
            PrimaryEntityName = JsonHelper.GetString(row, "msdyn_primaryentityname"),
            IsManaged = JsonHelper.GetBool(row, "msdyn_ismanaged") ?? false,
            IsCustomizable = JsonHelper.GetBool(row, "msdyn_iscustomizable") ?? true,
            Owner = JsonHelper.GetString(row, "msdyn_owner"),
            ModifiedOn = JsonHelper.GetDate(row, "msdyn_modifiedon"),
            CreatedOn = JsonHelper.GetDate(row, "msdyn_createdon")
        };

        item.BuildSearchIndex();
        return item;
    }

    public void Dispose() => _http.Dispose();
}

internal static class JsonHelper
{
    public static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean().ToString(),
            _ => null
        };
    }

    public static bool? GetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var b) ? b : null,
            _ => null
        };
    }

    public static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => int.TryParse(value.GetString(), out var s) ? s : null,
            _ => null
        };
    }

    public static DateTimeOffset? GetDate(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return DateTimeOffset.TryParse(text, out var date) ? date : null;
    }

    /// <summary>Depth-first search for a property name anywhere in the document.</summary>
    public static string? FindStringDeep(JsonElement element, string name, int depth = 0)
    {
        if (depth > 8) return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var text = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }

                    var nested = FindStringDeep(property.Value, name, depth + 1);
                    if (nested is not null) return nested;
                }

                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    var nested = FindStringDeep(child, name, depth + 1);
                    if (nested is not null) return nested;
                }

                break;
        }

        return null;
    }
}
