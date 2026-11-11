using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>Owned records: which tables have an owner, and how many of their rows one user or team owns.</summary>
public partial class OwnedRecordsTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-0000-0000-0000-000000000001");

    private static object Table(string name, string? display, string ownership = "UserOwned", string type = "Standard", bool isPrivate = false,
        string? entitySet = null) => new Dictionary<string, object?>
    {
        ["LogicalName"] = name,
        ["DisplayName"] = display is null ? null : new { UserLocalizedLabel = new { Label = display } },
        ["EntitySetName"] = entitySet ?? name + "s",
        ["PrimaryIdAttribute"] = name + "id",
        ["OwnershipType"] = ownership,
        ["TableType"] = type,
        ["IsPrivate"] = isPrivate
    };

    private static string Tables() => JsonSerializer.Serialize(new
    {
        value = new[]
        {
            Table("contact", "Contact"),
            Table("account", "Account"),
            Table("task", "Task"),
            Table("activitypointer", "Activity"),
            Table("businessunit", "Business Unit", ownership: "BusinessOwned"),
            Table("new_setting", "Setting", ownership: "OrganizationOwned"),
            Table("new_external", "External", type: "Virtual"),
            Table("new_internal", "Internal", isPrivate: true),
            Table("new_noset", "No set", entitySet: "")
        }
    });

    /// <summary>The table a FetchXML request reads, from its entity element.</summary>
    [GeneratedRegex(@"<entity name='([a-z_]+)'>")]
    private static partial Regex EntityName();

    private static string Count(long rows) => $"{{\"value\":[{{\"rowcount\":{rows}}}]}}";

    // ---------------------------------------------------------------- which tables

    [Fact]
    public async Task Only_readable_tables_with_an_owner_are_counted_and_not_the_activity_pointer()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "EntityDefinitions?", Tables());

        var tables = await Fakes.Dataverse(handler).GetOwnedTablesAsync();

        Assert.Equal(["account", "contact", "task"], tables.Select(t => t.LogicalName));
        Assert.Equal("Account (account)", tables[0].Label);
        Assert.Equal("accounts", tables[0].EntitySet);
        Assert.Equal("accountid", tables[0].PrimaryId);
        Assert.Contains("OwnershipType", handler.Requests[0].Url);
    }

    // ---------------------------------------------------------------- one table

    [Fact]
    public async Task A_tables_owned_rows_are_counted_by_their_owner_in_one_request()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "aggregate='true'", Count(42));

        var rows = await Fakes.Dataverse(handler).CountOwnedRowsAsync(new OwnedTable("account", "Account", "accounts", "accountid"), Alice);

        Assert.Equal(42, rows);
        var request = Assert.Single(handler.Requests);
        Assert.StartsWith(Fakes.ApiRoot + "accounts?fetchXml=", request.Url);
        Assert.Contains($"<condition attribute='ownerid' operator='eq' value='{Alice}' />", request.Url);
    }

    [Fact]
    public async Task Past_the_aggregate_limit_the_owned_rows_are_counted_by_pages_with_the_same_owner()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "aggregate='true'", HttpStatusCode.BadRequest, "AggregateQueryRecordLimit exceeded")
            .On(HttpMethod.Get, "<fetch page=", request =>
            {
                // 5,000 rows on page 1 and 1,234 on page 2: 6,234 owned.
                var page = int.Parse(Regex.Match(request.Url, @"page='(\d+)'").Groups[1].Value);
                var count = page switch { 1 => 5000, 2 => 1234, _ => 0 };
                var json = new StringBuilder("{\"value\":[");
                for (var i = 0; i < count; i++) json.Append(i > 0 ? "," : "").Append("{\"accountid\":\"p").Append(page).Append('-').Append(i).Append("\"}");
                return FakeHttpHandler.Json(json.Append("]}").ToString());
            });

        var rows = await Fakes.Dataverse(handler).CountOwnedRowsAsync(new OwnedTable("account", "Account", "accounts", "accountid"), Alice);

        Assert.Equal(6234, rows);
        Assert.All(handler.Requests.Where(r => r.Url.Contains("<fetch page=")),
            r => Assert.Contains($"value='{Alice}'", r.Url));
    }

    // ---------------------------------------------------------------- every table

    [Fact]
    public async Task Owned_tables_are_listed_largest_first_without_the_empty_ones_and_failures_are_kept_with_why()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "EntityDefinitions?", Tables())
            .OnError(HttpMethod.Get, "<entity name='task'>", HttpStatusCode.Forbidden, "Principal lacks prvReadTask")
            .On(HttpMethod.Get, "aggregate='true'", request => FakeHttpHandler.Json(Count(
                EntityName().Match(request.Url).Groups[1].Value switch { "contact" => 1200, "account" => 7, _ => 0 })));
        var reports = new List<(int Done, int Total)>();

        var result = await OwnedRecords.CountAsync(Fakes.Dataverse(handler), Alice, new SynchronousProgress(reports));

        Assert.Equal(["contact", "account"], result.Owned.Select(o => o.Table.LogicalName));
        Assert.Equal(1207, result.TotalRows);
        var failed = Assert.Single(result.Failed);
        Assert.Equal("task", failed.Table.LogicalName);
        Assert.Contains("prvReadTask", failed.Error);
        Assert.Equal(3, result.TablesCounted);
        Assert.Equal((0, 3), reports[0]);
        Assert.Equal((3, 3), reports.MaxBy(r => r.Done));
    }

    [Fact]
    public async Task A_cancelled_count_stops()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "EntityDefinitions?", Tables())
            .OnJson(HttpMethod.Get, "aggregate='true'", Count(1));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnedRecords.CountAsync(Fakes.Dataverse(handler), Alice, ct: cts.Token));
    }

    /// <summary>Records every report as it is made - Progress&lt;T&gt; would post them later.</summary>
    private sealed class SynchronousProgress(List<(int Done, int Total)> reports) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
