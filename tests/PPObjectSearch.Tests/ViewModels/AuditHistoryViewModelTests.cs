using System.Net;
using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The audit history window: whether auditing covers it, one line per column changed, and why it is empty.</summary>
public sealed class AuditHistoryViewModelTests
{
    private static readonly Guid Flow = Guid.Parse("a0000000-0000-0000-0000-000000000029");

    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";

    private static SolutionComponentItem Item(DateTimeOffset? createdOn = null) => new()
    {
        Name = "Case escalation notifier", ComponentTypeName = "Process", SubType = "Cloud flow",
        ComponentType = 29, ObjectId = Flow, CreatedOn = createdOn
    };

    private static FakeHttpHandler Handler(bool auditOn, string audits) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "organizations?", $$"""{"value":[{"isauditenabled":{{(auditOn ? "true" : "false")}}}]}""")
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='workflow')", """{"IsAuditEnabled":{"Value":true}}""")
        .OnJson(HttpMethod.Get, "audits?", audits);

    private static string Update(string when, string column, string? oldValue, string newValue) => $$"""
        {"createdon":"{{when}}","action{{Formatted}}":"Update","_userid_value{{Formatted}}":"Sam Okafor",
         "changedata":{{System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Serialize(new
         {
             changedAttributes = new[] { new { logicalName = column, oldValue, newValue } }
         }))}}}
        """;

    private static async Task<AuditHistoryViewModel> Loaded(FakeHttpHandler handler, SolutionComponentItem item)
    {
        var vm = new AuditHistoryViewModel(TestSessions.Connected(handler, [item]), Fakes.Dataverse(handler), item);
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task History_that_starts_after_the_flow_was_made_is_flagged_in_the_banner()
    {
        var audits = $$"""{"value":[{{Update("2026-10-06T09:48:00Z", "statecode", "1", "0")}},{{Update("2026-09-14T11:20:00Z", "statecode", "0", "1")}}]}""";

        var vm = await Loaded(Handler(auditOn: true, audits), Item(createdOn: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.True(vm.IsAuditPartial);
        Assert.False(vm.IsAuditOff);
        Assert.True(vm.ShowsAuditBanner);
        Assert.Contains("The audited changes start on 2026-09-14", vm.AuditState);
        Assert.Equal("Case escalation notifier", vm.Name);
        Assert.Equal("Cloud flow · Process", vm.TypeLabel);
        Assert.Equal("2 changes", vm.CountLabel);
        Assert.StartsWith("Read 2 audit records in ", vm.Status);
        Assert.False(vm.ShowsRow);

        var change = Assert.Single(vm.Records[0].Changes);
        Assert.Equal("Status", change.ColumnLabel);
        Assert.Equal("statecode", change.Column);
        Assert.Equal("On (activated)", change.OldShown);
        Assert.Equal("Off (draft)", change.NewShown);
    }

    [Fact]
    public async Task History_back_to_its_creation_is_shown_as_one_quiet_line()
    {
        var audits = $$"""
            {"value":[{{Update("2026-10-06T09:48:00Z", "statecode", "1", "0")}},
                      {"createdon":"2026-03-01T00:00:10Z","action{{Formatted}}":"Create"}]}
            """;

        var vm = await Loaded(Handler(auditOn: true, audits), Item(createdOn: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.False(vm.ShowsAuditBanner);
        Assert.Equal("Auditing is on for this environment and the workflow table.", vm.AuditState);
    }

    [Fact]
    public async Task Nothing_recorded_with_auditing_off_says_so()
    {
        var vm = await Loaded(Handler(auditOn: false, """{"value":[]}"""), Item());

        Assert.False(vm.HasRecords);
        Assert.True(vm.ShowsAuditBanner);
        Assert.Equal("No audited changes", vm.EmptyHeading);
        Assert.Equal("Auditing is not recording changes to this cloud flow, so there is nothing to show.", vm.EmptyText);
        Assert.Equal("0 changes", vm.CountLabel);
    }

    [Fact]
    public async Task Nothing_recorded_with_auditing_on_means_nothing_changed()
    {
        var vm = await Loaded(Handler(auditOn: true, """{"value":[]}"""), Item());

        Assert.False(vm.ShowsAuditBanner);
        Assert.Equal("Nothing about this cloud flow has changed while auditing was on.", vm.EmptyText);
    }

    [Fact]
    public async Task A_history_that_cannot_be_read_says_why_rather_than_nothing_changed()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "organizations?", """{"value":[{"isauditenabled":true}]}""")
            .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='workflow')", """{"IsAuditEnabled":{"Value":true}}""")
            .OnStatus(HttpMethod.Get, "audits?", HttpStatusCode.Forbidden);

        var vm = await Loaded(handler, Item());

        Assert.Equal("The audit history was not read", vm.EmptyHeading);
        Assert.Contains("View Audit History privilege", vm.EmptyText);
    }

    [Fact]
    public void A_long_value_is_shown_by_its_size_and_a_column_by_its_name()
    {
        var definition = new AuditChange("clientdata", new string('a', 41 * 1024), new string('b', 1001));

        Assert.Equal("Definition", definition.ColumnLabel);
        Assert.Equal("41 KB", definition.OldShown);
        Assert.Equal("1001 bytes", definition.NewShown);

        // A row's description or note is shown whole.
        var description = new string('c', AuditChange.LongValue);
        Assert.Equal(description, new AuditChange("description", null, description).NewShown);

        var other = new AuditChange("new_custom", null, "x");
        Assert.Equal("new_custom", other.ColumnLabel);
        Assert.Equal("(empty)", other.OldShown);
    }

    [Fact]
    public void Without_a_creation_date_or_any_record_there_is_no_telling_whether_history_is_missing()
    {
        var update = new AuditRecord { When = DateTimeOffset.UtcNow, Action = "Update" };

        Assert.False(AuditHistoryViewModel.IsPartial([update], createdOn: null));
        Assert.False(AuditHistoryViewModel.IsPartial([], DateTimeOffset.UtcNow.AddYears(-1)));
        Assert.True(AuditHistoryViewModel.IsPartial([update], DateTimeOffset.UtcNow.AddYears(-1)));
        Assert.False(AuditHistoryViewModel.IsPartial([update, new AuditRecord { When = DateTimeOffset.UtcNow.AddYears(-1), Action = "Create" }],
            DateTimeOffset.UtcNow.AddYears(-1)));
    }

    private static readonly Guid Account = Guid.Parse("b0000000-0000-0000-0000-000000000001");

    /// <summary>The account table, its columns, the row, and one change to it - as a link to the row finds them.</summary>
    private static FakeHttpHandler AccountHandler(bool rowThere) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='account')/Attributes", """
            {"value":[
              {"LogicalName":"name","DisplayName":{"UserLocalizedLabel":{"Label":"Account Name"}},"AttributeTypeName":{"Value":"StringType"}},
              {"LogicalName":"createdon","DisplayName":{"UserLocalizedLabel":{"Label":"Created On"}},"AttributeTypeName":{"Value":"DateTimeType"}}
            ]}
            """)
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='account')?$select=IsAuditEnabled", """{"IsAuditEnabled":{"Value":true}}""")
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='account')", """
            {"LogicalName":"account","DisplayName":{"UserLocalizedLabel":{"Label":"Account"}},"EntitySetName":"accounts",
             "PrimaryIdAttribute":"accountid","PrimaryNameAttribute":"name"}
            """)
        .On(HttpMethod.Get, $"accounts({Account})", _ => rowThere
            ? FakeHttpHandler.Json("""{"name":"Contoso","createdon":"2026-03-01T09:00:00Z"}""")
            : FakeHttpHandler.Json(FakeHttpHandler.ErrorJson("Does not exist"), HttpStatusCode.NotFound))
        .OnJson(HttpMethod.Get, "organizations?", """{"value":[{"isauditenabled":true}]}""")
        .OnJson(HttpMethod.Get, "RetrieveAuditDetails", """{"AuditDetail":{}}""")
        .OnJson(HttpMethod.Get, "audits?", $$"""
            {"value":[{"auditid":"c0000000-0000-0000-0000-000000000001","createdon":"2026-10-06T09:48:00Z","action{{Formatted}}":"Update",
              "changedata":"{\"changedAttributes\":[{\"logicalName\":\"name\",\"oldValue\":\"Contoso Ltd\",\"newValue\":\"Contoso\"}]}"}]}
            """);

    [Fact]
    public async Task A_link_to_a_row_finds_its_table_and_name_and_reads_its_history()
    {
        var handler = AccountHandler(rowThere: true);
        var session = TestSessions.Connected(handler, []);
        var vm = new AuditHistoryViewModel(session, Fakes.Dataverse(handler), new RecordReference("account", Account, "contoso.crm11.dynamics.com"));

        Assert.Equal(Account.ToString(), vm.Name);
        await vm.LoadAsync();

        Assert.Equal("Contoso", vm.Name);
        Assert.Equal("Account (account)", vm.TypeLabel);
        Assert.Equal($"Audit history — Contoso ({session.Title})", vm.Title);
        Assert.Equal("Account Name", Assert.Single(Assert.Single(vm.Records).Changes).ColumnLabel);

        // The row's creation date is read too: the history starts after it, so its start is missing.
        Assert.True(vm.IsAuditPartial);
        Assert.StartsWith("The audited changes start on 2026-10-06, but this row was created on 2026-03-01", vm.AuditState);
    }

    [Fact]
    public async Task A_deleted_row_still_shows_its_history_and_says_it_is_not_there()
    {
        var handler = AccountHandler(rowThere: false);
        var vm = new AuditHistoryViewModel(TestSessions.Connected(handler, []), Fakes.Dataverse(handler), new RecordReference("account", Account));

        await vm.LoadAsync();

        Assert.Equal(Account.ToString(), vm.Name);
        Assert.Equal("Account (account) · not there now - deleted?", vm.TypeLabel);
        Assert.Single(vm.Records);
    }

    [Fact]
    public async Task A_table_that_is_not_there_says_so_where_the_history_would_be()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "EntityDefinitions?", """{"value":[]}""");
        var session = TestSessions.Connected(handler, []);
        var vm = new AuditHistoryViewModel(session, Fakes.Dataverse(handler), new RecordReference("acount", Account));

        await vm.LoadAsync();

        Assert.False(vm.HasRecords);
        Assert.Equal("The audit history was not read", vm.EmptyHeading);
        Assert.Equal($"There is no table named 'acount' in {session.Title} - check its logical name.", vm.EmptyText);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("audits", StringComparison.Ordinal));
    }
}

