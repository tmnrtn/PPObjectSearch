using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class DataverseClientSolutionHistoryTests
{
    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";

    private static string Page(string rows) => "{\"value\":[" + rows + "]}";

    private static string Row(Guid id, string name, string start) =>
        $"{{\"msdyn_solutionhistoryid\":\"{id}\",\"msdyn_name\":\"{name}\",\"msdyn_starttime\":\"{start}\"}}";

    [Fact]
    public async Task History_is_ordered_on_the_server_with_labels_requested()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "msdyn_solutionhistories?", Page(
            $"{{\"msdyn_solutionhistoryid\":\"{id}\",\"msdyn_name\":\"Contoso\",\"msdyn_solutionversion\":\"1.2.3.4\"," +
            "\"msdyn_operation\":0,\"msdyn_operation" + Formatted + "\":\"Import\"," +
            "\"msdyn_suboperation\":2,\"msdyn_suboperation" + Formatted + "\":\"Upgrade\"," +
            "\"msdyn_ismanaged\":true,\"msdyn_ispatch\":false,\"msdyn_isoverwritecustomizations\":true," +
            "\"msdyn_publishername\":\"Contoso Pub\",\"msdyn_packagename\":\"pkg\",\"msdyn_packageversion\":\"9.0\"," +
            "\"msdyn_starttime\":\"2026-01-01T10:00:00Z\",\"msdyn_endtime\":\"2026-01-01T10:05:00Z\",\"msdyn_totaltime\":300," +
            "\"msdyn_status\":1,\"msdyn_result\":false,\"msdyn_errorcode\":\"0x80040\",\"msdyn_exceptionmessage\":\"failed\"," +
            "\"msdyn_exceptionstack\":\"at X\",\"msdyn_retrycount\":2,\"msdyn_activityid\":\"act\",\"msdyn_correlationid\":\"corr\"}"));
        using var client = Fakes.Dataverse(handler);

        var entries = await client.GetSolutionHistoryAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Contains("$top=500", request.Url);
        Assert.EndsWith("&$orderby=msdyn_starttime desc", request.Url);
        Assert.Contains("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"", request.Header("Prefer"));

        var e = Assert.Single(entries);
        Assert.Equal(id, e.Id);
        Assert.Equal("Contoso", e.SolutionName);
        Assert.Equal("1.2.3.4", e.Version);
        Assert.Equal(0, e.OperationCode);
        Assert.Equal("Import", e.Operation);
        Assert.Equal("Upgrade", e.SubOperation);
        Assert.True(e.IsManaged);
        Assert.False(e.IsPatch);
        Assert.True(e.OverwriteCustomizations);
        Assert.Equal("Contoso Pub", e.PublisherName);
        Assert.Equal("pkg", e.PackageName);
        Assert.Equal("9.0", e.PackageVersion);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), e.StartTime);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 5, 0, TimeSpan.Zero), e.EndTime);
        Assert.Equal(300, e.TotalSeconds);
        Assert.Equal(1, e.StatusCode);
        Assert.False(e.Succeeded);
        Assert.Equal("0x80040", e.ErrorCode);
        Assert.Equal("failed", e.ExceptionMessage);
        Assert.Equal("at X", e.ExceptionStack);
        Assert.Equal(2, e.RetryCount);
        Assert.Equal("act", e.ActivityId);
        Assert.Equal("corr", e.CorrelationId);
    }

    private static readonly string[] NewestEntryFirst = ["newest", "middle", "old"];

    [Fact]
    public async Task Refused_orderby_is_retried_without_it_and_sorted_newest_first_locally()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "$orderby=", HttpStatusCode.BadRequest, "orderby not supported on virtual entity")
            .OnJson(HttpMethod.Get, "msdyn_solutionhistories?", Page(
                Row(Guid.NewGuid(), "old", "2025-01-01T00:00:00Z") + "," +
                Row(Guid.NewGuid(), "newest", "2026-06-01T00:00:00Z") + "," +
                Row(Guid.NewGuid(), "middle", "2025-06-01T00:00:00Z")));
        using var client = Fakes.Dataverse(handler);

        var entries = await client.GetSolutionHistoryAsync();

        Assert.Equal(NewestEntryFirst, entries.Select(e => e.SolutionName));
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("$orderby", handler.Requests[1].Url);
        Assert.Contains("$top=500", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Errors_other_than_bad_request_are_not_retried()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "msdyn_solutionhistories?", HttpStatusCode.Forbidden, "no privilege");
        using var client = Fakes.Dataverse(handler);

        var ex = await Assert.ThrowsAsync<DataverseException>(() => client.GetSolutionHistoryAsync());

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Rows_without_an_id_are_skipped_and_defaults_fill_the_gaps()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "msdyn_solutionhistories?", Page(
            "{\"msdyn_name\":\"no id\"}," +
            $"{{\"msdyn_solutionhistoryid\":\"{id}\",\"msdyn_operation\":1}}," +
            $"{{\"msdyn_solutionhistoryid\":\"{Guid.NewGuid()}\"}}"));
        using var client = Fakes.Dataverse(handler);

        var entries = await client.GetSolutionHistoryAsync();

        Assert.Equal(2, entries.Count);
        var withCode = entries.Single(e => e.Id == id);
        Assert.Equal("(unnamed)", withCode.SolutionName);
        Assert.Equal("1", withCode.Operation);
        Assert.Contains(entries, e => e.Operation == "Unknown");
    }

    [Fact]
    public async Task Response_without_value_is_empty()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "msdyn_solutionhistories?", "{}");
        using var client = Fakes.Dataverse(handler);

        Assert.Empty(await client.GetSolutionHistoryAsync());
    }
}
