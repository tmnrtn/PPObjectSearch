using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PPObjectSearch.Auth;
using PPObjectSearch.Models;

namespace PPObjectSearch.Dataverse;

public sealed class DataverseException : Exception
{
    public DataverseException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>Identity of the connected environment, from RetrieveCurrentOrganization.</summary>
public sealed record OrganizationDetails(string? FriendlyName, string? UniqueName, string? EnvironmentId);

/// <summary>The bits of a table's metadata that maker portal links need.</summary>
public sealed record TableMetadata(Guid MetadataId, string? EntitySetName);

/// <summary>
/// Thin Dataverse Web API client covering the three calls this app needs:
/// who am I, list solutions, and list a solution's components.
/// </summary>
public sealed class DataverseClient : IDisposable
{
    private const string ApiPath = "/api/data/v9.2/";
    private const int PageSize = 5000;
    private const int MaxRows = 200_000;

    /// <summary>
    /// IIS rejects long request lines with 414. Dataverse paging cookies for
    /// msdyn_solutioncomponentsummary routinely exceed that, so anything near the limit is sent
    /// through $batch instead, which carries the URL in the request body.
    /// </summary>
    private const int MaxGetUrlLength = 1800;

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

    private static string BuildPreferHeader(bool includeFormattedValues)
    {
        var prefer = new List<string> { $"odata.maxpagesize={PageSize}" };
        if (includeFormattedValues) prefer.Add("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");
        return string.Join(",", prefer);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, bool includeFormattedValues = false)
    {
        // Skip the doomed GET entirely when the URL is already over the limit.
        if (url.Length > MaxGetUrlLength)
        {
            return await GetJsonViaBatchAsync(url, ct, includeFormattedValues).ConfigureAwait(false);
        }

        try
        {
            var token = await _auth.GetTokenAsync(EnvironmentUrl, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Prefer", BuildPreferHeader(includeFormattedValues));

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new DataverseException(
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {ExtractError(body)}",
                    response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (DataverseException ex) when (ex.StatusCode == HttpStatusCode.RequestUriTooLong)
        {
            return await GetJsonViaBatchAsync(url, ct, includeFormattedValues).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issues a GET inside a $batch POST. The URL travels in the request body, so paging cookies
    /// of any length are fine.
    /// </summary>
    private async Task<JsonDocument> GetJsonViaBatchAsync(string url, CancellationToken ct, bool includeFormattedValues)
    {
        var token = await _auth.GetTokenAsync(EnvironmentUrl, ct).ConfigureAwait(false);
        var boundary = "batch_" + Guid.NewGuid().ToString("N");

        var body = new StringBuilder()
            .Append("--").Append(boundary).Append("\r\n")
            .Append("Content-Type: application/http\r\n")
            .Append("Content-Transfer-Encoding: binary\r\n\r\n")
            .Append("GET ").Append(url).Append(" HTTP/1.1\r\n")
            .Append("Accept: application/json\r\n")
            .Append("OData-MaxVersion: 4.0\r\n")
            .Append("OData-Version: 4.0\r\n")
            .Append("Prefer: ").Append(BuildPreferHeader(includeFormattedValues)).Append("\r\n\r\n")
            .Append("--").Append(boundary).Append("--\r\n")
            .ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, EnvironmentUrl + ApiPath + "$batch")
        {
            Content = new StringContent(body, Encoding.UTF8)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={boundary}");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new DataverseException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {ExtractError(payload)}", response.StatusCode);
        }

        // The batch part carries its own HTTP status line ahead of the JSON payload.
        var innerStatus = Regex.Match(payload, @"HTTP/1\.\d\s+(?<code>\d{3})");
        var json = ExtractJsonObject(payload);

        if (innerStatus.Success && !innerStatus.Groups["code"].Value.StartsWith('2'))
        {
            throw new DataverseException(
                $"{innerStatus.Groups["code"].Value}: {ExtractError(json ?? payload)}");
        }

        if (json is null) throw new DataverseException("The $batch response contained no JSON payload.");

        return JsonDocument.Parse(json);
    }

    private static string? ExtractJsonObject(string payload)
    {
        var start = payload.IndexOf('{');
        var end = payload.LastIndexOf('}');
        return start >= 0 && end > start ? payload[start..(end + 1)] : null;
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

        // IIS-level failures return an HTML error page; a wall of markup helps nobody.
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('<')) return "the server returned an HTML error page.";

        return body.Length > 300 ? body[..300] + "..." : body;
    }

    public async Task<string> WhoAmIAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(EnvironmentUrl + ApiPath + "WhoAmI", ct).ConfigureAwait(false);
        return JsonHelper.GetString(doc.RootElement, "UserId") ?? string.Empty;
    }

    /// <summary>
    /// Reads the environment's own identity: the friendly name shown in the admin centre (used as
    /// the tab title) and, where the version exposes it, the Power Platform environment id.
    ///
    /// The organization table's own <c>name</c> column is deliberately not used - on many
    /// environments it holds the internal unique name (unq0f8c...), not anything readable.
    /// </summary>
    public async Task<OrganizationDetails?> RetrieveCurrentOrganizationAsync(CancellationToken ct = default)
    {
        try
        {
            var url = EnvironmentUrl + ApiPath +
                      "RetrieveCurrentOrganization(AccessType=Microsoft.Dynamics.CRM.EndpointAccessType'Default')";
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

            return new OrganizationDetails(
                JsonHelper.FindStringDeep(doc.RootElement, "FriendlyName"),
                JsonHelper.FindStringDeep(doc.RootElement, "UniqueName"),
                JsonHelper.FindStringDeep(doc.RootElement, "EnvironmentId"));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Not available on every version - the caller falls back to the host name.
            return null;
        }
    }

    /// <summary>
    /// Resolves the Power Platform environment id from the Global Discovery Service, for when
    /// <see cref="RetrieveCurrentOrganizationAsync"/> does not carry one.
    /// </summary>
    public async Task<string?> GetEnvironmentIdFromDiscoveryAsync(CancellationToken ct = default)
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

    /// <summary>
    /// Maps table logical names to their metadata id and entity set name. Both feed maker portal
    /// links: columns are addressed via their parent table's metadata id, and the solution
    /// explorer's URL segment for a component type is that type's entity set name
    /// (roleeditorlayout -> /objects/roleeditorlayouts).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, TableMetadata>> GetTableMetadataAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var url = EnvironmentUrl + ApiPath + "EntityDefinitions?$select=LogicalName,MetadataId,EntitySetName";

            while (url.Length > 0)
            {
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty("value", out var value))
                {
                    foreach (var row in value.EnumerateArray())
                    {
                        var logicalName = JsonHelper.GetString(row, "LogicalName");
                        if (string.IsNullOrWhiteSpace(logicalName)) continue;

                        Guid.TryParse(JsonHelper.GetString(row, "MetadataId"), out var id);
                        map[logicalName!] = new TableMetadata(id, JsonHelper.GetString(row, "EntitySetName"));
                    }
                }

                url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Best effort - links degrade to the solution page without it.
        }

        return map;
    }

    /// <summary>
    /// Dependencies in one direction, via RetrieveDependentComponents (what would break if this
    /// were deleted) or RetrieveRequiredComponents (what this needs).
    /// </summary>
    public async Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(
        Guid objectId,
        int componentType,
        DependencyDirection direction,
        CancellationToken ct = default)
    {
        var function = direction == DependencyDirection.Dependent
            ? "RetrieveDependentComponents"
            : "RetrieveRequiredComponents";

        // The returned dependency records describe both ends; which end is "the other one"
        // depends on the direction asked for.
        var prefix = direction == DependencyDirection.Dependent ? "dependentcomponent" : "requiredcomponent";

        var url = $"{EnvironmentUrl}{ApiPath}{function}(ObjectId=@p1,ComponentType=@p2)" +
                  $"?@p1={objectId}&@p2={componentType}";

        var results = new List<DependencyRef>();
        using var doc = await GetJsonAsync(url, ct, includeFormattedValues: true).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("value", out var value)) return results;

        foreach (var row in value.EnumerateArray())
        {
            if (!Guid.TryParse(JsonHelper.GetString(row, prefix + "objectid"), out var id)) continue;

            var type = JsonHelper.GetInt(row, prefix + "type") ?? 0;
            var typeName = JsonHelper.GetString(row, $"{prefix}type@OData.Community.Display.V1.FormattedValue")
                           ?? ComponentTypes.GetName(type);

            results.Add(new DependencyRef
            {
                ObjectId = id,
                ComponentType = type,
                ComponentTypeName = typeName,
                Direction = direction
            });
        }

        return results;
    }

    /// <summary>
    /// Every solution that carries a given object. Answers the layering question - whether
    /// something is only in the default solution, or owned by an unmanaged solution that should
    /// be edited instead.
    /// </summary>
    public async Task<IReadOnlyList<ContainingSolution>> GetContainingSolutionsAsync(
        Guid objectId,
        CancellationToken ct = default)
    {
        var url = EnvironmentUrl + ApiPath +
                  $"solutioncomponents?$select=componenttype&$filter=objectid eq {objectId}" +
                  "&$expand=solutionid($select=solutionid,uniquename,friendlyname,ismanaged,version)";

        var solutions = new List<ContainingSolution>();
        var seen = new HashSet<Guid>();

        while (url.Length > 0)
        {
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    if (!row.TryGetProperty("solutionid", out var solution) ||
                        solution.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!Guid.TryParse(JsonHelper.GetString(solution, "solutionid"), out var id)) continue;
                    if (!seen.Add(id)) continue;

                    var uniqueName = JsonHelper.GetString(solution, "uniquename") ?? id.ToString();

                    solutions.Add(new ContainingSolution
                    {
                        SolutionId = id,
                        UniqueName = uniqueName,
                        FriendlyName = JsonHelper.GetString(solution, "friendlyname") ?? uniqueName,
                        IsManaged = JsonHelper.GetBool(solution, "ismanaged") ?? false,
                        Version = JsonHelper.GetString(solution, "version")
                    });
                }
            }

            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }

        return solutions
            .OrderBy(s => s.IsManaged ? 1 : 0)
            .ThenBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads a component's solution layer stack from msdyn_componentlayers - the same
    /// undocumented virtual table the maker portal's "View solution layers" panel uses. There is
    /// no public Web API for this, so it is only attempted for the component types in
    /// <see cref="ComponentTypes.GetSdkName"/>; null means "not supported for this type", not
    /// "no layers".
    /// </summary>
    public async Task<IReadOnlyList<ComponentLayer>?> GetComponentLayersAsync(
        Guid objectId,
        int componentType,
        CancellationToken ct = default)
    {
        var sdkName = ComponentTypes.GetSdkName(componentType);
        if (sdkName is null) return null;

        var url = EnvironmentUrl + ApiPath +
                  $"msdyn_componentlayers?$filter=msdyn_componentid eq '{objectId}' " +
                  $"and msdyn_solutioncomponentname eq '{sdkName}'";

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var layers = new List<ComponentLayer>();
        if (doc.RootElement.TryGetProperty("value", out var value))
        {
            foreach (var row in value.EnumerateArray())
            {
                layers.Add(new ComponentLayer
                {
                    SolutionName = JsonHelper.GetString(row, "msdyn_solutionname") ?? "(unknown)",
                    PublisherName = JsonHelper.GetString(row, "msdyn_publishername"),
                    Order = JsonHelper.GetInt(row, "msdyn_order") ?? 0
                });
            }
        }

        return layers.OrderBy(l => l.Order).ToList();
    }

    /// <summary>
    /// Layer lookups are one Dataverse call per component - there is no bulk equivalent - so this
    /// runs a bounded number concurrently, the same tradeoff <see cref="GetComponentsInParallelAsync"/>
    /// makes, and never lets one component's failure abort the rest.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ComponentLayer>?>> GetComponentLayersBulkAsync(
        IReadOnlyList<(Guid ObjectId, int ComponentType)> targets,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(4);
        var results = new System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlyList<ComponentLayer>?>();
        var done = 0;

        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[target.ObjectId] = await GetComponentLayersAsync(target.ObjectId, target.ComponentType, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Best effort - one component's failure should not lose the results for the rest.
                results[target.ObjectId] = null;
            }
            finally
            {
                progress?.Report(Interlocked.Increment(ref done));
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
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
        // Paging is inherently serial - each page needs the previous page's cookie - so the only
        // way to overlap requests is to split the solution into independent slices. Component
        // type ranges partition the rows exactly, with no overlap and no gaps.
        if (await SupportsTypeRangeFilterAsync(solutionId, ct).ConfigureAwait(false))
        {
            try
            {
                var parallel = await GetComponentsInParallelAsync(solutionId, progress, ct).ConfigureAwait(false);
                await ApplyProcessCategoriesAsync(parallel, ct).ConfigureAwait(false);
                return parallel;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DataverseException)
            {
                // Partitioned read failed mid-flight - fall back to the single serial query.
            }
        }

        var items = new List<SolutionComponentItem>();
        var total = 0;

        await ReadAllPagesAsync(
            BuildComponentsUrl(solutionId, null),
            items,
            added => progress?.Report(total += added),
            ct).ConfigureAwait(false);

        await ApplyProcessCategoriesAsync(items, ct).ConfigureAwait(false);
        return items;
    }

    /// <summary>
    /// Fills in the sub type for processes from the workflow table's own category option set -
    /// the authoritative source for telling a cloud flow from a business rule, BPF or action.
    /// The component summary does not carry it.
    /// </summary>
    private async Task ApplyProcessCategoriesAsync(IReadOnlyList<SolutionComponentItem> items, CancellationToken ct)
    {
        var processes = items.Where(i => i.ComponentType == 29 && i.ObjectId != Guid.Empty).ToList();
        if (processes.Count == 0) return;

        try
        {
            var categories = new Dictionary<Guid, string>();
            var url = EnvironmentUrl + ApiPath + "workflows?$select=workflowid,category";

            while (url.Length > 0)
            {
                using var doc = await GetJsonAsync(url, ct, includeFormattedValues: true).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty("value", out var value))
                {
                    foreach (var row in value.EnumerateArray())
                    {
                        if (!Guid.TryParse(JsonHelper.GetString(row, "workflowid"), out var id)) continue;

                        var label = JsonHelper.GetString(row, "category@OData.Community.Display.V1.FormattedValue");

                        if (string.IsNullOrWhiteSpace(label))
                        {
                            var category = JsonHelper.GetInt(row, "category");
                            label = category is null ? null : ComponentTypes.GetProcessCategoryName(category.Value);
                        }

                        if (!string.IsNullOrWhiteSpace(label)) categories[id] = label!;
                    }
                }

                url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
            }

            foreach (var process in processes)
            {
                if (!categories.TryGetValue(process.ObjectId, out var label)) continue;

                process.SubType = label;
                // The search index was built before the category was known.
                process.BuildSearchIndex();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best effort - processes simply keep whatever sub type the summary supplied.
        }
    }

    private string BuildComponentsUrl(Guid solutionId, (int Low, int High)? typeRange)
    {
        var filter = $"(msdyn_solutionid eq {solutionId})";

        if (typeRange is { } range)
        {
            filter += range.Low == range.High
                ? $" and (msdyn_componenttype eq {range.Low})"
                : $" and (msdyn_componenttype ge {range.Low} and msdyn_componenttype le {range.High})";
        }

        return EnvironmentUrl + ApiPath + "msdyn_solutioncomponentsummaries?$filter=" + filter;
    }

    /// <summary>
    /// Contiguous, gap-free component type ranges. Split finely where the heavy types live
    /// (tables, columns, choices, processes, forms) so the slices finish in comparable times.
    /// </summary>
    private static readonly (int Low, int High)[] TypeRanges =
    {
        (0, 1), (2, 2), (3, 8), (9, 9), (10, 25), (26, 28), (29, 29), (30, 58), (59, 60),
        (61, 61), (62, 89), (90, 99), (100, 149), (150, 299), (300, 300), (301, 370),
        (371, 379), (380, 399), (400, 9999), (10000, int.MaxValue)
    };

    /// <summary>
    /// Not every Dataverse version accepts range operators on the summary virtual table, so this
    /// is probed once with a single-row request before committing to the partitioned read.
    /// </summary>
    private async Task<bool> SupportsTypeRangeFilterAsync(Guid solutionId, CancellationToken ct)
    {
        try
        {
            var url = BuildComponentsUrl(solutionId, (0, 1)) + "&$top=1";
            using var _ = await GetJsonAsync(url, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DataverseException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<SolutionComponentItem>> GetComponentsInParallelAsync(
        Guid solutionId,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        // Dataverse's service protection limits penalise aggressive fan-out; a handful of
        // concurrent readers captures most of the win without tripping them.
        using var gate = new SemaphoreSlim(4);
        var total = 0;

        var tasks = TypeRanges.Select(async range =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var slice = new List<SolutionComponentItem>();
                await ReadAllPagesAsync(
                    BuildComponentsUrl(solutionId, range),
                    slice,
                    added => progress?.Report(Interlocked.Add(ref total, added)),
                    ct).ConfigureAwait(false);
                return slice;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var slices = await Task.WhenAll(tasks).ConfigureAwait(false);
        return slices.SelectMany(s => s).ToList();
    }

    private async Task ReadAllPagesAsync(
        string url,
        List<SolutionComponentItem> into,
        Action<int> onRowsAdded,
        CancellationToken ct)
    {
        while (url.Length > 0 && into.Count < MaxRows)
        {
            ct.ThrowIfCancellationRequested();

            using var doc = await GetJsonAsync(url, ct, includeFormattedValues: true).ConfigureAwait(false);

            var added = 0;
            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    into.Add(ReadComponent(row));
                    added++;
                }
            }

            onRowsAdded(added);
            url = JsonHelper.GetString(doc.RootElement, "@odata.nextLink") ?? string.Empty;
        }
    }

    private static SolutionComponentItem ReadComponent(JsonElement row)
    {
        var componentType = JsonHelper.GetInt(row, "msdyn_componenttype") ?? 0;

        // Prefer the server's own label, then the formatted value annotation, then our map.
        var typeName = JsonHelper.GetString(row, "msdyn_componenttypename")
                       ?? JsonHelper.GetString(row, "msdyn_componenttype@OData.Community.Display.V1.FormattedValue")
                       ?? ComponentTypes.GetName(componentType);

        // Kept as its own column rather than folded into the type: processes all report as
        // "Process", and only the subtype separates a cloud flow from a business rule.
        //
        // Only the server's own label is trusted here. The raw msdyn_subtype number is NOT the
        // workflow category - reading it as one labelled every process "Dialog". Processes get
        // their real category from the workflow table in a follow-up pass.
        var subType = JsonHelper.GetString(row, "msdyn_subtypename")
                      ?? JsonHelper.GetString(row, "msdyn_subtype@OData.Community.Display.V1.FormattedValue");

        var name = JsonHelper.GetString(row, "msdyn_name");
        var displayName = JsonHelper.GetString(row, "msdyn_displayname");

        var item = new SolutionComponentItem
        {
            Name = name ?? displayName ?? "(unnamed)",
            DisplayName = displayName,
            ComponentType = componentType,
            ComponentTypeName = typeName,
            SubType = string.IsNullOrWhiteSpace(subType) ? null : subType,
            ComponentLogicalName = JsonHelper.GetString(row, "msdyn_componentlogicalname"),
            ObjectId = Guid.TryParse(JsonHelper.GetString(row, "msdyn_objectid"), out var objectId) ? objectId : Guid.Empty,
            SchemaName = JsonHelper.GetString(row, "msdyn_schemaname"),
            WorkflowIdUnique = Guid.TryParse(JsonHelper.GetString(row, "msdyn_workflowidunique"), out var uniqueId)
                ? uniqueId
                : null,
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
