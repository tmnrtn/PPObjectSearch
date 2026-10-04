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
/// The handful of Microsoft Graph reads the admin tools need: a group, its users, and a user.
///
/// Signs in through the tab's own account, in the environment's tenant, so the Entra side of a
/// comparison is always the same directory the Dataverse environment belongs to. The default
/// public client is pre-consented for Graph; a custom <c>ClientId</c> needs GroupMember.Read.All
/// and User.Read.All (or Directory.Read.All) delegated permissions for this to work.
/// </summary>
public sealed class GraphClient : IDisposable
{
    private const string Resource = "https://graph.microsoft.com";
    private const string BaseUrl = "https://graph.microsoft.com/v1.0/";
    private const string UserSelect = "id,displayName,userPrincipalName,mail,accountEnabled,userType";

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

            url = Str(doc.RootElement, "@odata.nextLink") ?? string.Empty;
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

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public void Dispose() => _http.Dispose();
}
