using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Admin;

/// <summary>The Environment admin window's filters, links between tabs, and what each pane says when a read fails or finds nothing.</summary>
public class EnvironmentAdminNavigationTests
{
    private const string Fv = "@OData.Community.Display.V1.FormattedValue";
    private const string Lt = "@Microsoft.Dynamics.CRM.lookuplogicalname";

    private static readonly Guid Alice = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid TeamA = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid RoleA = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid RoleB = Guid.Parse("33333333-0000-0000-0000-000000000002");
    private static readonly Guid UserMailbox = Guid.Parse("44444444-0000-0000-0000-000000000001");
    private static readonly Guid QueueMailbox = Guid.Parse("44444444-0000-0000-0000-000000000002");
    private static readonly Guid OtherMailbox = Guid.Parse("44444444-0000-0000-0000-000000000003");
    private static readonly Guid Support = Guid.Parse("55555555-0000-0000-0000-000000000001");
    private static readonly Guid Private = Guid.Parse("55555555-0000-0000-0000-000000000002");
    private static readonly Guid Empty = Guid.Parse("55555555-0000-0000-0000-000000000003");

    private static string Value(params object[] rows) => JsonSerializer.Serialize(new { value = rows });

    private static Dictionary<string, object?> User(Guid id, string name, string unit, bool disabled, Guid? mailbox = null) => new()
    {
        ["systemuserid"] = id, ["fullname"] = name, ["domainname"] = name.ToLowerInvariant() + "@contoso.com",
        ["_businessunitid_value" + Fv] = unit, ["isdisabled"] = disabled, ["accessmode"] = 0,
        ["accessmode" + Fv] = "Read-Write", ["_defaultmailbox_value"] = mailbox
    };

    private static Dictionary<string, object?> Role(Guid id, string name, bool managed) => new()
    {
        ["roleid"] = id, ["name"] = name, ["ismanaged"] = managed
    };

    private static Dictionary<string, object?> Mailbox(Guid id, string name, string? regardingType, Guid? regarding) => new()
    {
        ["mailboxid"] = id, ["name"] = name, ["_regardingobjectid_value"] = regarding,
        ["_regardingobjectid_value" + Lt] = regardingType, ["emailrouteraccessapproval"] = 1,
        ["emailrouteraccessapproval" + Fv] = "Approved", ["incomingemailstatus"] = 1, ["outgoingemailstatus"] = 1,
        ["incomingemaildeliverymethod"] = 2, ["outgoingemaildeliverymethod"] = 2, ["statecode"] = 0
    };

    private static Dictionary<string, object?> Queue(Guid id, string name, int viewType, Guid? mailbox, int state = 0) => new()
    {
        ["queueid"] = id, ["name"] = name, ["queueviewtype"] = viewType,
        ["_ownerid_value" + Fv] = "Service Desk", ["_ownerid_value" + Lt] = "team",
        ["_defaultmailbox_value"] = mailbox, ["statecode"] = state
    };

    /// <summary>
    /// Alice (Contoso, with a mailbox) and Bob (Fabrikam, disabled); a managed and an unmanaged
    /// role; mailboxes for a user, a queue and an account; a public queue with a mailbox, a
    /// private one and an empty public one. Anything else reads as empty.
    /// </summary>
    private static FakeHttpHandler Handler() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "systemusers?", Value(
            User(Alice, "Alice", "Contoso", false, UserMailbox),
            User(Bob, "Bob", "Fabrikam", true)))
        .OnJson(HttpMethod.Get, "roles?$select=roleid,name", Value(Role(RoleA, "Basic User", true), Role(RoleB, "Sales Custom", false)))
        .OnJson(HttpMethod.Get, $"mailboxid eq {UserMailbox}", Value(Mailbox(UserMailbox, "Alice", "systemuser", Alice)))
        .OnJson(HttpMethod.Get, $"mailboxid eq {QueueMailbox}", Value(Mailbox(QueueMailbox, "Support", "queue", Support)))
        .OnJson(HttpMethod.Get, "mailboxes?", Value(
            Mailbox(UserMailbox, "Alice", "systemuser", Alice),
            Mailbox(QueueMailbox, "Support", "queue", Support),
            Mailbox(OtherMailbox, "Accounts", "account", Guid.NewGuid())))
        .OnJson(HttpMethod.Get, "queues?", Value(
            Queue(Support, "Support", 0, QueueMailbox),
            Queue(Private, "Escalations", 1, null),
            Queue(Empty, "Old", 0, null)))
        .OnJson(HttpMethod.Get, $"queues({Support})/queuemembership_association", Value(
            new Dictionary<string, object?> { ["systemuserid"] = Alice, ["fullname"] = "Alice", ["isdisabled"] = false }))
        .OnJson(HttpMethod.Get, "", Value());

    private static EnvironmentAdminViewModel Admin(FakeHttpHandler handler, AdminTab tab = AdminTab.Users) =>
        new(AdminSessions.Disconnected(), Fakes.Dataverse(handler), tab);

    private static Task Settle(EnvironmentAdminViewModel admin) => admin.Work.WhenIdleAsync();

    // ---------------------------------------------------------------- the window

    [Fact]
    public void A_place_is_described_by_its_tab_and_row()
    {
        Assert.Equal("Security roles: Basic User", new AdminLocation(AdminTab.Roles, RoleA, "Basic User").Description);
        Assert.Equal("Queues", new AdminLocation(AdminTab.Queues, null, null).Description);
        Assert.Equal("Mailboxes", new AdminLocation(AdminTab.Mailboxes, null, "").Description);
    }

    [Fact]
    public void The_window_is_titled_after_its_tab_and_starts_with_no_history()
    {
        var admin = Admin(Handler());

        Assert.StartsWith("Environment admin — ", admin.Title);
        Assert.Equal("Back (Alt+Left)", admin.BackToolTip);
        Assert.Equal("Forward (Alt+Right)", admin.ForwardToolTip);
        Assert.Equal(new AdminLocation(AdminTab.Users, null, null), admin.Here);
    }

    [Fact]
    public async Task A_tab_that_cannot_be_read_says_why_and_can_be_refreshed()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "roles?", HttpStatusCode.Forbidden, "No read on role.");
        var admin = Admin(handler, AdminTab.Roles);

        await admin.LoadAsync();

        Assert.StartsWith("Could not read - ", admin.Roles.Status);
        Assert.Contains("No read on role.", admin.Roles.Status);
        Assert.False(admin.Roles.IsBusy);
        Assert.True(admin.Roles.RefreshCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- users

    [Fact]
    public async Task Disabled_users_can_be_listed_alone_and_cleared_filters_fall_back_to_all()
    {
        var admin = Admin(Handler());
        await admin.LoadAsync();
        var users = admin.Users;

        users.StatusFilter = UserStatusFilter.Disabled;
        Assert.Equal("Bob", Assert.Single(users.View.Cast<UserInfo>()).FullName);
        Assert.Equal("Showing 1 disabled", users.ShowingSummary);

        users.SelectedType = null!;
        users.SelectedUnit = null!;

        Assert.Equal(AdminUsersViewModel.AllTypes, users.SelectedType);
        Assert.Equal(AdminUsersViewModel.AllUnits, users.SelectedUnit);
    }

    [Fact]
    public async Task Refreshing_users_keeps_the_selected_one_and_drops_a_unit_that_is_gone()
    {
        var unit = "Contoso";
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "systemusers?", _ => FakeHttpHandler.Json(Value(User(Alice, "Alice", unit, false))))
            .OnJson(HttpMethod.Get, "", Value());
        var admin = Admin(handler);
        await admin.LoadAsync();
        admin.Users.SelectedUser = admin.Users.Items[0];
        admin.Users.SelectedUnit = "Contoso";
        await Settle(admin);
        unit = "Fabrikam";

        await admin.Users.RefreshCommand.ExecuteAsync(null);
        await Settle(admin);

        Assert.Equal(Alice, admin.Users.SelectedUser?.SystemUserId);
        Assert.Equal("Fabrikam", admin.Users.SelectedUser?.BusinessUnit);
        Assert.Equal(AdminUsersViewModel.AllUnits, admin.Users.SelectedUnit);
        Assert.Equal("Alice", admin.Users.SelectedLabel);
    }

    [Fact]
    public async Task A_user_with_no_role_is_told_they_cannot_sign_in_and_deselecting_clears_the_pane()
    {
        var admin = Admin(Handler());
        await admin.LoadAsync();
        admin.Users.StatusFilter = UserStatusFilter.All;

        admin.Users.SelectedUser = admin.Users.Items.First(u => u.SystemUserId == Bob);
        await Settle(admin);

        Assert.Equal("0 team(s), 0 direct role(s), 0 through teams. With no role, this user cannot sign in to apps.", admin.Users.DetailStatus);
        Assert.False(admin.Users.IsLoadingDetails);

        admin.Users.SelectedUser = null;
        await Settle(admin);

        Assert.Equal(string.Empty, admin.Users.DetailStatus);
        Assert.Null(admin.Users.Mailbox);
    }

    [Fact]
    public async Task A_users_details_that_cannot_be_read_say_why()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "systemusers?", Value(User(Alice, "Alice", "Contoso", false)))
            .OnError(HttpMethod.Get, "teammembership_association", HttpStatusCode.Forbidden, "No read on team.");
        var admin = Admin(handler);
        await admin.LoadAsync();

        admin.Users.SelectedUser = admin.Users.Items[0];
        await Settle(admin);

        Assert.StartsWith("Could not read this user's teams and roles - ", admin.Users.DetailStatus);
        Assert.False(admin.Users.IsLoadingDetails);
    }

    [Fact]
    public async Task A_users_mailbox_opens_on_the_mailboxes_tab_and_back_returns_to_the_user()
    {
        var admin = Admin(Handler());
        await admin.LoadAsync();
        admin.Users.SelectedUser = admin.Users.Items.First(u => u.SystemUserId == Alice);
        await Settle(admin);
        Assert.True(admin.Users.ShowMailboxCommand.CanExecute(null));

        admin.Users.ShowMailboxCommand.Execute(null);
        await Settle(admin);

        Assert.Equal(AdminTab.Mailboxes, admin.SelectedTab);
        Assert.Equal(UserMailbox, admin.Mailboxes.SelectedId);
        Assert.Equal("Alice", admin.Mailboxes.SelectedLabel);
        Assert.Equal("Back (Alt+Left) — to Users: Alice", admin.BackToolTip);
    }

    [Fact]
    public async Task Following_a_link_to_a_row_that_is_not_there_changes_tab_but_selects_nothing()
    {
        var admin = Admin(Handler());
        await admin.LoadAsync();

        await admin.ShowAsync(AdminTab.Queues, Guid.NewGuid());

        Assert.Equal(AdminTab.Queues, admin.SelectedTab);
        Assert.Null(admin.Queues.SelectedQueue);
        Assert.True(admin.CanGoBack);
    }

    [Fact]
    public async Task Linking_to_a_user_hidden_by_the_filters_clears_them()
    {
        var admin = Admin(Handler());
        await admin.LoadAsync();
        admin.Users.SearchText = "alice";
        admin.Users.SelectedType = "Read-Write";
        admin.Users.SelectedUnit = "Contoso";

        await admin.ShowAsync(AdminTab.Users, Bob);

        Assert.Equal(Bob, admin.Users.SelectedUser?.SystemUserId);
        Assert.Equal((string.Empty, AdminUsersViewModel.AllTypes, AdminUsersViewModel.AllUnits, UserStatusFilter.All),
            (admin.Users.SearchText, admin.Users.SelectedType, admin.Users.SelectedUnit, admin.Users.StatusFilter));
    }

    [Fact]
    public void The_users_pane_can_show_teams_instead_of_roles()
    {
        var admin = Admin(Handler());

        admin.Users.DetailView = UserDetailView.FieldSecurity;

        Assert.False(admin.Users.IsRolesView);
    }

    // ---------------------------------------------------------------- roles

    [Fact]
    public async Task Roles_filter_by_whether_they_are_managed()
    {
        var admin = Admin(Handler(), AdminTab.Roles);
        await admin.LoadAsync();
        var roles = admin.Roles;

        Assert.Equal((2, 1, 1), (roles.CountAll, roles.CountManaged, roles.CountUnmanaged));

        roles.ManagedFilter = RoleManagedFilter.Managed;
        Assert.Equal("Basic User", Assert.Single(roles.View.Cast<SecurityRoleInfo>()).Name);

        roles.ManagedFilter = RoleManagedFilter.Unmanaged;
        Assert.Equal("Sales Custom", Assert.Single(roles.View.Cast<SecurityRoleInfo>()).Name);
        Assert.Equal("Showing 1 of 2 roles", roles.ShowingSummary);
    }

    [Fact]
    public async Task Linking_to_a_role_hidden_by_the_filters_clears_them_and_refreshing_keeps_it()
    {
        var admin = Admin(Handler(), AdminTab.Roles);
        await admin.LoadAsync();
        admin.Roles.ManagedFilter = RoleManagedFilter.Unmanaged;
        admin.Roles.SearchText = "sales";

        await admin.ShowAsync(AdminTab.Roles, RoleA);
        await Settle(admin);

        Assert.Equal("Basic User", admin.Roles.SelectedLabel);
        Assert.Equal(RoleA, admin.Roles.SelectedId);
        Assert.Equal(RoleManagedFilter.All, admin.Roles.ManagedFilter);
        Assert.Equal(string.Empty, admin.Roles.SearchText);

        await admin.Roles.RefreshCommand.ExecuteAsync(null);
        await Settle(admin);

        Assert.Equal(RoleA, admin.Roles.SelectedRole?.RoleId);
    }

    [Fact]
    public async Task A_roles_privileges_and_holders_that_cannot_be_read_each_say_why()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "roles?$select=roleid,name", Value(Role(RoleA, "Basic User", true)))
            .OnError(HttpMethod.Get, "RetrieveRolePrivilegesRole", HttpStatusCode.InternalServerError, "Privileges unavailable.")
            .OnError(HttpMethod.Get, "_parentrootroleid_value", HttpStatusCode.Forbidden, "No read on user.");
        var admin = Admin(handler, AdminTab.Roles);
        await admin.LoadAsync();

        admin.Roles.SelectedRole = admin.Roles.Items[0];
        await Settle(admin);

        Assert.StartsWith("Could not read the privileges - ", admin.Roles.DetailStatus);
        Assert.StartsWith("Could not read who holds this role - ", admin.Roles.HolderStatus);
        Assert.False(admin.Roles.IsLoadingDetails);
        Assert.Equal("Showing 1 roles", admin.Roles.ShowingSummary);
    }

    [Fact]
    public async Task A_role_lists_its_holders_and_a_user_holder_links_to_the_users_tab()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "_parentrootroleid_value", Value(new Dictionary<string, object?>
            {
                ["roleid"] = RoleA, ["_businessunitid_value" + Fv] = "Contoso",
                ["systemuserroles_association"] = new object[] { new { systemuserid = Alice, fullname = "Alice", isdisabled = false } },
                ["teamroles_association"] = new object[] { new { teamid = TeamA, name = "Service Desk" } }
            }))
            .OnJson(HttpMethod.Get, "roles?$select=roleid,name", Value(Role(RoleA, "Basic User", true)))
            .OnJson(HttpMethod.Get, "RetrieveRolePrivilegesRole", """{"RolePrivileges":[]}""")
            .OnJson(HttpMethod.Get, "systemusers?", Value(User(Alice, "Alice", "Contoso", false)))
            .OnJson(HttpMethod.Get, "", Value());
        var admin = Admin(handler, AdminTab.Roles);
        await admin.LoadAsync();
        admin.Roles.SelectedRole = admin.Roles.Items[0];
        await Settle(admin);

        Assert.Equal("1 user(s) and 1 team(s). Team members hold it through their team.", admin.Roles.HolderStatus);
        var team = admin.Roles.Holders.Single(h => h.IsTeam);
        Assert.False(admin.Roles.ShowHolderCommand.CanExecute(team));

        admin.Roles.ShowHolderCommand.Execute(admin.Roles.Holders.Single(h => !h.IsTeam));
        await Settle(admin);

        Assert.Equal(AdminTab.Users, admin.SelectedTab);
        Assert.Equal(Alice, admin.Users.SelectedUser?.SystemUserId);
    }

    [Fact]
    public async Task The_privilege_grid_can_show_tables_the_role_grants_nothing_on()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "roles?$select=roleid,name", Value(Role(RoleA, "Basic User", true)))
            .OnJson(HttpMethod.Get, "RetrieveRolePrivilegesRole", """
                {"RolePrivileges":[{"PrivilegeId":"66666666-0000-0000-0000-000000000001","PrivilegeName":"prvReadAccount","Depth":"Global"}]}
                """)
            .OnJson(HttpMethod.Get, "", Value());
        var admin = Admin(handler, AdminTab.Roles);
        await admin.LoadAsync();
        admin.Roles.SelectedRole = admin.Roles.Items[0];
        await Settle(admin);

        admin.Roles.GrantedOnly = false;
        admin.Roles.DetailView = RoleDetailView.HeldBy;

        Assert.Equal(1, admin.Roles.PrivilegesView.Count);
        Assert.False(admin.Roles.IsPrivilegesView);
        Assert.Equal("1 table(s), 0 other privilege(s).", admin.Roles.DetailStatus);
    }

    // ---------------------------------------------------------------- mailboxes

    [Fact]
    public async Task Mailboxes_owned_by_neither_a_user_nor_a_queue_filter_as_other()
    {
        var admin = Admin(Handler(), AdminTab.Mailboxes);
        await admin.LoadAsync();
        var m = admin.Mailboxes;

        m.OwnerFilter = MailboxOwnerFilter.Other;
        Assert.Equal("Accounts", Assert.Single(m.View.Cast<MailboxInfo>()).Name);

        m.OwnerFilter = MailboxOwnerFilter.Users;
        Assert.Equal("Alice", Assert.Single(m.View.Cast<MailboxInfo>()).Name);

        m.SelectedApproval = null!;
        m.SelectedTest = null!;
        Assert.Equal((AdminMailboxesViewModel.AnyApproval, AdminMailboxesViewModel.AnyTest), (m.SelectedApproval, m.SelectedTest));
        Assert.Equal((3, 1), (m.CountAll, m.CountOther));
    }

    [Fact]
    public async Task A_queues_mailbox_links_to_the_queue_and_an_accounts_mailbox_links_nowhere()
    {
        var admin = Admin(Handler(), AdminTab.Mailboxes);
        await admin.LoadAsync();

        admin.Mailboxes.SelectedMailbox = admin.Mailboxes.Items.Single(m => m.MailboxId == OtherMailbox);
        Assert.False(admin.Mailboxes.ShowOwnerCommand.CanExecute(null));

        admin.Mailboxes.SelectedMailbox = admin.Mailboxes.Items.Single(m => m.MailboxId == QueueMailbox);
        admin.Mailboxes.ShowOwnerCommand.Execute(null);
        await Settle(admin);

        Assert.Equal(AdminTab.Queues, admin.SelectedTab);
        Assert.Equal("Support", admin.Queues.SelectedLabel);
        Assert.Equal(Support, admin.Queues.SelectedId);
    }

    [Fact]
    public async Task Linking_to_a_mailbox_hidden_by_the_filters_clears_them_and_refreshing_keeps_it()
    {
        var admin = Admin(Handler(), AdminTab.Mailboxes);
        await admin.LoadAsync();
        admin.Mailboxes.OwnerFilter = MailboxOwnerFilter.Users;
        admin.Mailboxes.SelectedTest = "Passed";

        await admin.ShowAsync(AdminTab.Mailboxes, OtherMailbox);

        Assert.Equal(OtherMailbox, admin.Mailboxes.SelectedMailbox?.MailboxId);
        Assert.Equal(MailboxOwnerFilter.All, admin.Mailboxes.OwnerFilter);
        Assert.Equal(AdminMailboxesViewModel.AnyTest, admin.Mailboxes.SelectedTest);

        await admin.Mailboxes.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(OtherMailbox, admin.Mailboxes.SelectedMailbox?.MailboxId);
    }

    // ---------------------------------------------------------------- queues

    [Fact]
    public async Task Queues_count_and_filter_public_and_private()
    {
        var admin = Admin(Handler(), AdminTab.Queues);
        await admin.LoadAsync();
        var q = admin.Queues;

        Assert.Equal((3, 2, 1), (q.CountAll, q.CountPublic, q.CountPrivate));

        q.TypeFilter = QueueTypeFilter.Public;

        Assert.Equal(["Support", "Old"], q.View.Cast<QueueDetail>().Select(x => x.Name));
        Assert.Equal("Showing 2 of 3 active queues", q.ShowingSummary);
    }

    [Fact]
    public async Task An_empty_queue_says_who_can_see_its_items()
    {
        var admin = Admin(Handler(), AdminTab.Queues);
        await admin.LoadAsync();

        admin.Queues.SelectedQueue = admin.Queues.Items.Single(q => q.QueueId == Private);
        await Settle(admin);
        Assert.Equal("This private queue has no members, so nobody can see its items.", admin.Queues.DetailStatus);

        admin.Queues.SelectedQueue = admin.Queues.Items.Single(q => q.QueueId == Empty);
        await Settle(admin);
        Assert.Equal("No members. A public queue is open to everyone with access to queues.", admin.Queues.DetailStatus);

        admin.Queues.SelectedQueue = null;
        await Settle(admin);
        Assert.Equal(string.Empty, admin.Queues.DetailStatus);
    }

    [Fact]
    public async Task A_queues_members_that_cannot_be_read_say_why()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "queues?", Value(Queue(Private, "Escalations", 1, null)))
            .OnError(HttpMethod.Get, "queuemembership_association", HttpStatusCode.Forbidden, "No read on queue.");
        var admin = Admin(handler, AdminTab.Queues);
        await admin.LoadAsync();

        admin.Queues.SelectedQueue = admin.Queues.Items[0];
        await Settle(admin);

        Assert.StartsWith("Could not read this queue's members - ", admin.Queues.DetailStatus);
        Assert.False(admin.Queues.IsLoadingDetails);
    }

    [Fact]
    public async Task A_queues_mailbox_and_members_link_to_their_tabs()
    {
        var admin = Admin(Handler(), AdminTab.Queues);
        await admin.LoadAsync();
        admin.Queues.SelectedQueue = admin.Queues.Items.Single(q => q.QueueId == Support);
        await Settle(admin);
        Assert.Equal("1 member(s).", admin.Queues.DetailStatus);
        Assert.Equal("Support", admin.Queues.Mailbox?.Name);

        admin.Queues.ShowMailboxCommand.Execute(null);
        await Settle(admin);
        Assert.Equal(QueueMailbox, admin.Mailboxes.SelectedMailbox?.MailboxId);

        await admin.BackCommand.ExecuteAsync(null);
        Assert.Equal(AdminTab.Queues, admin.SelectedTab);
        admin.Queues.ShowMemberCommand.Execute(admin.Queues.Members[0]);
        await Settle(admin);

        Assert.Equal(AdminTab.Users, admin.SelectedTab);
        Assert.Equal(Alice, admin.Users.SelectedUser?.SystemUserId);
        Assert.False(admin.Queues.ShowMemberCommand.CanExecute(null));
    }

    [Fact]
    public async Task Linking_to_an_inactive_queue_shows_inactive_ones_and_refreshing_keeps_it()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "queues?", Value(Queue(Support, "Support", 0, null), Queue(Private, "Retired", 1, null, state: 1)))
            .OnJson(HttpMethod.Get, "", Value());
        var admin = Admin(handler, AdminTab.Queues);
        await admin.LoadAsync();
        admin.Queues.TypeFilter = QueueTypeFilter.Public;

        await admin.ShowAsync(AdminTab.Queues, Private);
        await Settle(admin);

        Assert.Equal("Retired", admin.Queues.SelectedQueue?.Name);
        Assert.False(admin.Queues.ActiveOnly);
        Assert.Equal(QueueTypeFilter.All, admin.Queues.TypeFilter);

        await admin.Queues.RefreshCommand.ExecuteAsync(null);
        await Settle(admin);

        Assert.Equal(Private, admin.Queues.SelectedQueue?.QueueId);
    }
}
