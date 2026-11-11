using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Admin;

/// <summary>The Environment admin window's Teams tab: every team, its members, security roles and field security profiles.</summary>
public class EnvironmentAdminTeamsTests
{
    private const string Fv = "@OData.Community.Display.V1.FormattedValue";

    private static readonly Guid Alice = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid ServiceDesk = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid CaseSharing = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly Guid Engineers = Guid.Parse("22222222-0000-0000-0000-000000000003");
    private static readonly Guid Contoso = Guid.Parse("22222222-0000-0000-0000-000000000004");
    private static readonly Guid EngineersGroup = Guid.Parse("66666666-0000-0000-0000-000000000001");
    private static readonly Guid RoleRoot = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid RoleCopy = Guid.Parse("33333333-0000-0000-0000-000000000002");
    private static readonly Guid SupportQueue = Guid.Parse("55555555-0000-0000-0000-000000000001");

    private static string Value(params object[] rows) => JsonSerializer.Serialize(new { value = rows });

    private static Dictionary<string, object?> Team(Guid id, string name, int type, string typeLabel, string unit,
        Guid? group = null, bool isDefault = false, Guid? queue = null) => new()
    {
        ["teamid"] = id, ["name"] = name, ["teamtype"] = type, ["teamtype" + Fv] = typeLabel,
        ["_businessunitid_value"] = Guid.NewGuid(), ["_businessunitid_value" + Fv] = unit,
        ["azureactivedirectoryobjectid"] = group, ["isdefault"] = isDefault,
        ["membershiptype" + Fv] = group is null ? null : "Members and guests",
        ["_administratorid_value" + Fv] = "Alice", ["_queueid_value"] = queue, ["_queueid_value" + Fv] = queue is null ? null : "Support",
        ["description"] = name + " team", ["createdon"] = "2026-01-02T03:04:05Z"
    };

    private static string Teams() => Value(
        Team(CaseSharing, "Case sharing", 1, "Access", "Contoso"),
        Team(Contoso, "Contoso", 0, "Owner", "Contoso", isDefault: true),
        Team(Engineers, "Engineers", 2, "AAD Security Group", "Fabrikam", group: EngineersGroup),
        Team(ServiceDesk, "Service Desk", 0, "Owner", "Contoso", queue: SupportQueue));

    private static Dictionary<string, object?> Member(Guid id, string name, bool disabled) => new()
    {
        ["systemuserid"] = id, ["fullname"] = name, ["domainname"] = name.ToLowerInvariant() + "@contoso.com",
        ["isdisabled"] = disabled, ["accessmode"] = 0, ["accessmode" + Fv] = "Read-Write"
    };

    /// <summary>
    /// Four teams; Service Desk has Bob (disabled) and Alice, a role held in Contoso UK and a field
    /// security profile. Anything else reads as empty.
    /// </summary>
    private static FakeHttpHandler Handler() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, $"teams({ServiceDesk})/teammembership_association", Value(
            Member(Bob, "Bob", true), Member(Alice, "Alice", false)))
        .OnJson(HttpMethod.Get, $"teams({ServiceDesk})/teamroles_association", Value(new Dictionary<string, object?>
        {
            ["roleid"] = RoleCopy, ["name"] = "Customer Service Rep", ["_parentrootroleid_value"] = RoleRoot,
            ["_businessunitid_value" + Fv] = "Contoso UK"
        }))
        .OnJson(HttpMethod.Get, $"teams({ServiceDesk})/teamprofiles_association", Value(new Dictionary<string, object?>
        {
            ["fieldsecurityprofileid"] = Guid.NewGuid(), ["name"] = "Case notes", ["ismanaged"] = true
        }))
        .OnJson(HttpMethod.Get, "teams?", Teams())
        .OnJson(HttpMethod.Get, "systemusers?", Value(Member(Alice, "Alice", false), Member(Bob, "Bob", true)))
        .OnJson(HttpMethod.Get, "roles?$select=roleid,name", Value(new Dictionary<string, object?>
        {
            ["roleid"] = RoleRoot, ["name"] = "Customer Service Rep", ["ismanaged"] = true
        }))
        .OnJson(HttpMethod.Get, "RetrieveRolePrivilegesRole", """{"RolePrivileges":[]}""")
        .OnJson(HttpMethod.Get, "queues?", Value(new Dictionary<string, object?>
        {
            ["queueid"] = SupportQueue, ["name"] = "Support", ["queueviewtype"] = 0, ["statecode"] = 0
        }))
        .OnJson(HttpMethod.Get, "", Value());

    private static EnvironmentAdminViewModel Admin(FakeHttpHandler handler, AdminTab tab = AdminTab.Teams) =>
        new(AdminSessions.Disconnected(), Fakes.Dataverse(handler), tab);

    private static Task Settle(EnvironmentAdminViewModel admin) => admin.Work.WhenIdleAsync();

    private static async Task<EnvironmentAdminViewModel> WithServiceDeskSelected(FakeHttpHandler handler)
    {
        var admin = Admin(handler);
        await admin.LoadAsync();
        admin.Teams.SelectedTeam = admin.Teams.Items.First(t => t.TeamId == ServiceDesk);
        await Settle(admin);
        return admin;
    }

    // ---------------------------------------------------------------- reading

    [Fact]
    public async Task Teams_read_with_their_type_business_unit_administrator_and_queue()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "teams?", Teams());

        var teams = await Fakes.Dataverse(handler).GetTeamDetailsAsync();

        Assert.Equal(4, teams.Count);
        Assert.Contains("$orderby=name", handler.Requests[0].Url);
        Assert.Contains("_administratorid_value", handler.Requests[0].Url);

        var access = teams[0];
        Assert.True(access.IsAccessTeam);
        Assert.Equal("Access", access.TypeDisplay);

        var group = teams[2];
        Assert.True(group.IsEntraGroupTeam);
        Assert.Equal(EngineersGroup, group.AadObjectId);
        Assert.Equal("Members and guests", group.MembershipTypeLabel);

        var desk = teams[3];
        Assert.True(desk.IsOwnerTeam);
        Assert.Equal("Contoso", desk.BusinessUnit);
        Assert.Equal("Alice", desk.Administrator);
        Assert.Equal(SupportQueue, desk.QueueId);
        Assert.Equal("Support", desk.QueueName);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), desk.CreatedOn);
        Assert.True(teams[1].IsDefault);
    }

    [Fact]
    public async Task A_team_column_the_environment_lacks_falls_back_to_the_core_columns()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "_administratorid_value", HttpStatusCode.BadRequest, "Could not find a property named '_administratorid_value'")
            .OnJson(HttpMethod.Get, "teams?", Teams());

        var teams = await Fakes.Dataverse(handler).GetTeamDetailsAsync();

        Assert.Equal(4, teams.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("_administratorid_value", handler.Requests[1].Url);
    }

    // ---------------------------------------------------------------- the list

    [Fact]
    public async Task The_teams_tab_filters_by_type_business_unit_and_search()
    {
        var admin = Admin(Handler());
        var teams = admin.Teams;

        await admin.LoadAsync();

        Assert.StartsWith("Read 4 teams in ", teams.Status);
        Assert.Equal((4, 2, 1, 1), (teams.CountAll, teams.CountOwner, teams.CountAccess, teams.CountEntra));
        Assert.Equal([AdminTeamsViewModel.AllUnits, "Contoso", "Fabrikam"], teams.UnitOptions);
        Assert.Equal("Showing 4 teams", teams.ShowingSummary);

        teams.TypeFilter = TeamTypeFilter.Entra;
        Assert.Equal("Engineers", Assert.Single(teams.View.Cast<TeamDetail>()).Name);

        teams.TypeFilter = TeamTypeFilter.Owner;
        Assert.Equal(["Contoso", "Service Desk"], teams.View.Cast<TeamDetail>().Select(t => t.Name));
        Assert.Equal("Showing 2 of 4 teams", teams.ShowingSummary);

        teams.TypeFilter = TeamTypeFilter.All;
        teams.SelectedUnit = "Fabrikam";
        Assert.Equal("Engineers", Assert.Single(teams.View.Cast<TeamDetail>()).Name);

        teams.SelectedUnit = AdminTeamsViewModel.AllUnits;
        teams.SearchText = EngineersGroup.ToString();
        Assert.Equal("Engineers", Assert.Single(teams.View.Cast<TeamDetail>()).Name);
    }

    // ---------------------------------------------------------------- the selected team

    [Fact]
    public async Task A_team_shows_its_members_roles_and_field_security()
    {
        var admin = await WithServiceDeskSelected(Handler());
        var teams = admin.Teams;

        Assert.Equal(["Alice", "Bob"], teams.Members.Select(m => m.FullName));
        Assert.Equal("2 member(s), 1 disabled.", teams.MemberStatus);

        var role = Assert.Single(teams.Roles);
        Assert.Equal("Customer Service Rep", role.RoleName);
        Assert.Equal("Contoso UK", role.BusinessUnit);
        Assert.Equal(RoleRoot, role.DefinitionId);
        Assert.Equal("1 role(s). Every member holds them through this team.", teams.RoleStatus);

        Assert.Equal("Case notes", Assert.Single(teams.FieldProfiles).Name);
        Assert.Equal(string.Empty, teams.FieldProfileStatus);
        Assert.False(teams.IsLoadingDetails);
        Assert.Equal(TeamDetailView.Members, teams.DetailView);
    }

    [Fact]
    public async Task An_empty_team_says_why_by_its_type()
    {
        var admin = Admin(Handler());
        await admin.LoadAsync();

        admin.Teams.SelectedTeam = admin.Teams.Items.First(t => t.TeamId == CaseSharing);
        await Settle(admin);

        Assert.Equal("No members.", admin.Teams.MemberStatus);
        Assert.StartsWith("No security role. An access team holds none", admin.Teams.RoleStatus);
        Assert.StartsWith("No field security profile", admin.Teams.FieldProfileStatus);

        admin.Teams.SelectedTeam = admin.Teams.Items.First(t => t.TeamId == Engineers);
        await Settle(admin);

        Assert.StartsWith("No members yet. Group members join the team as they sign in", admin.Teams.MemberStatus);
        Assert.Equal("No security role - members get no privileges through this team.", admin.Teams.RoleStatus);
    }

    [Fact]
    public async Task A_read_that_fails_says_why_and_leaves_the_others()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "teamprofiles_association", HttpStatusCode.Forbidden, "Principal lacks prvReadFieldSecurityProfile")
            .OnError(HttpMethod.Get, "teammembership_association", HttpStatusCode.Forbidden, "No read on user.");
        var admin = await WithServiceDeskSelected(Chain(handler));

        Assert.StartsWith("Could not read this team's members - ", admin.Teams.MemberStatus);
        Assert.Contains("No read on user.", admin.Teams.MemberStatus);
        Assert.StartsWith("Could not read field security profiles - ", admin.Teams.FieldProfileStatus);
        Assert.Equal("Customer Service Rep", Assert.Single(admin.Teams.Roles).RoleName);
        Assert.False(admin.Teams.IsLoadingDetails);
    }

    /// <summary>The usual routes, after whatever <paramref name="handler"/> answers first.</summary>
    private static FakeHttpHandler Chain(FakeHttpHandler handler) => handler
        .OnJson(HttpMethod.Get, $"teams({ServiceDesk})/teamroles_association", Value(new Dictionary<string, object?>
        {
            ["roleid"] = RoleCopy, ["name"] = "Customer Service Rep", ["_parentrootroleid_value"] = RoleRoot
        }))
        .OnJson(HttpMethod.Get, "teams?", Teams())
        .OnJson(HttpMethod.Get, "", Value());

    // ---------------------------------------------------------------- links

    [Fact]
    public async Task A_member_opens_the_users_tab_and_back_returns_to_the_team()
    {
        var admin = await WithServiceDeskSelected(Handler());

        admin.Teams.ShowMemberCommand.Execute(admin.Teams.Members.Single(m => m.SystemUserId == Bob));
        await Settle(admin);

        Assert.Equal(AdminTab.Users, admin.SelectedTab);
        Assert.Equal(Bob, admin.Users.SelectedUser?.SystemUserId);
        Assert.Equal(UserStatusFilter.All, admin.Users.StatusFilter);
        Assert.Equal("Back (Alt+Left) — to Teams: Service Desk", admin.BackToolTip);

        await admin.BackCommand.ExecuteAsync(null);

        Assert.Equal(AdminTab.Teams, admin.SelectedTab);
        Assert.Equal(ServiceDesk, admin.Teams.SelectedTeam?.TeamId);
    }

    [Fact]
    public async Task A_teams_role_opens_the_roles_tab_on_its_definition()
    {
        var admin = await WithServiceDeskSelected(Handler());

        admin.Teams.ShowRoleCommand.Execute(admin.Teams.Roles[0]);
        await Settle(admin);

        Assert.Equal(AdminTab.Roles, admin.SelectedTab);
        Assert.Equal(RoleRoot, admin.Roles.SelectedRole?.RoleId);
    }

    [Fact]
    public async Task A_teams_queue_opens_the_queues_tab()
    {
        var admin = await WithServiceDeskSelected(Handler());
        Assert.True(admin.Teams.ShowQueueCommand.CanExecute(null));

        admin.Teams.ShowQueueCommand.Execute(null);
        await Settle(admin);

        Assert.Equal(AdminTab.Queues, admin.SelectedTab);
        Assert.Equal(SupportQueue, admin.Queues.SelectedQueue?.QueueId);
    }

    [Fact]
    public async Task Only_a_group_team_offers_entra_team_sync_and_only_one_with_a_queue_links_to_it()
    {
        var admin = Admin(Handler());
        await admin.LoadAsync();

        admin.Teams.SelectedTeam = admin.Teams.Items.First(t => t.TeamId == ServiceDesk);
        Assert.False(admin.Teams.EntraTeamSyncCommand.CanExecute(null));

        admin.Teams.SelectedTeam = admin.Teams.Items.First(t => t.TeamId == Engineers);
        Assert.True(admin.Teams.EntraTeamSyncCommand.CanExecute(null));
        Assert.False(admin.Teams.ShowQueueCommand.CanExecute(null));
        await Settle(admin);
    }

    [Fact]
    public async Task A_users_team_opens_the_teams_tab_clearing_a_filter_that_hides_it()
    {
        var admin = Admin(Handler(), AdminTab.Users);
        await admin.Teams.EnsureLoadedAsync();
        admin.Teams.TypeFilter = TeamTypeFilter.Access;

        admin.Users.ShowTeamCommand.Execute(new TeamInfo(ServiceDesk, "Service Desk", 0, "Owner", "Contoso", null, null, null, false));
        await Settle(admin);

        Assert.Equal(AdminTab.Teams, admin.SelectedTab);
        Assert.Equal(TeamTypeFilter.All, admin.Teams.TypeFilter);
        Assert.Equal(ServiceDesk, admin.Teams.SelectedTeam?.TeamId);
    }

    [Fact]
    public void A_teams_place_is_described_by_its_name()
    {
        Assert.Equal("Teams: Service Desk", new AdminLocation(AdminTab.Teams, ServiceDesk, "Service Desk").Description);
    }
}
