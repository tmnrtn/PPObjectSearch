using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Admin;

public class SecurityLookupTests
{
    private static RolePrivilege P(string name, PrivilegeDepth depth) => new(Guid.NewGuid(), name, depth);

    private static EffectiveGrant G(string role, string? unit = "Sales", string? team = null) => new(role, unit, team);

    [Fact]
    public void Effective_access_takes_the_widest_depth_and_names_the_roles_granting_it()
    {
        var rows = SecurityLookup.Effective(
        [
            (G("Salesperson"), [P("prvReadAccount", PrivilegeDepth.User), P("prvDeleteAccount", PrivilegeDepth.User)]),
            (G("Sales Manager", "Sales", "EMEA"), [P("prvReadAccount", PrivilegeDepth.BusinessUnit), P("prvExportToExcel", PrivilegeDepth.Organization)]),
            (G("Reader"), [P("prvReadAccount", PrivilegeDepth.BusinessUnit), P("prvReadContact", PrivilegeDepth.None)])
        ]);

        var account = Assert.Single(rows);
        Assert.Equal("Account", account.Table);
        Assert.Equal(PrivilegeDepth.BusinessUnit, account.Read.Depth);
        Assert.Equal(["Sales Manager via team EMEA in Sales", "Reader in Sales"], account.Read.GrantedBy);
        Assert.Equal(PrivilegeDepth.User, account.Delete.Depth);
        Assert.Equal("Salesperson", account.Delete.GrantedByLabel);
        Assert.Equal(PrivilegeDepth.None, account.Share.Depth);
        Assert.Equal(string.Empty, account.Share.Label);
        Assert.Null(account.Share.ToolTip);
    }

    [Fact]
    public void A_business_unit_depth_held_in_two_units_reaches_in_each()
    {
        var account = Assert.Single(SecurityLookup.Effective(
        [
            (G("Salesperson", "Probation North"), [P("prvReadAccount", PrivilegeDepth.BusinessUnit)]),
            (G("Basic User", "Probation South", "Probation South"), [P("prvReadAccount", PrivilegeDepth.BusinessUnit)]),
            (G("Basic User", "Probation North", "Probation North"), [P("prvReadAccount", PrivilegeDepth.BusinessUnit)])
        ]));

        Assert.Equal(["Probation North", "Probation South"], account.Read.BusinessUnits);
        Assert.Equal("records in each of Probation North and Probation South", account.Read.Reach);
        Assert.Equal(string.Join(Environment.NewLine,
            "Business unit - records in each of Probation North and Probation South",
            "Salesperson in Probation North",
            "Basic User via team Probation South in Probation South",
            "Basic User via team Probation North in Probation North"), account.Read.ToolTip);
    }

    [Fact]
    public void A_parent_depth_reaches_from_its_unit_down()
    {
        var account = Assert.Single(SecurityLookup.Effective([(G("Area Manager", "North"), [P("prvWriteAccount", PrivilegeDepth.ParentChild)])]));

        Assert.Equal("Parent: child BUs - records in North and the units beneath it" + Environment.NewLine + "Area Manager in North",
            account.Write.ToolTip);
    }

    [Fact]
    public void User_and_organization_depths_name_no_business_unit()
    {
        var account = Assert.Single(SecurityLookup.Effective(
        [
            (G("Salesperson"), [P("prvReadAccount", PrivilegeDepth.User), P("prvDeleteAccount", PrivilegeDepth.Organization)]),
            (G("Sales Manager", "EMEA", "EMEA"), [P("prvReadAccount", PrivilegeDepth.User)])
        ]));

        Assert.Empty(account.Read.BusinessUnits);
        Assert.Equal("records owned by them or team EMEA", account.Read.Reach);
        Assert.Equal(["Salesperson", "Sales Manager via team EMEA"], account.Read.GrantedBy);
        Assert.Empty(account.Delete.BusinessUnits);
        Assert.Equal("Organization - every record in the organization" + Environment.NewLine + "Salesperson", account.Delete.ToolTip);
    }

    [Fact]
    public void A_business_unit_depth_whose_unit_was_not_read_says_their_own()
    {
        var account = Assert.Single(SecurityLookup.Effective([(G("Salesperson", null), [P("prvReadAccount", PrivilegeDepth.BusinessUnit)])]));

        Assert.Equal("Business unit - records in their business unit" + Environment.NewLine + "Salesperson", account.Read.ToolTip);
    }

    [Fact]
    public void Units_held_in_put_the_users_own_first_then_direct_then_through_teams()
    {
        var units = SecurityLookup.UnitsHeldIn(
        [
            G("Basic User", "Probation North", "Probation North"),
            G("Auditor", "Head Office"),
            G("Salesperson", "Sales"),
            G("Sales Manager", "sales", "EMEA"),
            G("Sales Manager", "Sales", "UK"),
            G("Lost", null)
        ], "Sales");

        Assert.Equal(["Sales (their own; teams EMEA and UK)", "Head Office (direct)", "Probation North (team Probation North)"],
            units.Select(u => u.Label));
        Assert.Equal("Roles held in Sales (their own; teams EMEA and UK), Head Office (direct) and Probation North (team Probation North)",
            SecurityLookup.HeldInLabel(units));
        Assert.StartsWith("Business unit and Parent access reaches from each unit a role is held in:" + Environment.NewLine + "Sales (their own;",
            SecurityLookup.HeldInToolTip(units));
    }

    [Fact]
    public void Many_units_held_in_are_trimmed_to_three_and_a_count_with_all_on_hover()
    {
        var four = SecurityLookup.UnitsHeldIn(new[] { "A", "B", "C", "D" }.Select(u => G("Basic User", u, u)), null);
        var six = SecurityLookup.UnitsHeldIn(new[] { "A", "B", "C", "D", "E", "F" }.Select(u => G("Basic User", u, u)), null);

        Assert.Equal("Roles held in A (team A), B (team B), C (team C) and D (team D)", SecurityLookup.HeldInLabel(four));
        Assert.Equal("Roles held in A (team A), B (team B), C (team C) and 3 more", SecurityLookup.HeldInLabel(six));
        Assert.Contains("F (team F)", SecurityLookup.HeldInToolTip(six));
        Assert.Equal(string.Empty, SecurityLookup.HeldInLabel([]));
        Assert.Null(SecurityLookup.HeldInToolTip([]));
    }

    [Fact]
    public void Two_roles_differ_only_where_their_depths_do()
    {
        var dev = new[] { P("prvDeleteAccount", PrivilegeDepth.Organization), P("prvReadAccount", PrivilegeDepth.Organization), P("prvExportToExcel", PrivilegeDepth.Organization) };
        var prod = new[] { P("prvDeleteAccount", PrivilegeDepth.BusinessUnit), P("prvReadaccount", PrivilegeDepth.Organization), P("prvCreateContact", PrivilegeDepth.User) };

        var differences = SecurityLookup.Diff(dev, prod);

        Assert.Equal(3, differences.Count);
        Assert.Contains(differences, d => d is { Table: "Account", Action: "Delete", Left: PrivilegeDepth.Organization, Right: PrivilegeDepth.BusinessUnit });
        Assert.Contains(differences, d => d is { Table: "Contact", Action: "Create", Left: PrivilegeDepth.None, Right: PrivilegeDepth.User });
        // Privileges that are not a table's come last.
        Assert.Equal("ExportToExcel", differences[^1].Action);
        Assert.Equal("", differences[^1].Table);
    }

    [Fact]
    public void Holders_are_listed_once_with_the_widest_depth_and_every_path()
    {
        var holders = SecurityLookup.Holders(
        [
            new HeldRole("Salesperson", PrivilegeDepth.User, "Alex", false, "Sales", null),
            new HeldRole("Sales Manager", PrivilegeDepth.BusinessUnit, "EMEA", true, "Sales", null),
            new HeldRole("Sales Manager", PrivilegeDepth.BusinessUnit, "Alex", false, "Sales", "EMEA"),
            new HeldRole("Sales Manager", PrivilegeDepth.BusinessUnit, "Sam", false, "Sales", "EMEA")
        ]);

        var alex = Assert.Single(holders, h => h.Name == "Alex");
        Assert.Equal(PrivilegeDepth.BusinessUnit, alex.Depth);
        Assert.Equal("Salesperson in Sales; Sales Manager via team EMEA in Sales", alex.Via);
        Assert.Equal(3, holders.Count);
        Assert.Equal("Team", holders.Single(h => h.Name == "EMEA").Kind);
    }

    [Fact]
    public async Task A_tables_privileges_come_from_its_metadata()
    {
        var read = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='account')", $$"""
            {"Privileges":[
              {"PrivilegeId":"{{read}}","Name":"prvReadAccount","PrivilegeType":"Read"},
              {"PrivilegeId":"{{Guid.NewGuid()}}","Name":"prvAppendToAccount","PrivilegeType":8}
            ]}
            """);

        var privileges = await Fakes.Dataverse(handler).GetTablePrivilegesAsync("Account");

        Assert.Equal(["Read", "AppendTo"], privileges.Select(p => p.Action));
        Assert.Equal(read, privileges[0].PrivilegeId);
    }

    [Fact]
    public async Task Roles_granting_a_privilege_are_listed_once_by_their_root()
    {
        Guid root = Guid.NewGuid(), copy = Guid.NewGuid(), privilege = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "roleprivileges_association", $$"""
            {"value":[
              {"roleid":"{{root}}","name":"Sales Manager","_parentrootroleid_value":"{{root}}"},
              {"roleid":"{{copy}}","name":"Sales Manager","_parentrootroleid_value":"{{root}}"}
            ]}
            """);

        var grant = Assert.Single(await Fakes.Dataverse(handler).GetPrivilegeGrantsAsync(privilege));

        Assert.Equal(root, grant.RootRoleId);
        Assert.Contains($"privileges({privilege})", handler.Requests[0].Url);
    }

    [Fact]
    public async Task A_secured_columns_profiles_are_listed_with_who_holds_them()
    {
        Guid granted = Guid.NewGuid(), nothing = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "fieldpermissions?", $$"""
                {"value":[
                  {"_fieldsecurityprofileid_value":"{{granted}}","_fieldsecurityprofileid_value@OData.Community.Display.V1.FormattedValue":"HR","canread":4,"cancreate":0,"canupdate":4},
                  {"_fieldsecurityprofileid_value":"{{nothing}}","canread":0,"cancreate":0,"canupdate":0}
                ]}
                """)
            .OnJson(HttpMethod.Get, "systemuserprofiles_association", """{"value":[{"fullname":"Alex"}]}""")
            .OnJson(HttpMethod.Get, "teamprofiles_association", """{"value":[{"name":"HR team"}]}""");

        var grant = Assert.Single(await Fakes.Dataverse(handler).GetFieldPermissionGrantsAsync("contact", "new_salary"));

        Assert.Equal("HR", grant.ProfileName);
        Assert.True(grant.CanRead);
        Assert.False(grant.CanCreate);
        Assert.True(grant.CanUpdate);
        Assert.Equal("Alex", grant.UsersLabel);
        Assert.Equal("HR team", grant.TeamsLabel);
        Assert.Contains("attributelogicalname eq 'new_salary'", Uri.UnescapeDataString(handler.Requests[0].Url));
    }
}
