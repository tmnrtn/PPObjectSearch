using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class DataverseClientCoreTests
{
    private const string Api = Fakes.ApiRoot;

    private static string Page(string rows, string? nextLink = null) =>
        "{\"value\":[" + rows + "]" + (nextLink is null ? "" : ",\"@odata.nextLink\":" + JsonSerializer.Serialize(nextLink)) + "}";

    private static HttpResponseMessage BatchResponse(string innerStatusLine, string innerJson) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "--batchresponse_1\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n" +
                innerStatusLine + "\r\nContent-Type: application/json; odata.metadata=minimal\r\nOData-Version: 4.0\r\n\r\n" +
                innerJson + "\r\n--batchresponse_1--\r\n",
                Encoding.UTF8, "multipart/mixed")
        };

    // ---- NormalizeEnvironmentUrl ----

    [Theory]
    [InlineData("contoso.crm11.dynamics.com", "https://contoso.crm11.dynamics.com")]
    [InlineData("https://contoso.crm11.dynamics.com/", "https://contoso.crm11.dynamics.com")]
    [InlineData("  https://contoso.crm11.dynamics.com/main.aspx?appid=1#x  ", "https://contoso.crm11.dynamics.com")]
    [InlineData("https://contoso.crm11.dynamics.com/api/data/v9.2/", "https://contoso.crm11.dynamics.com")]
    [InlineData("http://localhost:5555/org", "http://localhost:5555")]
    [InlineData("contoso.crm.dynamics.com/some/path", "https://contoso.crm.dynamics.com")]
    public void Normalize_adds_scheme_and_drops_path(string input, string expected)
    {
        Assert.Equal(expected, DataverseClient.NormalizeEnvironmentUrl(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_rejects_empty_input_with_an_example(string? input)
    {
        var ex = Assert.Throws<DataverseException>(() => DataverseClient.NormalizeEnvironmentUrl(input!));
        Assert.Contains("Enter an environment URL", ex.Message);
    }

    [Theory]
    [InlineData("ftp://contoso.crm.dynamics.com")]
    [InlineData("https://")]
    [InlineData("https://bad host name")]
    public void Normalize_rejects_invalid_urls(string input)
    {
        var ex = Assert.Throws<DataverseException>(() => DataverseClient.NormalizeEnvironmentUrl(input));
        Assert.Contains("is not a valid environment URL", ex.Message);
    }

    [Fact]
    public void Constructor_normalizes_the_environment_url()
    {
        using var client = Fakes.Dataverse(new FakeHttpHandler(), "contoso.crm11.dynamics.com/main.aspx");
        Assert.Equal("https://contoso.crm11.dynamics.com", client.EnvironmentUrl);
    }

    // ---- GET basics ----

    [Fact]
    public async Task WhoAmI_sends_bearer_token_and_odata_headers_and_returns_user_id()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "WhoAmI", "{\"UserId\":\"u-1\",\"BusinessUnitId\":\"b\"}");
        using var client = Fakes.Dataverse(handler);

        var userId = await client.WhoAmIAsync();

        Assert.Equal("u-1", userId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Api + "WhoAmI", request.Url);
        Assert.Equal("Bearer token:" + Fakes.EnvironmentUrl, request.Authorization);
        Assert.Equal("4.0", request.Header("OData-Version"));
        Assert.Equal("4.0", request.Header("OData-MaxVersion"));
        Assert.Contains("application/json", request.Header("Accept"));
        Assert.Equal("odata.maxpagesize=5000", request.Header("Prefer"));
    }

    [Fact]
    public async Task WhoAmI_without_user_id_returns_empty_string()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "WhoAmI", "{}");
        using var client = Fakes.Dataverse(handler);

        Assert.Equal(string.Empty, await client.WhoAmIAsync());
    }

    [Fact]
    public async Task Error_message_is_taken_from_odata_error_json()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "WhoAmI", HttpStatusCode.Forbidden, "Principal user is missing prvReadX");
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.WhoAmIAsync());

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("403 Forbidden: Principal user is missing prvReadX", ex.Message);
    }

    [Fact]
    public async Task Html_error_page_is_summarised_rather_than_dumped()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "WhoAmI", _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("  <!DOCTYPE html><html><body>Bad gateway</body></html>", Encoding.UTF8, "text/html")
        });
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.WhoAmIAsync());

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.EndsWith(": the server returned an HTML error page.", ex.Message);
        Assert.DoesNotContain("<html>", ex.Message);
    }

    [Fact]
    public async Task Long_plain_text_error_is_truncated_to_300_characters()
    {
        var body = new string('e', 500);
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "WhoAmI", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(body)
        });
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.WhoAmIAsync());

        Assert.Equal("500 Internal Server Error: " + new string('e', 300) + "...", ex.Message);
    }

    [Fact]
    public async Task Short_plain_text_error_is_kept_whole()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "WhoAmI", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("try later")
        });
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.WhoAmIAsync());

        Assert.Equal("503 Service Unavailable: try later", ex.Message);
    }

    // ---- $batch fallback ----

    [Fact]
    public async Task Url_over_the_limit_goes_through_batch_without_trying_the_get()
    {
        var longNextLink = Api + "solutions?$skiptoken=" + new string('x', 2000);
        var solutionId = Guid.NewGuid();

        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "$batch", _ => BatchResponse("HTTP/1.1 200 OK",
                Page($"{{\"solutionid\":\"{solutionId}\",\"uniquename\":\"second\"}}")))
            .OnJson(HttpMethod.Get, "solutions?$select", Page($"{{\"solutionid\":\"{Guid.NewGuid()}\",\"uniquename\":\"first\"}}", longNextLink));
        using var client = Fakes.Dataverse(handler);

        var solutions = await client.GetSolutionsAsync();

        Assert.Equal(new[] { "first", "second" }, solutions.Select(s => s.UniqueName));
        Assert.Equal(solutionId, solutions[1].SolutionId);

        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Get && r.Url.Length > 1800);

        var batch = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, batch.Method);
        Assert.Equal(Api + "$batch", batch.Url);
        Assert.Equal("Bearer token:" + Fakes.EnvironmentUrl, batch.Authorization);
        Assert.StartsWith("multipart/mixed;", batch.Header("Content-Type")!.Replace(" ", ""));
        Assert.Contains("boundary=batch_", batch.Header("Content-Type"));
        Assert.Contains("GET " + longNextLink + " HTTP/1.1\r\n", batch.Body);
        Assert.Contains("Prefer: odata.maxpagesize=5000\r\n", batch.Body);
        Assert.Contains("Content-Type: application/http", batch.Body);
    }

    [Fact]
    public async Task Batch_body_boundary_matches_the_content_type_boundary()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Get, "WhoAmI", HttpStatusCode.RequestUriTooLong)
            .On(HttpMethod.Post, "$batch", _ => BatchResponse("HTTP/1.1 200 OK", "{\"UserId\":\"u\"}"));
        using var client = Fakes.Dataverse(handler);

        await client.WhoAmIAsync();

        var batch = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var boundary = batch.Header("Content-Type")!.Split("boundary=")[1].Trim('"', ' ');
        Assert.StartsWith("--" + boundary + "\r\n", batch.Body);
        Assert.EndsWith("--" + boundary + "--\r\n", batch.Body);
    }

    [Fact]
    public async Task Server_414_retries_through_batch_and_parses_the_inner_json()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Get, "WhoAmI", HttpStatusCode.RequestUriTooLong)
            .On(HttpMethod.Post, "$batch", _ => BatchResponse("HTTP/1.1 200 OK", "{\"UserId\":\"from-batch\"}"));
        using var client = Fakes.Dataverse(handler);

        var userId = await client.WhoAmIAsync();

        Assert.Equal("from-batch", userId);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Post }, handler.Requests.Select(r => r.Method));
        Assert.Contains("GET " + Api + "WhoAmI HTTP/1.1", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Inner_batch_failure_throws_with_the_inner_status_and_message()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Get, "WhoAmI", HttpStatusCode.RequestUriTooLong)
            .On(HttpMethod.Post, "$batch", _ => BatchResponse("HTTP/1.1 404 Not Found", FakeHttpHandler.ErrorJson("Resource not found")));
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.WhoAmIAsync());

        Assert.Equal("404: Resource not found", ex.Message);
    }

    [Fact]
    public async Task Outer_batch_failure_throws_with_status_code()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Get, "WhoAmI", HttpStatusCode.RequestUriTooLong)
            .OnError(HttpMethod.Post, "$batch", HttpStatusCode.BadRequest, "Malformed batch");
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.WhoAmIAsync());

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("400 Bad Request: Malformed batch", ex.Message);
    }

    [Fact]
    public async Task Batch_response_without_json_throws()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Get, "WhoAmI", HttpStatusCode.RequestUriTooLong)
            .On(HttpMethod.Post, "$batch", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("--b\r\nHTTP/1.1 204 No Content\r\n\r\n--b--")
            });
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.WhoAmIAsync());

        Assert.Contains("no JSON payload", ex.Message);
    }

    // ---- RetrieveCurrentOrganization ----

    [Fact]
    public async Task Current_organization_reads_nested_identity()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "RetrieveCurrentOrganization",
            "{\"Detail\":{\"OrganizationId\":\"o\",\"FriendlyName\":\"Contoso Dev\",\"UniqueName\":\"unq123\"," +
            "\"EnvironmentId\":\"env-42\",\"Endpoints\":{\"Count\":1}}}");
        using var client = Fakes.Dataverse(handler);

        var org = await client.RetrieveCurrentOrganizationAsync();

        Assert.Equal(new OrganizationDetails("Contoso Dev", "unq123", "env-42"), org);
        Assert.Contains("AccessType=Microsoft.Dynamics.CRM.EndpointAccessType'Default'", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Current_organization_returns_null_rather_than_throwing_on_http_failure()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "RetrieveCurrentOrganization", HttpStatusCode.NotFound, "no such function");
        using var client = Fakes.Dataverse(handler);

        Assert.Null(await client.RetrieveCurrentOrganizationAsync());
    }

    [Fact]
    public async Task Current_organization_returns_null_when_there_is_no_token()
    {
        var handler = new FakeHttpHandler();
        using var client = new DataverseClient(TestAuth.NoTokens(), Fakes.EnvironmentUrl, handler);

        Assert.Null(await client.RetrieveCurrentOrganizationAsync());
    }

    [Fact]
    public async Task Current_organization_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var auth = new EnvironmentAuthContext((_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult<string?>("t"); });
        using var client = new DataverseClient(auth, Fakes.EnvironmentUrl, new FakeHttpHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RetrieveCurrentOrganizationAsync(cts.Token));
    }

    // ---- Discovery and table metadata ----

    [Fact]
    public async Task Environment_id_from_discovery_matches_this_instance()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "globaldisco.crm.dynamics.com/api/discovery/v2.0/Instances",
            "{\"value\":[{\"ApiUrl\":\"https://other.crm.dynamics.com\",\"EnvironmentId\":\"wrong\"}," +
            "{\"ApiUrl\":\"https://contoso.crm11.dynamics.com/\",\"EnvironmentId\":\"env-7\"}]}");
        using var client = Fakes.Dataverse(handler);

        Assert.Equal("env-7", await client.GetEnvironmentIdFromDiscoveryAsync());
        Assert.Equal("Bearer token:https://globaldisco.crm.dynamics.com", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task Environment_id_from_discovery_ignores_a_host_that_only_contains_this_one()
    {
        // "xcontoso.crm11.dynamics.com" contains "contoso.crm11.dynamics.com" but is another environment.
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "globaldisco.crm.dynamics.com/api/discovery/v2.0/Instances",
            "{\"value\":[{\"ApiUrl\":\"https://xcontoso.crm11.dynamics.com\",\"EnvironmentId\":\"wrong\"}," +
            "{\"Url\":\"https://CONTOSO.crm11.dynamics.com\",\"EnvironmentId\":\"env-7\"}]}");
        using var client = Fakes.Dataverse(handler);

        Assert.Equal("env-7", await client.GetEnvironmentIdFromDiscoveryAsync());
    }

    [Fact]
    public async Task Environment_id_from_discovery_is_null_on_failure()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Get, "globaldisco", HttpStatusCode.Unauthorized);
        using var client = Fakes.Dataverse(handler);

        Assert.Null(await client.GetEnvironmentIdFromDiscoveryAsync());
    }

    [Fact]
    public async Task Table_metadata_is_read_across_pages_and_keyed_case_insensitively()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "skiptoken=2", Page("{\"LogicalName\":\"contact\",\"MetadataId\":\"" + Guid.NewGuid() + "\",\"EntitySetName\":\"contacts\"}"))
            .OnJson(HttpMethod.Get, "EntityDefinitions?$select", Page(
                "{\"LogicalName\":\"account\",\"MetadataId\":\"" + id + "\",\"EntitySetName\":\"accounts\"},{\"LogicalName\":\"\"}",
                Api + "EntityDefinitions?$skiptoken=2"));
        using var client = Fakes.Dataverse(handler);

        var map = await client.GetTableMetadataAsync();

        Assert.Equal(2, map.Count);
        Assert.Equal(new TableMetadata(id, "accounts"), map["ACCOUNT"]);
        Assert.Equal("contacts", map["contact"].EntitySetName);
    }

    [Fact]
    public async Task Table_metadata_failure_yields_an_empty_map()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "EntityDefinitions", HttpStatusCode.Forbidden, "no");
        using var client = Fakes.Dataverse(handler);

        Assert.Empty(await client.GetTableMetadataAsync());
    }

    // ---- Solutions ----

    [Fact]
    public async Task Solutions_are_read_with_publisher_and_defaults_across_pages()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "skiptoken=p2", Page($"{{\"solutionid\":\"{b}\",\"uniquename\":\"Beta\",\"ismanaged\":\"true\"}}"))
            .OnJson(HttpMethod.Get, "solutions?$select", Page(
                $"{{\"solutionid\":\"{a}\",\"uniquename\":\"Alpha\",\"friendlyname\":\"Alpha Solution\",\"ismanaged\":false," +
                "\"version\":\"1.0.0.1\",\"publisherid\":{\"friendlyname\":\"Contoso\"}}," +
                "{\"solutionid\":\"not-a-guid\",\"uniquename\":\"Broken\"}",
                Api + "solutions?$skiptoken=p2"));
        using var client = Fakes.Dataverse(handler);

        var solutions = await client.GetSolutionsAsync();

        Assert.Equal(2, solutions.Count);
        Assert.Equal(a, solutions[0].SolutionId);
        Assert.Equal("Alpha Solution", solutions[0].FriendlyName);
        Assert.False(solutions[0].IsManaged);
        Assert.Equal("1.0.0.1", solutions[0].Version);
        Assert.Equal("Contoso", solutions[0].PublisherName);

        Assert.Equal("Beta", solutions[1].FriendlyName);
        Assert.True(solutions[1].IsManaged);
        Assert.Null(solutions[1].PublisherName);

        var first = handler.Requests[0].Url;
        Assert.Contains("$filter=isvisible eq true", first);
        Assert.Contains("$orderby=friendlyname asc", first);
        Assert.Contains("$expand=publisherid($select=friendlyname)", first);
        Assert.Equal(Api + "solutions?$skiptoken=p2", handler.Requests[1].Url);
    }

    // ---- Solution components ----

    private static string ComponentRow(int type, string? typeName = null, string? name = "n", Guid? objectId = null,
        string? extra = null)
    {
        var fields = new List<string>
        {
            $"\"msdyn_componenttype\":{type}",
            $"\"msdyn_objectid\":\"{objectId ?? Guid.NewGuid()}\""
        };
        if (typeName is not null) fields.Add($"\"msdyn_componenttypename\":{JsonSerializer.Serialize(typeName)}");
        if (name is not null) fields.Add($"\"msdyn_name\":{JsonSerializer.Serialize(name)}");
        if (extra is not null) fields.Add(extra);
        return "{" + string.Join(",", fields) + "}";
    }

    private static FakeHttpHandler SerialComponents(string firstPage, string? secondPage = null)
    {
        var handler = new FakeHttpHandler()
            // Refuse the range-filter probe so the single serial query is used.
            .OnError(HttpMethod.Get, "$top=1", HttpStatusCode.BadRequest, "range operators not supported");

        if (secondPage is not null) handler.OnJson(HttpMethod.Get, "skiptoken=c2", secondPage);

        return handler
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", firstPage)
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,category", Page(""));
    }

    [Fact]
    public async Task Components_read_every_field_of_the_summary_row()
    {
        var solutionId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var unique = Guid.NewGuid();
        var row = ComponentRow(61, "Web Resource", "new_script.js", objectId,
            "\"msdyn_displayname\":\"Script\",\"msdyn_componentlogicalname\":\"webresource\",\"msdyn_schemaname\":\"new_Script\"," +
            $"\"msdyn_workflowidunique\":\"{unique}\",\"msdyn_primaryentityname\":\"account\",\"msdyn_ismanaged\":true," +
            "\"msdyn_iscustomizable\":false,\"msdyn_owner\":\"Tom\",\"msdyn_modifiedon\":\"2026-01-02T03:04:05Z\"," +
            "\"msdyn_createdon\":\"2025-12-01T00:00:00Z\",\"msdyn_subtypename\":\"Script (JScript)\"");

        var handler = SerialComponents(Page(row));
        using var client = Fakes.Dataverse(handler);

        var items = await client.GetSolutionComponentsAsync(solutionId);

        var item = Assert.Single(items);
        Assert.Equal("new_script.js", item.Name);
        Assert.Equal("Script", item.DisplayName);
        Assert.Equal(61, item.ComponentType);
        Assert.Equal("Web Resource", item.ComponentTypeName);
        Assert.Equal("Script (JScript)", item.SubType);
        Assert.Equal("webresource", item.ComponentLogicalName);
        Assert.Equal(objectId, item.ObjectId);
        Assert.Equal("new_Script", item.SchemaName);
        Assert.Equal(unique, item.WorkflowIdUnique);
        Assert.Equal("account", item.PrimaryEntityName);
        Assert.True(item.IsManaged);
        Assert.False(item.IsCustomizable);
        Assert.Equal("Tom", item.Owner);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), item.ModifiedOn);
        Assert.Equal(new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero), item.CreatedOn);
        Assert.Contains("new_script.js", item.SearchIndex, StringComparison.OrdinalIgnoreCase);

        var read = handler.Requests.Single(r => r.Url.Contains("msdyn_solutioncomponentsummaries") && !r.Url.Contains("$top=1"));
        Assert.Equal(Api + $"msdyn_solutioncomponentsummaries?$filter=(msdyn_solutionid eq {solutionId})", read.Url);
        Assert.Contains("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"", read.Header("Prefer"));
    }

    [Fact]
    public async Task Component_defaults_when_optional_fields_are_missing()
    {
        var handler = SerialComponents(Page("{\"msdyn_componenttype\":26,\"msdyn_displayname\":\"Only display\"}," +
                                            "{\"msdyn_componenttype\":26,\"msdyn_subtypename\":\"  \"}"));
        using var client = Fakes.Dataverse(handler);

        var items = await client.GetSolutionComponentsAsync(Guid.NewGuid());

        Assert.Equal("Only display", items[0].Name);
        Assert.Equal("(unnamed)", items[1].Name);
        Assert.Equal(Guid.Empty, items[1].ObjectId);
        Assert.Null(items[1].SubType);
        Assert.True(items[1].IsCustomizable);
        Assert.False(items[1].IsManaged);
        Assert.Null(items[1].WorkflowIdUnique);
    }

    [Theory]
    // The app's own friendly name wins over the server's localization key.
    [InlineData(61, "Customization.Type_WebResource", null, "Web Resource")]
    // Unknown types use the server label, with the localization prefix stripped.
    [InlineData(12345, "Customization.Type_FancyThing", null, "FancyThing")]
    [InlineData(12345, "customization.type_lower", null, "lower")]
    [InlineData(12345, "Fancy Thing", null, "Fancy Thing")]
    // The formatted value is used when the type name column is absent.
    [InlineData(12345, null, "Formatted Thing", "Formatted Thing")]
    // Nothing from the server: the numeric fallback.
    [InlineData(12345, null, null, "Component type 12345")]
    public async Task Component_type_naming(int type, string? typeName, string? formatted, string expected)
    {
        var extra = formatted is null
            ? null
            : $"\"msdyn_componenttype@OData.Community.Display.V1.FormattedValue\":{JsonSerializer.Serialize(formatted)}";
        var handler = SerialComponents(Page(ComponentRow(type, typeName, extra: extra)));
        using var client = Fakes.Dataverse(handler);

        var item = Assert.Single(await client.GetSolutionComponentsAsync(Guid.NewGuid()));

        Assert.Equal(expected, item.ComponentTypeName);
    }

    [Fact]
    public async Task Components_follow_next_link_and_report_progress()
    {
        var handler = SerialComponents(
            Page(ComponentRow(1) + "," + ComponentRow(2), Api + "msdyn_solutioncomponentsummaries?$skiptoken=c2"),
            Page(ComponentRow(26)));
        using var client = Fakes.Dataverse(handler);
        var reports = new List<int>();

        var items = await client.GetSolutionComponentsAsync(Guid.NewGuid(), new SyncProgress(reports));

        Assert.Equal(new[] { 1, 2, 26 }, items.Select(i => i.ComponentType));
        Assert.Equal(new[] { 2, 3 }, reports);
    }

    [Fact]
    public async Task Components_are_read_in_parallel_type_slices_when_range_filters_work()
    {
        var solutionId = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "$top=1", Page(""))
            .OnJson(HttpMethod.Get, "msdyn_componenttype eq 29)", Page(ComponentRow(29, name: "flow")))
            .OnJson(HttpMethod.Get, "msdyn_componenttype ge 0 and msdyn_componenttype le 1)", Page(ComponentRow(1, name: "table")))
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", Page(""))
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,category", Page(""));
        using var client = Fakes.Dataverse(handler);

        var items = await client.GetSolutionComponentsAsync(solutionId);

        Assert.Equal(new[] { "flow", "table" }, items.Select(i => i.Name).OrderBy(n => n));

        var sliceRequests = handler.Requests.Where(r => r.Url.Contains("msdyn_solutioncomponentsummaries") && !r.Url.Contains("$top=1")).ToList();
        Assert.Equal(20, sliceRequests.Count);
        Assert.All(sliceRequests, r => Assert.Contains($"(msdyn_solutionid eq {solutionId}) and (msdyn_componenttype", r.Url));
        Assert.Contains(sliceRequests, r => r.Url.Contains($"msdyn_componenttype ge 10000 and msdyn_componenttype le {int.MaxValue}"));
    }

    [Fact]
    public async Task Parallel_read_failing_mid_flight_falls_back_to_the_serial_query()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "$top=1", Page(""))
            .OnError(HttpMethod.Get, "msdyn_componenttype eq 29)", HttpStatusCode.InternalServerError, "slice failed")
            .OnJson(HttpMethod.Get, "msdyn_componenttype", Page(""))
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", Page(ComponentRow(61, name: "serial")))
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,category", Page(""));
        using var client = Fakes.Dataverse(handler);

        var items = await client.GetSolutionComponentsAsync(Guid.NewGuid());

        Assert.Equal("serial", Assert.Single(items).Name);
    }

    [Fact]
    public async Task Processes_get_their_category_from_the_workflow_table()
    {
        var flow = Guid.NewGuid();
        var rule = Guid.NewGuid();
        var unmatched = Guid.NewGuid();

        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$top=1", HttpStatusCode.BadRequest, "no ranges")
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", Page(
                ComponentRow(29, name: "flow", objectId: flow, extra: "\"msdyn_subtypename\":\"summary label\"") + "," +
                ComponentRow(29, name: "rule", objectId: rule) + "," +
                ComponentRow(29, name: "other", objectId: unmatched, extra: "\"msdyn_subtypename\":\"kept\"")))
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,category", Page(
                $"{{\"workflowid\":\"{flow}\",\"category\":5,\"category@OData.Community.Display.V1.FormattedValue\":\"Modern Flow\"}}," +
                $"{{\"workflowid\":\"{rule}\",\"category\":2}}"));
        using var client = Fakes.Dataverse(handler);

        var items = (await client.GetSolutionComponentsAsync(Guid.NewGuid())).ToDictionary(i => i.Name);

        Assert.Equal("Modern Flow", items["flow"].SubType);
        Assert.Equal(5, items["flow"].ProcessCategory);
        Assert.Equal("Business Rule", items["rule"].SubType);
        Assert.Equal(2, items["rule"].ProcessCategory);
        Assert.Equal("kept", items["other"].SubType);
        Assert.Null(items["other"].ProcessCategory);
    }

    [Fact]
    public async Task Process_categories_are_asked_for_the_solutions_processes_only()
    {
        var flow = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$top=1", HttpStatusCode.BadRequest, "no ranges")
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", Page(ComponentRow(29, name: "flow", objectId: flow)))
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,category", Page(""));
        using var client = Fakes.Dataverse(handler);

        await client.GetSolutionComponentsAsync(Guid.NewGuid());

        var read = Assert.Single(handler.Requests, r => r.Url.Contains("workflows?"));
        Assert.Contains($"In(PropertyName='workflowid',PropertyValues=['{flow}'])", read.Url);
    }

    [Fact]
    public async Task A_solution_with_no_processes_reads_no_workflows()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$top=1", HttpStatusCode.BadRequest, "no ranges")
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", Page(ComponentRow(1, name: "table")));
        using var client = Fakes.Dataverse(handler);

        var items = await client.GetSolutionComponentsAsync(Guid.NewGuid());

        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("workflows?"));
        Assert.False(Assert.IsType<ComponentList>(items).IsTruncated);
    }

    [Fact]
    public async Task Process_category_failure_leaves_summary_sub_type()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$top=1", HttpStatusCode.BadRequest, "no ranges")
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", Page(ComponentRow(29, extra: "\"msdyn_subtypename\":\"Summary\"")))
            .OnError(HttpMethod.Get, "workflows?", HttpStatusCode.Forbidden, "no read on workflow");
        using var client = Fakes.Dataverse(handler);

        var item = Assert.Single(await client.GetSolutionComponentsAsync(Guid.NewGuid()));

        Assert.Equal("Summary", item.SubType);
    }

    [Fact]
    public async Task Serial_component_read_failure_propagates()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "msdyn_solutioncomponentsummaries", HttpStatusCode.Forbidden, "denied");
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.GetSolutionComponentsAsync(Guid.NewGuid()));
        Assert.Contains("denied", ex.Message);
    }

    // ---- Dependencies and containing solutions ----

    [Fact]
    public async Task Dependent_components_read_the_dependent_end()
    {
        var objectId = Guid.NewGuid();
        var dep = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "RetrieveDependentComponents", Page(
            $"{{\"dependentcomponentobjectid\":\"{dep}\",\"dependentcomponenttype\":26," +
            "\"dependentcomponenttype@OData.Community.Display.V1.FormattedValue\":\"Saved Query\"," +
            $"\"requiredcomponentobjectid\":\"{objectId}\",\"requiredcomponenttype\":1}}," +
            "{\"dependentcomponentobjectid\":\"bad\"}"));
        using var client = Fakes.Dataverse(handler);

        var result = await client.GetDependenciesAsync(objectId, 1, DependencyDirection.Dependent);

        var d = Assert.Single(result);
        Assert.Equal(dep, d.ObjectId);
        Assert.Equal(26, d.ComponentType);
        Assert.Equal("Saved Query", d.ComponentTypeName);
        Assert.Equal(DependencyDirection.Dependent, d.Direction);
        Assert.Equal(Api + $"RetrieveDependentComponents(ObjectId=@p1,ComponentType=@p2)?@p1={objectId}&@p2=1", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Required_components_read_the_required_end_and_name_the_type_locally()
    {
        var objectId = Guid.NewGuid();
        var req = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "RetrieveRequiredComponents", Page(
            $"{{\"dependentcomponentobjectid\":\"{objectId}\",\"dependentcomponenttype\":26," +
            $"\"requiredcomponentobjectid\":\"{req}\",\"requiredcomponenttype\":61}}"));
        using var client = Fakes.Dataverse(handler);

        var d = Assert.Single(await client.GetDependenciesAsync(objectId, 26, DependencyDirection.Required));

        Assert.Equal(req, d.ObjectId);
        Assert.Equal("Web Resource", d.ComponentTypeName);
        Assert.Equal(DependencyDirection.Required, d.Direction);
    }

    [Fact]
    public async Task Containing_solutions_are_deduplicated_and_unmanaged_first()
    {
        var objectId = Guid.NewGuid();
        var managed = Guid.NewGuid();
        var zed = Guid.NewGuid();
        var alpha = Guid.NewGuid();

        string Row(Guid id, string name, bool isManaged) =>
            $"{{\"componenttype\":1,\"solutionid\":{{\"solutionid\":\"{id}\",\"uniquename\":\"{name}\",\"ismanaged\":{isManaged.ToString().ToLowerInvariant()},\"version\":\"1.0\"}}}}";

        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "skiptoken=s2", Page(Row(alpha, "Alpha", false) + "," + Row(managed, "Aaa", true)))
            .OnJson(HttpMethod.Get, "solutioncomponents?", Page(
                Row(managed, "Aaa", true) + "," + Row(zed, "Zed", false) + ",{\"componenttype\":1,\"solutionid\":null}",
                Api + "solutioncomponents?$skiptoken=s2"));
        using var client = Fakes.Dataverse(handler);

        var result = await client.GetContainingSolutionsAsync(objectId);

        Assert.Equal(new[] { "Alpha", "Zed", "Aaa" }, result.Select(s => s.FriendlyName));
        Assert.True(result[2].IsManaged);
        Assert.Equal("1.0", result[0].Version);
        Assert.Contains($"$filter=objectid eq {objectId}", handler.Requests[0].Url);
    }

    // ---- Component layers ----

    [Fact]
    public async Task Component_layers_are_null_for_unsupported_types_without_a_request()
    {
        var handler = new FakeHttpHandler();
        using var client = Fakes.Dataverse(handler);

        Assert.Null(await client.GetComponentLayersAsync(Guid.NewGuid(), 12345));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Component_layers_are_listed_top_layer_first()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "msdyn_componentlayers", Page(
            "{\"msdyn_solutionname\":\"System\",\"msdyn_order\":0},{\"msdyn_solutionname\":\"Active\",\"msdyn_order\":2,\"msdyn_publishername\":\"Contoso\"}," +
            "{\"msdyn_order\":1}"));
        using var client = Fakes.Dataverse(handler);

        var layers = await client.GetComponentLayersAsync(id, 61);

        Assert.Equal(new[] { "Active", "(unknown)", "System" }, layers!.Select(l => l.SolutionName));
        Assert.Contains($"msdyn_componentid eq '{id}' and msdyn_solutioncomponentname eq 'WebResource'", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Bulk_layers_leave_out_a_failing_component_and_keep_the_rest()
    {
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var unsupported = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, bad.ToString(), HttpStatusCode.InternalServerError, "boom")
            .OnError(HttpMethod.Get, unsupported.ToString(), HttpStatusCode.BadRequest, "not supported")
            .OnJson(HttpMethod.Get, good.ToString(), Page("{\"msdyn_solutionname\":\"Active\",\"msdyn_order\":1}"));
        using var client = Fakes.Dataverse(handler);
        var reports = new List<int>();

        var result = await client.GetComponentLayersBulkAsync(
            new[] { (good, 61), (bad, 61), (unsupported, 61) }, new SyncProgress(reports));

        Assert.Single(result[good]!);

        // A failure is not "unsupported": it is left out so the caller can say it was not read.
        Assert.False(result.ContainsKey(bad));
        Assert.Null(result[unsupported]);
        Assert.Equal(3, reports.Count);
    }

    // ---- JsonHelper ----

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void GetString_reads_strings_numbers_and_booleans()
    {
        var e = Json("{\"s\":\"x\",\"n\":42,\"d\":1.5,\"t\":true,\"f\":false,\"o\":{},\"a\":[],\"z\":null}");

        Assert.Equal("x", JsonHelper.GetString(e, "s"));
        Assert.Equal("42", JsonHelper.GetString(e, "n"));
        Assert.Equal("1.5", JsonHelper.GetString(e, "d"));
        Assert.Equal("True", JsonHelper.GetString(e, "t"));
        Assert.Equal("False", JsonHelper.GetString(e, "f"));
        Assert.Null(JsonHelper.GetString(e, "o"));
        Assert.Null(JsonHelper.GetString(e, "a"));
        Assert.Null(JsonHelper.GetString(e, "z"));
        Assert.Null(JsonHelper.GetString(e, "missing"));
        Assert.Null(JsonHelper.GetString(Json("[1]"), "s"));
        Assert.Null(JsonHelper.GetString(default, "s"));
    }

    [Fact]
    public void GetBool_reads_booleans_and_boolean_strings()
    {
        var e = Json("{\"t\":true,\"f\":false,\"st\":\"True\",\"sf\":\"false\",\"junk\":\"yes\",\"n\":1}");

        Assert.True(JsonHelper.GetBool(e, "t"));
        Assert.False(JsonHelper.GetBool(e, "f"));
        Assert.True(JsonHelper.GetBool(e, "st"));
        Assert.False(JsonHelper.GetBool(e, "sf"));
        Assert.Null(JsonHelper.GetBool(e, "junk"));
        Assert.Null(JsonHelper.GetBool(e, "n"));
        Assert.Null(JsonHelper.GetBool(e, "missing"));
        Assert.Null(JsonHelper.GetBool(Json("\"x\""), "t"));
    }

    [Fact]
    public void GetInt_reads_integers_and_integer_strings()
    {
        var e = Json("{\"n\":7,\"s\":\"12\",\"big\":9999999999,\"frac\":1.5,\"junk\":\"x\",\"b\":true}");

        Assert.Equal(7, JsonHelper.GetInt(e, "n"));
        Assert.Equal(12, JsonHelper.GetInt(e, "s"));
        Assert.Null(JsonHelper.GetInt(e, "big"));
        Assert.Null(JsonHelper.GetInt(e, "frac"));
        Assert.Null(JsonHelper.GetInt(e, "junk"));
        Assert.Null(JsonHelper.GetInt(e, "b"));
        Assert.Null(JsonHelper.GetInt(e, "missing"));
    }

    [Fact]
    public void GetDate_parses_iso_dates_and_returns_null_otherwise()
    {
        var e = Json("{\"d\":\"2026-03-04T05:06:07Z\",\"junk\":\"not a date\"}");

        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), JsonHelper.GetDate(e, "d"));
        Assert.Null(JsonHelper.GetDate(e, "junk"));
        Assert.Null(JsonHelper.GetDate(e, "missing"));
    }

    [Fact]
    public void FindStringDeep_searches_objects_and_arrays_depth_first()
    {
        var e = Json("{\"a\":{\"Name\":\"\"},\"b\":[{\"x\":1},{\"Name\":\"found\"}],\"Name\":\"later\"}");

        Assert.Equal("found", JsonHelper.FindStringDeep(e, "Name"));
    }

    [Fact]
    public void FindStringDeep_ignores_non_string_matches_and_is_case_sensitive()
    {
        var e = Json("{\"Name\":5,\"name\":\"lower\",\"inner\":{\"Name\":\"deep\"}}");

        Assert.Equal("deep", JsonHelper.FindStringDeep(e, "Name"));
        Assert.Null(JsonHelper.FindStringDeep(e, "Missing"));
    }

    [Fact]
    public void FindStringDeep_stops_beyond_depth_eight()
    {
        string Nest(int depth) => depth == 0 ? "{\"Target\":\"here\"}" : "{\"n\":" + Nest(depth - 1) + "}";

        Assert.Equal("here", JsonHelper.FindStringDeep(Json(Nest(8)), "Target"));
        Assert.Null(JsonHelper.FindStringDeep(Json(Nest(9)), "Target"));
    }

    /// <summary>Records reports synchronously, unlike Progress&lt;T&gt; which posts them.</summary>
    private sealed class SyncProgress(List<int> into) : IProgress<int>
    {
        public void Report(int value)
        {
            lock (into) into.Add(value);
        }
    }
}
