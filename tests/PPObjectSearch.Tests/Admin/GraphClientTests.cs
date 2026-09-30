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
}
