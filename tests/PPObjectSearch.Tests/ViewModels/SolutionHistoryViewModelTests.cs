using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The Solution history window: loading, the segmented filter with its counts, search and export.</summary>
public class SolutionHistoryViewModelTests
{
    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";
    private const string Route = "msdyn_solutionhistories?";

    private static Dictionary<string, object?> Row(string name, int operation, string operationLabel, int status, bool? result,
        string? start, string? errorCode = null, string? end = null) => new()
    {
        ["msdyn_solutionhistoryid"] = Guid.NewGuid(),
        ["msdyn_name"] = name,
        ["msdyn_solutionversion"] = "1.0.0.0",
        ["msdyn_operation"] = operation,
        ["msdyn_operation" + Formatted] = operationLabel,
        ["msdyn_ismanaged"] = true,
        ["msdyn_publishername"] = "Contoso",
        ["msdyn_starttime"] = start,
        ["msdyn_endtime"] = end,
        ["msdyn_totaltime"] = 12,
        ["msdyn_status"] = status,
        ["msdyn_result"] = result,
        ["msdyn_errorcode"] = errorCode
    };

    private static string Page(IEnumerable<Dictionary<string, object?>> rows) => JsonSerializer.Serialize(new { value = rows });

    private static string History() => Page(
    [
        Row("Contoso Core", 0, "Import", 1, true, "2026-03-01T12:00:00Z"),
        Row("Contoso Portal", 0, "Import", 1, false, "2026-02-01T12:00:00Z", errorCode: "0x80048033"),
        Row("Contoso Legacy", 1, "Uninstall", 1, true, "2026-01-15T12:00:00Z", end: "2026-01-15T12:01:00Z"),
        Row("Contoso Core", 2, "Export", 1, true, "2026-01-10T12:00:00Z"),
        Row("Contoso Core", 3, "Publish", 0, null, null)
    ]);

    private static SolutionHistoryViewModel History(FakeHttpHandler handler) =>
        new(TestSessions.Disconnected(), Fakes.Dataverse(handler));

    private static async Task<SolutionHistoryViewModel> Loaded(string page)
    {
        var history = History(new FakeHttpHandler().OnJson(HttpMethod.Get, Route, page));
        await history.LoadAsync();
        return history;
    }

    private static string[] Shown(SolutionHistoryViewModel history) =>
        history.EntriesView.Cast<SolutionHistoryEntry>().Select(e => $"{e.Operation} {e.SolutionName}").ToArray();

    [Fact]
    public async Task Loading_lists_every_operation_newest_first_with_a_summary()
    {
        var history = await Loaded(History());

        Assert.Equal("Solution history — contoso", history.Title);
        Assert.Equal(
            ["Import Contoso Core", "Import Contoso Portal", "Uninstall Contoso Legacy", "Export Contoso Core", "Publish Contoso Core"],
            Shown(history));
        Assert.Same(history.Entries[0], history.SelectedEntry);
        Assert.Equal((5, 2, 1, 1, 1),
            (history.CountAll, history.CountImports, history.CountUninstalls, history.CountExports, history.CountFailed));
        Assert.Equal("5 of 5", history.Summary);

        var oldest = history.Entries.Where(e => e.StartTime is not null).Min(e => e.StartTime!.Value);
        Assert.Equal($"5 operation(s) back to {oldest:yyyy-MM-dd}. 1 failed.", history.Status);
        Assert.False(history.IsBusy);
        Assert.True(history.ExportCommand.CanExecute(null));
        Assert.True(history.ImportLogCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(SolutionHistoryFilter.Imports, "Import Contoso Core,Import Contoso Portal")]
    [InlineData(SolutionHistoryFilter.Uninstalls, "Uninstall Contoso Legacy")]
    [InlineData(SolutionHistoryFilter.Exports, "Export Contoso Core")]
    [InlineData(SolutionHistoryFilter.Failed, "Import Contoso Portal")]
    public async Task The_segmented_filter_narrows_the_list(SolutionHistoryFilter filter, string expected)
    {
        var history = await Loaded(History());

        history.StatusFilter = filter;

        Assert.Equal(expected.Split(','), Shown(history));
        Assert.Equal($"{expected.Split(',').Length} of 5", history.Summary);
        Assert.Equal(5, history.CountAll);
    }

    [Fact]
    public async Task Search_terms_must_all_match_and_the_counts_follow_them()
    {
        var history = await Loaded(History());

        history.SearchText = "core IMPORT";

        Assert.Equal(["Import Contoso Core"], Shown(history));
        Assert.Equal((1, 1, 0, 0, 0),
            (history.CountAll, history.CountImports, history.CountUninstalls, history.CountExports, history.CountFailed));

        history.SearchText = "0x80048033";
        Assert.Equal(["Import Contoso Portal"], Shown(history));
        Assert.Equal(1, history.CountFailed);

        history.StatusFilter = SolutionHistoryFilter.Exports;
        Assert.Empty(Shown(history));
        Assert.False(history.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_asynchronous_export_counts_as_an_export()
    {
        var history = await Loaded(Page([Row("Contoso Core", 10, "Export (async)", 1, true, "2026-03-01T12:00:00Z")]));

        history.StatusFilter = SolutionHistoryFilter.Exports;

        Assert.Equal(1, history.CountExports);
        Assert.Equal(["Export (async) Contoso Core"], Shown(history));
    }

    [Fact]
    public async Task Setting_the_same_filter_or_search_again_changes_nothing()
    {
        var history = await Loaded(History());
        history.SearchText = "core";
        var changed = new List<string?>();
        history.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        history.SearchText = "core";
        history.StatusFilter = SolutionHistoryFilter.All;

        Assert.Empty(changed);
        Assert.False(history.EntriesView.Filter("not an entry"));
    }

    [Fact]
    public async Task An_empty_history_says_nothing_was_recorded()
    {
        var history = await Loaded("""{"value":[]}""");

        Assert.Equal("No solution operations recorded.", history.Status);
        Assert.Null(history.SelectedEntry);
        Assert.False(history.ImportLogCommand.CanExecute(null));
        Assert.False(history.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_full_page_is_marked_as_the_most_recent_and_undated_rows_give_no_date()
    {
        var rows = Enumerable.Range(0, DataverseClient.MaxSolutionHistory)
            .Select(i => Row($"Solution {i}", 2, "Export", 1, true, start: null));

        var history = await Loaded(Page(rows));

        Assert.Equal($"{DataverseClient.MaxSolutionHistory:N0} operation(s) (the most recent).", history.Status);
    }

    [Fact]
    public async Task A_failed_read_is_reported_and_refresh_reads_again()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, Route, HttpStatusCode.Forbidden, "No privilege");
        var history = History(handler);
        Assert.Equal("Loading solution history...", history.Status);

        await history.RefreshCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not read the solution history - ", history.Status);
        Assert.Contains("No privilege", history.Status);
        Assert.False(history.IsBusy);
    }

    [Fact]
    public async Task Choosing_an_entry_enables_its_import_log()
    {
        var history = await Loaded(History());
        var raised = 0;
        history.ImportLogCommand.CanExecuteChanged += (_, _) => raised++;

        history.SelectedEntry = null;
        Assert.False(history.ImportLogCommand.CanExecute(null));

        history.SelectedEntry = history.Entries[2];
        Assert.True(history.ImportLogCommand.CanExecute(null));
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task Export_writes_the_shown_operations_to_the_chosen_file()
    {
        var history = await Loaded(History());
        history.StatusFilter = SolutionHistoryFilter.Imports;
        var path = Path.Combine(Path.GetTempPath(), $"ppos-history-{Guid.NewGuid():N}.csv");
        string? suggested = null;
        history.ChooseExportFile = name =>
        {
            suggested = name;
            return path;
        };

        try
        {
            history.ExportCommand.Execute(null);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal("solution-history-contoso.csv", suggested);
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("Result,Solution,Version,Operation", lines[0]);
            Assert.StartsWith("Succeeded,Contoso Core,1.0.0.0,Import,", lines[1]);
            Assert.StartsWith("Failed,Contoso Portal,", lines[2]);
            Assert.Contains("0x80048033", lines[2]);
            Assert.Equal($"Exported 2 operation(s) to {path}.", history.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Exported_times_are_written_the_same_way_in_every_culture()
    {
        var history = await Loaded(History());
        history.StatusFilter = SolutionHistoryFilter.Uninstalls;
        var path = Path.Combine(Path.GetTempPath(), $"ppos-history-{Guid.NewGuid():N}.csv");
        history.ChooseExportFile = _ => path;
        var entry = history.EntriesView.Cast<SolutionHistoryEntry>().Single();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        try
        {
            history.ExportCommand.Execute(null);

            Assert.Equal(
                $"Succeeded,Contoso Legacy,1.0.0.0,Uninstall,,Managed,Contoso,{entry.StartTime!.Value.ToString("yyyy-MM-dd HH:mm:ss", invariant)}," +
                $"{entry.EndTime!.Value.ToString("yyyy-MM-dd HH:mm:ss", invariant)},12,,",
                (await File.ReadAllLinesAsync(path))[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancelling_the_export_writes_nothing()
    {
        var history = await Loaded(History());
        var before = history.Status;
        history.ChooseExportFile = _ => null;

        history.ExportCommand.Execute(null);

        Assert.Equal(before, history.Status);
    }
}
