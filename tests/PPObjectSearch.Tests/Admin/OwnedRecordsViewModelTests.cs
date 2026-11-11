using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Admin;

/// <summary>The Owned records window, and opening it from a user or a team in Environment admin.</summary>
public class OwnedRecordsViewModelTests
{
    private const string Fv = "@OData.Community.Display.V1.FormattedValue";

    private static readonly Guid Alice = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid ServiceDesk = Guid.Parse("22222222-0000-0000-0000-000000000001");

    private static object Table(string name, string display) => new Dictionary<string, object?>
    {
        ["LogicalName"] = name, ["DisplayName"] = new { UserLocalizedLabel = new { Label = display } },
        ["EntitySetName"] = name + "s", ["PrimaryIdAttribute"] = name + "id", ["OwnershipType"] = "UserOwned",
        ["TableType"] = "Standard", ["IsPrivate"] = false
    };

    private static string Count(long rows) => $"{{\"value\":[{{\"rowcount\":{rows}}}]}}";

    /// <summary>Alice owns 12 contacts and 3 accounts, no tasks, and her emails cannot be read.</summary>
    private static FakeHttpHandler Handler() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "EntityDefinitions?", JsonSerializer.Serialize(new
        {
            value = new[] { Table("account", "Account"), Table("contact", "Contact"), Table("email", "Email"), Table("task", "Task") }
        }))
        .OnJson(HttpMethod.Get, "<entity name='contact'>", Count(12))
        .OnJson(HttpMethod.Get, "<entity name='account'>", Count(3))
        .OnJson(HttpMethod.Get, "<entity name='task'>", Count(0))
        .OnError(HttpMethod.Get, "<entity name='email'>", HttpStatusCode.Forbidden, "Principal lacks prvReadEmail");

    private static OwnedRecordsViewModel Window(FakeHttpHandler handler, bool isTeam = false) =>
        new(AdminSessions.Connected(handler), Fakes.Dataverse(handler), isTeam ? ServiceDesk : Alice,
            isTeam ? "Service Desk" : "Alice Smith", isTeam);

    [Fact]
    public async Task Counting_lists_the_owned_tables_largest_first_then_the_ones_that_could_not_be_counted()
    {
        var vm = Window(Handler());

        await vm.RunAsync();

        Assert.Equal(["contact", "account", "email"], vm.Rows.Select(r => r.Table.LogicalName));
        Assert.True(vm.Rows[2].Failed);
        Assert.Equal("Alice Smith owns 15 record(s) in 2 table(s).", vm.Summary);
        Assert.StartsWith("Counted 4 tables with an owner in ", vm.Status);
        Assert.EndsWith("1 could not be counted - they are listed last, with why.", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Equal("Owned records — Alice Smith", vm.Title);
        Assert.Equal("User", vm.OwnerKind);
    }

    [Fact]
    public async Task An_owner_of_nothing_says_so()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "EntityDefinitions?", JsonSerializer.Serialize(new { value = new[] { Table("account", "Account") } }))
            .OnJson(HttpMethod.Get, "aggregate='true'", Count(0));
        var vm = Window(handler, isTeam: true);

        await vm.RunAsync();

        Assert.Empty(vm.Rows);
        Assert.Equal("Service Desk owns no records.", vm.Summary);
        Assert.Equal("Team", vm.OwnerKind);
        Assert.False(vm.ExportCsvCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_table_list_that_cannot_be_read_says_why()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "EntityDefinitions?", HttpStatusCode.Forbidden, "No metadata.");
        var vm = Window(handler);

        await vm.RunAsync();

        Assert.Equal("Could not count the records.", vm.Summary);
        Assert.Contains("No metadata.", vm.Status);
        Assert.True(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Only_a_counted_table_offers_its_rows_and_the_rows_are_filtered_to_the_owner()
    {
        var vm = Window(Handler());
        await vm.RunAsync();

        Assert.True(vm.ShowRowsCommand.CanExecute(vm.Rows[0]));
        Assert.False(vm.ShowRowsCommand.CanExecute(vm.Rows[2]));

        var filter = OwnedRecordsViewModel.OwnerFilter(Alice, "Alice Smith");
        Assert.Equal($"_ownerid_value eq {Alice}", filter.Filter);
        Assert.Equal("Owner equals 'Alice Smith'", filter.Label);
    }

    [Fact]
    public async Task The_export_has_a_line_per_table_with_its_count_or_why_not()
    {
        var vm = Window(Handler());
        await vm.RunAsync();

        var lines = vm.CsvLines().ToList();

        Assert.Equal("Table,Display name,Rows owned,Could not count", lines[0]);
        Assert.Equal("contact,Contact,12,", lines[1]);
        Assert.StartsWith("email,Email,,", lines[3]);
        Assert.Contains("prvReadEmail", lines[3]);
    }

    [Fact]
    public async Task A_selected_user_or_team_can_have_their_records_counted()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "systemusers?", JsonSerializer.Serialize(new
            {
                value = new[] { new Dictionary<string, object?> { ["systemuserid"] = Alice, ["fullname"] = "Alice Smith", ["isdisabled"] = false } }
            }))
            .OnJson(HttpMethod.Get, "teams?", JsonSerializer.Serialize(new
            {
                value = new[] { new Dictionary<string, object?> { ["teamid"] = ServiceDesk, ["name"] = "Service Desk", ["teamtype"] = 0, ["teamtype" + Fv] = "Owner" } }
            }))
            .OnJson(HttpMethod.Get, "", """{"value":[]}""");
        var admin = new EnvironmentAdminViewModel(AdminSessions.Disconnected(), Fakes.Dataverse(handler));
        await admin.Users.EnsureLoadedAsync();
        await admin.Teams.EnsureLoadedAsync();

        Assert.False(admin.Users.OwnedRecordsCommand.CanExecute(null));
        Assert.False(admin.Teams.OwnedRecordsCommand.CanExecute(null));

        admin.Users.SelectedUser = admin.Users.Items[0];
        admin.Teams.SelectedTeam = admin.Teams.Items[0];
        await admin.Work.WhenIdleAsync();

        Assert.True(admin.Users.OwnedRecordsCommand.CanExecute(null));
        Assert.True(admin.Teams.OwnedRecordsCommand.CanExecute(null));
    }
}
