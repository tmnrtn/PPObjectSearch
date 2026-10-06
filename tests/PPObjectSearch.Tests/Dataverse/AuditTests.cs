using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class AuditTests
{
    private static readonly Guid Definition = Guid.Parse("a0000000-0000-0000-0000-000000000380");
    private static readonly Guid Value = Guid.Parse("a0000000-0000-0000-0000-000000000381");
    private static readonly Guid Flow = Guid.Parse("a0000000-0000-0000-0000-000000000029");

    [Fact]
    public void Change_data_is_read_column_by_column_with_states_named()
    {
        var changes = DataverseClient.ParseChangeData(
            """{"changedAttributes":[{"logicalName":"statecode","oldValue":"1","newValue":"0"},{"logicalName":"name","oldValue":null,"newValue":"Sync"}]}""",
            "workflow");

        Assert.Equal(2, changes.Count);
        Assert.Equal("statecode: On (activated) → Off (draft)", changes[0].ToString());
        Assert.Equal("name: (empty) → Sync", changes[1].ToString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"other":1}""")]
    public void Change_data_that_cannot_be_read_is_kept_as_text(string data)
    {
        var change = Assert.Single(DataverseClient.ParseChangeData(data, "workflow"));
        Assert.Equal(data, change.NewValue);
    }

    [Fact]
    public void A_steps_state_reads_as_enabled_or_disabled()
    {
        var change = Assert.Single(DataverseClient.ParseChangeData(
            """{"changedAttributes":[{"logicalName":"statecode","oldValue":"0","newValue":"1"}]}""", "sdkmessageprocessingstep"));
        Assert.Equal("Enabled", change.OldValue);
        Assert.Equal("Disabled", change.NewValue);
    }

    [Fact]
    public async Task A_variables_history_covers_its_definition_and_its_value_rows()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "environmentvariablevalues?", $$"""{"value":[{"environmentvariablevalueid":"{{Value}}"}]}""")
            .OnJson(HttpMethod.Get, $"_objectid_value eq {Value}", """
                {"value":[{"createdon":"2026-10-02T09:00:00Z","action@OData.Community.Display.V1.FormattedValue":"Update",
                  "_userid_value@OData.Community.Display.V1.FormattedValue":"Ana Lee",
                  "changedata":"{\"changedAttributes\":[{\"logicalName\":\"value\",\"oldValue\":\"https://a\",\"newValue\":\"https://b\"}]}"}]}
                """)
            .OnJson(HttpMethod.Get, $"_objectid_value eq {Definition}", """
                {"value":[{"createdon":"2026-09-01T09:00:00Z","action@OData.Community.Display.V1.FormattedValue":"Create"}]}
                """);

        var records = await Fakes.Dataverse(handler).GetAuditHistoryAsync(380, Definition);

        Assert.Equal(2, records.Count);
        Assert.Equal("Current value", records[0].Row);
        Assert.Equal("Ana Lee", records[0].By);
        Assert.Equal("value: https://a → https://b", records[0].ChangesLabel);
        Assert.Equal("Definition", records[1].Row);
        Assert.Equal("Create", records[1].Action);
    }

    [Fact]
    public async Task Audit_status_reads_the_environment_and_table_settings()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "organizations?", """{"value":[{"isauditenabled":true}]}""")
            .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='workflow')", """{"IsAuditEnabled":{"Value":false,"CanBeChanged":true}}""");

        var status = await Fakes.Dataverse(handler).GetAuditStatusAsync("workflow");

        Assert.True(status.EnvironmentEnabled);
        Assert.False(status.TableEnabled);
        Assert.False(status.IsOn);
        Assert.StartsWith("Auditing is off for the workflow table", status.Describe("workflow"));
    }

    [Fact]
    public async Task A_flows_history_reads_its_own_row_only()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "audits?", """{"value":[]}""");

        Assert.Empty(await Fakes.Dataverse(handler).GetAuditHistoryAsync(29, Flow));
        Assert.Single(handler.Requests);
        Assert.Contains($"_objectid_value eq {Flow}", Uri.UnescapeDataString(handler.Requests[0].Url));
        Assert.False(DataverseClient.HasAuditHistory(61));
    }
}
