using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

/// <summary>A saved comparison run without the window, as the command line does it.</summary>
public class ReferenceDataRunTests
{
    private const string ColumnsJson = """
        {"value":[
          {"LogicalName":"new_thingid","AttributeTypeName":{"Value":"UniqueidentifierType"},"IsPrimaryId":true},
          {"LogicalName":"new_name","AttributeTypeName":{"Value":"StringType"},"IsPrimaryName":true}
        ]}
        """;

    private static string EntitiesJson(params string[] names) => JsonSerializer.Serialize(new
    {
        value = names.Select(n => new { LogicalName = n, EntitySetName = n + "s", PrimaryIdAttribute = n + "id", PrimaryNameAttribute = "new_name" })
    });

    private static string RowsJson(params (int Id, string Name)[] rows) => JsonSerializer.Serialize(new
    {
        value = rows.Select(r => new Dictionary<string, object> { [PrimaryId] = G(r.Id), ["new_name"] = r.Name })
    });

    private static FakeHttpHandler Side(string rows) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "EntityDefinitions?$select=LogicalName", EntitiesJson(Table))
        .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_thing')/Attributes", ColumnsJson)
        .OnJson(HttpMethod.Get, "new_things?$select=", rows);

    private static ReferenceDataConfig Config(int maxRows = 100) => new()
    {
        Name = "Lookups",
        MaxRowsPerEntity = maxRows,
        Entities = new List<ReferenceEntityConfig>
        {
            new() { LogicalName = Table },
            new() { LogicalName = "new_missing" },
            new() { LogicalName = "new_switchedoff", IsEnabled = false }
        }
    };

    private static readonly string[] MissingTableSkipped = ["new_missing: skipped - Not in the source environment."];
    private static readonly string[] ProgressPerStep = ["new_thing: reading metadata...", "new_thing: reading rows...", "new_missing: reading metadata..."];

    [Fact]
    public async Task Each_enabled_table_is_planned_read_and_compared()
    {
        var source = Side(RowsJson((1, "Alpha"), (2, "Beta")));
        var target = Side(RowsJson((2, "Beta old"), (3, "Gamma")));
        var progress = new List<string>();

        var result = await ReferenceDataRun.RunAsync(Config(), Fakes.Dataverse(source), Fakes.Dataverse(target), new SyncProgress(progress));

        Assert.Equal(1, result.Tables);
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(3, result.Differences.Count());
        Assert.Equal(MissingTableSkipped, result.Warnings);
        Assert.Equal(ProgressPerStep, progress);
        Assert.DoesNotContain(source.Requests, r => r.Url.Contains("switchedoff"));
    }

    [Fact]
    public async Task A_table_cut_short_by_the_cap_is_warned_about()
    {
        var rows = RowsJson((1, "Alpha"), (2, "Beta"));

        var result = await ReferenceDataRun.RunAsync(Config(maxRows: 2), Fakes.Dataverse(Side(rows)), Fakes.Dataverse(Side(rows)));

        Assert.Contains("new_thing: hit the 2 row cap, so the result is partial.", result.Warnings);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public async Task A_configuration_without_tables_compares_nothing()
    {
        var handler = new FakeHttpHandler();

        var result = await ReferenceDataRun.RunAsync(new ReferenceDataConfig(), Fakes.Dataverse(handler), Fakes.Dataverse(handler));

        Assert.Equal(0, result.Tables);
        Assert.Empty(result.Rows);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancelling_stops_before_anything_is_read()
    {
        var handler = new FakeHttpHandler();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReferenceDataRun.RunAsync(
            Config(), Fakes.Dataverse(handler), Fakes.Dataverse(handler), ct: new CancellationToken(canceled: true)));

        Assert.Empty(handler.Requests);
    }

    /// <summary>Reports on the caller's thread, so the messages are in order when the run returns.</summary>
    private sealed class SyncProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
