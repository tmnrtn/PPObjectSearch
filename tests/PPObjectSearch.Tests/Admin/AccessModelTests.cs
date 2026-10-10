using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Admin;

/// <summary>Users, roles, mailboxes and queues as the admin tools read and label them.</summary>
public class AccessModelTests
{
    private const string Fv = "@OData.Community.Display.V1.FormattedValue";
    private const string Lt = "@Microsoft.Dynamics.CRM.lookuplogicalname";

    private static string Rows(params object[] rows) => JsonSerializer.Serialize(new { value = rows });

    private static UserInfo User(bool app = false, int? mode = null, string? modeLabel = null) =>
        new(Guid.NewGuid(), "Alice", null, null, null, null, false, mode, modeLabel, app, null, null, null, null, null);

    private static MailboxInfo Mailbox(string? regardingType, int? incoming = 1, int? outgoing = 1, bool? scheduled = false) =>
        new(Guid.NewGuid(), "m", null, null, null, regardingType, 1, "Approved", incoming, null, outgoing, null, null,
            scheduled, null, null, null, null, 2, null, 2, null, null, null, null, true);

    private static QueueDetail Queue(int? viewType, string? viewTypeLabel, string? owner, string? ownerType) =>
        new(Guid.NewGuid(), "Support", null, viewType, viewTypeLabel, owner, ownerType, null, null, null, null, null, null, null, null,
            null, null, null, null, true);

    [Fact]
    public async Task A_user_is_read_with_every_column_the_users_tab_shows()
    {
        Guid id = Guid.NewGuid(), unit = Guid.NewGuid(), oid = Guid.NewGuid(), mailbox = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "systemusers?", Rows(new Dictionary<string, object?>
        {
            ["systemuserid"] = id, ["fullname"] = "Alice Smith", ["domainname"] = "alice@contoso.com",
            ["internalemailaddress"] = "alice.smith@contoso.com", ["_businessunitid_value"] = unit,
            ["_businessunitid_value" + Fv] = "Contoso", ["isdisabled"] = false, ["accessmode"] = 1,
            ["accessmode" + Fv] = "Administrative", ["azureactivedirectoryobjectid"] = oid, ["title"] = "Engineer",
            ["caltype" + Fv] = "Professional", ["_defaultmailbox_value"] = mailbox, ["createdon"] = "2024-03-01T09:00:00Z"
        }));

        var user = Assert.Single(await Fakes.Dataverse(handler).GetUsersAsync());

        Assert.Equal(id, user.SystemUserId);
        Assert.Equal("alice.smith@contoso.com", user.Email);
        Assert.Equal(unit, user.BusinessUnitId);
        Assert.Equal(1, user.AccessMode);
        Assert.Equal("Administrative", user.UserType);
        Assert.Equal(oid, user.AadObjectId);
        Assert.Equal("Engineer", user.Title);
        Assert.Equal("Professional", user.LicenseType);
        Assert.Equal(mailbox, user.DefaultMailboxId);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 9, 0, 0, TimeSpan.Zero), user.CreatedOn);
        Assert.Equal("Enabled", user.StatusLabel);
        Assert.Contains("Engineer", user.SearchText);
    }

    [Theory]
    [InlineData(true, 0, "Read-Write", "Application user")]
    [InlineData(false, 3, "Non-interactive", "Non-interactive")]
    [InlineData(false, 3, null, "3")]
    [InlineData(false, null, null, "Unknown")]
    public void A_users_type_is_the_application_flag_else_the_access_mode(bool app, int? mode, string? label, string expected)
    {
        Assert.Equal(expected, User(app, mode, label).UserType);
    }

    [Fact]
    public void A_role_held_through_a_team_says_so_and_falls_back_to_its_own_id()
    {
        Guid role = Guid.NewGuid(), team = Guid.NewGuid();

        var assignment = new RoleAssignment(role, null, "Salesperson", "Contoso", team, "Service Desk");

        Assert.Equal(role, assignment.RoleId);
        Assert.False(assignment.IsDirect);
        Assert.Equal("Team: Service Desk", assignment.Via);
        Assert.Equal(role, assignment.DefinitionId);
    }

    [Fact]
    public void A_field_profile_says_where_it_comes_from_and_whether_it_is_managed()
    {
        var profile = Guid.NewGuid();

        var direct = new FieldProfileAssignment(profile, "Salary readers", null, true, null, null);
        var viaTeam = new FieldProfileAssignment(profile, "Salary readers", null, false, Guid.NewGuid(), "HR");

        Assert.Equal(profile, direct.ProfileId);
        Assert.Equal(("Direct", "Managed"), (direct.Via, direct.ManagedLabel));
        Assert.Equal(("Team: HR", "Unmanaged"), (viaTeam.Via, viaTeam.ManagedLabel));
    }

    [Fact]
    public void A_role_and_a_holder_label_themselves()
    {
        var role = new SecurityRoleInfo(Guid.NewGuid(), "Basic User", "Contoso", false, null);

        Assert.Equal("Unmanaged", role.ManagedLabel);
        Assert.Equal("Basic User Contoso", role.SearchText);
        Assert.Equal("User", new RoleHolder(Guid.NewGuid(), "Alice", false, null, null).Kind);
        Assert.Equal("Team", new RoleHolder(Guid.NewGuid(), "EMEA", true, null, null).Kind);
    }

    [Fact]
    public async Task A_mailbox_is_read_with_its_statuses_delivery_and_profile()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "mailboxes?", Rows(new Dictionary<string, object?>
        {
            ["mailboxid"] = Guid.NewGuid(), ["name"] = "Support", ["emailaddress"] = "support@contoso.com",
            ["_regardingobjectid_value"] = Guid.NewGuid(), ["_regardingobjectid_value" + Fv] = "Support",
            ["_regardingobjectid_value" + Lt] = "queue",
            ["emailrouteraccessapproval"] = 1, ["emailrouteraccessapproval" + Fv] = "Approved",
            ["incomingemailstatus"] = 1, ["incomingemailstatus" + Fv] = "Success",
            ["outgoingemailstatus"] = 2, ["outgoingemailstatus" + Fv] = "Failure",
            ["actstatus" + Fv] = "Not Run", ["testemailconfigurationscheduled"] = false,
            ["testmailboxaccesscompletedon"] = "2024-03-01T09:00:00Z",
            ["enabledforincomingemail"] = true, ["enabledforoutgoingemail"] = true, ["enabledforact"] = false,
            ["incomingemaildeliverymethod"] = 2, ["incomingemaildeliverymethod" + Fv] = "Server-Side Synchronization",
            ["outgoingemaildeliverymethod"] = 2, ["outgoingemaildeliverymethod" + Fv] = "Server-Side Synchronization",
            ["_emailserverprofile_value" + Fv] = "Exchange Online", ["isforwardmailbox"] = false,
            ["isemailaddressapprovedbyo365admin"] = true, ["statecode"] = 0
        }));

        var mailbox = Assert.Single(await Fakes.Dataverse(handler).GetMailboxesAsync());

        Assert.Equal("Success", mailbox.IncomingStatusLabel);
        Assert.Equal("Failure", mailbox.OutgoingStatusLabel);
        Assert.Equal("Not Run", mailbox.AppointmentsStatusLabel);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 9, 0, 0, TimeSpan.Zero), mailbox.TestCompletedOn);
        Assert.Equal((true, true, false), (mailbox.EnabledForIncoming, mailbox.EnabledForOutgoing, mailbox.EnabledForAppointments));
        Assert.Equal("Server-Side Synchronization", mailbox.IncomingDeliveryLabel);
        Assert.Equal("Server-Side Synchronization", mailbox.OutgoingDeliveryLabel);
        Assert.Equal("Exchange Online", mailbox.ServerProfile);
        Assert.Equal(false, mailbox.IsForwardMailbox);
        Assert.Equal(true, mailbox.ApprovedByExchangeAdmin);
        Assert.True(mailbox.IsActive);
        Assert.Equal("Failed", mailbox.TestLabel);
        Assert.Equal("Queue", mailbox.OwnerKind);
    }

    [Theory]
    [InlineData(1, 1, false, "Passed")]
    [InlineData(1, 0, false, "Partly passed")]
    [InlineData(1, 1, true, "Test scheduled")]
    [InlineData(0, 0, false, "Not tested")]
    public void A_mailboxs_test_label_reads_its_status(int incoming, int outgoing, bool scheduled, string expected)
    {
        Assert.Equal(expected, Mailbox("systemuser", incoming, outgoing, scheduled).TestLabel);
    }

    [Theory]
    [InlineData("systemuser", "User")]
    [InlineData("queue", "Queue")]
    [InlineData(null, "None")]
    [InlineData("", "None")]
    [InlineData("account", "account")]
    public void A_mailboxs_owner_is_a_user_a_queue_or_whatever_else_owns_it(string? regardingType, string expected)
    {
        Assert.Equal(expected, Mailbox(regardingType).OwnerKind);
    }

    [Fact]
    public async Task A_queue_is_read_with_its_mailbox_settings_and_counts()
    {
        var mailbox = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "queues?", Rows(new Dictionary<string, object?>
        {
            ["queueid"] = Guid.NewGuid(), ["name"] = "Support", ["queueviewtype"] = 0,
            ["_ownerid_value" + Fv] = "Alice", ["_ownerid_value" + Lt] = "systemuser",
            ["_businessunitid_value" + Fv] = "Contoso",
            ["_defaultmailbox_value"] = mailbox, ["_defaultmailbox_value" + Fv] = "Support mailbox",
            ["emailrouteraccessapproval" + Fv] = "Approved",
            ["incomingemaildeliverymethod" + Fv] = "Server-Side Synchronization",
            ["outgoingemaildeliverymethod" + Fv] = "None",
            ["isemailaddressapprovedbyo365admin"] = false, ["numberofmembers"] = 4, ["numberofitems"] = 12,
            ["description"] = "First line", ["statecode"] = 1
        }));

        var queue = Assert.Single(await Fakes.Dataverse(handler).GetQueueDetailsAsync());

        Assert.Equal("Contoso", queue.BusinessUnit);
        Assert.Equal("Support mailbox", queue.MailboxName);
        Assert.Equal("Approved", queue.ApprovalLabel);
        Assert.Equal("Server-Side Synchronization", queue.IncomingDeliveryLabel);
        Assert.Equal("None", queue.OutgoingDeliveryLabel);
        Assert.Equal(false, queue.ApprovedByExchangeAdmin);
        Assert.Equal((4, 12), (queue.MemberCount, queue.ItemCount));
        Assert.Equal("First line", queue.Description);
        Assert.False(queue.IsActive);
        Assert.Equal("Public", queue.TypeLabel);
        Assert.Equal("Alice (User)", queue.OwnerLabel);
    }

    [Theory]
    [InlineData(1, null, "Private")]
    [InlineData(0, null, "Public")]
    [InlineData(1, "Privé", "Privé")]
    public void A_queues_type_is_its_label_else_worked_out_from_the_view_type(int viewType, string? label, string expected)
    {
        Assert.Equal(expected, Queue(viewType, label, null, null).TypeLabel);
    }

    [Theory]
    [InlineData(null, null, "—")]
    [InlineData("Alice", null, "Alice")]
    [InlineData("Alice", "", "Alice")]
    [InlineData("Service Desk", "Team", "Service Desk (Team)")]
    public void A_queues_owner_shows_its_type_when_known(string? owner, string? ownerType, string expected)
    {
        Assert.Equal(expected, Queue(0, null, owner, ownerType).OwnerLabel);
    }
}
