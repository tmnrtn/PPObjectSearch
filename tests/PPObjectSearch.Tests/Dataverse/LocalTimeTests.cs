using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

/// <summary>
/// Dataverse sends every timestamp in UTC. The app shows them in local time, so a run that
/// started at 09:15 on the user's clock says 09:15, not 08:15.
/// </summary>
public class LocalTimeTests
{
    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_utc_timestamp_is_read_in_local_time()
    {
        var date = JsonHelper.GetDate(Row("""{ "d": "2026-07-01T08:15:00Z" }"""), "d");

        var utc = new DateTimeOffset(2026, 7, 1, 8, 15, 0, TimeSpan.Zero);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(utc), date!.Value.Offset);
        Assert.Equal(utc.ToLocalTime().DateTime, date.Value.DateTime);
    }

    [Fact]
    public void Converting_keeps_the_same_moment()
    {
        var date = JsonHelper.GetDate(Row("""{ "d": "2026-01-15T23:30:00Z" }"""), "d");

        // Equality on DateTimeOffset is the instant, whatever the offset.
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 23, 30, 0, TimeSpan.Zero), date);
        Assert.Equal(new DateTime(2026, 1, 15, 23, 30, 0, DateTimeKind.Utc), date!.Value.UtcDateTime);
    }

    [Fact]
    public void A_timestamp_with_its_own_offset_is_converted_too()
    {
        var date = JsonHelper.GetDate(Row("""{ "d": "2026-07-01T10:15:00+02:00" }"""), "d");

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 15, 0, TimeSpan.Zero), date);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(date!.Value), date.Value.Offset);
    }

    [Fact]
    public void A_timestamp_without_an_offset_is_taken_as_utc()
    {
        var date = JsonHelper.GetDate(Row("""{ "d": "2026-07-01T08:15:00" }"""), "d");

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 15, 0, TimeSpan.Zero), date);
    }

    [Theory]
    [InlineData("""{ "d": "not a date" }""")]
    [InlineData("""{ "d": null }""")]
    [InlineData("""{ }""")]
    public void Anything_else_is_no_date(string json) =>
        Assert.Null(JsonHelper.GetDate(Row(json), "d"));

    [Fact]
    public async Task Flow_run_times_arrive_in_local_time()
    {
        var flowId = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "flowruns", JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new Dictionary<string, object?>
                {
                    ["flowrunid"] = Guid.NewGuid().ToString(),
                    ["name"] = "08584501234567890123456789CU01",
                    ["status"] = "Succeeded",
                    ["starttime"] = "2026-07-01T08:15:00Z",
                    ["endtime"] = "2026-07-01T08:16:30Z",
                }
            }
        }));

        var runs = await Fakes.Dataverse(handler).GetCloudFlowRunsAsync(flowId);

        var run = Assert.Single(runs);
        var expectedOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTimeOffset(2026, 7, 1, 8, 15, 0, TimeSpan.Zero));
        Assert.Equal(expectedOffset, run.StartTime!.Value.Offset);
        Assert.Equal(expectedOffset, run.EndTime!.Value.Offset);
        Assert.Equal(TimeSpan.FromSeconds(90), run.EndTime - run.StartTime);
    }
}
