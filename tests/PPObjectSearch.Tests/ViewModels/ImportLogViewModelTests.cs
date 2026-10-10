using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The import log window: finding the job behind a history row, following it, and listing its components.</summary>
public class ImportLogViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Job = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Invoice = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly SolutionHistoryEntry Entry = new()
    {
        Id = Guid.NewGuid(), SolutionName = "core", Version = "1.2.0.0", Operation = "Import", StartTime = T0
    };

    private static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Jobs(double? progress = 100, bool completed = true, double startedMinutes = 1) => JsonSerializer.Serialize(new
    {
        value = new[]
        {
            new Dictionary<string, object?>
            {
                ["importjobid"] = Job.ToString(), ["solutionname"] = "core", ["progress"] = progress,
                ["startedon"] = Iso(T0.AddMinutes(startedMinutes)), ["completedon"] = completed ? Iso(T0.AddMinutes(startedMinutes + 4)) : null,
                ["createdon"] = Iso(T0.AddMinutes(startedMinutes))
            }
        }
    });

    private const string ProblemLog = """
        <importexportxml progress="100" processed="true">
          <entities>
            <entity id="{11111111-1111-1111-1111-111111111111}" LocalizedName="Invoice" OriginalName="new_invoice">
              <result result="success" errorcode="0" errortext="" />
            </entity>
          </entities>
          <nodes>
            <node name="Notify on approval" id="{22222222-2222-2222-2222-222222222222}">
              <result result="failure" errorcode="0x80048d19" errortext="Connection reference new_outlook not found" />
            </node>
            <node name="Archive">
              <result result="warning" errortext="Deprecated action" />
            </node>
          </nodes>
        </importexportxml>
        """;

    private const string CleanLog = """
        <importexportxml progress="100" processed="true">
          <entities>
            <entity id="{11111111-1111-1111-1111-111111111111}" LocalizedName="Invoice" OriginalName="new_invoice">
              <result result="success" errorcode="0" errortext="" />
            </entity>
          </entities>
        </importexportxml>
        """;

    private static string Data(string? xml) => JsonSerializer.Serialize(new { data = xml });

    private static FakeHttpHandler Handler(string jobs, string? log = ProblemLog) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "importjobs?", jobs)
        .OnJson(HttpMethod.Get, $"importjobs({Job})?$select=data", Data(log));

    private static ImportLogViewModel Window(FakeHttpHandler handler, params SolutionComponentItem[] items) =>
        new(TestSessions.Connected(handler, items: items), Fakes.Dataverse(handler), Entry);

    [Fact]
    public void Before_reading_it_names_the_import_and_says_it_is_finding_the_job()
    {
        var vm = Window(new FakeHttpHandler());

        Assert.Equal("Import log — core 1.2.0.0", vm.Title);
        Assert.Equal("core 1.2.0.0", vm.SolutionLabel);
        Assert.Equal("Finding the import job...", vm.StatusLine);
        Assert.NotNull(vm.Session);
        Assert.False(vm.IsRunning);
        Assert.False(vm.SaveXmlCommand.CanExecute(null));
        Assert.False(vm.CopyProblemsCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_log_with_problems_opens_on_just_the_failures_and_warnings()
    {
        var handler = Handler(Jobs());
        var vm = Window(handler);

        await vm.LoadAsync();

        Assert.Equal(3, vm.Rows.Count);
        Assert.True(vm.ProblemsOnly);
        Assert.Equal(["Notify on approval", "Archive"], vm.RowsView.Cast<ImportLogRow>().Select(r => r.Name));
        Assert.Equal(2, vm.ShownCount);
        Assert.Equal("2 of 3 components", vm.CountLabel);
        Assert.True(vm.HasRows);
        Assert.Equal(string.Empty, vm.Status);
        Assert.StartsWith("Read 3 components - 1 failure, 1 warning - in ", vm.StatusLine);
        Assert.Contains(" The import took ", vm.ReadSummary);
        Assert.True(vm.SaveXmlCommand.CanExecute(null));
        Assert.True(vm.CopyProblemsCommand.CanExecute(null));
        Assert.False(vm.IsBusy);
        Assert.Contains("solutionname eq 'core'", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Clearing_problems_only_shows_every_component()
    {
        var vm = Window(Handler(Jobs()));
        await vm.LoadAsync();

        vm.ProblemsOnly = false;

        Assert.Equal(3, vm.ShownCount);
        Assert.Equal("3 components", vm.CountLabel);
    }

    [Fact]
    public async Task Reading_again_replaces_the_rows_and_keeps_what_is_chosen()
    {
        var handler = Handler(Jobs());
        var vm = Window(handler);
        await vm.LoadAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ProblemsOnly = true;
        vm.SelectedRow = null;

        Assert.Empty(raised);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Several_problems_are_counted_and_a_job_without_times_gives_no_duration()
    {
        const string log = """
            <importexportxml>
              <nodes>
                <node name="A"><result result="failure" errortext="x" /></node>
                <node name="B"><result result="failure" errortext="y" /></node>
                <node name="C"><result result="warning" errortext="z" /></node>
                <node name="D"><result result="warning" errortext="w" /></node>
              </nodes>
            </importexportxml>
            """;
        var jobs = JsonSerializer.Serialize(new
        {
            value = new[] { new Dictionary<string, object?> { ["importjobid"] = Job.ToString(), ["progress"] = 100, ["createdon"] = Iso(T0) } }
        });
        var vm = Window(Handler(jobs, log));

        await vm.LoadAsync();

        Assert.StartsWith("Read 4 components - 2 failures, 2 warnings - in ", vm.ReadSummary);
        Assert.DoesNotContain("The import took", vm.ReadSummary);
    }

    [Fact]
    public async Task A_clean_import_lists_everything_and_says_so_when_filtered_to_problems()
    {
        var vm = Window(Handler(Jobs(), CleanLog));

        await vm.LoadAsync();

        Assert.False(vm.ProblemsOnly);
        Assert.Equal("1 component", vm.CountLabel);
        Assert.StartsWith("Read 1 component - 0 failures, 0 warnings - in ", vm.ReadSummary);
        Assert.False(vm.CopyProblemsCommand.CanExecute(null));

        vm.ProblemsOnly = true;

        Assert.False(vm.HasRows);
        Assert.Equal("0 of 1 component", vm.CountLabel);
        Assert.Equal("No failures or warnings", vm.EmptyHeading);
        Assert.Equal("Every component imported cleanly. Clear Failures and warnings only to see them all.", vm.EmptyText);
    }

    [Fact]
    public async Task No_job_kept_for_the_solution_explains_why()
    {
        var vm = Window(Handler("""{"value":[]}"""));

        await vm.LoadAsync();

        Assert.Equal("No import job found for core 1.2.0.0.", vm.Status);
        Assert.Equal("No import log", vm.EmptyHeading);
        Assert.StartsWith("No import job is kept for core - Dataverse removes them after a while", vm.EmptyText);
        Assert.False(vm.SaveXmlCommand.CanExecute(null));
    }

    [Fact]
    public async Task Jobs_that_started_far_from_the_operation_are_not_taken_for_it()
    {
        var vm = Window(Handler(Jobs(startedMinutes: 600)));

        await vm.LoadAsync();

        Assert.Equal("No import job for this solution started near this operation - Dataverse may have removed it.", vm.EmptyText);
    }

    [Fact]
    public async Task A_job_without_a_log_says_so()
    {
        var vm = Window(Handler(Jobs(), log: null));

        await vm.LoadAsync();

        Assert.Equal("The import job has no log.", vm.Status);
        Assert.Equal("The import job has no log - Dataverse keeps none for some operations.", vm.EmptyText);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task A_log_that_cannot_be_read_says_why_and_offers_to_read_again()
    {
        var vm = Window(new FakeHttpHandler().OnError(HttpMethod.Get, "importjobs?", HttpStatusCode.Forbidden, "no read on import jobs"));

        await vm.LoadAsync();

        Assert.StartsWith("Could not read the import log - ", vm.Status);
        Assert.Contains("no read on import jobs", vm.Status);
        Assert.Equal("The import log could not be read - the status bar says why. Read again to try once more.", vm.EmptyText);
        Assert.False(vm.IsRunning);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_empty_log_says_it_lists_no_components()
    {
        var vm = Window(Handler(Jobs(), "<importexportxml />"));

        await vm.LoadAsync();

        Assert.Empty(vm.Rows);
        Assert.Equal("No import log", vm.EmptyHeading);
        Assert.Equal("The import log lists no components.", vm.EmptyText);
        Assert.Equal("0 components", vm.CountLabel);
    }

    [Theory]
    [InlineData(40.0, "40")]
    [InlineData(null, "0")]
    public async Task An_import_still_running_is_followed_until_the_window_closes(double? progress, string percent)
    {
        var vm = Window(Handler(Jobs(progress, completed: false)));

        var load = vm.LoadAsync();
        await TestSessions.Until(() => vm.IsRunning);

        Assert.Equal(progress ?? 0, vm.Progress);
        Assert.True(vm.IsBusy);
        Assert.Equal("Import in progress", vm.EmptyHeading);
        Assert.Equal($"The log appears once the import finishes - {percent}% so far, checked every 5 seconds.", vm.EmptyText);
        Assert.Equal($"Import in progress - {percent}%. Refreshing every 5 seconds.", vm.Status);
        Assert.False(vm.RefreshCommand.CanExecute(null));

        await vm.LoadAsync();
        vm.Stop();
        await load;

        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task While_the_job_is_being_found_the_list_says_so()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "importjobs?", async _ =>
        {
            await gate.Task;
            return FakeHttpHandler.Json("""{"value":[]}""");
        });
        var vm = Window(handler);

        var load = vm.LoadAsync();

        Assert.Equal("Reading the import log", vm.EmptyHeading);
        Assert.Equal("Finding the import job behind this solution history row.", vm.EmptyText);

        gate.SetResult();
        await load;

        Assert.Equal("No import log", vm.EmptyHeading);
    }

    [Fact]
    public async Task A_component_in_the_loaded_list_can_be_opened()
    {
        var handler = Handler(Jobs());
        var session = TestSessions.Connected(handler,
            items: new SolutionComponentItem { Name = "new_invoice", ComponentTypeName = "Table", ComponentType = 1, ObjectId = Invoice });
        var vm = new ImportLogViewModel(session, Fakes.Dataverse(handler), Entry);
        await vm.LoadAsync();
        var invoice = vm.Rows.Single(r => r.Id == Invoice);
        var flow = vm.Rows.Single(r => r.Name == "Notify on approval");
        var archive = vm.Rows.Single(r => r.Id is null);

        Assert.True(vm.OpenCommand.CanExecute(invoice));
        Assert.False(vm.OpenCommand.CanExecute(flow));
        Assert.False(vm.OpenCommand.CanExecute(archive));
        Assert.False(vm.OpenCommand.CanExecute(null));

        vm.SelectedRow = invoice;

        Assert.True(vm.OpenCommand.CanExecute(null));

        TestSessions.DropClient(session);
        vm.OpenCommand.Execute(null);
        vm.OpenCommand.Execute(flow);

        Assert.Same(invoice, vm.SelectedRow);
    }
}
