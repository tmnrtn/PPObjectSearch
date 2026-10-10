using System.Globalization;
using System.Net;
using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The failures window reading a period: the summary band, the source pills, the selected component and opening a failure.</summary>
public class FailuresWindowTests
{
    private const string PluginType = "Contoso.Plugins.Validate";

    private static readonly Guid FlowA = Guid.Parse("c0000000-0000-0000-0000-00000000000a");
    private static readonly Guid FlowB = Guid.Parse("c0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Activation = Guid.Parse("c0000000-0000-0000-0000-0000000000ac");
    private static readonly Guid Definition = Guid.Parse("c0000000-0000-0000-0000-0000000000de");
    private static readonly Guid Step = Guid.Parse("c0000000-0000-0000-0000-0000000000ff");

    private static readonly SolutionInfo Core = new() { SolutionId = Guid.NewGuid(), UniqueName = "core", FriendlyName = "Contoso Core" };

    private static string Ago(double hours) => DateTimeOffset.UtcNow.AddHours(-hours).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static SolutionComponentItem FlowItem() =>
        new() { Name = "Sync orders", ComponentTypeName = "Process", ComponentType = 29, ObjectId = FlowA };

    private static SolutionComponentItem StepItem() =>
        new() { Name = "Validate: Update of account", ComponentTypeName = "Plug-in Step", ComponentType = 92, ObjectId = Step };

    private static SolutionComponentItem TypeItem() =>
        new() { Name = PluginType, ComponentTypeName = "Plug-in Type", ComponentType = 90, ObjectId = Guid.NewGuid() };

    private static FakeHttpHandler Handler(bool tracesFail = false, int flowState = 1, bool stateFails = false, bool stateMissing = false)
    {
        var handler = new FakeHttpHandler();
        if (stateMissing) handler.OnJson(HttpMethod.Get, "workflows?$select=workflowid,statecode", """{"value":[]}""");
        if (tracesFail) handler.OnError(HttpMethod.Get, "plugintracelogs?", HttpStatusCode.Forbidden, "no read on traces");
        if (stateFails) handler.OnError(HttpMethod.Get, "workflows?$select=workflowid,statecode", HttpStatusCode.Forbidden, "no state");

        return handler
            .OnJson(HttpMethod.Get, "flowruns?", $$"""
                {"value":[
                  {"name":"run1","status":"Failed","starttime":"{{Ago(2)}}","errorcode":"ActionFailed","errormessage":"Record 1a2b3c4d-0000-0000-0000-000000000001 not found","_workflow_value":"{{FlowA}}"},
                  {"name":"run2","status":"Succeeded","starttime":"{{Ago(2.5)}}","_workflow_value":"{{FlowA}}"},
                  {"name":"run3","status":"Failed","starttime":"{{Ago(3)}}","errormessage":"Record 1a2b3c4d-0000-0000-0000-000000000002 not found","_workflow_value":"{{FlowA}}"},
                  {"name":"run4","status":"Succeeded","starttime":"{{Ago(3.5)}}","_workflow_value":"{{FlowB}}"}
                ]}
                """)
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,name&", $$"""
                {"value":[{"workflowid":"{{FlowA}}","name":"Sync orders"},{"workflowid":"{{FlowB}}","name":"Notify"}]}
                """)
            .OnJson(HttpMethod.Get, "asyncoperations?", $$"""
                {"value":[{"name":"Escalate case","completedon":"{{Ago(4)}}","friendlymessage":"Owner is disabled","errorcode":-2147220891,
                  "_workflowactivationid_value":"{{Activation}}","_regardingobjectid_value":"x","_regardingobjectid_value@OData.Community.Display.V1.FormattedValue":"CAS-001"}]}
                """)
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,name,_parentworkflowid_value", $$"""
                {"value":[{"workflowid":"{{Activation}}","name":"Escalate case","_parentworkflowid_value":"{{Definition}}"}]}
                """)
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,statecode", $$"""
                {"value":[{"workflowid":"{{FlowA}}","statecode":{{flowState}}},{"workflowid":"{{Definition}}","statecode":0}]}
                """)
            .OnJson(HttpMethod.Get, "organizations?", """{"value":[{"plugintracelogsetting":1}]}""")
            .OnJson(HttpMethod.Get, "plugintracelogs?", $$"""
                {"value":[{"plugintracelogid":"t1","createdon":"{{Ago(5)}}","typename":"{{PluginType}}","messagename":"Update",
                  "primaryentity":"account","pluginstepid":"{{Step}}",
                  "exceptiondetails":"Unhandled exception: \nException type: System.ServiceModel.FaultException\nMessage: Name is required\nDetail: ..."}]}
                """);
    }

    /// <summary>The window on a tab, with progress reports landing as they are made.</summary>
    private static FailuresViewModel Open(EnvironmentSessionViewModel session)
    {
        InlineSynchronizationContext.Install();
        return new FailuresViewModel(session);
    }

    private static FailuresViewModel Window(FakeHttpHandler handler, SolutionInfo? solution = null, params SolutionComponentItem[] items) =>
        Open(TestSessions.Connected(handler, solution: solution, items: items));

    private static async Task<FailuresViewModel> Loaded(FakeHttpHandler handler, SolutionInfo? solution = null, params SolutionComponentItem[] items)
    {
        var vm = Window(handler, solution, items);
        await vm.LoadAsync();
        return vm;
    }

    // ---------------------------------------------------------------- reading

    [Fact]
    public async Task A_read_ranks_the_failing_components_and_fills_the_summary_band()
    {
        var vm = await Loaded(Handler());

        Assert.Equal(["Sync orders", "Escalate case", PluginType], vm.Components.Select(c => c.ComponentName));
        Assert.Equal(4, vm.TotalFailures);
        Assert.Equal("failures in 3 components", vm.TotalLabel);
        Assert.Equal("3 components", vm.ComponentCountLabel);
        Assert.True(vm.HasComponents);
        Assert.Equal((2, 1, 1), (vm.FlowFailureCount, vm.WorkflowFailureCount, vm.PluginFailureCount));
        Assert.NotEmpty(vm.Trend);
        Assert.Contains(vm.Trend, b => b.Height > 0);
        Assert.Equal(string.Empty, vm.Notes);
        Assert.Equal(string.Empty, vm.Status);
        Assert.StartsWith("Read 4 flow runs, 1 system job and 1 trace log in ", vm.StatusLine);
        Assert.False(vm.IsBusy);
        Assert.True(vm.ExportCsvCommand.CanExecute(null));
        Assert.True(vm.ExportMarkdownCommand.CanExecute(null));
        Assert.True(vm.LoadCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_top_component_is_selected_with_its_errors_grouped_and_its_latest_failures_listed()
    {
        var vm = await Loaded(Handler());

        var selected = vm.SelectedComponent!;
        Assert.Equal("Sync orders", selected.ComponentName);
        Assert.True(vm.HasSelection);
        Assert.Equal(1, Assert.Single(vm.SelectedErrors).Components);
        Assert.Equal("Grouped by error · 1", vm.SelectedErrorsHeading);
        Assert.Equal(["run1", "run3"], vm.RecentFailures.Select(f => f.RunName));
        Assert.StartsWith("2 of 3 runs failed · first ", vm.SelectedRunsLabel);
        Assert.True(vm.OpenLatestFailedRunCommand.CanExecute(null));
        Assert.True(vm.OpenComponentDetailsCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(1, true, "On")]
    [InlineData(0, false, "Off")]
    public async Task The_selected_flow_shows_whether_it_is_still_switched_on(int state, bool on, string label)
    {
        var vm = await Loaded(Handler(flowState: state));

        await TestSessions.Until(() => vm.SelectedStateLabel is not null);

        Assert.Equal(label, vm.SelectedStateLabel);
        Assert.Equal(on, vm.IsSelectedOn);
    }

    [Fact]
    public async Task A_classic_workflow_shows_activated_or_draft_and_a_plugin_shows_no_state()
    {
        var vm = await Loaded(Handler());

        vm.SelectedComponent = vm.Components.Single(c => c.Source == FailureSource.ClassicWorkflow);
        await TestSessions.Until(() => vm.SelectedStateLabel is not null);

        Assert.Equal("Draft", vm.SelectedStateLabel);
        Assert.False(vm.IsSelectedOn);
        Assert.StartsWith("1 failure · first ", vm.SelectedRunsLabel);

        vm.SelectedComponent = vm.Components.Single(c => c.Source == FailureSource.Plugin);

        Assert.Null(vm.SelectedStateLabel);
        Assert.Equal("Name is required", Assert.Single(vm.RecentFailures).ErrorMessage);
    }

    [Fact]
    public async Task A_state_that_cannot_be_read_leaves_the_chip_off()
    {
        var handler = Handler(stateFails: true);
        var vm = await Loaded(handler);

        await TestSessions.Until(() => handler.Requests.Exists(r => r.Url.Contains("statecode")));

        Assert.Null(vm.SelectedStateLabel);
        Assert.Equal("Sync orders", vm.SelectedComponent!.ComponentName);
    }

    [Fact]
    public async Task A_component_the_state_read_does_not_mention_gets_no_chip()
    {
        var handler = Handler(stateMissing: true);
        var vm = await Loaded(handler);

        await TestSessions.Until(() => handler.Requests.Exists(r => r.Url.Contains("statecode")));

        Assert.Null(vm.SelectedStateLabel);
        Assert.False(vm.IsSelectedOn);
    }

    [Fact]
    public async Task Clearing_the_selection_empties_the_detail_pane()
    {
        var vm = await Loaded(Handler());

        vm.SelectedComponent = null;

        Assert.False(vm.HasSelection);
        Assert.Empty(vm.SelectedErrors);
        Assert.Empty(vm.RecentFailures);
        Assert.Equal(string.Empty, vm.SelectedRunsLabel);
        Assert.Null(vm.SelectedStateLabel);
        Assert.False(vm.OpenLatestFailedRunCommand.CanExecute(null));
        Assert.False(vm.OpenComponentDetailsCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_source_that_cannot_be_read_is_noted_and_the_rest_are_shown()
    {
        var vm = await Loaded(Handler(tracesFail: true));

        Assert.Contains("the plug-in trace log could not be read", vm.Notes);
        Assert.Equal(0, vm.PluginFailureCount);
        Assert.Equal(3, vm.TotalFailures);
    }

    [Fact]
    public async Task Nothing_failing_says_to_try_a_longer_period()
    {
        var vm = await Loaded(new FakeHttpHandler().OnJson(HttpMethod.Get, "", """{"value":[]}"""));

        Assert.Equal(0, vm.TotalFailures);
        Assert.False(vm.HasComponents);
        Assert.Null(vm.SelectedComponent);
        Assert.Equal("No failures", vm.EmptyHeading);
        Assert.Equal("Nothing failed in this period. Try a longer period, or include more sources.", vm.EmptyText);
        Assert.Equal("failures in 0 components", vm.TotalLabel);
        Assert.False(vm.ExportCsvCommand.CanExecute(null));
        Assert.True(vm.ExportMarkdownCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_tab_that_is_not_connected_reads_nothing()
    {
        var vm = Open(TestSessions.Disconnected());

        await vm.LoadAsync();

        Assert.Equal("Failures — contoso", vm.Title);
        Assert.NotNull(vm.Session);
        Assert.Equal(0, vm.TotalFailures);
        Assert.Equal(string.Empty, vm.StatusLine);
        Assert.False(vm.ExportMarkdownCommand.CanExecute(null));
        Assert.Equal((0, 0, 0), (vm.FlowFailureCount, vm.WorkflowFailureCount, vm.PluginFailureCount));
    }

    // ---------------------------------------------------------------- sources

    [Fact]
    public async Task Turning_a_source_off_hides_its_failures_without_reading_again()
    {
        var handler = Handler();
        var vm = await Loaded(handler);
        var reads = handler.Requests.Count(r => !r.Url.Contains("statecode"));

        vm.IncludeFlows = false;

        Assert.Equal(["Escalate case", PluginType], vm.Components.Select(c => c.ComponentName));
        Assert.Equal(2, vm.TotalFailures);
        Assert.Equal(2, vm.FlowFailureCount);
        Assert.Equal("Escalate case", vm.SelectedComponent!.ComponentName);

        vm.IncludePlugins = false;

        Assert.Equal("Escalate case", Assert.Single(vm.Components).ComponentName);
        Assert.Equal("failure in 1 component", vm.TotalLabel);
        Assert.Equal("1 component", vm.ComponentCountLabel);

        vm.IncludeFlows = true;

        Assert.Equal(2, vm.Components.Count);
        Assert.Equal(reads, handler.Requests.Count(r => !r.Url.Contains("statecode")));
    }

    [Fact]
    public async Task With_every_source_off_the_list_says_to_include_one()
    {
        var vm = await Loaded(Handler());

        vm.IncludeFlows = false;
        vm.IncludeClassicWorkflows = false;
        vm.IncludePlugins = false;

        Assert.Empty(vm.Components);
        Assert.Equal("No sources included", vm.EmptyHeading);
        Assert.Equal("Include flow runs, workflow jobs or plug-ins above.", vm.EmptyText);
        Assert.False(vm.ExportCsvCommand.CanExecute(null));
    }

    [Fact]
    public void A_source_pill_before_any_read_changes_only_the_empty_text()
    {
        var vm = Window(new FakeHttpHandler());

        vm.IncludeClassicWorkflows = false;
        vm.IncludeClassicWorkflows = false;

        Assert.Empty(vm.Components);
        Assert.Equal("No failures", vm.EmptyHeading);
    }

    // ---------------------------------------------------------------- solution

    [Fact]
    public async Task Narrowing_to_the_solution_on_screen_reads_again_for_its_components_only()
    {
        var handler = Handler();
        var vm = await Loaded(handler, Core, FlowItem(), StepItem(), TypeItem());

        Assert.True(vm.HasSolution);
        Assert.Equal("Only Contoso Core", vm.SolutionLabel);

        vm.SolutionOnly = true;
        await TestSessions.Until(() => handler.Requests.Count(r => r.Url.Contains("flowruns?")) == 2 && !vm.IsBusy);

        var flowRuns = handler.Requests.Last(r => r.Url.Contains("flowruns?")).Url;
        Assert.Contains($"_workflow_value eq {FlowA}", flowRuns);
        Assert.Equal("Sync orders", vm.Components[0].ComponentName);
    }

    [Fact]
    public async Task Nothing_failing_in_the_solution_names_it()
    {
        var vm = Window(new FakeHttpHandler().OnJson(HttpMethod.Get, "", """{"value":[]}"""), Core, FlowItem());
        vm.SolutionOnly = true;

        await vm.LoadAsync();

        Assert.Equal("Nothing in Contoso Core failed in this period. Try a longer period, or the whole environment.", vm.EmptyText);
    }

    [Fact]
    public void Narrowing_before_any_read_waits_for_the_first_one()
    {
        var handler = new FakeHttpHandler();
        var vm = Window(handler, Core);

        vm.SolutionOnly = true;

        Assert.True(vm.SolutionOnly);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void The_default_solution_cannot_be_narrowed_to()
    {
        var vm = Window(new FakeHttpHandler(), new SolutionInfo { SolutionId = Guid.NewGuid(), UniqueName = "Default", FriendlyName = "Default Solution" });

        Assert.False(vm.HasSolution);
        Assert.Equal(string.Empty, vm.SolutionLabel);
    }

    [Fact]
    public void Choosing_a_solution_in_the_tab_updates_the_narrowing_until_the_window_lets_go()
    {
        var session = TestSessions.Disconnected();
        var vm = Open(session);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        session.SelectedSolution = Core;

        Assert.Contains(nameof(FailuresViewModel.HasSolution), raised);
        Assert.Contains(nameof(FailuresViewModel.SolutionLabel), raised);
        Assert.True(vm.HasSolution);

        raised.Clear();
        session.SearchText = "anything";
        Assert.Empty(raised);

        vm.Detach();
        session.SelectedSolution = new SolutionInfo { SolutionId = Guid.NewGuid(), UniqueName = "other", FriendlyName = "Other" };

        Assert.Empty(raised);
    }

    // ---------------------------------------------------------------- period and progress

    [Fact]
    public async Task Choosing_a_fixed_period_reads_it_and_custom_waits_for_dates()
    {
        var handler = Handler();
        var vm = Window(handler);

        vm.Period = "Last 24 hours";
        await TestSessions.Until(() => !vm.IsBusy && vm.TotalFailures > 0);

        Assert.False(vm.IsCustom);
        var flowRuns = handler.Requests.Count(r => r.Url.Contains("flowruns?"));
        Assert.Equal(1, flowRuns);

        vm.Period = "Custom";
        vm.CustomFrom = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified);
        vm.CustomTo = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.True(vm.IsCustom);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified), vm.CustomFrom);
        Assert.Equal(new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Unspecified), vm.CustomTo);
        Assert.Equal(flowRuns, handler.Requests.Count(r => r.Url.Contains("flowruns?")));

        vm.Period = "Custom";
        Assert.Equal(5, vm.Periods.Count);
    }

    [Fact]
    public async Task While_reading_the_window_is_busy_and_stopping_ends_the_read()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "flowruns?", async _ =>
        {
            await gate.Task;
            return FakeHttpHandler.Json("""{"value":[]}""");
        });
        var vm = Window(handler);

        var load = vm.LoadAsync();

        Assert.True(vm.IsBusy);
        Assert.False(vm.LoadCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.StartsWith("Reading ", vm.StatusLine);

        vm.CancelCommand.Execute(null);
        gate.SetResult();
        await load;

        Assert.Equal("Stopped.", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, vm.TotalFailures);
    }

    [Fact]
    public async Task A_newer_read_replaces_one_still_running()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var gated = new FakeHttpHandler().OnAsync(HttpMethod.Get, "flowruns?", async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) await gate.Task;
            return FakeHttpHandler.Json("""{"value":[]}""");
        }).OnJson(HttpMethod.Get, "", """{"value":[]}""");
        var vm = Window(gated);

        var first = vm.LoadAsync();
        await vm.LoadAsync();

        Assert.False(vm.IsBusy);

        gate.SetResult();
        await first;

        Assert.False(vm.IsBusy);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Closing_the_window_stops_a_read_in_progress()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "flowruns?", async _ =>
        {
            await gate.Task;
            return FakeHttpHandler.Json("""{"value":[]}""");
        });
        var vm = Window(handler);
        var load = vm.LoadAsync();

        vm.Detach();
        gate.SetResult();
        await load;

        Assert.Equal("Stopped.", vm.Status);
        Assert.Equal(0, vm.TotalFailures);
    }

    [Fact]
    public async Task Disposing_the_window_stops_a_read_in_progress_and_lets_go_of_the_tab()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "flowruns?", async _ =>
        {
            await gate.Task;
            return FakeHttpHandler.Json($$"""
                {"value":[{"name":"run1","status":"Failed","starttime":"{{Ago(2)}}","errormessage":"Boom","_workflow_value":"{{FlowA}}"}]}
                """);
        }).OnJson(HttpMethod.Get, "", """{"value":[]}""");
        var session = TestSessions.Connected(handler);
        var vm = Open(session);
        var load = vm.LoadAsync();

        vm.Dispose();
        gate.SetResult();
        await load;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        session.UseLoadedSolution(Core, []);

        Assert.Equal("Stopped.", vm.Status);
        Assert.Equal(0, vm.TotalFailures);
        Assert.Empty(vm.Components);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task Narrowing_without_a_solution_on_screen_reads_the_whole_environment()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "", """{"value":[]}""");
        var vm = Window(handler);
        vm.SolutionOnly = true;

        await vm.LoadAsync();

        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("_workflow_value eq"));
        Assert.Equal("Nothing failed in this period. Try a longer period, or include more sources.", vm.EmptyText);
    }

    [Fact]
    public void Choosing_what_is_already_chosen_changes_nothing()
    {
        var handler = new FakeHttpHandler();
        var vm = Window(handler);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IncludeFlows = true;
        vm.IncludeClassicWorkflows = true;
        vm.IncludePlugins = true;
        vm.SolutionOnly = false;
        vm.Period = "Last 7 days";
        vm.SelectedComponent = null;
        vm.CancelCommand.Execute(null);

        Assert.Empty(raised);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void A_custom_period_with_only_a_start_runs_to_now_and_one_starting_tomorrow_is_turned_round()
    {
        var now = new DateTimeOffset(2026, 10, 4, 14, 30, 0, TimeSpan.Zero);

        var (from, to) = FailuresViewModel.Range("Custom", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified), null, now);
        var (laterFrom, laterTo) = FailuresViewModel.Range("Custom", new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Unspecified), null, now);

        Assert.Equal((new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), now), (from, to));
        Assert.Equal((now, new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero)), (laterFrom, laterTo));
    }

    // ---------------------------------------------------------------- opening

    [Fact]
    public async Task A_failure_whose_component_is_not_loaded_says_how_to_reach_it()
    {
        var vm = await Loaded(Handler());

        vm.OpenEventCommand.Execute(vm.RecentFailures[0]);

        Assert.Equal("Sync orders is not in the loaded list, so its details cannot be opened here. " +
                     "Load the default solution to reach every component.", vm.Status);
    }

    [Fact]
    public async Task A_loaded_flow_opens_without_a_complaint()
    {
        var session = TestSessions.Connected(Handler(), items: FlowItem());
        var vm = Open(session);
        await vm.LoadAsync();
        TestSessions.DropClient(session);

        vm.OpenLatestFailedRunCommand.Execute(null);
        vm.OpenComponentDetailsCommand.Execute(null);

        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public async Task A_plugin_failure_is_found_by_its_step_or_else_its_type()
    {
        var byStep = TestSessions.Connected(Handler(), items: StepItem());
        var byType = TestSessions.Connected(Handler(), items: TypeItem());
        var neither = TestSessions.Connected(Handler(), items: FlowItem());
        var results = new List<string>();

        foreach (var session in new[] { byStep, byType, neither })
        {
            var vm = Open(session);
            await vm.LoadAsync();
            TestSessions.DropClient(session);
            vm.SelectedComponent = vm.Components.Single(c => c.Source == FailureSource.Plugin);

            vm.OpenComponentDetailsCommand.Execute(null);
            results.Add(vm.Status);
        }

        Assert.Equal(string.Empty, results[0]);
        Assert.Equal(string.Empty, results[1]);
        Assert.StartsWith($"{PluginType} is not in the loaded list", results[2]);
    }

    [Fact]
    public async Task A_classic_workflow_whose_definition_is_not_loaded_cannot_be_opened()
    {
        var vm = await Loaded(Handler(), null, FlowItem());
        vm.SelectedComponent = vm.Components.Single(c => c.Source == FailureSource.ClassicWorkflow);

        vm.OpenComponentDetailsCommand.Execute(null);

        Assert.StartsWith("Escalate case is not in the loaded list", vm.Status);
    }

    [Fact]
    public void Only_a_failure_can_be_opened_and_nothing_selected_opens_nothing()
    {
        var vm = Window(new FakeHttpHandler());

        Assert.False(vm.OpenEventCommand.CanExecute("not a failure"));
        Assert.False(vm.OpenEventCommand.CanExecute(null));

        vm.OpenEventCommand.Execute(null);
        vm.OpenComponentDetailsCommand.Execute(null);
        vm.OpenLatestFailedRunCommand.Execute(null);

        Assert.Equal(string.Empty, vm.Status);
    }

    // ---------------------------------------------------------------- wording

    [Fact]
    public void The_read_summary_counts_each_source_in_the_singular_or_plural()
    {
        var one = new FailureData { FlowRunsRead = 1, SystemJobsRead = 1, TraceLogsRead = 1 };
        var many = new FailureData { FlowRunsRead = 2, SystemJobsRead = 0, TraceLogsRead = 3 };

        Assert.StartsWith("Read 1 flow run, 1 system job and 1 trace log in ", FailuresViewModel.Describe(one, TimeSpan.FromSeconds(2)));
        Assert.StartsWith("Read 2 flow runs, 0 system jobs and 3 trace logs in ", FailuresViewModel.Describe(many, TimeSpan.FromSeconds(2)));
    }
}
