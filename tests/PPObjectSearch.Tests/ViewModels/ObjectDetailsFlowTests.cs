using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ViewModels.DetailsHarness;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>A cloud flow in the details window: its design, definition, runs, connections and on/off state.</summary>
public class ObjectDetailsFlowTests
{
    private static readonly Guid ChildFlow = Guid.Parse("c1d00000-0000-0000-0000-000000000001");

    private static readonly string FlowId = FlowUniqueId.ToString();

    private static readonly string Definition = """
        {"properties":{
          "connectionReferences":{
            "shared_office365":{"connection":{"connectionReferenceLogicalName":"new_outlook"}},
            "shared_teams":{"connection":{"connectionReferenceLogicalName":"new_teams"}}},
          "definition":{
            "triggers":{"manual":{"type":"Request","kind":"Button"}},
            "actions":{
              "Send":{"type":"OpenApiConnection","inputs":{"host":{"apiId":"/x/shared_office365","operationId":"SendEmailV2"}}},
              "Escalate":{"type":"Workflow","runAfter":{"Send":["Succeeded"]},"inputs":{"host":{"workflowReferenceName":"CHILD"}}}}}}}
        """.Replace("CHILD", ChildFlow.ToString());

    private const string NoReferences = """
        {"definition":{"triggers":{"manual":{"type":"Request"}},"actions":{"Do":{"type":"Compose"}}}}
        """;

    private static readonly string DataverseRuns = $$"""
        {"value":[
          {"name":"run-1","status":"Succeeded","starttime":"2026-10-01T08:00:00Z","workflowid":"{{FlowId}}"},
          {"name":"run-2","status":"Succeeded","starttime":"2026-10-01T07:00:00Z"}
        ]}
        """;

    private const string LiveRuns = """
        {"value":[
          {"name":"run-live","properties":{"status":"Running","startTime":"2026-10-01T09:00:00Z"}},
          {"name":"run-2","properties":{"status":"Failed","startTime":"2026-10-01T07:00:00Z","endTime":"2026-10-01T07:00:05Z"}}
        ]}
        """;

    private const string Outlook = """
        {"value":[{"connectionreferenceid":"0c000000-0000-0000-0000-000000000001","connectionreferencelogicalname":"new_outlook",
                   "connectionreferencedisplayname":"Outlook","connectorid":"/providers/Microsoft.PowerApps/apis/shared_office365","connectionid":"abc"}]}
        """;

    private static string FlowRecord(string? definition, bool? on)
    {
        int? state = on switch { true => 1, false => 0, null => null };
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["clientdata"] = definition, ["statecode"] = state });
    }

    /// <summary>A cloud flow, on, whose definition sends a mail and calls a child flow; routes added first win.</summary>
    private static FakeHttpHandler Flow(string? definition = null, bool on = true, Action<FakeHttpHandler>? first = null)
    {
        var handler = new FakeHttpHandler();
        first?.Invoke(handler);

        return handler
            .OnJson(HttpMethod.Get, $"workflows({Id})?$select=clientdata,statecode", FlowRecord(definition ?? Definition, on))
            .OnJson(HttpMethod.Get, "$select=workflowid,statecode", $$"""{"value":[{"workflowid":"{{Id}}","statecode":{{(on ? 1 : 0)}}}]}""")
            .OnJson(HttpMethod.Get, "$select=workflowid,name", $$"""{"value":[{"workflowid":"{{ChildFlow}}","name":"Escalate case"}]}""")
            .OnJson(HttpMethod.Get, "flowruns?", Empty)
            .OnJson(HttpMethod.Get, "connectionreferences?", Empty)
            .OnJson(HttpMethod.Get, "/runs?api-version", Empty)
            .OnJson(HttpMethod.Get, "/connections?", Empty)
            .Quiet();
    }

    // ---------------------------------------------------------------- design and definition

    [Fact]
    public async Task A_flow_is_drawn_and_its_definition_shown_as_json()
    {
        var details = Details(Flow(), CloudFlow());

        await details.LoadAsync();

        Assert.True(details.HasFlowDiagram);
        var escalate = details.FlowDiagram!.Cards.Single(c => c.Title == "Escalate");
        Assert.Equal("Escalate case", escalate.ChildFlowName);
        Assert.Equal(CodeLanguage.Json, details.SourceLanguage);
        Assert.Contains("\"connectionReferences\": {", details.SourceText);
        Assert.Equal($"The flow's definition (workflow.clientdata), {details.SourceText!.Length:N0} characters.", details.SourceStatus);
        Assert.Equal("Definition", details.SourceTabHeader);
        Assert.True(details.IsFlowOn);
        Assert.Equal("On", details.FlowStateLabel);
        Assert.Equal(DetailsTab.Design, details.SelectedTab);
        Assert.Equal(details.FlowDiagram.StatusLine, details.StatusText);
        Assert.True(details.CopySourceCommand.CanExecute(null));
        Assert.True(details.SaveSourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task Closing_the_window_stops_the_diagram_reading_for_a_run()
    {
        var details = Details(Flow(), CloudFlow(), EnvironmentId);
        await details.LoadAsync();
        var raised = 0;
        details.PropertyChanged += (_, _) => raised++;

        details.Detach();
        await details.LoadAsync();

        Assert.True(details.HasFlowUrl);
        Assert.True(details.HasFlowDiagram);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Off_the_design_tab_the_status_line_is_what_the_window_read()
    {
        var details = Details(Flow(), CloudFlow());
        await details.LoadAsync();

        details.SelectedTab = DetailsTab.Source;

        Assert.Equal(details.Status, details.StatusText);
    }

    [Fact]
    public async Task A_flow_without_a_definition_has_nothing_to_draw()
    {
        var details = Details(Flow(first: h => h.OnJson(HttpMethod.Get, $"workflows({Id})?", """{"clientdata":null}""")
            .OnJson(HttpMethod.Get, "$select=workflowid,statecode", Empty)), CloudFlow());

        await details.LoadAsync();

        Assert.False(details.HasFlowDiagram);
        Assert.Equal("This flow has no definition stored in Dataverse, so there is nothing to draw.", details.DesignStatus);
        Assert.Null(details.SourceText);
        Assert.Equal("This flow has no definition stored in Dataverse.", details.SourceStatus);
        Assert.Null(details.FlowStateLabel);
        Assert.Equal("This flow uses no connection references - its connections, if any, are embedded in it.", details.ConnectionsStatus);
        Assert.Equal("0", details.ConnectionsTabCount);
        Assert.False(details.CopySourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_definition_that_cannot_be_drawn_says_why()
    {
        var details = Details(Flow("not json at all"), CloudFlow());

        await details.LoadAsync();

        Assert.False(details.HasFlowDiagram);
        Assert.StartsWith("Could not draw the flow - ", details.DesignStatus);
        Assert.Equal("not json at all", details.SourceText);
    }

    [Fact]
    public async Task A_definition_that_cannot_be_read_is_reported_on_its_tabs()
    {
        var details = Details(Flow(first: h => h.OnError(HttpMethod.Get, $"workflows({Id})?", HttpStatusCode.Forbidden, "No read on workflow")), CloudFlow());

        await details.LoadAsync();

        Assert.StartsWith("Could not read the definition - ", details.SourceStatus);
        Assert.StartsWith("Could not read the connection references - ", details.ConnectionsStatus);
        Assert.Contains("definition: ", details.Status);
        Assert.Contains("connections: ", details.Status);
    }

    // ---------------------------------------------------------------- runs

    [Fact]
    public async Task Without_the_environment_id_runs_come_from_dataverse_alone_and_say_so()
    {
        var details = Details(Flow(first: h => h.OnJson(HttpMethod.Get, "flowruns?", DataverseRuns)), CloudFlow());

        await details.LoadAsync();

        Assert.Equal(["run-1", "run-2"], details.Runs.Select(r => r.Name));
        Assert.All(details.Runs, r => Assert.Null(r.PortalUrl));
        Assert.Equal("Latest 2 run(s), 0 failed. Runs in progress cannot be shown: the environment's Power Platform id is unknown.", details.RunsStatus);
        Assert.Equal("2", details.RunsTabCount);
        Assert.Equal("Run history", details.RunsTabHeader);
        Assert.False(details.OpenRunCommand.CanExecute(null));
    }

    [Fact]
    public async Task No_runs_in_dataverse_explains_why_there_may_be_none()
    {
        var details = Details(Flow(), CloudFlow());

        await details.LoadAsync();

        Assert.StartsWith("No runs recorded in Dataverse. Cloud flow run history is kept for 28 days by default", details.RunsStatus);
        Assert.EndsWith("Runs in progress cannot be shown: the environment's Power Platform id is unknown.", details.RunsStatus);
    }

    [Fact]
    public async Task Runs_in_progress_are_merged_in_live_from_power_automate()
    {
        var opened = new List<string?>();
        var handler = Flow(first: h => h
            .OnJson(HttpMethod.Get, "flowruns?", DataverseRuns)
            .OnJson(HttpMethod.Get, "/runs?api-version", LiveRuns));
        var details = Details(handler, CloudFlow(), EnvironmentId, openUrl: opened.Add);

        await details.LoadAsync();

        Assert.Equal(["run-live", "run-1", "run-2"], details.Runs.Select(r => r.Name));
        Assert.Equal("Latest 3 run(s), 1 failed. 1 running not in Dataverse yet - shown live from Power Automate. " +
                     "1 run(s) have moved on since Dataverse's copy and show their live status.", details.RunsStatus);
        Assert.Equal("run-1", details.SelectedRun?.Name);
        Assert.Contains(handler.Requests, r => r.Url.Contains($"/environments/{EnvironmentId}/flows/{FlowId}/runs?"));

        details.OpenRunCommand.Execute(details.Runs[0]);
        details.OpenRunCommand.Execute(null);

        Assert.Equal(
            [$"https://make.powerautomate.com/environments/{EnvironmentId}/flows/{FlowId}/runs/run-live",
             $"https://make.powerautomate.com/environments/{EnvironmentId}/flows/{FlowId}/runs/run-1"],
            opened);
    }

    [Fact]
    public async Task A_live_list_that_cannot_be_read_leaves_dataverses_runs_and_says_what_may_be_missing()
    {
        var details = Details(Flow(first: h => h
            .OnJson(HttpMethod.Get, "flowruns?", DataverseRuns)
            .OnError(HttpMethod.Get, "/runs?api-version", HttpStatusCode.InternalServerError, "PA down")), CloudFlow(), EnvironmentId);

        await details.LoadAsync();

        Assert.Equal(2, details.Runs.Count);
        Assert.StartsWith("Latest 2 run(s), 0 failed. Runs in progress, or finished in the last few minutes, may be missing: " +
                          "Power Automate's live list could not be read - ", details.RunsStatus);
        Assert.Equal($"https://make.powerautomate.com/environments/{EnvironmentId}/flows/{FlowId}/runs/run-1", details.Runs[0].PortalUrl);
    }

    [Fact]
    public async Task When_dataverse_cannot_list_runs_power_automate_still_can()
    {
        var details = Details(Flow(first: h => h
            .OnError(HttpMethod.Get, "flowruns?", HttpStatusCode.Forbidden, "No flowrun access")
            .OnJson(HttpMethod.Get, "/runs?api-version", LiveRuns)), CloudFlow(), EnvironmentId);

        await details.LoadAsync();

        Assert.Equal(["run-live", "run-2"], details.Runs.Select(r => r.Name));
        Assert.Equal("run-2", details.SelectedRun?.Name);
        Assert.EndsWith("Dataverse's run history could not be read, so these come from Power Automate alone.", details.RunsStatus);
        Assert.Contains("runs: ", details.Status);
    }

    [Fact]
    public async Task No_runs_anywhere_says_how_long_they_are_kept()
    {
        var details = Details(Flow(), CloudFlow(), EnvironmentId);

        await details.LoadAsync();

        Assert.Empty(details.Runs);
        Assert.Equal("No runs in Dataverse or in Power Automate. Run history is kept for about 28 days.", details.RunsStatus);
    }

    // ---------------------------------------------------------------- a run on the diagram

    [Fact]
    public async Task A_run_cannot_be_drawn_without_the_environment_id()
    {
        var details = Details(Flow(first: h => h.OnJson(HttpMethod.Get, "flowruns?", DataverseRuns)), CloudFlow());
        await details.LoadAsync();
        details.SelectedTab = DetailsTab.Runs;

        Assert.True(details.ShowRunOnDiagramCommand.CanExecute(null));

        await details.ShowRunOnDiagramCommand.ExecuteAsync(null);

        Assert.Equal(DetailsTab.Design, details.SelectedTab);
        Assert.True(details.FlowDiagram!.IsRunNoticeError);
        Assert.StartsWith("This environment's Power Platform id could not be found", details.FlowDiagram.RunNotice);
    }

    [Fact]
    public async Task A_run_is_read_step_by_step_and_drawn_on_the_diagram()
    {
        var run = $"/environments/{EnvironmentId}/flows/{FlowId}/runs/run-1";
        var handler = Flow(first: h => h
            .OnJson(HttpMethod.Get, "flowruns?", DataverseRuns)
            .OnJson(HttpMethod.Get, run + "/actions", """{"value":[{"name":"Send","properties":{"status":"Succeeded"}}]}""")
            .OnJson(HttpMethod.Get, run, """{"name":"run-1","properties":{"status":"Succeeded","trigger":{"name":"manual","status":"Succeeded"}}}"""));
        var details = Details(handler, CloudFlow(), EnvironmentId);
        await details.LoadAsync();

        await details.ShowRunOnDiagramCommand.ExecuteAsync(details.Runs.Single(r => r.Name == "run-1"));

        Assert.True(details.FlowDiagram!.IsRunMode);
        Assert.Equal(FlowStepOutcome.Succeeded, details.FlowDiagram.Cards.Single(c => c.Title == "Send").RunOutcome);
        Assert.False(details.FlowDiagram.HasRunNotice);
    }

    [Fact]
    public async Task Disposing_the_window_drops_a_step_s_content_still_arriving_and_stops_following_the_tab()
    {
        var run = $"/environments/{EnvironmentId}/flows/{FlowId}/runs/run-1";
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var handler = Flow(first: h => h
            .OnAsync(HttpMethod.Get, "https://x/in/Send", _ => slow.Task)
            .OnJson(HttpMethod.Get, "flowruns?", DataverseRuns)
            .OnJson(HttpMethod.Get, run + "/actions",
                """{"value":[{"name":"Send","properties":{"status":"Succeeded","inputsLink":{"uri":"https://x/in/Send"}}}]}""")
            .OnJson(HttpMethod.Get, run, """{"name":"run-1","properties":{"status":"Succeeded","trigger":{"name":"manual","status":"Succeeded"}}}"""));
        var session = Session();
        var details = Details(handler, CloudFlow(), EnvironmentId, session);
        await details.LoadAsync();
        await details.ShowRunOnDiagramCommand.ExecuteAsync(details.Runs.Single(r => r.Name == "run-1"));
        var diagram = details.FlowDiagram!;
        diagram.Selected = diagram.Cards.Single(c => c.Title == "Send");
        diagram.IsInputsShown = true;
        var raised = 0;
        details.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ObjectDetailsViewModel.Title)) raised++; };

        details.Dispose();
        slow.SetResult(FakeHttpHandler.Json("""{"late":true}"""));
        await diagram.Work.WhenIdleAsync();
        session.EnvironmentUrl = "https://contoso-uat.crm11.dynamics.com";

        Assert.False(diagram.HasContent);
        Assert.False(diagram.IsContentLoading);
        Assert.False(diagram.HasContentError);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task A_run_that_cannot_be_read_says_why_on_the_diagram()
    {
        var details = Details(Flow(first: h => h
            .OnJson(HttpMethod.Get, "flowruns?", DataverseRuns)
            .OnError(HttpMethod.Get, "/runs/run-1", HttpStatusCode.NotFound, "Gone")), CloudFlow(), EnvironmentId);
        await details.LoadAsync();

        await details.ShowRunOnDiagramCommand.ExecuteAsync(null);

        Assert.False(details.FlowDiagram!.IsRunMode);
        Assert.True(details.FlowDiagram.IsRunNoticeError);
        Assert.Contains("28 days", details.FlowDiagram.RunNotice);
    }

    [Fact]
    public async Task Only_a_named_run_of_a_drawn_flow_can_be_shown_on_the_diagram()
    {
        var details = Details(Flow(), CloudFlow());

        Assert.False(details.ShowRunOnDiagramCommand.CanExecute(null));

        await details.LoadAsync();

        Assert.False(details.ShowRunOnDiagramCommand.CanExecute(null));
        Assert.False(details.ShowRunOnDiagramCommand.CanExecute(new ProcessRun { Name = " ", Status = "Running" }));
        Assert.True(details.ShowRunOnDiagramCommand.CanExecute(new ProcessRun { Name = "run-9", Status = "Running" }));
    }

    // ---------------------------------------------------------------- connections

    [Fact]
    public async Task A_broken_connection_is_why_a_flow_is_off()
    {
        var handler = Flow(on: false, first: h => h
            .OnJson(HttpMethod.Get, "connectionreferences?", Outlook)
            .OnJson(HttpMethod.Get, "/connections?", """{"value":[{"name":"abc","properties":{"statuses":[{"status":"Error","error":{"message":"Token expired"}}]}}]}"""));
        var details = Details(handler, CloudFlow(), EnvironmentId);

        await details.LoadAsync();

        var row = Assert.Single(details.ConnectionRows);
        Assert.Equal("Error - Token expired", row.HealthLabel);
        Assert.True(details.HasBrokenConnection);
        Assert.False(details.IsFlowOn);
        Assert.Equal("Off", details.FlowStateLabel);
        Assert.Equal("1 of 1 connection reference(s) have no working connection - which is why this flow is off, or will not stay on. " +
                     "Bind them in the solution, then turn it on.", details.ConnectionsStatus);
        Assert.Equal("1", details.ConnectionsTabCount);
        Assert.Contains("connection references not found in this environment: new_teams", details.Status);
        Assert.Contains(handler.Requests, r => r.Url.Contains("connectionreferencelogicalname eq 'new_outlook'"));
    }

    [Fact]
    public async Task A_broken_connection_on_a_running_flow_fails_the_steps_that_use_it()
    {
        var details = Details(Flow(first: h => h.OnJson(HttpMethod.Get, "connectionreferences?", """
            {"value":[{"connectionreferenceid":"0c000000-0000-0000-0000-000000000001","connectionreferencelogicalname":"new_outlook"},
                      {"connectionreferenceid":"0c000000-0000-0000-0000-000000000002","connectionreferencelogicalname":"new_teams"}]}
            """)), CloudFlow(), EnvironmentId);

        await details.LoadAsync();

        Assert.Equal(2, details.ConnectionRows.Count);
        Assert.Equal("2 of 2 connection reference(s) have no working connection - the flow fails when it reaches a step that uses them.",
            details.ConnectionsStatus);
        Assert.DoesNotContain("not found in this environment", details.Status);
    }

    [Fact]
    public async Task Without_the_environment_id_a_bound_reference_reads_as_bound()
    {
        var details = Details(Flow(first: h => h.OnJson(HttpMethod.Get, "connectionreferences?", Outlook)), CloudFlow());

        await details.LoadAsync();

        Assert.Equal("Bound - status not read", Assert.Single(details.ConnectionRows).HealthLabel);
        Assert.False(details.HasBrokenConnection);
        Assert.Equal("1 connection reference(s). (the environment id is not known, so connection status was not read.)", details.ConnectionsStatus);
    }

    [Fact]
    public async Task Connection_status_that_cannot_be_read_is_noted()
    {
        var details = Details(Flow(first: h => h
            .OnJson(HttpMethod.Get, "connectionreferences?", Outlook)
            .OnError(HttpMethod.Get, "/connections?", HttpStatusCode.InternalServerError, "PA down")), CloudFlow(), EnvironmentId);

        await details.LoadAsync();

        Assert.StartsWith("1 connection reference(s). (connection status could not be read - ", details.ConnectionsStatus);
    }

    [Fact]
    public async Task A_flow_opened_on_its_connections_reads_its_definition_for_them()
    {
        var details = Details(Flow(NoReferences, first: h => h.OnJson(HttpMethod.Get, "connectionreferences?", Outlook)), CloudFlow());

        await details.LoadAsync();

        Assert.Empty(details.ConnectionRows);
        Assert.Equal("This flow uses no connection references - its connections, if any, are embedded in it.", details.ConnectionsStatus);
    }

    // ---------------------------------------------------------------- on and off

    [Fact]
    public async Task From_a_tab_the_flow_can_be_turned_off()
    {
        var session = Session();
        var details = Details(Flow(), CloudFlow(), session: session);

        await details.LoadAsync();

        Assert.True(details.IsSwitchedOn);
        Assert.Equal("On", details.SwitchStateLabel);
        Assert.Null(details.OtherSwitchStateLabel);
        Assert.Equal("Turn off", details.SwitchButtonLabel);
        Assert.Equal("Turn off…", details.SwitchButtonText);
        Assert.Equal($"Turns the flow off in {session.Title}. Asks first; recorded in the run log.", details.SwitchToolTip);
        Assert.True(details.ToggleSwitchCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_state_that_cannot_be_read_offers_no_switch()
    {
        var details = Details(Flow(first: h => h.OnError(HttpMethod.Get, "$select=workflowid,statecode", HttpStatusCode.Forbidden, "No state")),
            CloudFlow(), session: Session());

        await details.LoadAsync();

        Assert.Null(details.IsSwitchedOn);
        Assert.False(details.ToggleSwitchCommand.CanExecute(null));
        Assert.Contains("state: ", details.Status);
    }

    [Fact]
    public async Task A_flow_the_state_read_does_not_find_keeps_the_state_from_its_definition()
    {
        var details = Details(Flow(first: h => h.OnJson(HttpMethod.Get, "$select=workflowid,statecode", Empty)), CloudFlow());

        await details.LoadAsync();

        Assert.Null(details.IsSwitchedOn);
        Assert.True(details.IsFlowOn);
    }
}
