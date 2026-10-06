using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;

namespace PPObjectSearch.PowerApps;

public sealed class PowerAppsException : Exception
{
    public PowerAppsException(string message, HttpStatusCode? statusCode = null) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>Someone a canvas app is shared with, and how.</summary>
public sealed record AppPermission(string Principal, string? Email, string Type, string Role);

/// <summary>
/// Reads what Dataverse does not hold about a canvas app - who it is shared with - from the Power
/// Apps API. Read-only. Signs in as the tab's account; an app's sharing is shown to its owners and
/// co-owners, and to environment admins through the admin scope.
/// </summary>
public sealed class PowerAppsClient : IDisposable
{
    private const string ApiVersion = "api-version=2016-11-01";
    private const int MaxPages = 20;

    private readonly EnvironmentAuthContext _auth;
    private readonly HttpClient _http;

    public PowerAppsClient(EnvironmentAuthContext auth) : this(auth, handler: null)
    {
    }

    /// <summary>For tests: every request goes through <paramref name="handler"/> rather than the network.</summary>
    internal PowerAppsClient(EnvironmentAuthContext auth, HttpMessageHandler? handler)
    {
        _auth = auth;
        _http = handler is null ? new HttpClient(Core.RetryHandler.Shared, disposeHandler: false) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    private string BaseUrl => (_auth.Cloud.PowerAppsApi ?? throw new PowerAppsException(
        $"The Power Apps API is not known for the {_auth.Cloud.Name} cloud.")) + "/providers/Microsoft.PowerApps/";

    /// <summary>
    /// Everyone a canvas app is shared with - users, groups and the whole organisation - with their
    /// role. Tries the maker's route, then the admin route if that is refused.
    /// </summary>
    public async Task<IReadOnlyList<AppPermission>> GetAppPermissionsAsync(string environmentId, Guid appId, CancellationToken ct = default)
    {
        var env = Uri.EscapeDataString(environmentId);
        var maker = $"{BaseUrl}apps/{appId}/permissions?{ApiVersion}&$filter=" + Uri.EscapeDataString($"environment eq '{environmentId}'");
        var admin = $"{BaseUrl}scopes/admin/environments/{env}/apps/{appId}/permissions?{ApiVersion}";

        try
        {
            return await ReadPermissionsAsync(maker, ct).ConfigureAwait(false);
        }
        catch (PowerAppsException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            try
            {
                return await ReadPermissionsAsync(admin, ct).ConfigureAwait(false);
            }
            catch (PowerAppsException adminEx) when (adminEx.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw new PowerAppsException(
                    "Only the app's owners, co-owners and environment admins can see who it is shared with.", adminEx.StatusCode);
            }
        }
    }

    private async Task<IReadOnlyList<AppPermission>> ReadPermissionsAsync(string url, CancellationToken ct)
    {
        var permissions = new List<AppPermission>();
        string? next = url;

        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            using var doc = await SendAsync(next, ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in value.EnumerateArray())
                {
                    if (!row.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) continue;

                    var principal = properties.TryGetProperty("principal", out var p) ? p : default;
                    var type = JsonHelper.GetString(principal, "type") ?? "User";

                    permissions.Add(new AppPermission(
                        type.Equals("Tenant", StringComparison.OrdinalIgnoreCase)
                            ? "Everyone in the organisation"
                            : JsonHelper.GetString(principal, "displayName") ?? JsonHelper.GetString(principal, "email")
                              ?? JsonHelper.GetString(principal, "id") ?? "(unknown)",
                        JsonHelper.GetString(principal, "email"),
                        type,
                        RoleLabel(JsonHelper.GetString(properties, "roleName"))));
                }
            }

            next = Links.SameHostNext(JsonHelper.GetString(doc.RootElement, "nextLink"), next,
                message => new PowerAppsException(message));
        }

        return permissions
            .OrderBy(p => p.Role == "Owner" ? 0 : p.Role == "Co-owner" ? 1 : 2)
            .ThenBy(p => p.Principal, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static string RoleLabel(string? role) => role switch
    {
        "Owner" => "Owner",
        "CanEdit" => "Co-owner",
        "CanView" => "User",
        "CanViewWithShare" => "User (can share)",
        null or "" => "(unknown)",
        _ => role
    };

    private async Task<JsonDocument> SendAsync(string url, CancellationToken ct)
    {
        string token;
        try
        {
            token = await _auth.GetTokenAsync(_auth.Cloud.PowerAppsResource ?? throw new PowerAppsException(
                $"The Power Apps API is not known for the {_auth.Cloud.Name} cloud."), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (PowerAppsException) { throw; }
        catch (Exception ex)
        {
            throw new PowerAppsException("Could not sign in to Power Apps - " + ex.Message);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new PowerAppsException($"Power Apps: {(int)response.StatusCode} {ErrorMessage(body)}", response.StatusCode);
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // not JSON
        }

        return body.Length > 300 ? body[..300] : body;
    }

    public void Dispose() => _http.Dispose();
}
