using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The recent changes window: reading the timeline, filtering it, and what it says when empty.</summary>
public class ChangesViewModelTests
{
    private static readonly Guid DefaultId = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid Script = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Flow = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid Table = Guid.Parse("a0000000-0000-0000-0000-000000000003");

    private static readonly SolutionInfo Default = new() { SolutionId = DefaultId, UniqueName = "Default", FriendlyName = "Default Solution" };

    private static string Ago(double hours) => DateTimeOffset.UtcNow.AddHours(-hours).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Component(int type, string name, Guid id, double hoursAgo, bool managed) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["msdyn_componenttype"] = type, ["msdyn_name"] = name, ["msdyn_objectid"] = id.ToString(),
        ["msdyn_modifiedon"] = Ago(hoursAgo), ["msdyn_ismanaged"] = managed
    });

    private static string Changed() => $$"""
        {"value":[
          {{Component(61, "new_script.js", Script, 1, false)}},
          {{Component(29, "Notify on approval", Flow, 3, true)}},
          {{Component(1, "new_invoice", Table, 5, false)}}
        ]}
        """;

    private static string History() => $$"""
        {"value":[{"msdyn_solutionhistoryid":"{{Guid.NewGuid()}}","msdyn_name":"Core","msdyn_solutionversion":"1.2.0.0",
          "msdyn_operation":0,"msdyn_operation@OData.Community.Display.V1.FormattedValue":"Import",
          "msdyn_starttime":"{{Ago(2)}}","msdyn_status":1,"msdyn_result":true,"msdyn_ismanaged":true}]}
        """;

    private static FakeHttpHandler Handler(bool historyFails = false)
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", Changed())
            .OnJson(HttpMethod.Get, "webresourceset?", $$"""
                {"value":[{"webresourceid":"{{Script}}","_modifiedby_value":"u1","_modifiedby_value@OData.Community.Display.V1.FormattedValue":"Alex"}]}
                """)
            .OnJson(HttpMethod.Get, "workflows?", """{"value":[]}""");

        return historyFails
            ? handler.OnError(HttpMethod.Get, "msdyn_solutionhistories", HttpStatusCode.Forbidden, "no history for you")
            : handler.OnJson(HttpMethod.Get, "msdyn_solutionhistories", History());
    }

    private static ChangesViewModel Window(FakeHttpHandler handler, bool withDefault = true) =>
        new(TestSessions.Connected(handler, solution: withDefault ? Default : null));

    [Fact]
    public async Task Component_changes_and_solution_operations_are_read_onto_one_timeline()
    {
        var handler = Handler();
        var vm = Window(handler);

        await vm.LoadAsync();

        Assert.Equal(["new_script.js", "Core", "Notify on approval", "new_invoice"], vm.Entries.Select(e => e.What));
        Assert.Equal("Alex", vm.Entries[0].By);
        Assert.Equal(["All types", "Import", "Process", "Table", "Web Resource"], vm.Types);
        Assert.Equal("All types", vm.SelectedType);
        Assert.Equal("4 changes", vm.CountLabel);
        Assert.True(vm.HasEntries);
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.Warnings);
        Assert.Equal(string.Empty, vm.Status);
        Assert.StartsWith("Read 3 component changes and 1 solution operation in the last 7 days in ", vm.StatusLine);
        Assert.True(vm.ExportCsvCommand.CanExecute(null));
        Assert.True(vm.ExportMarkdownCommand.CanExecute(null));
        Assert.Contains(handler.Requests, r => r.Url.Contains($"msdyn_solutionid eq {DefaultId}"));
    }

    [Fact]
    public async Task The_type_and_unmanaged_filters_narrow_what_is_shown()
    {
        var vm = Window(Handler());
        await vm.LoadAsync();

        vm.SelectedType = "Web Resource";

        Assert.Equal("1 change", vm.CountLabel);
        Assert.Equal("new_script.js", Assert.Single(vm.EntriesView.Cast<ChangeEntry>()).What);

        vm.SelectedType = "All types";
        vm.UnmanagedOnly = true;

        Assert.Equal(["new_script.js", "new_invoice"], vm.EntriesView.Cast<ChangeEntry>().Select(e => e.What));

        vm.SelectedType = "Process";

        Assert.False(vm.HasEntries);
        Assert.Equal("Nothing matches", vm.EmptyHeading);
        Assert.Equal("No change matches the type and Unmanaged only. Choose All types, or clear Unmanaged only.", vm.EmptyText);

        vm.UnmanagedOnly = false;
        vm.SelectedType = "Import";

        Assert.Equal("Core", Assert.Single(vm.EntriesView.Cast<ChangeEntry>()).What);
    }

    [Fact]
    public async Task A_filter_that_lets_nothing_through_says_to_choose_all_types()
    {
        var vm = Window(Handler());
        await vm.LoadAsync();

        vm.SelectedType = "Nothing like this";

        Assert.Equal("0 changes", vm.CountLabel);
        Assert.Equal("No change matches the type. Choose All types.", vm.EmptyText);
    }

    [Fact]
    public async Task The_chosen_type_is_kept_across_a_refresh_when_it_is_still_there()
    {
        var vm = Window(Handler());
        await vm.LoadAsync();
        vm.SelectedType = "Process";

        await vm.LoadAsync();

        Assert.Equal("Process", vm.SelectedType);
        Assert.Equal("1 change", vm.CountLabel);
    }

    [Fact]
    public async Task A_type_that_is_gone_after_a_refresh_goes_back_to_all_types()
    {
        var vm = Window(Handler());
        await vm.LoadAsync();
        vm.SelectedType = "Nothing like this";

        await vm.LoadAsync();

        Assert.Equal("All types", vm.SelectedType);
    }

    [Fact]
    public async Task Solution_history_that_cannot_be_read_leaves_the_component_changes_and_says_so()
    {
        var vm = Window(Handler(historyFails: true));

        await vm.LoadAsync();

        Assert.Equal(3, vm.Entries.Count);
        Assert.All(vm.Entries, e => Assert.False(e.IsSolutionOperation));
        Assert.StartsWith("Solution history could not be read, so imports, upgrades and uninstalls are missing", vm.Warnings);
        Assert.Contains("no history for you", vm.Warnings);
    }

    [Fact]
    public async Task A_read_cut_short_warns_that_older_changes_are_missing()
    {
        var rows = string.Join(",", Enumerable.Range(0, DataverseClient.MaxRecentChanges)
            .Select(i => Component(1, $"t{i}", Guid.NewGuid(), 1, false)));
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries",
                $$"""{"value":[{{rows}}],"@odata.nextLink":"{{Fakes.ApiRoot}}msdyn_solutioncomponentsummaries?page=2"}""")
            .OnJson(HttpMethod.Get, "msdyn_solutionhistories", """{"value":[]}""");
        var vm = Window(handler);

        await vm.LoadAsync();

        Assert.Equal(DataverseClient.MaxRecentChanges, vm.Entries.Count);
        Assert.StartsWith("Only the newest 5,000 changed components were read", vm.Warnings);
    }

    [Fact]
    public async Task A_single_change_with_no_type_chosen_reads_in_the_singular_under_all_types()
    {
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", $$"""{"value":[{{Component(1, "new_invoice", Table, 5, false)}}]}""")
            .OnJson(HttpMethod.Get, "msdyn_solutionhistories", """{"value":[]}""");
        var vm = Window(handler);
        vm.SelectedType = null;

        await vm.LoadAsync();

        Assert.Equal("All types", vm.SelectedType);
        Assert.StartsWith("Read 1 component change and 0 solution operations", vm.ReadSummary);
        Assert.Equal("1 change", vm.CountLabel);
    }

    [Fact]
    public void Choosing_what_is_already_chosen_changes_nothing()
    {
        var handler = new FakeHttpHandler();
        var vm = Window(handler);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.Range = "Last 7 days";
        vm.SelectedType = "All types";
        vm.UnmanagedOnly = false;
        vm.SelectedEntry = null;
        vm.CancelCommand.Execute(null);

        Assert.Empty(changes);
        Assert.Empty(handler.Requests);
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changes_that_cannot_be_read_are_reported_in_the_status_bar()
    {
        var vm = Window(new FakeHttpHandler()
            .OnError(HttpMethod.Get, "msdyn_solutioncomponentsummaries", HttpStatusCode.Forbidden, "not allowed"));

        await vm.LoadAsync();

        Assert.StartsWith("Could not read the changes - ", vm.Status);
        Assert.Contains("not allowed", vm.StatusLine);
        Assert.Empty(vm.Entries);
        Assert.False(vm.IsBusy);
        Assert.False(vm.ExportCsvCommand.CanExecute(null));
    }

    [Fact]
    public async Task Nothing_changed_says_to_try_a_longer_range()
    {
        var vm = Window(new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", """{"value":[]}""")
            .OnJson(HttpMethod.Get, "msdyn_solutionhistories", """{"value":[]}"""));

        await vm.LoadAsync();

        Assert.Equal("No changes", vm.EmptyHeading);
        Assert.Equal("Nothing was changed, imported or uninstalled in the last 7 days. Try a longer time range.", vm.EmptyText);
        Assert.StartsWith("Read 0 component changes and 0 solution operations", vm.ReadSummary);
    }

    [Fact]
    public async Task Changing_the_range_reads_again_from_further_back()
    {
        var handler = Handler();
        var vm = Window(handler);

        vm.Range = "Last 30 days";
        await TestSessions.Until(() => !vm.IsBusy && vm.Entries.Count > 0);

        var since = DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Contains(handler.Requests, r => r.Url.Contains($"msdyn_modifiedon ge {since}"));
        Assert.Contains("in the last 30 days", vm.ReadSummary);
    }

    [Fact]
    public async Task While_reading_the_list_says_what_is_being_read_and_stopping_ends_it()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "msdyn_solutioncomponentsummaries", async _ =>
        {
            await gate.Task;
            return FakeHttpHandler.Json(Changed());
        });
        var vm = Window(handler);

        var load = vm.LoadAsync();

        Assert.True(vm.IsBusy);
        Assert.Equal("Reading changes", vm.EmptyHeading);
        Assert.Equal("Components changed and solutions imported in the last 7 days are being read.", vm.EmptyText);
        Assert.StartsWith("Reading components changed since ", vm.StatusLine);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        gate.SetResult();
        await load;

        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Entries);
        Assert.Equal("Stopped.", vm.Status);
    }

    [Fact]
    public async Task Without_the_default_solution_the_changes_cannot_be_read()
    {
        var handler = new FakeHttpHandler();
        var vm = Window(handler, withDefault: false);

        await vm.LoadAsync();
        await vm.LoadAsync();

        Assert.Equal("The default solution is not listed in this tab, so the environment's changes cannot be read.", vm.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_tab_that_is_not_connected_reads_nothing()
    {
        var vm = new ChangesViewModel(TestSessions.Disconnected());

        await vm.LoadAsync();

        Assert.Equal(string.Empty, vm.StatusLine);
        Assert.Equal("Recent changes — contoso", vm.Title);
        Assert.NotNull(vm.Session);
        Assert.Equal(["Last 24 hours", "Last 7 days", "Last 30 days"], vm.Ranges);
    }

    [Fact]
    public async Task Only_a_component_change_can_be_opened()
    {
        var session = TestSessions.Connected(Handler(), solution: Default);
        var vm = new ChangesViewModel(session);
        await vm.LoadAsync();
        var component = vm.Entries.First(e => !e.IsSolutionOperation);
        var operation = vm.Entries.First(e => e.IsSolutionOperation);

        Assert.False(vm.OpenCommand.CanExecute(null));
        vm.OpenCommand.Execute(null);
        Assert.True(vm.OpenCommand.CanExecute(component));
        Assert.False(vm.OpenCommand.CanExecute(operation));

        vm.SelectedEntry = component;

        Assert.True(vm.OpenCommand.CanExecute(null));

        TestSessions.DropClient(session);
        vm.OpenCommand.Execute(null);
        vm.OpenCommand.Execute(operation);

        Assert.Same(component, vm.SelectedEntry);
    }

    [Theory]
    [InlineData("Last 24 hours", 1)]
    [InlineData("Last 7 days", 7)]
    [InlineData("Last 30 days", 30)]
    [InlineData("Anything else", 7)]
    public void Each_range_covers_its_days(string range, int days)
    {
        Assert.Equal(TimeSpan.FromDays(days), ChangesViewModel.Span(range));
    }
}
