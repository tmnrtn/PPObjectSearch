using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>Reading table audit, change tracking, link and search settings from one environment.</summary>
public class TableSettingsTests
{
    private static readonly Guid Profile = Guid.Parse("50000000-0000-0000-0000-000000000005");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_tables_settings_are_read_from_its_managed_properties()
    {
        var table = DataverseClient.ReadTableSetting(Json("""
            {"LogicalName":"Contoso_Case","MetadataId":"60000000-0000-0000-0000-000000000006","IsCustomEntity":true,"IsManaged":true,
             "DisplayName":{"UserLocalizedLabel":{"Label":"Case"}},
             "IsAuditEnabled":{"Value":true,"CanBeChanged":true},"ChangeTrackingEnabled":false,
             "SyncToExternalSearchIndex":true,"CanEnableSyncToExternalSearchIndex":{"Value":true}}
            """));

        Assert.NotNull(table);
        Assert.Equal("contoso_case", table.LogicalName);
        Assert.Equal("Case", table.DisplayName);
        Assert.Equal(Guid.Parse("60000000-0000-0000-0000-000000000006"), table.MetadataId);
        Assert.True(table.IsAuditEnabled);
        Assert.False(table.ChangeTrackingEnabled);
        Assert.True(table.SearchEnabled);
        Assert.True(table.CanEnableSearch);
    }

    [Theory]
    [InlineData("""{"LogicalName":"contoso_ref","IsCustomAttribute":true,"IsValidForUpdate":true,"IsLogical":false,"IsAuditEnabled":{"Value":false}}""", true)]
    [InlineData("""{"LogicalName":"name","IsCustomAttribute":false,"IsValidForUpdate":true,"IsLogical":false}""", false)]
    [InlineData("""{"LogicalName":"contoso_calc","IsCustomAttribute":true,"IsValidForUpdate":false,"IsLogical":false}""", false)]
    [InlineData("""{"LogicalName":"contoso_refname","IsCustomAttribute":true,"IsValidForUpdate":true,"IsLogical":true}""", false)]
    public void Only_custom_writable_real_columns_are_offered_for_audit(string json, bool kept)
    {
        Assert.Equal(kept, DataverseClient.ReadColumnAudit(Json(json)) is not null);
    }

    [Fact]
    public void Search_status_lists_each_indexed_table_with_its_columns_and_sync_time()
    {
        var inner = """
            {"value":{"entitystatusresults":[{"entitylogicalname":"account","objecttypecode":1,"primarynamefield":"name",
              "lastdatasynctimestamp":"73630169!09/12/2022 14:26:14","entitystatus":"EntitySyncComplete",
              "searchableindexedfieldinfomap":{"name":{"indexfieldname":"d_0"},"accountnumber":{"indexfieldname":"a46"}}},
              {"entitylogicalname":"contact","entitystatus":"EntitySyncInProgress"}],
              "status":"provisioned","lockboxstatus":"Disabled","cmkstatus":"Disabled"}}
            """;

        var status = DataverseClient.ParseSearchStatus(Json(JsonSerializer.Serialize(new { response = inner })), 5515, 3);

        Assert.True(status.IsProvisioned);
        Assert.Equal(5515, status.DocumentCount);
        Assert.Equal(2, status.Tables.Count);

        var account = status.Tables[0];
        Assert.True(account.IsComplete);
        Assert.Equal(["accountnumber", "name"], account.IndexedColumns);
        Assert.Equal(new DateTimeOffset(2022, 9, 12, 14, 26, 14, TimeSpan.Zero), account.LastDataSync);

        Assert.False(status.Tables[1].IsComplete);
        Assert.Equal("In progress", status.Tables[1].StatusLabel);
    }

    [Fact]
    public void Search_not_provisioned_reads_as_such_with_no_tables()
    {
        var status = DataverseClient.ParseSearchStatus(Json("""{"response":"{\"value\":{\"status\":\"notprovisioned\",\"lockboxstatus\":\"Unknown\"}}"}"""));

        Assert.False(status.IsProvisioned);
        Assert.Equal("Not provisioned", status.StatusLabel);
        Assert.Empty(status.Tables);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0!")]
    [InlineData("not a time")]
    public void A_search_timestamp_that_cannot_be_read_is_no_time(string? text)
    {
        Assert.Null(DataverseClient.ParseSearchTimestamp(text));
    }

    [Fact]
    public async Task Links_are_read_with_their_folder_and_each_tables_state()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "synapselinkprofiles?", $$"""
                {"value":[{"synapselinkprofileid":"{{Profile}}","name":"Lake","profilestate":1,
                  "profilestate@OData.Community.Display.V1.FormattedValue":"Active",
                  "_datalakefolder_value@OData.Community.Display.V1.FormattedValue":"contosolake"}]}
                """)
            .OnJson(HttpMethod.Get, "synapselinkprofileentitystates", $$"""
                {"value":[{"entityname":"Contoso_Case","_profile_value":"{{Profile}}","syncstate":2,
                  "syncstate@OData.Community.Display.V1.FormattedValue":"InProgress","lakerecordcount":"120","sourcerecordcount":130,"statecode":0}]}
                """);

        var links = await Fakes.Dataverse(handler).GetSynapseLinkAsync();

        var profile = Assert.Single(links.Profiles);
        Assert.True(profile.IsActive);
        Assert.Equal("contosolake", profile.FolderLabel);

        var table = Assert.Single(links.Tables);
        Assert.Equal("contoso_case", table.Table);
        Assert.False(table.IsHealthy);
        Assert.Equal(120, table.LakeRecordCount);
        Assert.Equal(130, table.SourceRecordCount);
    }

    [Fact]
    public async Task No_profiles_means_no_second_read()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "synapselinkprofiles?", """{"value":[]}""");

        var links = await Fakes.Dataverse(handler).GetSynapseLinkAsync();

        Assert.Empty(links.Profiles);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("synapselinkprofileentitystates"));
    }

    [Fact]
    public async Task Organization_settings_fall_back_to_the_older_retention_column()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "organizations", """
            {"value":[{"isauditenabled":true,"isuseraccessauditenabled":false,"auditretentionperiod":30,"isexternalsearchindexenabled":true}]}
            """);

        var settings = await Fakes.Dataverse(handler).GetEnvironmentDataSettingsAsync();

        Assert.True(settings.AuditEnabled);
        Assert.False(settings.UserAccessAuditEnabled);
        Assert.Null(settings.ReadAuditEnabled);
        Assert.Equal("30 days", settings.RetentionLabel);
        Assert.True(settings.SearchEnabled);
    }

    [Fact]
    public async Task A_part_that_cannot_be_read_is_a_problem_not_a_failure()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "EntityDefinitions?", """{"value":[{"LogicalName":"account","IsAuditEnabled":{"Value":true}}]}""")
            .OnError(HttpMethod.Get, "synapselinkprofiles", HttpStatusCode.Forbidden, "no read on synapselinkprofile")
            .OnJson(HttpMethod.Get, "organizations", """{"value":[{"isauditenabled":true}]}""")
            .OnJson(HttpMethod.Get, "searchstatus", """{"response":"{\"value\":{\"status\":\"notprovisioned\"}}"}""")
            .OnStatus(HttpMethod.Get, "searchstatistics", HttpStatusCode.BadRequest);

        var snapshot = await TableSettingsReader.ReadAsync("Dev", Fakes.Dataverse(handler), CancellationToken.None);

        Assert.True(snapshot.TablesRead);
        Assert.True(snapshot.Tables["account"].IsAuditEnabled);
        Assert.Null(snapshot.Synapse);
        Assert.NotNull(snapshot.Search);
        var problem = Assert.Single(snapshot.Problems);
        Assert.StartsWith("Synapse Link could not be read", problem);
    }

    [Fact]
    public async Task Columns_are_read_only_for_tables_the_environment_has_and_a_failure_is_counted()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "LogicalName='account')/Attributes", """
                {"value":[{"LogicalName":"contoso_tier","IsCustomAttribute":true,"IsValidForUpdate":true,"IsAuditEnabled":{"Value":false}}]}
                """)
            .OnError(HttpMethod.Get, "LogicalName='contact')/Attributes", HttpStatusCode.InternalServerError, "boom");

        var snapshot = new TableSettingsSnapshot
        {
            Environment = "Dev",
            Tables = new Dictionary<string, PPObjectSearch.Models.TableSetting>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = new("account", null, Guid.Empty, false, false, true, true, false, true),
                ["contact"] = new("contact", null, Guid.Empty, false, false, true, true, false, true)
            }
        };

        await TableSettingsReader.ReadColumnsAsync(snapshot, Fakes.Dataverse(handler), ["account", "contact", "lead"], CancellationToken.None);

        Assert.Single(snapshot.Columns["account"]);
        Assert.False(snapshot.Columns.ContainsKey("contact"));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("'lead'"));
        Assert.Contains("Columns of 1 table could not be read (contact)", Assert.Single(snapshot.Problems));
    }
}
