using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// One Dataverse environment behind a <see cref="FakeHttpHandler"/>, with what an environment tab
/// reads on connecting: who am I, the organization, its type, table metadata, the solutions and
/// each solution's objects. Every environment has a host of its own, so what the user library
/// keeps per environment - favourites, saved searches, recent objects - never leaks between tests.
/// </summary>
internal sealed class FakeEnvironment
{
    public const string EnvironmentId = "aaaaaaaa-0000-0000-0000-000000000001";

    private readonly List<object> _solutions = new();
    private readonly Dictionary<Guid, List<Dictionary<string, object?>>> _components = new();
    private readonly List<Dictionary<string, object?>> _workflows = new();

    public FakeEnvironment(string sku = "Sandbox")
    {
        Url = $"https://t{Guid.NewGuid():N}.crm11.dynamics.com";
        Sku = sku;
    }

    public string Url { get; }
    public string Host => new Uri(Url).Host;
    public string Sku { get; set; }
    public string? FriendlyName { get; set; } = "Contoso Dev";
    public bool WithOrganizationEnvironmentId { get; set; } = true;

    /// <summary>Routes a test adds; tried before the standard ones.</summary>
    public FakeHttpHandler Handler { get; } = new();

    private bool _standardRoutes;

    public Guid AddSolution(string friendly, string unique, bool managed = false)
    {
        var id = Guid.NewGuid();
        _solutions.Add(new Dictionary<string, object?>
        {
            ["solutionid"] = id, ["uniquename"] = unique, ["friendlyname"] = friendly,
            ["ismanaged"] = managed, ["version"] = "1.0.0.0", ["publisherid"] = new { friendlyname = "Contoso" }
        });
        _components[id] = new List<Dictionary<string, object?>>();
        return id;
    }

    public Guid AddComponent(Guid solutionId, int type, string name, string? displayName = null, bool managed = false,
        string? subType = null, string? logicalName = null, int? workflowCategory = null, Guid? id = null)
    {
        var objectId = id ?? Guid.NewGuid();
        _components[solutionId].Add(new Dictionary<string, object?>
        {
            ["msdyn_componenttype"] = type, ["msdyn_name"] = name, ["msdyn_displayname"] = displayName,
            ["msdyn_objectid"] = objectId, ["msdyn_ismanaged"] = managed, ["msdyn_subtypename"] = subType,
            ["msdyn_componentlogicalname"] = logicalName
        });

        if (workflowCategory is { } category)
        {
            _workflows.Add(new Dictionary<string, object?> { ["workflowid"] = objectId, ["category"] = category });
        }

        return objectId;
    }

    public static string Value(IEnumerable<object> rows) => JsonSerializer.Serialize(new { value = rows });

    /// <summary>The routes every connect needs, added once - after anything the test routed first.</summary>
    public FakeHttpHandler Routes()
    {
        if (_standardRoutes) return Handler;
        _standardRoutes = true;

        Handler
            .OnJson(HttpMethod.Get, "/WhoAmI", "{\"UserId\":\"11111111-0000-0000-0000-000000000001\"}")
            .On(HttpMethod.Get, "RetrieveCurrentOrganization", _ => FakeHttpHandler.Json(JsonSerializer.Serialize(new
            {
                Detail = new
                {
                    FriendlyName,
                    UniqueName = "unq1",
                    EnvironmentId = WithOrganizationEnvironmentId ? EnvironmentId : null
                }
            })))
            .On(HttpMethod.Get, "BusinessAppPlatform/environments", _ => FakeHttpHandler.Json(Value(new object[]
            {
                new
                {
                    name = EnvironmentId,
                    properties = new
                    {
                        displayName = FriendlyName, environmentSku = Sku,
                        linkedEnvironmentMetadata = new { instanceApiUrl = Url }
                    }
                }
            })))
            .OnJson(HttpMethod.Get, "/api/discovery/v2.0/Instances", Value(new object[]
            {
                new { ApiUrl = Url, EnvironmentId }
            }))
            .OnJson(HttpMethod.Get, "EntityDefinitions?", Value(new object[]
            {
                new { LogicalName = "account", MetadataId = "70816501-edb9-4740-a16c-6a5efbc05d84", EntitySetName = "accounts" }
            }))
            .On(HttpMethod.Get, "solutions?", _ => FakeHttpHandler.Json(Value(_solutions)))
            // The partitioned read's probe is refused, so each solution is read in one query.
            .OnError(HttpMethod.Get, "msdyn_componenttype le 1)&$top=1", HttpStatusCode.BadRequest, "Range filters are not supported")
            .OnAsync(HttpMethod.Get, "msdyn_solutioncomponentsummaries", ComponentsAsync)
            .On(HttpMethod.Get, "workflows?$select=workflowid,category", _ => FakeHttpHandler.Json(Value(_workflows)));

        return Handler;
    }

    /// <summary>Solutions whose objects cannot be read: the read fails with a server error.</summary>
    public HashSet<Guid> Failing { get; } = new();

    /// <summary>A solution's objects are held back until its gate is opened - for loads that overlap.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> Gates { get; } = new();

    public TaskCompletionSource Gate(Guid solutionId) =>
        Gates[solutionId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<HttpResponseMessage> ComponentsAsync(RecordedRequest request)
    {
        var (id, rows) = _components.First(c => request.Url.Contains(c.Key.ToString(), StringComparison.OrdinalIgnoreCase));

        if (Gates.TryGetValue(id, out var gate)) await gate.Task.ConfigureAwait(false);

        return Failing.Contains(id)
            ? FakeHttpHandler.Json(FakeHttpHandler.ErrorJson("The solution could not be read"), HttpStatusCode.InternalServerError)
            : FakeHttpHandler.Json(Value(rows));
    }

    /// <summary>An auth context with the tenant already known, so connecting never asks the network who it is.</summary>
    public static EnvironmentAuthContext Auth() =>
        new((resource, _) => Task.FromResult<string?>("token:" + resource), tenantId: "22222222-0000-0000-0000-000000000002");

    public EnvironmentSessionViewModel Session(AppSettings? settings = null, string? solution = null)
    {
        Routes();
        return new EnvironmentSessionViewModel(new AuthenticationService(), settings ?? new AppSettings(),
            new TabState { EnvironmentUrl = Url, SolutionUniqueName = solution },
            Auth, (auth, url) => new DataverseClient(auth, url, new LikeTheNetwork(Handler)));
    }

    /// <summary>A session that has connected and loaded its first solution.</summary>
    public async Task<EnvironmentSessionViewModel> ConnectedAsync(AppSettings? settings = null, string? solution = null)
    {
        var session = Session(settings, solution);
        await session.ConnectAsync();
        return session;
    }

    /// <summary>
    /// Answers later and on another thread, as the network does - never inline. Progress reported
    /// along the way then reaches the tab before the work's own continuation, as it does in the app.
    /// </summary>
    private sealed class LikeTheNetwork(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(1, CancellationToken.None).ConfigureAwait(false);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
