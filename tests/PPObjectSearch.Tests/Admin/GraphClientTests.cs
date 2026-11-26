using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Graph;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Admin;

public class GraphClientTests
{
    private const string GroupId = "aaaaaaaa-0000-0000-0000-000000000001";

    private static object User(string id, string name, bool enabled = true) => new
    {
        id,
        displayName = name,
        userPrincipalName = name.ToLowerInvariant() + "@contoso.com",
        mail = name.ToLowerInvariant() + "@contoso.com",
        accountEnabled = enabled,
        userType = "Member"
    };

    private static string Page(object[] rows, string? nextLink = null) =>
        JsonSerializer.Serialize(nextLink is null
            ? new Dictionary<string, object> { ["value"] = rows }
            : new Dictionary<string, object> { ["value"] = rows, ["@odata.nextLink"] = nextLink });

    [Fact]
    public async Task GetGroupAsync_reads_the_group()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"groups/{GroupId}?",
            JsonSerializer.Serialize(new { id = GroupId, displayName = "Sales RBAC", securityEnabled = true, mailEnabled = false }));

        var group = await Fakes.Graph(handler).GetGroupAsync(GroupId);

        Assert.NotNull(group);
        Assert.Equal("Sales RBAC", group.DisplayName);
        Assert.True(group.SecurityEnabled);
        Assert.False(group.MailEnabled);
        Assert.Equal("Bearer token:https://graph.microsoft.com", handler.Requests[0].Authorization);
        Assert.StartsWith("https://graph.microsoft.com/v1.0/", handler.Requests[0].Url);
    }

    [Fact]
    public async Task GetGroupAsync_returns_null_for_a_missing_group()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "groups/", HttpStatusCode.NotFound, "Resource does not exist.");

        Assert.Null(await Fakes.Graph(handler).GetGroupAsync(GroupId));
    }

    [Fact]
    public async Task A_forbidden_read_explains_the_missing_permission()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "groups/", HttpStatusCode.Forbidden, "Insufficient privileges.");

        var ex = await Assert.ThrowsAsync<GraphException>(() => Fakes.Graph(handler).GetGroupAsync(GroupId));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("Insufficient privileges.", ex.Message);
        Assert.Contains("GroupMember.Read.All", ex.Message);
    }

    [Fact]
    public async Task GetGroupTransitiveUsersAsync_reads_nested_users_across_pages_once_each()
    {
        const string next = "https://graph.microsoft.com/v1.0/next-page";
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "next-page", Page([User("u2", "Bob"), User("U1", "Alice again")]))
            .OnJson(HttpMethod.Get, "transitiveMembers/microsoft.graph.user", Page([User("u1", "Alice")], next));

        var users = await Fakes.Graph(handler).GetGroupTransitiveUsersAsync(GroupId);

        // u1 is reachable through two nested groups; it is listed once.
        Assert.Equal(["Alice", "Bob"], users.Select(u => u.DisplayName));
        Assert.Contains("$top=999", handler.Requests[0].Url);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetGroupTransitiveUsersAsync_reads_user_fields()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "transitiveMembers", Page([User("u1", "Alice", enabled: false)]));

        var user = Assert.Single(await Fakes.Graph(handler).GetGroupTransitiveUsersAsync(GroupId));

        Assert.Equal("u1", user.Id);
        Assert.Equal("alice@contoso.com", user.Upn);
        Assert.Equal(false, user.AccountEnabled);
        Assert.Equal("Member", user.UserType);
    }

    [Fact]
    public async Task GetNonUserMemberCountsAsync_counts_other_member_types_once_each()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "transitiveMembers?", Page(
        [
            new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.user", ["id"] = "u1" },
            new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.group", ["id"] = "g1" },
            new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.group", ["id"] = "g1" },
            new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.group", ["id"] = "g2" },
            new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.device", ["id"] = "d1" },
        ]));

        var counts = await Fakes.Graph(handler).GetNonUserMemberCountsAsync(GroupId);

        Assert.Equal(2, counts["group"]);
        Assert.Equal(1, counts["device"]);
        Assert.False(counts.ContainsKey("user"));
    }

    [Fact]
    public async Task TryGetUserAsync_escapes_the_upn_and_returns_null_when_missing()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Get, "users/", HttpStatusCode.NotFound);

        Assert.Null(await Fakes.Graph(handler).TryGetUserAsync("o'brien@contoso.com"));
        // Escaped so a UPN can never be read as more path; the @ must not survive raw.
        Assert.Contains("users/o%27brien%40contoso.com?", handler.Requests[0].Uri.OriginalString);
    }

    [Fact]
    public async Task TryGetUserAsync_reads_the_user()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "users/u1", JsonSerializer.Serialize(User("u1", "Alice")));

        var user = await Fakes.Graph(handler).TryGetUserAsync("u1");

        Assert.Equal("Alice", user?.DisplayName);
    }

    [Theory]
    [InlineData("[\"AAAAAAAA-0000-0000-0000-000000000001\"]", true)]
    [InlineData("[]", false)]
    public async Task IsTransitiveMemberAsync_asks_checkMemberGroups(string value, bool expected)
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Post, "checkMemberGroups", $"{{\"value\":{value}}}");

        var result = await Fakes.Graph(handler).IsTransitiveMemberAsync("u1", GroupId);

        Assert.Equal(expected, result);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(GroupId, body.RootElement.GetProperty("groupIds")[0].GetString());
    }

    [Fact]
    public async Task IsTransitiveMemberAsync_returns_null_when_the_question_fails()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Post, "checkMemberGroups", HttpStatusCode.Forbidden, "nope");

        Assert.Null(await Fakes.Graph(handler).IsTransitiveMemberAsync("u1", GroupId));
    }

    [Fact]
    public async Task GetUserLicensesAsync_lists_each_sku_with_only_its_provisioned_plans()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "users/u1/licenseDetails", Page([
            new
            {
                skuId = "sku-e3", skuPartNumber = "ENTERPRISEPACK",
                servicePlans = new object[]
                {
                    new { servicePlanName = "YAMMER_ENTERPRISE", provisioningStatus = "Disabled" },
                    new { servicePlanName = "EXCHANGE_S_ENTERPRISE", provisioningStatus = "Success" },
                    new { servicePlanName = "DYN365_CDS_O365_P2", provisioningStatus = "Success" }
                }
            },
            new { skuId = "sku-pa", skuPartNumber = "POWERAPPS_PER_USER", servicePlans = Array.Empty<object>() }
        ]));

        var licenses = await Fakes.Graph(handler).GetUserLicensesAsync("u1");

        Assert.NotNull(licenses);
        Assert.Equal(["ENTERPRISEPACK", "POWERAPPS_PER_USER"], licenses.Select(l => l.Name));
        Assert.Equal("DYN365_CDS_O365_P2, EXCHANGE_S_ENTERPRISE", licenses[0].PlansLabel);
        Assert.Empty(licenses[1].EnabledPlans);
        Assert.Contains("$select=skuId,skuPartNumber,servicePlans", handler.Requests[0].Url);
    }

    [Fact]
    public async Task GetUserLicensesAsync_returns_null_for_a_missing_user()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "licenseDetails", HttpStatusCode.NotFound, "Resource does not exist.");

        Assert.Null(await Fakes.Graph(handler).GetUserLicensesAsync("u1"));
    }

    [Fact]
    public async Task GetUserGroupsAsync_lists_nested_groups_once_marking_the_direct_ones()
    {
        // The second page repeats a group, as Graph can across pages; the next-page route goes first to win the match.
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "users/u1/memberOf", Page([new { id = "g-sec" }]))
            .OnJson(HttpMethod.Get, "skiptoken=2", Page([
                new { id = "g-sec", displayName = "Case Work", securityEnabled = true, mailEnabled = false, groupTypes = Array.Empty<string>() },
                new { id = "g-dl", displayName = "Bulletins", securityEnabled = false, mailEnabled = true, groupTypes = Array.Empty<string>() }
            ]))
            .OnJson(HttpMethod.Get, "users/u1/transitiveMemberOf", Page([
                new { id = "g-sec", displayName = "Case Work", securityEnabled = true, mailEnabled = false, groupTypes = Array.Empty<string>() },
                new { id = "g-365", displayName = "All staff", securityEnabled = false, mailEnabled = true, groupTypes = new[] { "Unified", "DynamicMembership" } }
            ], nextLink: "https://graph.microsoft.com/v1.0/users/u1/transitiveMemberOf/microsoft.graph.group?$skiptoken=2"));

        var groups = await Fakes.Graph(handler).GetUserGroupsAsync("u1");

        Assert.NotNull(groups);
        Assert.Equal(["All staff", "Bulletins", "Case Work"], groups.Select(g => g.Name));
        Assert.Equal(["Microsoft 365", "Distribution", "Security"], groups.Select(g => g.Kind));
        Assert.Equal(["Nested · dynamic", "Nested", "Direct"], groups.Select(g => g.Membership));
        Assert.Contains(handler.Requests, r => r.Url.Contains("memberOf/microsoft.graph.group?$select=id"));
        Assert.Contains(handler.Requests, r => r.Url.Contains("transitiveMemberOf/microsoft.graph.group?$select=id,displayName"));
    }

    [Fact]
    public async Task GetUserGroupsAsync_returns_null_for_a_missing_user()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "memberOf", HttpStatusCode.NotFound, "Resource does not exist.");

        Assert.Null(await Fakes.Graph(handler).GetUserGroupsAsync("u1"));
    }

    /// <summary>The urls a $batch request asked for.</summary>
    private static List<string> Urls(RecordedRequest request) =>
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("requests").EnumerateArray()
            .Select(r => r.GetProperty("url").GetString()!)
            .ToList();

    /// <summary>Answers each request in a $batch with what <paramref name="answer"/> says for its user.</summary>
    private static Func<RecordedRequest, HttpResponseMessage> Batch(Func<string, object> answer) => request =>
    {
        using var body = JsonDocument.Parse(request.Body!);
        var responses = new System.Text.Json.Nodes.JsonArray();
        foreach (var r in body.RootElement.GetProperty("requests").EnumerateArray())
        {
            var response = JsonSerializer.SerializeToNode(answer(r.GetProperty("url").GetString()!.Split('/')[2]))!.AsObject();
            response["id"] = r.GetProperty("id").GetString();
            responses.Add(response);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new System.Text.Json.Nodes.JsonObject { ["responses"] = responses }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json")
        };
    };

    private static object Skus(params string[] names) =>
        new { status = 200, body = new { value = names.Select(n => new { skuId = "id-" + n, skuPartNumber = n }) } };

    [Fact]
    public async Task GetUsersLicensesAsync_reads_twenty_users_to_a_batch()
    {
        var ids = Enumerable.Range(0, 45).Select(i => $"u{i}").ToList();
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "v1.0/$batch", Batch(id => id == "u7" ? Skus("POWERAPPS_PER_USER", "SPE_E5") : Skus()));
        var progress = new List<int>();

        var licenses = await Fakes.Graph(handler).GetUsersLicensesAsync(ids, new SyncProgress(progress));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([5, 20, 20], handler.Requests.Select(r => Urls(r).Count).Order());
        Assert.Contains("/users/u0/licenseDetails?$select=skuId,skuPartNumber", handler.Requests.SelectMany(Urls));
        Assert.Equal(45, licenses.Count);
        Assert.Equal(["POWERAPPS_PER_USER", "SPE_E5"], licenses["u7"].Select(l => l.Name));
        Assert.Equal("id-SPE_E5", licenses["u7"][1].SkuId);
        Assert.Empty(licenses["u8"]);
        Assert.Equal(45, progress.Max());
    }

    [Fact]
    public async Task GetUsersLicensesAsync_counts_a_missing_user_as_holding_none_and_leaves_out_a_failed_one()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "$batch", Batch(id => id switch
        {
            "gone" => new { status = 404 },
            "broken" => new { status = 500 },
            _ => Skus("FLOW_FREE")
        }));

        var licenses = await Fakes.Graph(handler).GetUsersLicensesAsync(["ok", "gone", "broken"]);

        Assert.Equal(["gone", "ok"], licenses.Keys.Order());
        Assert.Empty(licenses["gone"]);
    }

    [Fact]
    public async Task GetUsersLicensesAsync_asks_again_for_users_graph_throttled()
    {
        var asked = 0;
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "$batch", Batch(id =>
            id == "busy" && Interlocked.Increment(ref asked) == 1
                ? new { status = 429, headers = new Dictionary<string, string> { ["Retry-After"] = "7" } }
                : Skus("SPE_E5")));
        var waits = new List<TimeSpan>();
        var graph = Fakes.Graph(handler);
        graph.Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; };

        var licenses = await graph.GetUsersLicensesAsync(["calm", "busy"]);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(["/users/busy/licenseDetails?$select=skuId,skuPartNumber"], Urls(handler.Requests[1]));
        Assert.Equal([TimeSpan.FromSeconds(7)], waits);
        Assert.Equal("SPE_E5", Assert.Single(licenses["busy"]).Name);
    }

    /// <summary>Reports straight away, where Progress&lt;T&gt; would post them for later.</summary>
    private sealed class SyncProgress(List<int> into) : IProgress<int>
    {
        public void Report(int value)
        {
            lock (into) into.Add(value);
        }
    }
}
