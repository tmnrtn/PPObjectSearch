using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.Dataverse;

public class ImportLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private static string Log() => $"""
        <importexportxml start="{T0:O}" progress="100" processed="true">
          <entities>
            <entity id="{"{"}11111111-1111-1111-1111-111111111111{"}"}" LocalizedName="Invoice" OriginalName="new_invoice">
              <result result="success" errorcode="0" errortext="" datetimeticks="{T0.AddSeconds(30).UtcTicks}" />
            </entity>
          </entities>
          <nodes>
            <node name="Notify on approval" id="{"{"}22222222-2222-2222-2222-222222222222{"}"}">
              <result result="failure" errorcode="0x80048d19" errortext="Connection reference new_outlook not found | missing" datetimeticks="{T0.AddSeconds(90).UtcTicks}" />
            </node>
            <node name="Archive">
              <result result="warning" errortext="Deprecated action" />
            </node>
          </nodes>
        </importexportxml>
        """;

    [Fact]
    public void Every_component_with_a_result_is_read_with_its_timing()
    {
        var log = ImportLogParser.Parse(Log());

        Assert.Equal(3, log.Rows.Count);
        Assert.Equal(100, log.Progress);
        Assert.True(log.Processed);

        var invoice = log.Rows[0];
        Assert.Equal(("entities", "Invoice", "Success"), (invoice.Section, invoice.Name, invoice.ResultLabel));
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), invoice.Id);
        Assert.Null(invoice.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(30), invoice.Duration);

        var flow = log.Rows[1];
        Assert.True(flow.IsFailure);
        Assert.Equal("0x80048d19", flow.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(60), flow.Duration);
        Assert.Equal("60 s", flow.DurationLabel);

        Assert.True(log.Rows[2].IsWarning);
        Assert.Null(log.Rows[2].Id);
        Assert.Equal((1, 1), (log.Failures, log.Warnings));
    }

    [Fact]
    public void Problems_copy_as_a_markdown_table_failures_first()
    {
        var markdown = ImportLogParser.Parse(Log()).ProblemsAsMarkdown("Core 1.2");
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        Assert.Equal("### Import of Core 1.2: 1 failure(s), 1 warning(s)", lines[0]);
        Assert.StartsWith("| Failure | nodes | Notify on approval | Connection reference new_outlook not found \\| missing |", lines[4]);
        Assert.StartsWith("| Warning |", lines[5]);
    }

    [Fact]
    public void A_history_row_is_matched_to_the_job_that_started_closest_to_it()
    {
        var jobs = new[]
        {
            new ImportJobInfo(Guid.NewGuid(), "core", 100, T0.AddDays(-1), T0.AddDays(-1), T0.AddDays(-1)),
            new ImportJobInfo(Guid.NewGuid(), "core", 100, T0.AddMinutes(1), T0.AddMinutes(9), T0),
            new ImportJobInfo(Guid.NewGuid(), "core", 40, T0.AddMinutes(30), null, T0.AddMinutes(30))
        };
        var entry = new SolutionHistoryEntry { Id = Guid.NewGuid(), SolutionName = "core", Operation = "Import", StartTime = T0 };

        Assert.Same(jobs[1], DataverseClient.MatchImportJob(jobs, entry));
        Assert.Null(DataverseClient.MatchImportJob(jobs[..1], entry));
        Assert.False(jobs[2].IsFinished);
    }

    [Fact]
    public async Task Jobs_are_listed_for_the_solution_and_the_log_read_on_its_own()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "importjobs?", $$"""{"value":[{"importjobid":"{{id}}","solutionname":"core","progress":42.5,"startedon":"2026-10-04T09:00:00Z"}]}""")
            .OnJson(HttpMethod.Get, $"importjobs({id})?$select=data", """{"data":"<importexportxml />"}""");
        var client = Fakes.Dataverse(handler);

        var job = Assert.Single(await client.GetImportJobsAsync("core"));
        Assert.Equal(42.5, job.Progress);
        Assert.False(job.IsFinished);
        Assert.Contains("solutionname eq 'core'", Uri.UnescapeDataString(handler.Requests[0].Url));

        Assert.Equal("<importexportxml />", await client.GetImportJobDataAsync(id));
    }
}
