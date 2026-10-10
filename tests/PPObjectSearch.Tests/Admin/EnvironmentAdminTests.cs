using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Admin;

/// <summary>
/// The Environment admin window: users with their teams and roles, security roles with their
/// privileges and holders, mailboxes and queues. Read-only throughout.
/// </summary>
public class EnvironmentAdminTests
{
    private const string Fv = "@OData.Community.Display.V1.FormattedValue";
    private const string Lt = "@Microsoft.Dynamics.CRM.lookuplogicalname";

    private static readonly Guid Alice = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid AppUser = Guid.Parse("11111111-0000-0000-0000-000000000003");
    private static readonly Guid TeamA = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid RoleRoot = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid RoleCopy = Guid.Parse("33333333-0000-0000-0000-000000000002");
    private static readonly Guid MailboxA = Guid.Parse("44444444-0000-0000-0000-000000000001");
    private static readonly Guid QueueA = Guid.Parse("55555555-0000-0000-0000-000000000001");

    private static string Value(params object[] rows) => JsonSerializer.Serialize(new { value = rows });

    private static Dictionary<string, object?> User(Guid id, string name, string bu, bool disabled, int accessMode, string mode,
        Guid? app = null, Guid? mailbox = null) => new()
    {
        ["systemuserid"] = id, ["fullname"] = name, ["domainname"] = name.ToLowerInvariant().Replace(' ', '.') + "@contoso.com",
        ["_businessunitid_value"] = Guid.NewGuid(), ["_businessunitid_value" + Fv] = bu,
        ["isdisabled"] = disabled, ["accessmode"] = accessMode, ["accessmode" + Fv] = mode,
        ["applicationid"] = app, ["_defaultmailbox_value"] = mailbox
    };

    private static string Users() => Value(
        User(Alice, "Alice Smith", "Contoso", false, 0, "Read-Write", mailbox: MailboxA),
        User(Bob, "Bob Jones", "Contoso UK", true, 0, "Read-Write"),
        User(AppUser, "Flow Bot", "Contoso", false, 4, "Non-interactive", app: Guid.NewGuid()));

    private static Dictionary<string, object?> Mailbox(Guid id, string name, string regardingType, Guid regarding, int approval, string approvalLabel,
        int incoming, int outgoing, int inDelivery = 2, int outDelivery = 2, bool scheduled = false) => new()
    {
        ["mailboxid"] = id, ["name"] = name, ["emailaddress"] = name.Replace(' ', '.') + "@contoso.com",
        ["_regardingobjectid_value"] = regarding, ["_regardingobjectid_value" + Fv] = name, ["_regardingobjectid_value" + Lt] = regardingType,
        ["emailrouteraccessapproval"] = approval, ["emailrouteraccessapproval" + Fv] = approvalLabel,
        ["incomingemailstatus"] = incoming, ["outgoingemailstatus"] = outgoing,
        ["incomingemaildeliverymethod"] = inDelivery, ["outgoingemaildeliverymethod"] = outDelivery,
        ["testemailconfigurationscheduled"] = scheduled, ["statecode"] = 0
    };

    private static EnvironmentAdminViewModel Admin(FakeHttpHandler handler, AdminTab tab = AdminTab.Users) =>
        new(new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(),
                new TabState { EnvironmentUrl = Fakes.EnvironmentUrl }),
            Fakes.Dataverse(handler), tab);

    /// <summary>Selections and links start loads nobody awaits; this waits for them.</summary>
    private static Task Settle(EnvironmentAdminViewModel admin) => admin.Work.WhenIdleAsync();

    // ---------------------------------------------------------------- users

    [Fact]
    public async Task Users_read_with_their_type_business_unit_and_status()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "systemusers?", Users());

        var users = await Fakes.Dataverse(handler).GetUsersAsync();

        Assert.Equal(3, users.Count);
        Assert.Equal("Read-Write", users[0].UserType);
        Assert.Equal("Contoso", users[0].BusinessUnit);
        Assert.Equal(MailboxA, users[0].DefaultMailboxId);
        Assert.Equal("Disabled", users[1].StatusLabel);
        Assert.Equal("Application user", users[2].UserType);
        Assert.Contains("$orderby=fullname", handler.Requests[0].Url);
    }

    [Fact]
    public async Task A_column_the_environment_lacks_falls_back_to_the_core_columns()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "caltype", HttpStatusCode.BadRequest, "Could not find a property named 'caltype'")
            .OnJson(HttpMethod.Get, "systemusers?", Users());

        var users = await Fakes.Dataverse(handler).GetUsersAsync();

        Assert.Equal(3, users.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("caltype", handler.Requests[1].Url);
    }

    [Fact]
    public async Task The_users_tab_shows_enabled_users_first_and_filters_by_type_and_unit()
    {
        var admin = Admin(new FakeHttpHandler().OnJson(HttpMethod.Get, "systemusers?", Users()));
        var users = admin.Users;

        await admin.LoadAsync();

        Assert.Equal(UserStatusFilter.Enabled, users.StatusFilter);
        Assert.Equal(2, users.View.Count);
        Assert.Equal((3, 2, 1), (users.CountAll, users.CountEnabled, users.CountDisabled));
        Assert.Equal([AdminUsersViewModel.AllTypes, "Application user", "Read-Write"], users.TypeOptions);
        Assert.Equal([AdminUsersViewModel.AllUnits, "Contoso", "Contoso UK"], users.UnitOptions);

        users.SelectedType = "Application user";
        Assert.Equal("Flow Bot", Assert.Single(users.View.Cast<UserInfo>()).FullName);

        users.SelectedType = AdminUsersViewModel.AllTypes;
        users.StatusFilter = UserStatusFilter.All;
        users.SelectedUnit = "Contoso UK";
        Assert.Equal("Bob Jones", Assert.Single(users.View.Cast<UserInfo>()).FullName);

        users.SelectedUnit = AdminUsersViewModel.AllUnits;
        users.SearchText = "alice";
        Assert.Equal("Alice Smith", Assert.Single(users.View.Cast<UserInfo>()).FullName);
    }

    [Fact]
    public async Task A_users_roles_come_directly_and_through_each_team_in_their_business_unit()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "systemusers?", Users())
            .OnJson(HttpMethod.Get, $"systemusers({Alice})/teammembership_association", Value(
                new Dictionary<string, object?> { ["teamid"] = TeamA, ["name"] = "Service Desk", ["teamtype" + Fv] = "Owner", ["isdefault"] = false, ["_businessunitid_value" + Fv] = "Contoso" }))
            .OnJson(HttpMethod.Get, $"systemusers({Alice})/systemuserroles_association", Value(
                new Dictionary<string, object?> { ["roleid"] = RoleCopy, ["name"] = "Basic User", ["_parentrootroleid_value"] = RoleRoot, ["_businessunitid_value" + Fv] = "Contoso UK" }))
            .OnJson(HttpMethod.Get, $"teams({TeamA})/teamroles_association", Value(
                new Dictionary<string, object?> { ["roleid"] = Guid.NewGuid(), ["name"] = "Customer Service Rep", ["_businessunitid_value" + Fv] = "Contoso" }))
            .OnJson(HttpMethod.Get, $"mailboxid eq {MailboxA}", Value(Mailbox(MailboxA, "Alice Smith", "systemuser", Alice, 1, "Approved", 1, 1)));
        var admin = Admin(handler);
        await admin.LoadAsync();

        admin.Users.SelectedUser = admin.Users.Items.First(u => u.SystemUserId == Alice);
        await Settle(admin);

        var roles = admin.Users.RoleAssignments;
        Assert.Equal(["Basic User", "Customer Service Rep"], roles.Select(r => r.RoleName));
        Assert.Equal("Direct", roles[0].Via);
        Assert.Equal("Contoso UK", roles[0].BusinessUnit);
        Assert.Equal(RoleRoot, roles[0].DefinitionId);
        Assert.Equal("Team: Service Desk", roles[1].Via);
        Assert.Equal((1, 1), (admin.Users.DirectRoleCount, admin.Users.TeamRoleCount));
        Assert.Equal("Service Desk", Assert.Single(admin.Users.Teams).Name);
        Assert.Equal(MailboxTestStatus.Passed, admin.Users.Mailbox!.TestStatus);
    }

    [Fact]
    public async Task A_users_role_opens_the_roles_tab_on_that_role()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "roles?", Value(
                new Dictionary<string, object?> { ["roleid"] = RoleRoot, ["name"] = "Basic User", ["ismanaged"] = true }))
            .OnJson(HttpMethod.Get, "RetrieveRolePrivilegesRole", """{"RolePrivileges":[]}""");
        var admin = Admin(handler);

        admin.Users.ShowRoleCommand.Execute(new RoleAssignment(RoleCopy, RoleRoot, "Basic User", "Contoso UK", null, null));
        await Settle(admin);

        Assert.Equal(AdminTab.Roles, admin.SelectedTab);
        Assert.Equal(RoleRoot, admin.Roles.SelectedRole?.RoleId);
    }

    // ---------------------------------------------------------------- security roles

    [Fact]
    public async Task Roles_are_listed_once_at_the_business_unit_they_were_defined_in()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "roles?", Value(
            new Dictionary<string, object?> { ["roleid"] = RoleRoot, ["name"] = "Basic User", ["ismanaged"] = true, ["_businessunitid_value" + Fv] = "Contoso" }));

        var roles = await Fakes.Dataverse(handler).GetSecurityRolesAsync();

        Assert.Equal("Managed", Assert.Single(roles).ManagedLabel);
        Assert.Contains("_parentroleid_value eq null", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Privileges_come_with_their_depth_by_name_or_number()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"RetrieveRolePrivilegesRole(RoleId={RoleRoot})", """
            {"RolePrivileges":[
              {"PrivilegeId":"66666666-0000-0000-0000-000000000001","PrivilegeName":"prvReadAccount","Depth":"Global"},
              {"PrivilegeId":"66666666-0000-0000-0000-000000000002","PrivilegeName":"prvWriteAccount","Depth":1},
              {"PrivilegeId":"66666666-0000-0000-0000-000000000003","PrivilegeName":"prvCreateAccount","Depth":"Basic"},
              {"PrivilegeId":"66666666-0000-0000-0000-000000000004","PrivilegeName":"prvAppendToAccount","Depth":"Deep"}
            ]}
            """);

        var privileges = await Fakes.Dataverse(handler).GetRolePrivilegesAsync(RoleRoot);

        Assert.Equal(
            [PrivilegeDepth.Organization, PrivilegeDepth.BusinessUnit, PrivilegeDepth.User, PrivilegeDepth.ParentChild],
            privileges.Select(p => p.Depth));
    }

    [Fact]
    public async Task Privileges_without_a_name_are_named_from_the_privilege_table()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "RetrieveRolePrivilegesRole", """
                {"RolePrivileges":[{"PrivilegeId":"66666666-0000-0000-0000-000000000001","Depth":"Local"}]}
                """)
            .OnJson(HttpMethod.Get, "privileges?", Value(
                new Dictionary<string, object?> { ["privilegeid"] = "66666666-0000-0000-0000-000000000001", ["name"] = "prvExportToExcel" }));

        var privilege = Assert.Single(await Fakes.Dataverse(handler).GetRolePrivilegesAsync(RoleRoot));

        Assert.Equal("prvExportToExcel", privilege.Name);
    }

    [Fact]
    public void The_privilege_grid_puts_each_tables_eight_privileges_on_one_row()
    {
        var (tables, misc) = PrivilegeMatrix.Build(
        [
            new(Guid.NewGuid(), "prvReadAccount", PrivilegeDepth.Organization),
            new(Guid.NewGuid(), "prvAppendToAccount", PrivilegeDepth.BusinessUnit),
            new(Guid.NewGuid(), "prvAppendAccount", PrivilegeDepth.User),
            new(Guid.NewGuid(), "prvCreatecontact", PrivilegeDepth.User),
            new(Guid.NewGuid(), "prvExportToExcel", PrivilegeDepth.Organization),
            new(Guid.NewGuid(), "BulkDelete", PrivilegeDepth.Organization)
        ]);

        Assert.Equal(["Account", "contact"], tables.Select(t => t.Table));
        var account = tables[0];
        Assert.Equal(PrivilegeDepth.Organization, account.Read);
        Assert.Equal(PrivilegeDepth.BusinessUnit, account.AppendTo);
        Assert.Equal(PrivilegeDepth.User, account.Append);
        Assert.Equal(PrivilegeDepth.None, account.Delete);
        Assert.Equal(PrivilegeDepth.Organization, account.Widest);

        // ExportToExcel starts with none of the eight actions, so it is not a table's.
        Assert.Equal(["BulkDelete", "ExportToExcel"], misc.Select(m => m.Name));
        Assert.Equal("Organization", misc[0].DepthLabel);
    }

    [Theory]
    [InlineData("prvAppendToAccount", "AppendTo", "Account")]
    [InlineData("prvAppendAccount", "Append", "Account")]
    [InlineData("prvShareincident", "Share", "incident")]
    public void A_table_privilege_name_says_its_action_and_table(string name, string action, string table)
    {
        Assert.Equal((action, table), PrivilegeMatrix.Parse(name));
    }

    [Theory]
    [InlineData("prvRead")]
    [InlineData("ReadAccount")]
    [InlineData("prvGoMobile")]
    public void Other_names_are_not_table_privileges(string name)
    {
        Assert.Null(PrivilegeMatrix.Parse(name));
    }

    [Fact]
    public async Task A_roles_holders_are_found_through_every_business_units_copy()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"_parentrootroleid_value eq {RoleRoot}", Value(
            new Dictionary<string, object?>
            {
                ["roleid"] = RoleRoot, ["_businessunitid_value" + Fv] = "Contoso",
                ["systemuserroles_association"] = new object[] { new { systemuserid = Alice, fullname = "Alice Smith", domainname = "alice@contoso.com", isdisabled = false } },
                ["teamroles_association"] = new object[] { new { teamid = TeamA, name = "Service Desk" } }
            },
            new Dictionary<string, object?>
            {
                ["roleid"] = RoleCopy, ["_businessunitid_value" + Fv] = "Contoso UK",
                ["systemuserroles_association"] = new object[] { new { systemuserid = Bob, fullname = "Bob Jones", isdisabled = true } },
                ["teamroles_association"] = Array.Empty<object>()
            }));

        var holders = await Fakes.Dataverse(handler).GetRoleHoldersAsync(RoleRoot);

        Assert.Equal(["Alice Smith", "Bob Jones", "Service Desk"], holders.Select(h => h.Name));
        Assert.Equal("Contoso UK", holders[1].BusinessUnit);
        Assert.Equal("disabled", holders[1].Detail);
        Assert.Equal("Team", holders[2].Kind);
        Assert.Contains("$expand=systemuserroles_association", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Selecting_a_role_reads_its_privileges_and_holders_and_hides_tables_it_grants_nothing_on()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "roles?$select=roleid,name", Value(
                new Dictionary<string, object?> { ["roleid"] = RoleRoot, ["name"] = "Basic User", ["ismanaged"] = false }))
            .OnJson(HttpMethod.Get, "RetrieveRolePrivilegesRole", """
                {"RolePrivileges":[
                  {"PrivilegeId":"66666666-0000-0000-0000-000000000001","PrivilegeName":"prvReadAccount","Depth":"Global"},
                  {"PrivilegeId":"66666666-0000-0000-0000-000000000002","PrivilegeName":"prvReadContact","Depth":"Basic"}
                ]}
                """)
            .OnJson(HttpMethod.Get, "_parentrootroleid_value", Value());
        var admin = Admin(handler, AdminTab.Roles);
        await admin.LoadAsync();

        admin.Roles.SelectedRole = admin.Roles.Items[0];
        await Settle(admin);

        Assert.Equal(2, admin.Roles.PrivilegesView.Count);
        admin.Roles.PrivilegeSearch = "cont";
        Assert.Equal("Contact", Assert.Single(admin.Roles.PrivilegesView.Cast<PrivilegeMatrixRow>()).Table);
        Assert.Contains("Nobody holds this role", admin.Roles.HolderStatus);
        Assert.Equal(1, admin.Roles.CountUnmanaged);
    }

    // ---------------------------------------------------------------- mailboxes

    [Theory]
    [InlineData(1, 1, 2, 2, false, MailboxTestStatus.Passed)]
    [InlineData(1, 2, 2, 2, false, MailboxTestStatus.Failed)]
    [InlineData(1, 0, 2, 2, false, MailboxTestStatus.Partial)]
    [InlineData(0, 1, 0, 2, false, MailboxTestStatus.Passed)]   // no incoming delivery: only outgoing counts
    [InlineData(0, 0, 2, 2, false, MailboxTestStatus.NotRun)]
    [InlineData(1, 1, 2, 2, true, MailboxTestStatus.Pending)]
    [InlineData(0, 0, 0, 0, false, MailboxTestStatus.NotRun)]
    public void A_mailboxs_test_status_counts_only_the_directions_it_delivers(
        int incoming, int outgoing, int inDelivery, int outDelivery, bool scheduled, MailboxTestStatus expected)
    {
        var mailbox = new MailboxInfo(Guid.NewGuid(), "m", null, null, null, null, 1, "Approved", incoming, null, outgoing, null, null,
            scheduled, null, null, null, null, inDelivery, null, outDelivery, null, null, null, null, true);

        Assert.Equal(expected, mailbox.TestStatus);
    }

    [Fact]
    public async Task Mailboxes_read_their_owner_approval_and_tests()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "mailboxes?", Value(
            Mailbox(MailboxA, "Alice Smith", "systemuser", Alice, 1, "Approved", 1, 1),
            Mailbox(Guid.NewGuid(), "Support", "queue", QueueA, 2, "Pending Approval", 0, 0)));

        var mailboxes = await Fakes.Dataverse(handler).GetMailboxesAsync();

        Assert.Equal(["User", "Queue"], mailboxes.Select(m => m.OwnerKind));
        Assert.True(mailboxes[0].IsApproved);
        Assert.Equal("Not tested", mailboxes[1].TestLabel);
        Assert.Contains("odata.include-annotations=\"*\"", handler.Requests[0].Header("Prefer"));
    }

    [Fact]
    public async Task The_mailboxes_tab_filters_by_owner_approval_and_test()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "mailboxes?", Value(
            Mailbox(MailboxA, "Alice Smith", "systemuser", Alice, 1, "Approved", 1, 1),
            Mailbox(Guid.NewGuid(), "Support", "queue", QueueA, 2, "Pending Approval", 0, 0),
            Mailbox(Guid.NewGuid(), "Sales", "queue", Guid.NewGuid(), 1, "Approved", 2, 1)));
        var admin = Admin(handler, AdminTab.Mailboxes);
        await admin.LoadAsync();
        var m = admin.Mailboxes;

        Assert.Equal((3, 1, 2, 0), (m.CountAll, m.CountUsers, m.CountQueues, m.CountOther));

        m.OwnerFilter = MailboxOwnerFilter.Queues;
        Assert.Equal(2, m.View.Count);

        m.SelectedApproval = "Approved";
        Assert.Equal("Sales", Assert.Single(m.View.Cast<MailboxInfo>()).Name);

        m.SelectedApproval = AdminMailboxesViewModel.AnyApproval;
        m.SelectedTest = "Not tested";
        Assert.Equal("Support", Assert.Single(m.View.Cast<MailboxInfo>()).Name);
    }

    [Fact]
    public async Task A_mailboxs_owner_opens_on_its_own_tab_with_hidden_filters_cleared()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "mailboxes?", Value(Mailbox(MailboxA, "Bob Jones", "systemuser", Bob, 1, "Approved", 1, 1)))
            .OnJson(HttpMethod.Get, "systemusers?", Users())
            .OnJson(HttpMethod.Get, "", Value());
        var admin = Admin(handler, AdminTab.Mailboxes);
        await admin.LoadAsync();
        admin.Mailboxes.SelectedMailbox = admin.Mailboxes.Items[0];

        admin.Mailboxes.ShowOwnerCommand.Execute(null);
        await Settle(admin);

        // Bob is disabled, which the default Enabled filter hides - so it is cleared.
        Assert.Equal(AdminTab.Users, admin.SelectedTab);
        Assert.Equal(Bob, admin.Users.SelectedUser?.SystemUserId);
        Assert.Equal(UserStatusFilter.All, admin.Users.StatusFilter);
    }

    // ---------------------------------------------------------------- queues

    private static Dictionary<string, object?> Queue(Guid id, string name, int viewType, string owner, string ownerType, Guid? mailbox, int state = 0) => new()
    {
        ["queueid"] = id, ["name"] = name, ["emailaddress"] = name.ToLowerInvariant() + "@contoso.com",
        ["queueviewtype"] = viewType, ["queueviewtype" + Fv] = viewType == 1 ? "Private" : "Public",
        ["_ownerid_value"] = Guid.NewGuid(), ["_ownerid_value" + Fv] = owner, ["_ownerid_value" + Lt] = ownerType,
        ["_defaultmailbox_value"] = mailbox, ["_defaultmailbox_value" + Fv] = mailbox is null ? null : name,
        ["incomingemailfilteringmethod" + Fv] = "All email messages", ["ignoreunsolicitedemail"] = true,
        ["numberofmembers"] = 2, ["statecode"] = state
    };

    [Fact]
    public async Task Queues_read_their_owner_type_and_email_settings()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "queues?", Value(Queue(QueueA, "Support", 1, "Service Desk", "team", MailboxA)));

        var queue = Assert.Single(await Fakes.Dataverse(handler).GetQueueDetailsAsync());

        Assert.True(queue.IsPrivate);
        Assert.Equal("Service Desk (Team)", queue.OwnerLabel);
        Assert.Equal(MailboxA, queue.MailboxId);
        Assert.Equal("All email messages", queue.IncomingFilteringLabel);
        Assert.True(queue.IgnoreUnsolicited);
    }

    [Fact]
    public async Task The_queues_tab_shows_active_queues_and_a_queues_members_and_mailbox()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "queues?", Value(
                Queue(QueueA, "Support", 1, "Service Desk", "team", MailboxA),
                Queue(Guid.NewGuid(), "Old", 0, "Alice Smith", "systemuser", null, state: 1)))
            .OnJson(HttpMethod.Get, $"queues({QueueA})/queuemembership_association", Value(
                new Dictionary<string, object?> { ["systemuserid"] = Bob, ["fullname"] = "Bob Jones", ["isdisabled"] = true },
                new Dictionary<string, object?> { ["systemuserid"] = Alice, ["fullname"] = "Alice Smith", ["isdisabled"] = false }))
            .OnJson(HttpMethod.Get, $"mailboxid eq {MailboxA}", Value(Mailbox(MailboxA, "Support", "queue", QueueA, 3, "Rejected", 0, 0)));
        var admin = Admin(handler, AdminTab.Queues);
        await admin.LoadAsync();
        var q = admin.Queues;

        Assert.Equal("Support", Assert.Single(q.View.Cast<QueueDetail>()).Name);
        q.ActiveOnly = false;
        Assert.Equal(2, q.View.Count);
        q.TypeFilter = QueueTypeFilter.Private;
        Assert.Equal(1, q.View.Count);

        q.SelectedQueue = q.Items[0];
        await Settle(admin);

        Assert.Equal(["Alice Smith", "Bob Jones"], q.Members.Select(m => m.FullName));
        Assert.Equal("Rejected", q.Mailbox?.ApprovalLabel);
        Assert.Contains("1 disabled", q.DetailStatus);
    }

    // ---------------------------------------------------------------- the window

    [Fact]
    public async Task Each_tab_reads_only_once_it_is_opened()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "systemusers?", Users())
            .OnJson(HttpMethod.Get, "queues?", Value());
        var admin = Admin(handler);

        await admin.LoadAsync();
        Assert.All(handler.Requests, r => Assert.Contains("systemusers", r.Url));

        admin.SelectedTab = AdminTab.Queues;
        await Settle(admin);
        admin.SelectedTab = AdminTab.Users;
        admin.SelectedTab = AdminTab.Queues;
        await Settle(admin);

        Assert.Equal(1, handler.Requests.Count(r => r.Url.Contains("queues?")));
        Assert.Equal(1, handler.Requests.Count(r => r.Url.Contains("systemusers?")));
    }

    [Fact]
    public async Task Nothing_in_the_window_writes()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "", Value());
        var admin = Admin(handler);

        foreach (var tab in Enum.GetValues<AdminTab>())
        {
            admin.SelectedTab = tab;
            await Settle(admin);
        }

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }
    // ---------------------------------------------------------------- round 3: history, summaries, detail views

    private static FakeHttpHandler LinkedHandler() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "mailboxes?", Value(Mailbox(MailboxA, "Bob Jones", "systemuser", Bob, 1, "Approved", 1, 1)))
        .OnJson(HttpMethod.Get, "systemusers?", Users())
        .OnJson(HttpMethod.Get, "", Value());

    [Fact]
    public async Task Following_a_link_can_be_undone_with_back_and_redone_with_forward()
    {
        var admin = Admin(LinkedHandler(), AdminTab.Mailboxes);
        await admin.LoadAsync();
        admin.Mailboxes.SelectedMailbox = admin.Mailboxes.Items[0];
        Assert.False(admin.CanGoBack);

        admin.Mailboxes.ShowOwnerCommand.Execute(null);
        await Settle(admin);

        Assert.Equal(AdminTab.Users, admin.SelectedTab);
        Assert.True(admin.CanGoBack);
        Assert.False(admin.CanGoForward);
        Assert.Equal("Back (Alt+Left) — to Mailboxes: Bob Jones", admin.BackToolTip);

        await admin.BackCommand.ExecuteAsync(null);
        await Settle(admin);

        Assert.Equal(AdminTab.Mailboxes, admin.SelectedTab);
        Assert.Equal(MailboxA, admin.Mailboxes.SelectedMailbox?.MailboxId);
        Assert.True(admin.CanGoForward);
        Assert.Equal("Forward (Alt+Right) — to Users: Bob Jones", admin.ForwardToolTip);

        await admin.ForwardCommand.ExecuteAsync(null);
        await Settle(admin);

        Assert.Equal(AdminTab.Users, admin.SelectedTab);
        Assert.Equal(Bob, admin.Users.SelectedUser?.SystemUserId);
    }

    [Fact]
    public async Task A_tab_clicked_by_hand_is_not_history()
    {
        var admin = Admin(LinkedHandler());
        await admin.LoadAsync();

        admin.SelectedTab = AdminTab.Mailboxes;
        admin.SelectedTab = AdminTab.Queues;
        await Settle(admin);

        Assert.False(admin.CanGoBack);
        Assert.False(admin.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_new_link_after_going_back_drops_the_forward_history()
    {
        var admin = Admin(LinkedHandler(), AdminTab.Mailboxes);
        await admin.LoadAsync();
        admin.Mailboxes.SelectedMailbox = admin.Mailboxes.Items[0];
        admin.Mailboxes.ShowOwnerCommand.Execute(null);
        await Settle(admin);
        await admin.BackCommand.ExecuteAsync(null);
        await Settle(admin);
        Assert.True(admin.CanGoForward);

        admin.Mailboxes.ShowOwnerCommand.Execute(null);
        await Settle(admin);

        Assert.False(admin.CanGoForward);
    }

    [Fact]
    public async Task The_status_bar_says_what_was_read_and_what_is_showing()
    {
        var admin = Admin(new FakeHttpHandler().OnJson(HttpMethod.Get, "systemusers?", Users()));

        await admin.LoadAsync();

        Assert.Matches(@"^Read 3 users in \d+\.\d s$", admin.Users.Status);
        Assert.Equal("Showing 2 enabled", admin.Users.ShowingSummary);

        admin.Users.SearchText = "alice";
        Assert.Equal("Showing 1 of 2 enabled", admin.Users.ShowingSummary);

        admin.Users.SearchText = string.Empty;
        admin.Users.StatusFilter = UserStatusFilter.All;
        Assert.Equal("Showing 3 users", admin.Users.ShowingSummary);
    }

    [Theory]
    [InlineData(5, 5, "Showing 5 roles")]
    [InlineData(2, 5, "Showing 2 of 5 roles")]
    public void Showing_says_of_only_when_something_narrows_it(int shown, int of, string expected)
    {
        Assert.Equal(expected, AdminPaneViewModel.Showing(shown, of, "roles"));
    }

    [Fact]
    public async Task A_selected_role_says_how_many_tables_it_grants_on()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "roles?$select=roleid,name", Value(
                new Dictionary<string, object?> { ["roleid"] = RoleRoot, ["name"] = "Basic User", ["ismanaged"] = true }))
            .OnJson(HttpMethod.Get, "RetrieveRolePrivilegesRole", """
                {"RolePrivileges":[
                  {"PrivilegeId":"66666666-0000-0000-0000-000000000001","PrivilegeName":"prvReadAccount","Depth":"Global"},
                  {"PrivilegeId":"66666666-0000-0000-0000-000000000002","PrivilegeName":"prvReadContact","Depth":"Basic"}
                ]}
                """)
            .OnJson(HttpMethod.Get, "_parentrootroleid_value", Value());
        var admin = Admin(handler, AdminTab.Roles);
        await admin.LoadAsync();
        Assert.Equal("Showing 1 roles", admin.Roles.ShowingSummary);

        admin.Roles.SelectedRole = admin.Roles.Items[0];
        await Settle(admin);

        Assert.Equal("Basic User grants on 2 table(s)", admin.Roles.ShowingSummary);
    }

    [Fact]
    public void Detail_panes_open_on_roles_and_table_privileges()
    {
        var admin = Admin(new FakeHttpHandler());

        Assert.Equal(UserDetailView.Roles, admin.Users.DetailView);
        Assert.True(admin.Users.IsRolesView);
        Assert.Equal(RoleDetailView.Privileges, admin.Roles.DetailView);

        admin.Users.DetailView = UserDetailView.Teams;
        admin.Roles.DetailView = RoleDetailView.HeldBy;

        Assert.False(admin.Users.IsRolesView);
        Assert.False(admin.Roles.IsPrivilegesView);
    }

    [Fact]
    public void Links_open_in_the_default_browser_until_a_profile_is_chosen()
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(),
            new TabState { EnvironmentUrl = Fakes.EnvironmentUrl });

        Assert.Equal("the default browser", session.BrowserProfileLabel);
    }
    // ---------------------------------------------------------------- field security profiles

    private static FakeHttpHandler FieldSecurityHandler(bool profilesFail = false)
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "systemusers?", Users())
            .OnJson(HttpMethod.Get, $"systemusers({Alice})/teammembership_association", Value(
                new Dictionary<string, object?> { ["teamid"] = TeamA, ["name"] = "Service Desk", ["isdefault"] = false }))
            .OnJson(HttpMethod.Get, $"systemusers({Alice})/systemuserroles_association", Value(
                new Dictionary<string, object?> { ["roleid"] = RoleCopy, ["name"] = "Basic User" }))
            .OnJson(HttpMethod.Get, $"teams({TeamA})/teamroles_association", Value());

        if (profilesFail)
        {
            return handler
                .OnError(HttpMethod.Get, "profiles_association", HttpStatusCode.Forbidden, "Principal lacks prvReadFieldSecurityProfile")
                .OnJson(HttpMethod.Get, "", Value());
        }

        return handler
            .OnJson(HttpMethod.Get, $"systemusers({Alice})/systemuserprofiles_association", Value(
                new Dictionary<string, object?> { ["fieldsecurityprofileid"] = Guid.NewGuid(), ["name"] = "Salary readers", ["ismanaged"] = false }))
            .OnJson(HttpMethod.Get, $"teams({TeamA})/teamprofiles_association", Value(
                new Dictionary<string, object?> { ["fieldsecurityprofileid"] = Guid.NewGuid(), ["name"] = "Case notes", ["ismanaged"] = true, ["description"] = "Secured case fields" }))
            .OnJson(HttpMethod.Get, "", Value());
    }

    [Fact]
    public async Task A_users_field_security_profiles_come_directly_and_through_each_team()
    {
        var admin = Admin(FieldSecurityHandler());
        await admin.LoadAsync();

        admin.Users.SelectedUser = admin.Users.Items.First(u => u.SystemUserId == Alice);
        await Settle(admin);

        var profiles = admin.Users.FieldProfiles;
        Assert.Equal(["Salary readers", "Case notes"], profiles.Select(p => p.Name));
        Assert.Equal("Direct", profiles[0].Via);
        Assert.Equal("Unmanaged", profiles[0].ManagedLabel);
        Assert.Equal("Team: Service Desk", profiles[1].Via);
        Assert.Equal("Secured case fields", profiles[1].Description);
        Assert.Equal(string.Empty, admin.Users.FieldProfileStatus);
    }

    [Fact]
    public async Task Field_security_that_cannot_be_read_says_so_and_leaves_the_roles()
    {
        var admin = Admin(FieldSecurityHandler(profilesFail: true));
        await admin.LoadAsync();

        admin.Users.SelectedUser = admin.Users.Items.First(u => u.SystemUserId == Alice);
        await Settle(admin);

        Assert.Empty(admin.Users.FieldProfiles);
        Assert.Contains("Could not read field security profiles", admin.Users.FieldProfileStatus);
        Assert.Equal("Basic User", Assert.Single(admin.Users.RoleAssignments).RoleName);
        Assert.DoesNotContain("Could not read", admin.Users.DetailStatus);
    }

    [Fact]
    public async Task A_user_with_no_field_security_profile_is_told_what_that_means()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "systemusers?", Users())
            .OnJson(HttpMethod.Get, "", Value());
        var admin = Admin(handler);
        await admin.LoadAsync();

        admin.Users.SelectedUser = admin.Users.Items.First(u => u.SystemUserId == Bob);
        await Settle(admin);

        Assert.Contains("no access to secured columns", admin.Users.FieldProfileStatus);
        Assert.Contains(handler.Requests, r => r.Url.Contains($"systemusers({Bob})/systemuserprofiles_association"));
    }
}
