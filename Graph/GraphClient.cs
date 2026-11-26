using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Models;

namespace PPObjectSearch.Graph;

public sealed class GraphException : Exception
{
    public GraphException(string message, HttpStatusCode? statusCode = null) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// The handful of Microsoft Graph reads the admin tools need: a group, its users, and a user, their licences and groups -
/// and every user's licences at once, for the Users tab's licence filter.
///
/// Signs in through the tab's own account, in the environment's tenant, so the Entra side of a
/// comparison is always the same directory the Dataverse environment belongs to. The default
/// public client is pre-consented for Graph; a custom <c>ClientId</c> needs GroupMember.Read.All
/// and User.Read.All (or Directory.Read.All) delegated permissions for this to work.
/// </summary>
public sealed class GraphClient : IDisposable
{
    // Graph lives at a different host in each sovereign cloud.
    private string Resource => _auth.Cloud.Graph;
    private string BaseUrl => _auth.Cloud.Graph + "/v1.0/";
    private const string UserSelect = "id,displayName,userPrincipalName,mail,accountEnabled,userType";
    private const string GroupSelect = "id,displayName,description,securityEnabled,mailEnabled,groupTypes";

    /// <summary>A group big enough to hit this is not one to reconcile by eye.</summary>
    private const int MaxMembers = 100_000;

    private readonly EnvironmentAuthContext _auth;
    private readonly HttpClient _http;

    public GraphClient(EnvironmentAuthContext auth) : this(auth, handler: null)
    {
    }

    /// <summary>For tests: every request goes through <paramref name="handler"/> rather than the network.</summary>
    internal GraphClient(EnvironmentAuthContext auth, HttpMessageHandler? handler)
    {
        _auth = auth;
        // The shared pipeline retries throttled and transient failures; tests pass their own.
        _http = handler is null ? new HttpClient(Core.RetryHandler.Shared, disposeHandler: false) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    /// <summary>The group, or null where Graph says it does not exist.</summary>
    public async Task<EntraGroup?> GetGroupAsync(string groupId, CancellationToken ct = default)
    {
        using var doc = await GetJsonOrNullAsync(
            BaseUrl + $"groups/{Uri.EscapeDataString(groupId)}?$select=id,displayName,securityEnabled,mailEnabled", ct)
            .ConfigureAwait(false);

        if (doc is null) return null;

        var root = doc.RootElement;
        return new EntraGroup(
            Str(root, "id") ?? groupId,
            Str(root, "displayName"),
            Bool(root, "securityEnabled"),
            Bool(root, "mailEnabled"));
    }

    /// <summary>
    /// Every user in the group, nested groups included - Dataverse group teams honour nested
    /// membership, so direct members alone would report people as missing who are not.
    /// </summary>
    public async Task<IReadOnlyList<EntraUser>> GetGroupTransitiveUsersAsync(string groupId, CancellationToken ct = default)
    {
        var users = new Dictionary<string, EntraUser>(StringComparer.OrdinalIgnoreCase);
        var url = BaseUrl + $"groups/{Uri.EscapeDataString(groupId)}/transitiveMembers/microsoft.graph.user" +
                  $"?$select={UserSelect}&$top=999";

        await ReadAllPagesAsync(url, row =>
        {
            // A user reachable through several nested groups is listed once per route.
            if (ReadUser(row) is { } user) users.TryAdd(user.Id, user);
        }, ct).ConfigureAwait(false);

        return users.Values.ToList();
    }

    /// <summary>Transitive members that are not users - nested groups, devices, service principals - by type.</summary>
    public async Task<IReadOnlyDictionary<string, int>> GetNonUserMemberCountsAsync(string groupId, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var url = BaseUrl + $"groups/{Uri.EscapeDataString(groupId)}/transitiveMembers?$select=id&$top=999";

        await ReadAllPagesAsync(url, row =>
        {
            var type = Str(row, "@odata.type")?.Replace("#microsoft.graph.", string.Empty) ?? "unknown";
            if (type == "user" || !seen.Add(Str(row, "id") ?? string.Empty)) return;
            counts[type] = counts.GetValueOrDefault(type) + 1;
        }, ct).ConfigureAwait(false);

        return counts;
    }

    /// <summary>A user by object id or UPN, or null where there is no such user.</summary>
    public async Task<EntraUser?> TryGetUserAsync(string idOrUpn, CancellationToken ct = default)
    {
        using var doc = await GetJsonOrNullAsync(
            BaseUrl + $"users/{Uri.EscapeDataString(idOrUpn)}?$select={UserSelect}", ct).ConfigureAwait(false);

        return doc is null ? null : ReadUser(doc.RootElement);
    }

    /// <summary>
    /// The licences assigned to a user, each with the service plans it has switched on - those
    /// provisioned, not the ones an admin turned off. Null where there is no such user.
    /// </summary>
    public async Task<IReadOnlyList<EntraLicense>?> GetUserLicensesAsync(string userId, CancellationToken ct = default)
    {
        var licenses = new List<EntraLicense>();
        var url = BaseUrl + $"users/{Uri.EscapeDataString(userId)}/licenseDetails?$select=skuId,skuPartNumber,servicePlans";

        try
        {
            await ReadAllPagesAsync(url, row =>
            {
                var plans = row.TryGetProperty("servicePlans", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray()
                        .Where(p => Str(p, "provisioningStatus") == "Success")
                        .Select(p => Str(p, "servicePlanName"))
                        .OfType<string>()
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : [];

                licenses.Add(new EntraLicense(Str(row, "skuId") ?? string.Empty, Str(row, "skuPartNumber"), plans));
            }, ct).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return licenses.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Requests to a <c>$batch</c> - Graph takes no more than 20.</summary>
    private const int BatchSize = 20;

    /// <summary>Batches in flight at once: quicker than one at a time, gentle enough not to be throttled hard.</summary>
    private const int BatchesInParallel = 4;

    /// <summary>Times a throttled request in a batch is asked again before it counts as failed.</summary>
    private const int ThrottledAttempts = 4;

    /// <summary>How long to wait before asking again; tests make it instant.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>
    /// The licences each user holds, by SKU - no service plans - read 20 users to a <c>$batch</c>
    /// request. A user Entra does not know holds none; a user whose read failed is left out, so
    /// they are never mistaken for someone with no licence. <paramref name="progress"/> gets how
    /// many users have been read so far.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<EntraLicense>>> GetUsersLicensesAsync(
        IReadOnlyCollection<string> userIds, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var licenses = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<EntraLicense>>(StringComparer.OrdinalIgnoreCase);
        var read = 0;
        using var gate = new SemaphoreSlim(BatchesInParallel);

        await Task.WhenAll(userIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchSize).Select(async chunk =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var (id, held) in await ReadLicenseBatchAsync(chunk, ct).ConfigureAwait(false))
                    licenses[id] = held;
                progress?.Report(Interlocked.Add(ref read, chunk.Length));
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return licenses;
    }

    /// <summary>One batch of users' licences, asking again for those Graph throttled.</summary>
    private async Task<Dictionary<string, IReadOnlyList<EntraLicense>>> ReadLicenseBatchAsync(string[] userIds, CancellationToken ct)
    {
        var licenses = new Dictionary<string, IReadOnlyList<EntraLicense>>(StringComparer.OrdinalIgnoreCase);
        var pending = userIds;

        for (var attempt = 1; pending.Length > 0 && attempt <= ThrottledAttempts; attempt++)
        {
            var body = JsonSerializer.Serialize(new
            {
                requests = pending.Select((id, i) => new
                {
                    id = i.ToString(),
                    method = "GET",
                    url = $"/users/{Uri.EscapeDataString(id)}/licenseDetails?$select=skuId,skuPartNumber"
                })
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "$batch")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var doc = await SendAsync(request, ct).ConfigureAwait(false);

            var throttled = new List<string>();
            var wait = TimeSpan.FromSeconds(attempt * 2);

            if (doc.RootElement.TryGetProperty("responses", out var responses) && responses.ValueKind == JsonValueKind.Array)
            {
                foreach (var response in responses.EnumerateArray())
                {
                    if (!int.TryParse(Str(response, "id"), out var i) || i < 0 || i >= pending.Length) continue;
                    var status = response.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? code : 0;

                    switch (status)
                    {
                        case 200:
                            licenses[pending[i]] = ReadSkus(response);
                            break;
                        case 404:
                            licenses[pending[i]] = [];
                            break;
                        case 429 or 503 or 504:
                            throttled.Add(pending[i]);
                            if (RetryAfter(response) is { } after && after > wait) wait = after;
                            break;
                    }
                }
            }

            pending = throttled.ToArray();
            if (pending.Length > 0 && attempt < ThrottledAttempts) await Delay(wait, ct).ConfigureAwait(false);
        }

        return licenses;
    }

    private static List<EntraLicense> ReadSkus(JsonElement response) =>
        response.TryGetProperty("body", out var body) && body.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Select(row => new EntraLicense(Str(row, "skuId") ?? string.Empty, Str(row, "skuPartNumber"), []))
                .ToList()
            : [];

    /// <summary>The Retry-After a throttled request in a batch carries, capped at a minute.</summary>
    private static TimeSpan? RetryAfter(JsonElement response)
    {
        if (!response.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object) return null;

        foreach (var header in headers.EnumerateObject())
        {
            if (header.Name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase) &&
                header.Value.ValueKind == JsonValueKind.String && int.TryParse(header.Value.GetString(), out var seconds))
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
            }
        }

        return null;
    }

    /// <summary>
    /// The groups a user is in, nested membership included, each marked direct or not - the
    /// groups behind a group team or a group-assigned licence count whichever way the user is in
    /// them. Directory roles and administrative units are left out. Null where there is no such user.
    /// </summary>
    public async Task<IReadOnlyList<EntraUserGroup>?> GetUserGroupsAsync(string userId, CancellationToken ct = default)
    {
        var user = BaseUrl + $"users/{Uri.EscapeDataString(userId)}/";
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await ReadAllPagesAsync(user + "memberOf/microsoft.graph.group?$select=id&$top=999", row =>
            {
                if (Str(row, "id") is { } id) direct.Add(id);
            }, ct).ConfigureAwait(false);

            await ReadAllPagesAsync(user + $"transitiveMemberOf/microsoft.graph.group?$select={GroupSelect}&$top=999", row =>
            {
                // Clone: the row's document is disposed with its page.
                if (Str(row, "id") is { } id) groups.TryAdd(id, row.Clone());
            }, ct).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return groups.Values
            .Select(row => ReadUserGroup(row, direct))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Asks Entra directly whether the user is a transitive member of the group - a second
    /// opinion for anyone the group listing did not include. Null if the question failed.
    /// </summary>
    public async Task<bool?> IsTransitiveMemberAsync(string userId, string groupId, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { groupIds = new[] { groupId } });
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + $"users/{Uri.EscapeDataString(userId)}/checkMemberGroups")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var doc = await SendAsync(request, ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("value", out var value)) return false;

            return value.EnumerateArray().Any(v =>
                string.Equals(v.GetString(), groupId, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- plumbing

    private async Task ReadAllPagesAsync(string url, Action<JsonElement> onRow, CancellationToken ct)
    {
        var read = 0;

        while (!string.IsNullOrEmpty(url))
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var doc = await SendAsync(request, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var row in value.EnumerateArray())
                {
                    onRow(row);
                    read++;
                }
            }

            if (read >= MaxMembers)
            {
                throw new GraphException(
                    $"The group has more than {MaxMembers:N0} members, which is more than this tool will read.");
            }

            url = Core.Links.SameHostNext(Str(doc.RootElement, "@odata.nextLink"), url, m => new GraphException(m)) ?? string.Empty;
        }
    }

    private async Task<JsonDocument?> GetJsonOrNullAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            return await SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync(Resource, ct).ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var message = ExtractError(body);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                message += " - the signed-in account (or the app's client id) may lack the Graph permissions " +
                           "to read groups and users (GroupMember.Read.All, User.Read.All).";
            }

            throw new GraphException($"Microsoft Graph: {(int)response.StatusCode} {message}", response.StatusCode);
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
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
            // not JSON
        }

        return body.Length > 300 ? body[..300] + "..." : body;
    }

    private static EntraUser? ReadUser(JsonElement row)
    {
        var id = Str(row, "id");
        if (id is null) return null;

        return new EntraUser(
            id,
            Str(row, "displayName"),
            Str(row, "userPrincipalName"),
            Str(row, "mail"),
            Bool(row, "accountEnabled"),
            Str(row, "userType"));
    }

    private static EntraUserGroup ReadUserGroup(JsonElement row, HashSet<string> direct)
    {
        var id = Str(row, "id")!;
        var groupTypes = row.TryGetProperty("groupTypes", out var types) && types.ValueKind == JsonValueKind.Array
            ? types.EnumerateArray().Select(t => t.GetString()).OfType<string>().ToList()
            : [];

        return new EntraUserGroup(
            id,
            Str(row, "displayName"),
            Str(row, "description"),
            Bool(row, "securityEnabled"),
            Bool(row, "mailEnabled"),
            groupTypes.Contains("Unified", StringComparer.OrdinalIgnoreCase),
            groupTypes.Contains("DynamicMembership", StringComparer.OrdinalIgnoreCase),
            direct.Contains(id));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public void Dispose() => _http.Dispose();
}
