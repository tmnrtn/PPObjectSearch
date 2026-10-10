using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Admin;

/// <summary>The security lookup window: who can do something, what a user can do, who sees a secured column, and how two roles differ.</summary>
public class SecurityLookupViewModelTests
{
    private const string F = "@OData.Community.Display.V1.FormattedValue";

    private static readonly Guid Alice = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Emea = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid Manager = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid Salesperson = Guid.Parse("33333333-0000-0000-0000-000000000002");
    private static readonly Guid DeleteAccount = Guid.Parse("66666666-0000-0000-0000-000000000001");
    private static readonly Guid ReadAccount = Guid.Parse("66666666-0000-0000-0000-000000000002");

    private static string Rows(params object[] rows) => JsonSerializer.Serialize(new { value = rows });

    private static Dictionary<string, object?> UserRow(Guid id, string name, bool disabled) => new()
    {
        ["systemuserid"] = id, ["fullname"] = name, ["domainname"] = name.ToLowerInvariant() + "@contoso.com",
        ["isdisabled"] = disabled, ["accessmode"] = 0, ["accessmode" + F] = "Read-Write"
    };

    private static string RolePrivileges(params (Guid Id, string Name, string Depth)[] privileges) =>
        JsonSerializer.Serialize(new { RolePrivileges = privileges.Select(p => new { PrivilegeId = p.Id, PrivilegeName = p.Name, p.Depth }) });

    /// <summary>
    /// Sales Manager grants delete on accounts organization-wide and is held by Alice and by the
    /// EMEA team, whose member is Sam; Salesperson grants only read, and Alice holds it directly.
    /// </summary>
    private static FakeHttpHandler Handler() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='account')", $$"""
            {"Privileges":[
              {"PrivilegeId":"{{DeleteAccount}}","Name":"prvDeleteAccount","PrivilegeType":"Delete"},
              {"PrivilegeId":"{{ReadAccount}}","Name":"prvReadAccount","PrivilegeType":"Read"}
            ]}
            """)
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='team')", """{"Privileges":[]}""")
        .OnJson(HttpMethod.Get, "EntityDefinitions?", Rows(
            new { LogicalName = "contact", EntitySetName = "contacts" },
            new { LogicalName = "Account", EntitySetName = "accounts" },
            new { LogicalName = "msdyn_secret", EntitySetName = "secrets", IsPrivate = true }))
        .OnJson(HttpMethod.Get, "systemusers?", Rows(UserRow(Alice, "Alice", false), UserRow(Bob, "Bob", true)))
        .OnJson(HttpMethod.Get, $"_parentrootroleid_value eq {Manager}", Rows(new Dictionary<string, object?>
        {
            ["roleid"] = Manager, ["_businessunitid_value" + F] = "Contoso",
            ["systemuserroles_association"] = new object[] { new { systemuserid = Alice, fullname = "Alice", isdisabled = false } },
            ["teamroles_association"] = new object[] { new { teamid = Emea, name = "EMEA" } }
        }))
        .OnJson(HttpMethod.Get, "roles?", Rows(
            new Dictionary<string, object?> { ["roleid"] = Manager, ["name"] = "Sales Manager", ["ismanaged"] = false },
            new Dictionary<string, object?> { ["roleid"] = Salesperson, ["name"] = "Salesperson", ["ismanaged"] = true }))
        .OnJson(HttpMethod.Get, "roleprivileges_association", Rows(
            new Dictionary<string, object?> { ["roleid"] = Manager, ["name"] = "Sales Manager", ["_parentrootroleid_value"] = Manager },
            new Dictionary<string, object?> { ["roleid"] = Salesperson, ["name"] = "Salesperson", ["_parentrootroleid_value"] = Salesperson }))
        .OnJson(HttpMethod.Get, $"RoleId={Manager}", RolePrivileges((DeleteAccount, "prvDeleteAccount", "Global"), (ReadAccount, "prvReadAccount", "Global")))
        .OnJson(HttpMethod.Get, $"RoleId={Salesperson}", RolePrivileges((ReadAccount, "prvReadAccount", "Basic")))
        .OnJson(HttpMethod.Get, $"teams({Emea})/teammembership_association", Rows(UserRow(Guid.NewGuid(), "Sam", false)))
        .OnJson(HttpMethod.Get, $"systemusers({Alice})/systemuserroles_association", Rows(
            new Dictionary<string, object?> { ["roleid"] = Salesperson, ["name"] = "Salesperson" }))
        .OnJson(HttpMethod.Get, $"systemusers({Alice})/teammembership_association", Rows(
            new Dictionary<string, object?> { ["teamid"] = Emea, ["name"] = "EMEA", ["isdefault"] = false }))
        .OnJson(HttpMethod.Get, $"teams({Emea})/teamroles_association", Rows(
            new Dictionary<string, object?> { ["roleid"] = Guid.NewGuid(), ["name"] = "Sales Manager", ["_parentrootroleid_value"] = Manager }))
        .OnJson(HttpMethod.Get, "fieldpermissions?", Rows(new Dictionary<string, object?>
        {
            ["_fieldsecurityprofileid_value"] = Guid.NewGuid(), ["_fieldsecurityprofileid_value" + F] = "HR",
            ["canread"] = 4, ["cancreate"] = 0, ["canupdate"] = 4
        }))
        .OnJson(HttpMethod.Get, "systemuserprofiles_association", Rows(new { fullname = "Alice" }))
        .OnJson(HttpMethod.Get, "teamprofiles_association", Rows(new { name = "HR team" }));

    private static SecurityLookupViewModel Lookup(FakeHttpHandler handler, params EnvironmentSessionViewModel[] others)
    {
        var session = AdminSessions.Connected(handler);
        return new SecurityLookupViewModel(session, others);
    }

    private static bool IsWrite(RecordedRequest r) => r.Method != HttpMethod.Get;

    // ---------------------------------------------------------------- opening

    [Fact]
    public void The_window_opens_on_who_can_with_delete_chosen_and_nothing_read()
    {
        var lookup = Lookup(new FakeHttpHandler());

        Assert.Equal(SecurityLookupTab.WhoCan, lookup.Tab);
        Assert.Equal(0, lookup.TabIndex);
        Assert.Equal("Delete", lookup.Action);
        Assert.Equal(8, lookup.Actions.Count);
        Assert.StartsWith("Security lookup — ", lookup.Title);
        Assert.Single(lookup.Sessions);
        Assert.Same(lookup.Session, lookup.LeftSession);
        Assert.Same(lookup.Session, lookup.RightSession);
        Assert.True(lookup.IsEmpty);
        Assert.False(lookup.HasResults);
        Assert.Equal("0 holders", lookup.ResultCountLabel);
        Assert.Equal("Who can do it?", lookup.EmptyHeading);
        Assert.Equal(string.Empty, lookup.StatusLine);
        Assert.False(lookup.RunCommand.CanExecute(null));
        Assert.False(lookup.CancelCommand.CanExecute(null));
        Assert.False(lookup.ExportCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(SecurityLookupTab.WhoCan, "Who can do it?", "0 holders")]
    [InlineData(SecurityLookupTab.Effective, "Choose a user", "0 tables")]
    [InlineData(SecurityLookupTab.SecuredColumn, "Who sees a secured column?", "0 profiles")]
    [InlineData(SecurityLookupTab.CompareRoles, "Choose two roles", "0 differences")]
    public void Each_tab_says_what_it_needs_before_anything_is_looked_up(SecurityLookupTab tab, string heading, string count)
    {
        var lookup = Lookup(new FakeHttpHandler());

        lookup.TabIndex = (int)tab;

        Assert.Equal(tab, lookup.Tab);
        Assert.Equal(heading, lookup.EmptyHeading);
        Assert.NotEmpty(lookup.EmptyText);
        Assert.Equal(count, lookup.ResultCountLabel);
    }

    [Fact]
    public void Only_connected_tabs_are_offered_for_comparing_and_the_window_s_own_comes_first()
    {
        var other = AdminSessions.Connected(new FakeHttpHandler(), "https://fabrikam.crm.dynamics.com");
        var disconnected = AdminSessions.Disconnected("https://northwind.crm.dynamics.com");

        var lookup = Lookup(new FakeHttpHandler(), other, disconnected);

        Assert.Equal([lookup.Session, other], lookup.Sessions);
    }

    [Fact]
    public void Each_tab_can_run_once_it_has_what_it_needs()
    {
        var lookup = Lookup(new FakeHttpHandler());
        var role = new SecurityRoleInfo(Manager, "Sales Manager", null, false, null);

        lookup.TableName = "account";
        Assert.True(lookup.RunCommand.CanExecute(null));
        lookup.Action = " ";
        Assert.False(lookup.RunCommand.CanExecute(null));
        lookup.Action = "Read";
        Assert.True(lookup.RunCommand.CanExecute(null));

        lookup.Tab = SecurityLookupTab.Effective;
        Assert.False(lookup.RunCommand.CanExecute(null));
        lookup.SelectedUser = new UserInfo(Alice, "Alice", null, null, null, null, false, 0, null, false, null, null, null, null, null);
        Assert.True(lookup.RunCommand.CanExecute(null));

        lookup.Tab = SecurityLookupTab.SecuredColumn;
        Assert.False(lookup.RunCommand.CanExecute(null));
        lookup.ColumnName = "new_salary";
        Assert.True(lookup.RunCommand.CanExecute(null));

        lookup.Tab = SecurityLookupTab.CompareRoles;
        lookup.LeftRole = role;
        Assert.False(lookup.RunCommand.CanExecute(null));
        lookup.RightRole = role;
        Assert.True(lookup.RunCommand.CanExecute(null));
    }

    [Fact]
    public void The_pickers_are_read_once_skipping_private_tables_and_disabled_users() => AdminUiThread.Run(async () =>
    {
        var handler = Handler();
        var lookup = Lookup(handler);

        await lookup.EnsureListsAsync();
        await lookup.EnsureListsAsync();

        Assert.Equal(["Account", "contact"], lookup.Tables.Select(t => t.LogicalName));
        Assert.Equal("Alice", Assert.Single(lookup.Users).FullName);
        Assert.Equal(["Sales Manager", "Salesperson"], lookup.LeftRoles.Select(r => r.Name));
        Assert.Equal(2, lookup.RightRoles.Count);
        Assert.Equal(1, handler.Requests.Count(r => r.Url.Contains("EntityDefinitions?")));
        Assert.Equal(string.Empty, lookup.Notes);
    });

    [Fact]
    public void Pickers_that_cannot_be_read_leave_a_note_to_type_instead() => AdminUiThread.Run(async () =>
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "EntityDefinitions", HttpStatusCode.Forbidden, "No metadata.");
        var lookup = Lookup(handler);

        await lookup.EnsureListsAsync();

        Assert.StartsWith("Could not read the tables and users for the pickers, so type a table's logical name instead - ", lookup.Notes);
        Assert.Empty(lookup.Tables);
    });

    [Fact]
    public void A_tab_that_is_not_connected_reads_nothing() => AdminUiThread.Run(async () =>
    {
        var lookup = new SecurityLookupViewModel(AdminSessions.Disconnected(), []) { TableName = "account" };

        await lookup.EnsureListsAsync();
        await lookup.RunCommand.ExecuteAsync(null);

        Assert.Empty(lookup.Tables);
        Assert.Equal("Could not look this up - Not connected.", lookup.Status);
        Assert.Equal(lookup.Status, lookup.StatusLine);
        Assert.False(lookup.IsBusy);
    });

    // ---------------------------------------------------------------- who can

    [Fact]
    public void Who_can_lists_every_holder_of_the_roles_granting_the_privilege() => AdminUiThread.Run(async () =>
    {
        var handler = Handler();
        var lookup = Lookup(handler);
        lookup.TableName = " Account ";

        await lookup.RunCommand.ExecuteAsync(null);

        var grant = Assert.Single(lookup.Grants);
        Assert.Equal("Sales Manager", grant.Role);
        Assert.Equal("Organization", grant.DepthLabel);
        Assert.Equal(["Alice", "EMEA", "Sam"], lookup.Holders.Select(h => h.Name).Order());
        Assert.Equal("Team", lookup.Holders.Single(h => h.Name == "EMEA").Kind);
        Assert.True(lookup.HasResults);
        Assert.False(lookup.IsEmpty);
        Assert.Equal("3 holders", lookup.ResultCountLabel);
        Assert.Matches(@"^Read 1 role granting prvDeleteAccount in \d+\.\d s - 2 users and 1 team hold them$", lookup.ReadSummary);
        Assert.Equal(lookup.ReadSummary, lookup.StatusLine);
        Assert.Equal("No one can do it", lookup.EmptyHeading);
        Assert.True(lookup.ExportCommand.CanExecute(null));
        Assert.DoesNotContain(handler.Requests, IsWrite);
    });

    [Fact]
    public void Who_can_exports_one_line_per_holder() => AdminUiThread.Run(async () =>
    {
        var lookup = Lookup(Handler());
        lookup.TableName = "account";
        await lookup.RunCommand.ExecuteAsync(null);

        var lines = lookup.ExportLines();

        Assert.Equal("Name,Kind,Delete depth on account,Via", lines[0]);
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("EMEA,Team,Organization,"));
    });

    [Fact]
    public void A_table_without_the_privilege_says_so_in_place_of_the_list() => AdminUiThread.Run(async () =>
    {
        var lookup = Lookup(Handler());
        lookup.TableName = "team";

        await lookup.RunCommand.ExecuteAsync(null);

        Assert.Empty(lookup.Holders);
        Assert.True(lookup.IsEmpty);
        Assert.Equal("No Delete privilege", lookup.EmptyHeading);
        Assert.StartsWith("team has no Delete privilege", lookup.EmptyText);
        Assert.StartsWith("Read team's privileges in ", lookup.StatusLine);
    });

    [Fact]
    public void A_lookup_that_fails_says_why() => AdminUiThread.Run(async () =>
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "EntityDefinitions", HttpStatusCode.NotFound, "Entity 'nope' not found.");
        var lookup = Lookup(handler);
        lookup.TableName = "nope";

        await lookup.RunCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not look this up - ", lookup.Status);
        Assert.Contains("Entity 'nope' not found.", lookup.Status);
        Assert.False(lookup.IsBusy);
    });

    [Fact]
    public void A_lookup_can_be_stopped_part_way() => AdminUiThread.Run(async () =>
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "EntityDefinitions", _ => gate.Task);
        var lookup = Lookup(handler);
        lookup.TableName = "account";

        var running = lookup.RunCommand.ExecuteAsync(null);
        Assert.True(lookup.IsBusy);
        Assert.True(lookup.CancelCommand.CanExecute(null));
        Assert.False(lookup.ExportCommand.CanExecute(null));
        Assert.Equal("Reading account's privileges...", lookup.StatusLine);

        lookup.CancelCommand.Execute(null);
        gate.SetResult(FakeHttpHandler.Json($$"""{"Privileges":[{"PrivilegeId":"{{DeleteAccount}}","Name":"prvDeleteAccount","PrivilegeType":"Delete"}]}"""));
        await running;

        Assert.Equal("Stopped.", lookup.Status);
        Assert.False(lookup.IsBusy);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("roleprivileges_association"));
    });

    [Fact]
    public void Switching_tabs_shows_each_tab_s_own_results() => AdminUiThread.Run(async () =>
    {
        var lookup = Lookup(Handler());
        await lookup.EnsureListsAsync();
        lookup.TableName = "account";
        await lookup.RunCommand.ExecuteAsync(null);

        lookup.Tab = SecurityLookupTab.SecuredColumn;

        Assert.False(lookup.HasResults);
        Assert.Equal(string.Empty, lookup.ReadSummary);

        lookup.Tab = SecurityLookupTab.WhoCan;

        Assert.True(lookup.HasResults);
        Assert.StartsWith("Read 1 role", lookup.StatusLine);
    });

    // ---------------------------------------------------------------- effective access

    [Fact]
    public void Effective_access_takes_roles_held_directly_and_through_teams() => AdminUiThread.Run(async () =>
    {
        var handler = Handler();
        var lookup = Lookup(handler);
        await lookup.EnsureListsAsync();
        lookup.Tab = SecurityLookupTab.Effective;
        lookup.SelectedUser = lookup.Users[0];

        await lookup.RunCommand.ExecuteAsync(null);

        var account = Assert.Single(lookup.EffectiveRows);
        Assert.Equal("Account", account.Table);
        Assert.Equal(PrivilegeDepth.Organization, account.Read.Depth);
        Assert.Equal(["Sales Manager (Team: EMEA)"], account.Read.GrantedBy);
        Assert.Equal(PrivilegeDepth.Organization, account.Delete.Depth);
        Assert.Equal("1 table", lookup.ResultCountLabel);
        Assert.True(lookup.HasResults);
        Assert.Matches(@"^Read Alice's 2 role assignments \(1 through teams\) in \d+\.\d s - hover a cell for the roles that grant it$", lookup.ReadSummary);
        Assert.Equal("No access", lookup.EmptyHeading);
        Assert.Equal(1, handler.Requests.Count(r => r.Url.Contains($"RoleId={Manager}")));
    });

    [Fact]
    public void Effective_access_exports_a_row_per_table_with_a_column_per_privilege() => AdminUiThread.Run(async () =>
    {
        var lookup = Lookup(Handler());
        await lookup.EnsureListsAsync();
        lookup.Tab = SecurityLookupTab.Effective;
        lookup.SelectedUser = lookup.Users[0];
        await lookup.RunCommand.ExecuteAsync(null);

        var lines = lookup.ExportLines();

        Assert.Equal(["Table,Create,Read,Write,Delete,Append,AppendTo,Assign,Share", "Account,,Organization,,Organization,,,,"], lines);
    });

    // ---------------------------------------------------------------- secured columns

    [Fact]
    public void A_secured_column_lists_the_profiles_granting_it_and_who_holds_them() => AdminUiThread.Run(async () =>
    {
        var handler = Handler();
        var lookup = Lookup(handler);
        lookup.Tab = SecurityLookupTab.SecuredColumn;
        lookup.TableName = "contact ";
        lookup.ColumnName = " new_salary";

        await lookup.RunCommand.ExecuteAsync(null);

        var grant = Assert.Single(lookup.FieldGrants);
        Assert.Equal("HR", grant.ProfileName);
        Assert.Equal("1 profile", lookup.ResultCountLabel);
        Assert.True(lookup.HasResults);
        Assert.Contains(handler.Requests, r => r.Url.Contains("attributelogicalname eq 'new_salary'"));
        Assert.Equal("No profile grants it", lookup.EmptyHeading);
        Assert.Equal(["Profile,Read,Create,Update,Users,Teams", "HR,Yes,No,Yes,Alice,HR team"], lookup.ExportLines());
    });

    // ---------------------------------------------------------------- comparing roles

    [Fact]
    public void Two_roles_in_one_environment_differ_where_their_depths_do() => AdminUiThread.Run(async () =>
    {
        var lookup = Lookup(Handler());
        await lookup.EnsureListsAsync();
        lookup.Tab = SecurityLookupTab.CompareRoles;
        lookup.LeftRole = lookup.LeftRoles.Single(r => r.RoleId == Manager);
        Assert.Null(lookup.RightRole);
        lookup.RightRole = lookup.RightRoles.Single(r => r.RoleId == Salesperson);

        await lookup.RunCommand.ExecuteAsync(null);

        Assert.Equal(["Read", "Delete"], lookup.Differences.Select(d => d.Action));
        Assert.Equal("2 differences", lookup.ResultCountLabel);
        Assert.True(lookup.HasResults);
        Assert.Matches(@"^Read 3 privileges of Sales Manager and Salesperson in \d+\.\d s$", lookup.ReadSummary);
        Assert.Equal("Table,Privilege,Sales Manager,Salesperson", lookup.ExportLines()[0]);
        Assert.Equal("Account,Read,Organization,User", lookup.ExportLines()[1]);
        Assert.Equal("Account,Delete,Organization,None", lookup.ExportLines()[2]);
    });

    [Fact]
    public void The_same_role_in_another_environment_is_picked_to_compare_with() => AdminUiThread.Run(async () =>
    {
        var prodHandler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "roles?", Rows(new Dictionary<string, object?> { ["roleid"] = Salesperson, ["name"] = "sales manager" }))
            .OnJson(HttpMethod.Get, $"RoleId={Salesperson}", RolePrivileges((DeleteAccount, "prvDeleteAccount", "Global")));
        var prod = AdminSessions.Connected(prodHandler, "https://prod.crm11.dynamics.com");
        var lookup = Lookup(Handler(), prod);
        await lookup.EnsureListsAsync();
        lookup.Tab = SecurityLookupTab.CompareRoles;

        lookup.RightSession = prod;
        await AdminSessions.Until(() => lookup.RightRoles.Count == 1);
        lookup.LeftRole = lookup.LeftRoles.Single(r => r.RoleId == Manager);

        Assert.Equal(Salesperson, lookup.RightRole?.RoleId);

        await lookup.RunCommand.ExecuteAsync(null);

        var difference = Assert.Single(lookup.Differences);
        Assert.Equal(("Read", PrivilegeDepth.Organization, PrivilegeDepth.None), (difference.Action, difference.Left, difference.Right));
        Assert.Contains(prodHandler.Requests, r => r.Url.Contains($"RoleId={Salesperson}"));
        Assert.Equal("No differences", lookup.EmptyHeading);
    });

    [Fact]
    public void Picking_another_environment_on_the_left_reads_its_roles() => AdminUiThread.Run(async () =>
    {
        var prodHandler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "roles?", Rows(new Dictionary<string, object?> { ["roleid"] = Salesperson, ["name"] = "System Customizer" }));
        var prod = AdminSessions.Connected(prodHandler, "https://prod.crm11.dynamics.com");
        var lookup = Lookup(new FakeHttpHandler(), prod);

        lookup.LeftSession = prod;
        await AdminSessions.Until(() => lookup.LeftRoles.Count == 1);

        Assert.Equal("System Customizer", lookup.LeftRoles[0].Name);
        lookup.LeftSession = null;
        Assert.Empty(lookup.LeftRoles);
    });
}
