using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public partial class DataverseClientRowCountTests
{
    private const int PageSize = 5000;
    private static readonly Guid MetadataId = Guid.Parse("12345678-0000-0000-0000-000000000001");

    private const string AccountDefinition =
        "{\"LogicalName\":\"account\",\"EntitySetName\":\"accounts\",\"PrimaryIdAttribute\":\"accountid\",\"ObjectTypeCode\":1}";

    /// <summary>The page number a FetchXML page probe asks for.</summary>
    [GeneratedRegex(@"page='(\d+)'")]
    private static partial Regex PageNumber();

    private static FakeHttpHandler WithDefinition(string definition = AccountDefinition) =>
        new FakeHttpHandler().OnJson(HttpMethod.Get, $"EntityDefinitions({MetadataId})", definition);

    /// <summary>Answers page probes as a table of <paramref name="total"/> rows would.</summary>
    private static FakeHttpHandler SimulatePages(FakeHttpHandler handler, long total, bool ignorePageNumber = false) =>
        handler.On(HttpMethod.Get, "<fetch page=", request =>
        {
            var page = int.Parse(PageNumber().Match(request.Url).Groups[1].Value);
            if (ignorePageNumber) page = 1;

            var before = (long)(page - 1) * PageSize;
            var count = (int)Math.Clamp(total - before, 0, PageSize);

            var json = new StringBuilder("{\"value\":[");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"accountid\":\"k").Append(before + i).Append("\"}");
            }

            return FakeHttpHandler.Json(json.Append("]}").ToString());
        });

    private static int PageProbes(FakeHttpHandler handler) => handler.Requests.Count(r => r.Url.Contains("<fetch page="));

    [Fact]
    public async Task Aggregate_count_answers_in_one_request_without_max_page_size()
    {
        var handler = WithDefinition()
            .OnJson(HttpMethod.Get, "aggregate='true'", "{\"value\":[{\"rowcount\":1234}]}");
        using var client = Fakes.Dataverse(handler);

        var result = await client.CountRowsAsync(MetadataId);

        Assert.Equal(1234, result.Rows);
        Assert.Equal(1, result.Requests);
        Assert.Equal("aggregate count", result.Method);

        var aggregate = handler.Requests[1];
        Assert.StartsWith(Fakes.ApiRoot + "accounts?fetchXml=", aggregate.Url);
        Assert.Contains("<attribute name='accountid' alias='rowcount' aggregate='count' />", aggregate.Url);
        Assert.Contains("<entity name='account'>", aggregate.Url);
        Assert.Null(aggregate.Header("Prefer"));
    }

    [Fact]
    public async Task Refused_aggregate_falls_back_to_page_search_and_counts_the_attempt()
    {
        var handler = SimulatePages(
            WithDefinition().OnError(HttpMethod.Get, "aggregate='true'", HttpStatusCode.BadRequest, "AggregateQueryRecordLimit exceeded"),
            total: 1234);
        using var client = Fakes.Dataverse(handler);

        var result = await client.CountRowsAsync(MetadataId);

        Assert.Equal(1234, result.Rows);
        Assert.Equal("page search", result.Method);
        Assert.Equal(2, result.Requests);
        Assert.Equal(1, PageProbes(handler));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(4999L)]
    [InlineData(5000L)]
    [InlineData(5001L)]
    [InlineData(25123L)]
    [InlineData(20000L)]
    [InlineData(40000L)]
    [InlineData(123456L)]
    [InlineData(1000000L)]
    public async Task Page_search_finds_the_exact_count(long total)
    {
        var handler = SimulatePages(WithDefinition(), total);
        using var client = Fakes.Dataverse(handler);

        // A snapshot over the aggregate limit skips the aggregate attempt entirely.
        var result = await client.CountRowsAsync(MetadataId, expectedRows: 60_000);

        Assert.Equal(total, result.Rows);
        Assert.Equal("page search", result.Method);
        Assert.Equal(PageProbes(handler), result.Requests);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("aggregate='true'"));
    }

    [Fact]
    public async Task Page_probes_request_only_the_key_ordered_and_without_max_page_size()
    {
        var handler = SimulatePages(WithDefinition(), 10);
        using var client = Fakes.Dataverse(handler);

        await client.CountRowsAsync(MetadataId, expectedRows: 100_000);

        var probe = handler.Requests.Single(r => r.Url.Contains("<fetch page="));
        Assert.Contains("<fetch page='1' count='5000' no-lock='true'>", probe.Url);
        Assert.Contains("<attribute name='accountid' /><order attribute='accountid' />", probe.Url);
        Assert.Null(probe.Header("Prefer"));
    }

    private static readonly int[] DoublingThenHalving = [1, 2, 4, 8, 6];

    [Fact]
    public async Task Page_search_uses_doubling_then_halving()
    {
        var handler = SimulatePages(WithDefinition(), 25123);
        using var client = Fakes.Dataverse(handler);

        await client.CountRowsAsync(MetadataId, expectedRows: 60_000);

        var pages = handler.Requests
            .Where(r => r.Url.Contains("<fetch page="))
            .Select(r => int.Parse(PageNumber().Match(r.Url).Groups[1].Value));
        Assert.Equal(DoublingThenHalving, pages);
    }

    [Fact]
    public async Task Page_number_being_ignored_is_detected_rather_than_counting_forever()
    {
        var handler = SimulatePages(WithDefinition(), 100_000, ignorePageNumber: true);
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.CountRowsAsync(MetadataId, expectedRows: 60_000));

        Assert.Contains("returned page 1 again when asked for page 2", ex.Message);
    }

    [Fact]
    public async Task Small_expected_count_still_tries_the_aggregate()
    {
        var handler = WithDefinition().OnJson(HttpMethod.Get, "aggregate='true'", "{\"value\":[{\"rowcount\":\"7\"}]}");
        using var client = Fakes.Dataverse(handler);

        var result = await client.CountRowsAsync(MetadataId, expectedRows: 50_000);

        Assert.Equal(7, result.Rows);
        Assert.Equal("aggregate count", result.Method);
    }

    [Fact]
    public async Task Progress_is_reported_while_counting()
    {
        var handler = SimulatePages(WithDefinition(), 6000);
        using var client = Fakes.Dataverse(handler);
        var reports = new List<string>();

        await client.CountRowsAsync(MetadataId, expectedRows: 60_000, progress: new SyncProgress(reports));

        Assert.Contains("Counting account... page 1", reports);
        Assert.Contains("Counting account... page 2", reports);
    }

    [Fact]
    public async Task Table_without_an_entity_set_cannot_be_counted()
    {
        var handler = WithDefinition("{\"LogicalName\":\"virtualthing\"}");
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.CountRowsAsync(MetadataId));

        Assert.Contains("virtualthing has no entity set", ex.Message);
    }

    [Fact]
    public async Task Table_without_a_logical_name_cannot_be_counted()
    {
        var handler = WithDefinition("{}");
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.CountRowsAsync(MetadataId));

        Assert.Contains("no logical name", ex.Message);
    }

    [Fact]
    public async Task Primary_key_defaults_to_logical_name_plus_id()
    {
        var handler = WithDefinition("{\"LogicalName\":\"new_thing\",\"EntitySetName\":\"new_things\"}")
            .OnJson(HttpMethod.Get, "aggregate='true'", "{\"value\":[{\"rowcount\":1}]}");
        using var client = Fakes.Dataverse(handler);

        await client.CountRowsAsync(MetadataId);

        Assert.Contains("<attribute name='new_thingid' alias='rowcount'", handler.Requests[1].Url);
    }

    // ---- Snapshot ----

    [Fact]
    public async Task Snapshot_reads_the_stored_count_and_when_it_was_refreshed()
    {
        var handler = WithDefinition()
            .OnJson(HttpMethod.Get, "RetrieveTotalRecordCount",
                "{\"EntityRecordCountCollection\":{\"Count\":2,\"Keys\":[\"contact\",\"account\"],\"Values\":[5,98765]}}")
            .OnJson(HttpMethod.Get, "recordcountsnapshots?", "{\"value\":[{\"lastupdated\":\"2026-09-29T02:00:00Z\"}]}");
        using var client = Fakes.Dataverse(handler);

        var snapshot = await client.GetRowCountSnapshotAsync(MetadataId);

        Assert.Equal(new RowCountSnapshot(98765, new DateTimeOffset(2026, 9, 29, 2, 0, 0, TimeSpan.Zero)), snapshot);
        Assert.Contains("RetrieveTotalRecordCount(EntityNames=@p1)?@p1=[\"account\"]", handler.Requests[1].Url);
        Assert.Contains("$filter=objecttypecode eq 1", handler.Requests[2].Url);
    }

    [Fact]
    public async Task Snapshot_count_stands_when_the_refresh_time_cannot_be_read()
    {
        var handler = WithDefinition()
            .OnJson(HttpMethod.Get, "RetrieveTotalRecordCount",
                "{\"EntityRecordCountCollection\":{\"Keys\":[\"account\"],\"Values\":[10]}}")
            .OnError(HttpMethod.Get, "recordcountsnapshots?", HttpStatusCode.Forbidden, "no read");
        using var client = Fakes.Dataverse(handler);

        var snapshot = await client.GetRowCountSnapshotAsync(MetadataId);

        Assert.Equal(new RowCountSnapshot(10, null), snapshot);
    }

    [Fact]
    public async Task Snapshot_without_an_object_type_code_skips_the_refresh_time()
    {
        var handler = WithDefinition("{\"LogicalName\":\"account\",\"EntitySetName\":\"accounts\"}")
            .OnJson(HttpMethod.Get, "RetrieveTotalRecordCount",
                "{\"EntityRecordCountCollection\":{\"Keys\":[\"account\"],\"Values\":[10]}}");
        using var client = Fakes.Dataverse(handler);

        var snapshot = await client.GetRowCountSnapshotAsync(MetadataId);

        Assert.Equal(new RowCountSnapshot(10, null), snapshot);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task No_stored_count_for_the_table_is_null()
    {
        var handler = WithDefinition()
            .OnJson(HttpMethod.Get, "RetrieveTotalRecordCount",
                "{\"EntityRecordCountCollection\":{\"Keys\":[\"contact\"],\"Values\":[10]}}");
        using var client = Fakes.Dataverse(handler);

        Assert.Null(await client.GetRowCountSnapshotAsync(MetadataId));
        Assert.Equal(2, handler.Requests.Count);
    }

    private sealed class SyncProgress(List<string> into) : IProgress<string>
    {
        public void Report(string value) => into.Add(value);
    }
}
