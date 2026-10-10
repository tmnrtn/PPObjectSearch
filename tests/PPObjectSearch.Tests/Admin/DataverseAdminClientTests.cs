using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Admin;

public class DataverseAdminClientTests
{
    private const string F = "@OData.Community.Display.V1.FormattedValue";

    private static readonly Guid TeamId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid QueueId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static string Rows(params object[] rows) => JsonSerializer.Serialize(new { value = rows });

    private static Dictionary<string, object?> UserRow(Guid id, string name, Guid? oid = null, bool disabled = false, Guid? appId = null) => new()
    {
        ["systemuserid"] = id.ToString(),
        ["fullname"] = name,
        ["domainname"] = name.ToLowerInvariant() + "@contoso.com",
        ["internalemailaddress"] = name.ToLowerInvariant() + "@contoso.com",
        ["azureactivedirectoryobjectid"] = oid?.ToString(),
        ["isdisabled"] = disabled,
        ["accessmode"] = 0,
        ["accessmode" + F] = "Read-Write",
        ["applicationid"] = appId?.ToString(),
    };

    // ---------------------------------------------------------------- reads

    [Fact]
    public async Task GetTeamsAsync_reads_team_details_with_labels()
    {
        var oid = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/teams?", Rows(new Dictionary<string, object?>
        {
            ["teamid"] = TeamId.ToString(),
            ["name"] = "Sales",
            ["teamtype"] = 2,
            ["teamtype" + F] = "AAD Security Group",
            ["azureactivedirectoryobjectid"] = oid.ToString(),
            ["membershiptype"] = 0,
            ["membershiptype" + F] = "Members and guests",
            ["isdefault"] = false,
            ["_businessunitid_value" + F] = "Contoso",
        }));

        var team = Assert.Single(await Fakes.Dataverse(handler).GetTeamsAsync(entraGroupTeamsOnly: false));

        Assert.Equal(TeamId, team.TeamId);
        Assert.Equal("Sales", team.Name);
        Assert.Equal("AAD Security Group", team.TypeDisplay);
        Assert.Equal("Contoso", team.BusinessUnit);
        Assert.Equal(oid, team.AadObjectId);
        Assert.True(team.IsEntraGroupTeam);
        Assert.Equal("Members and guests", team.MembershipTypeLabel);
        Assert.Contains("$orderby=name", handler.Requests[0].Url);
        Assert.DoesNotContain("$filter", handler.Requests[0].Url);
    }

    [Fact]
    public async Task GetTeamsAsync_filters_to_entra_group_teams_on_request()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/teams?", Rows());

        await Fakes.Dataverse(handler).GetTeamsAsync(entraGroupTeamsOnly: true);

        Assert.Contains("$filter=azureactivedirectoryobjectid ne null", handler.Requests[0].Url);
    }

    [Fact]
    public async Task GetTeamsAsync_asks_for_formatted_values()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/teams?", Rows());

        await Fakes.Dataverse(handler).GetTeamsAsync(false);

        Assert.Contains("OData.Community.Display.V1.FormattedValue", handler.Requests[0].Header("Prefer"));
    }

    [Fact]
    public async Task GetQueuesAsync_reads_active_queues()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/queues?", Rows(new Dictionary<string, object?>
        {
            ["queueid"] = QueueId.ToString(),
            ["name"] = "MSU Engineering",
            ["emailaddress"] = "msu@contoso.com",
            ["queueviewtype" + F] = "Public",
            ["_ownerid_value" + F] = "Admin",
        }));

        var queue = Assert.Single(await Fakes.Dataverse(handler).GetQueuesAsync());

        Assert.Equal(QueueId, queue.QueueId);
        Assert.Equal("MSU Engineering", queue.Name);
        Assert.Equal("Public", queue.TypeLabel);
        Assert.Equal("Admin", queue.Owner);
        Assert.Contains("$filter=statecode eq 0", handler.Requests[0].Url);
    }

    [Fact]
    public async Task GetTeamMembersAsync_reads_the_team_membership_relationship()
    {
        var oid = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"teams({TeamId})/teammembership_association",
            Rows(UserRow(UserId, "Alice", oid, disabled: true, appId: appId)));

        var user = Assert.Single(await Fakes.Dataverse(handler).GetTeamMembersAsync(TeamId));

        Assert.Equal(UserId, user.SystemUserId);
        Assert.Equal("Alice", user.FullName);
        Assert.Equal("alice@contoso.com", user.DomainName);
        Assert.Equal(oid, user.AadObjectId);
        Assert.True(user.IsDisabled);
        Assert.Equal("Read-Write", user.AccessMode);
        Assert.True(user.IsApplicationUser);
    }

    [Fact]
    public async Task GetQueueMembersAsync_reads_the_queue_membership_relationship()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"queues({QueueId})/queuemembership_association",
            Rows(UserRow(UserId, "Bob")));

        var user = Assert.Single(await Fakes.Dataverse(handler).GetQueueMembersAsync(QueueId));

        Assert.Equal("Bob", user.FullName);
        Assert.False(user.IsApplicationUser);
        Assert.Null(user.AadObjectId);
    }

    [Fact]
    public async Task Member_reads_follow_next_links()
    {
        var second = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "page=2", Rows(UserRow(second, "Second")))
            .OnJson(HttpMethod.Get, "teammembership_association", JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["value"] = new[] { UserRow(UserId, "First") },
                ["@odata.nextLink"] = Fakes.ApiRoot + $"teams({TeamId})/teammembership_association?page=2",
            }));

        var users = await Fakes.Dataverse(handler).GetTeamMembersAsync(TeamId);

        Assert.Equal(["First", "Second"], users.Select(u => u.FullName));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Rows_without_a_parsable_id_are_skipped()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "teammembership_association",
            Rows(new Dictionary<string, object?> { ["fullname"] = "No id" }, UserRow(UserId, "Has id")));

        var users = await Fakes.Dataverse(handler).GetTeamMembersAsync(TeamId);

        Assert.Equal("Has id", Assert.Single(users).FullName);
    }

    [Fact]
    public async Task GetUsersByAadObjectIdsAsync_queries_in_chunks_of_100_and_skips_non_guids()
    {
        var ids = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid().ToString()).Append("not-a-guid").ToList();
        ids.Add(ids[0]); // duplicate

        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "$batch", _ => FakeHttpHandler.Json(Rows()))
            .On(HttpMethod.Get, "systemusers", _ => FakeHttpHandler.Json(Rows()));

        await Fakes.Dataverse(handler).GetUsersByAadObjectIdsAsync(ids);

        // Long URLs go through $batch, so read the GET line from wherever it travelled.
        var queries = handler.Requests.Select(r => r.Body ?? r.Url).ToList();
        Assert.Equal(2, queries.Count);
        Assert.All(queries, q => Assert.Contains("Microsoft.Dynamics.CRM.In(PropertyName='azureactivedirectoryobjectid'", Uri.UnescapeDataString(q)));
        Assert.DoesNotContain(queries, q => q.Contains("not-a-guid"));
        Assert.Equal(150, queries.Sum(q => ids.Take(150).Count(id => q.Contains(id))));
    }

    [Fact]
    public async Task GetUsersByAadObjectIdsAsync_with_nothing_to_look_up_makes_no_request()
    {
        var handler = new FakeHttpHandler();

        Assert.Empty(await Fakes.Dataverse(handler).GetUsersByAadObjectIdsAsync([]));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FindUsersForEntraUsersAsync_matches_on_object_id_or_upn_and_escapes_guest_upns()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "$batch", _ => FakeHttpHandler.Json(Rows()))
            .OnJson(HttpMethod.Get, "systemusers", Rows(UserRow(UserId, "Alice"), UserRow(UserId, "Alice")));

        var oid = Guid.NewGuid();
        var users = await Fakes.Dataverse(handler).FindUsersForEntraUsersAsync(
        [
            new PPObjectSearch.Models.EntraUser(oid.ToString(), "Alice", "o'neil_contoso.com#EXT#@tenant.onmicrosoft.com", null, true),
            new PPObjectSearch.Models.EntraUser("not-a-guid", "Nobody", null, null, true),
        ]);

        // The same record returned twice is listed once.
        Assert.Single(users);

        var query = handler.Requests.Single().Url;
        Assert.Contains($"In(PropertyName='azureactivedirectoryobjectid',PropertyValues=['{oid}'])", query);
        Assert.Contains(" or Microsoft.Dynamics.CRM.In(PropertyName='domainname',PropertyValues=['o''neil_contoso.com#EXT#@tenant.onmicrosoft.com'])", query);
        Assert.DoesNotContain("not-a-guid", query);

        // '#' would otherwise end the query string; it travels encoded.
        Assert.Contains("%23EXT%23", handler.Requests.Single().Uri.OriginalString);
    }

    [Fact]
    public async Task FindUsersForEntraUsersAsync_with_nothing_usable_makes_no_request()
    {
        var handler = new FakeHttpHandler();

        var users = await Fakes.Dataverse(handler).FindUsersForEntraUsersAsync(
            [new PPObjectSearch.Models.EntraUser("not-a-guid", "Nobody", null, null, true)]);

        Assert.Empty(users);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------- writes

    [Fact]
    public async Task SyncGroupMembersToTeamAsync_posts_the_bound_action()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "SyncGroupMembersToTeam", HttpStatusCode.NoContent);

        await Fakes.Dataverse(handler).SyncGroupMembersToTeamAsync(TeamId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Fakes.ApiRoot + $"teams({TeamId})/Microsoft.Dynamics.CRM.SyncGroupMembersToTeam", request.Url);
        Assert.Equal("{}", request.Body);
        Assert.Equal("Bearer token:" + Fakes.EnvironmentUrl, request.Authorization);
    }

    [Fact]
    public async Task RemoveTeamMemberAsync_posts_RemoveMembersTeam_with_a_typed_systemuser()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "RemoveMembersTeam", HttpStatusCode.NoContent);

        await Fakes.Dataverse(handler).RemoveTeamMemberAsync(TeamId, UserId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Fakes.ApiRoot + $"teams({TeamId})/Microsoft.Dynamics.CRM.RemoveMembersTeam", request.Url);

        using var body = JsonDocument.Parse(request.Body!);
        var member = Assert.Single(body.RootElement.GetProperty("Members").EnumerateArray().ToList());
        Assert.Equal("Microsoft.Dynamics.CRM.systemuser", member.GetProperty("@odata.type").GetString());
        Assert.Equal(UserId, member.GetProperty("systemuserid").GetGuid());
    }

    [Fact]
    public async Task AddQueueMemberAsync_associates_through_the_membership_relationship()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Post, "queuemembership_association/$ref", HttpStatusCode.NoContent);

        await Fakes.Dataverse(handler).AddQueueMemberAsync(QueueId, UserId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Fakes.ApiRoot + $"queues({QueueId})/queuemembership_association/$ref", request.Url);

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(Fakes.ApiRoot + $"systemusers({UserId})", body.RootElement.GetProperty("@odata.id").GetString());
    }

    [Fact]
    public async Task RemoveQueueMemberAsync_disassociates_the_one_user()
    {
        var handler = new FakeHttpHandler().OnStatus(HttpMethod.Delete, "queuemembership_association", HttpStatusCode.NoContent);

        await Fakes.Dataverse(handler).RemoveQueueMemberAsync(QueueId, UserId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(Fakes.ApiRoot + $"queues({QueueId})/queuemembership_association({UserId})/$ref", request.Url);
    }

    [Fact]
    public async Task WhoAmIAsAsync_impersonates_the_entra_user_and_returns_their_user_id()
    {
        var oid = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/WhoAmI",
            JsonSerializer.Serialize(new { UserId, BusinessUnitId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() }));

        var userId = await Fakes.Dataverse(handler).WhoAmIAsAsync(oid);

        Assert.Equal(UserId, userId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(Fakes.ApiRoot + "WhoAmI", request.Url);
        Assert.Equal(oid.ToString(), request.Header("CallerObjectId"));
        Assert.Equal("Bearer token:" + Fakes.EnvironmentUrl, request.Authorization);
    }

    [Fact]
    public async Task WhoAmIAsAsync_without_the_delegate_privilege_throws_the_server_message()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "/WhoAmI", HttpStatusCode.Forbidden,
            "Principal user is missing prvActOnBehalfOfAnotherUser privilege.");

        var ex = await Assert.ThrowsAsync<DataverseException>(() => Fakes.Dataverse(handler).WhoAmIAsAsync(Guid.NewGuid()));

        Assert.Contains("prvActOnBehalfOfAnotherUser", ex.Message);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task WhoAmIAsAsync_with_no_user_id_in_the_answer_throws()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/WhoAmI", "{}");

        await Assert.ThrowsAsync<DataverseException>(() => Fakes.Dataverse(handler).WhoAmIAsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_refused_write_throws_with_the_server_message()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Post, "RemoveMembersTeam", HttpStatusCode.BadRequest,
            "Cannot remove members from a group team.");

        var ex = await Assert.ThrowsAsync<DataverseException>(() => Fakes.Dataverse(handler).RemoveTeamMemberAsync(TeamId, UserId));

        Assert.Contains("Cannot remove members from a group team.", ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }
}
