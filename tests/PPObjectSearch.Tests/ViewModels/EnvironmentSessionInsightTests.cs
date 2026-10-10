using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// What an environment tab reads about the selected object for the detail pane - a flow's latest
/// runs, a variable's value, whether it is switched on - and the opt-in unmanaged layer check.
/// </summary>
public class EnvironmentSessionInsightTests
{
    private static readonly TimeSpan InsightDebounce = TimeSpan.FromMilliseconds(250);

    private static string Runs(params (string Status, string Start, string? Error)[] runs) =>
        JsonSerializer.Serialize(new
        {
            value = runs.Select(r => new { name = "run-" + r.Start, status = r.Status, starttime = r.Start, errormessage = r.Error })
        });

    private static string States(string idColumn, Guid id, int state) =>
        JsonSerializer.Serialize(new { value = new[] { new Dictionary<string, object> { [idColumn] = id, ["statecode"] = state } } });

    private static SolutionComponentItem Row(EnvironmentSessionViewModel session, string name) =>
        session.AllItems.Single(i => i.Name == name);

    [Fact]
    public Task A_selected_cloud_flow_shows_its_latest_run_and_whether_it_is_on() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        var flow = env.AddComponent(solution, 29, "Notify owner", workflowCategory: 5);
        env.AddComponent(solution, 1, "account", "Account");
        env.Handler
            .OnJson(HttpMethod.Get, "flowruns?", Runs(("Failed", "2026-10-09T10:00:00Z", "Connection expired\nat step 2"),
                ("Succeeded", "2026-10-08T10:00:00Z", null)))
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,statecode", States("workflowid", flow, 1));
        var session = await env.ConnectedAsync();

        session.SelectedItem = Row(session, "Notify owner");
        Assert.Null(session.LatestRun);
        EnvironmentSessionThread.Elapse(session, InsightDebounce);
        await EnvironmentSessionThread.Until(() => session.LatestRun is not null, "the latest run");

        Assert.Equal("Failed", session.LatestRun!.Run.Status);
        Assert.Equal("1 of last 2 failed", session.LatestRun.FailedLabel);
        Assert.Equal("Connection expired", session.LatestRun.ErrorLine);
        Assert.Contains(env.Handler.Requests, r => r.Url.Contains($"_workflow_value eq {flow}&$top={EnvironmentSessionViewModel.RecentRunCount}", StringComparison.Ordinal));
        Assert.True(session.SelectedSwitchedOn);
        Assert.False(session.ShowSwitchOn);
        Assert.True(session.ShowSwitchOff);
        Assert.Null(session.SelectedVariable);

        // Moving to a table clears the pane; coming back answers from what was just read.
        session.SelectedItem = Row(session, "account");
        Assert.Null(session.LatestRun);
        Assert.Null(session.SelectedSwitchedOn);
        var requests = env.Handler.Requests.Count;

        session.SelectedItem = Row(session, "Notify owner");

        Assert.Equal("Failed", session.LatestRun?.Run.Status);
        Assert.True(session.SelectedSwitchedOn);
        Assert.Equal(requests, env.Handler.Requests.Count);
    });

    [Fact]
    public Task An_answer_for_a_row_no_longer_selected_is_kept_but_not_shown() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        var slow = env.AddComponent(solution, 29, "Slow flow", workflowCategory: 5);
        env.AddComponent(solution, 29, "Other flow", workflowCategory: 5);
        var runs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Handler
            .OnAsync(HttpMethod.Get, $"_workflow_value eq {slow}", async _ =>
            {
                await runs.Task;
                return FakeHttpHandler.Json(Runs(("Succeeded", "2026-10-09T10:00:00Z", null)));
            })
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,statecode", "{\"value\":[]}");
        var session = await env.ConnectedAsync();

        session.SelectedItem = Row(session, "Slow flow");
        EnvironmentSessionThread.Elapse(session, InsightDebounce);
        session.SelectedItem = Row(session, "Other flow");
        runs.SetResult();
        await EnvironmentSessionThread.Until(() => env.Handler.Requests.Any(r => r.Url.Contains("workflows?$select=workflowid,statecode", StringComparison.Ordinal)),
            "the slow flow's state to be read");
        await Task.Delay(50);

        Assert.Null(session.LatestRun);

        session.SelectedItem = Row(session, "Slow flow");

        Assert.Equal("the only run", session.LatestRun?.FailedLabel);
        Assert.Null(session.SelectedSwitchedOn);
    });

    [Fact]
    public Task A_selected_environment_variable_shows_its_value_here() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        var definition = env.AddComponent(solution, 380, "new_ApiUrl");
        var valueRecord = env.AddComponent(solution, 381, "new_ApiUrl value");
        var unset = env.AddComponent(solution, 380, "new_Unset");
        env.Handler
            .OnJson(HttpMethod.Get, $"environmentvariablevalues({valueRecord})",
                JsonSerializer.Serialize(new Dictionary<string, object> { ["_environmentvariabledefinitionid_value"] = definition }))
            .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({definition})", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["schemaname"] = "new_ApiUrl", ["type"] = 100000000, ["defaultvalue"] = "https://default",
                ["environmentvariabledefinition_environmentvariablevalue"] = new[] { new { environmentvariablevalueid = valueRecord, value = "https://here" } }
            }))
            .OnJson(HttpMethod.Get, $"environmentvariabledefinitions({unset})", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["schemaname"] = "new_Unset", ["type"] = 100000000,
                ["environmentvariabledefinition_environmentvariablevalue"] = Array.Empty<object>()
            }));
        var session = await env.ConnectedAsync();

        session.SelectedItem = Row(session, "new_ApiUrl");
        Assert.True(session.IsSelectedEnvironmentVariable);
        Assert.Null(session.SelectedVariableValue);
        EnvironmentSessionThread.Elapse(session, InsightDebounce);
        await EnvironmentSessionThread.Until(() => session.SelectedVariable is not null, "the variable");
        Assert.Equal("https://here", session.SelectedVariableValue);

        session.SelectedItem = Row(session, "new_ApiUrl value");
        EnvironmentSessionThread.Elapse(session, InsightDebounce);
        await EnvironmentSessionThread.Until(() => session.SelectedVariable is not null, "the variable through its value record");
        Assert.Equal("new_ApiUrl", session.SelectedVariable!.SchemaName);

        session.SelectedItem = Row(session, "new_Unset");
        EnvironmentSessionThread.Elapse(session, InsightDebounce);
        await EnvironmentSessionThread.Until(() => session.SelectedVariable is not null, "the unset variable");
        Assert.Equal("Not set", session.SelectedVariableValue);
        Assert.Null(session.LatestRun);
        Assert.Null(session.SelectedSwitchedOn);
    });

    [Fact]
    public Task A_selected_plug_in_step_shows_whether_it_is_enabled() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        var step = env.AddComponent(solution, 92, "account create step");
        env.Handler.OnJson(HttpMethod.Get, "sdkmessageprocessingsteps?", States("sdkmessageprocessingstepid", step, 1));
        var session = await env.ConnectedAsync();

        session.SelectedItem = Row(session, "account create step");
        EnvironmentSessionThread.Elapse(session, InsightDebounce);
        await EnvironmentSessionThread.Until(() => session.SelectedSwitchedOn is not null, "the step's state");

        Assert.False(session.SelectedSwitchedOn);
        Assert.True(session.ShowSwitchOn);
        Assert.False(session.ShowSwitchOff);
        Assert.Null(session.LatestRun);
    });

    [Fact]
    public Task Nothing_is_read_while_the_pane_is_closed_and_opening_it_reads() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        var step = env.AddComponent(solution, 92, "account create step");
        env.AddComponent(solution, 1, "account", "Account");
        env.Handler.OnJson(HttpMethod.Get, "sdkmessageprocessingsteps?", States("sdkmessageprocessingstepid", step, 0));
        var session = await env.ConnectedAsync(new AppSettings { IsDetailPaneOpen = false });

        session.SelectedItem = Row(session, "account create step");
        Assert.False(EnvironmentSessionThread.Timer(session, InsightDebounce).IsEnabled);

        session.ToggleDetailPaneCommand.Execute(null);
        EnvironmentSessionThread.Elapse(session, InsightDebounce);
        await EnvironmentSessionThread.Until(() => session.SelectedSwitchedOn is not null, "the step's state");

        Assert.True(session.SelectedSwitchedOn);

        // A table has nothing to read.
        session.SelectedItem = Row(session, "account");
        Assert.False(EnvironmentSessionThread.Timer(session, InsightDebounce).IsEnabled);
    });

    [Fact]
    public Task A_failed_read_leaves_the_pane_empty() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 29, "Notify owner", workflowCategory: 5);
        env.Handler.OnError(HttpMethod.Get, "flowruns?", HttpStatusCode.InternalServerError, "Flow runs are unavailable");
        var session = await env.ConnectedAsync();

        session.SelectedItem = Row(session, "Notify owner");
        EnvironmentSessionThread.Elapse(session, InsightDebounce);
        await EnvironmentSessionThread.Until(() => env.Handler.Requests.Any(r => r.Url.Contains("flowruns?", StringComparison.Ordinal)), "the runs to be asked for");
        await Task.Delay(50);

        Assert.Null(session.LatestRun);
        Assert.Null(session.SelectedSwitchedOn);
    });

    // ---------------------------------------------------------------- unmanaged layers

    private static async Task<(FakeEnvironment Env, EnvironmentSessionViewModel Session)> LayeredAsync(Action<FakeHttpHandler, Guid, Guid, Guid>? extra = null)
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        var account = env.AddComponent(solution, 1, "account", "Account");
        var contact = env.AddComponent(solution, 1, "contact", "Contact");
        env.AddComponent(solution, 24, "Main form");
        var script = env.AddComponent(solution, 61, "new_script.js");
        extra?.Invoke(env.Handler, account, contact, script);
        env.Handler
            .OnJson(HttpMethod.Get, $"msdyn_componentid eq '{account}'", JsonSerializer.Serialize(new
            {
                value = new[] { new { msdyn_solutionname = "Active", msdyn_order = 2 }, new { msdyn_solutionname = "Base", msdyn_order = 1 } }
            }))
            .OnJson(HttpMethod.Get, $"msdyn_componentid eq '{contact}'", JsonSerializer.Serialize(new
            {
                value = new[] { new { msdyn_solutionname = "Base", msdyn_order = 1 } }
            }))
            .OnError(HttpMethod.Get, $"msdyn_componentid eq '{script}'", HttpStatusCode.InternalServerError, "Layers are unavailable");
        return (env, await env.ConnectedAsync());
    }

    [Fact]
    public Task Checking_layers_marks_each_object_and_says_what_could_not_be_checked() => EnvironmentSessionThread.Run(async () =>
    {
        var (env, session) = await LayeredAsync();
        Assert.True(session.CheckUnmanagedLayersCommand.CanExecute(null));
        var statuses = new List<string>();
        session.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(session.Status)) statuses.Add(session.Status); };

        await session.CheckUnmanagedLayersCommand.ExecuteAsync(null);

        Assert.Equal("Checked 2 object(s) for an unmanaged layer (1 of this type aren't supported by the layers check; " +
                     "1 could not be read - run the check again to retry them).", session.Status);
        Assert.Contains(statuses, s => s.StartsWith("Checking for unmanaged layers... ", StringComparison.Ordinal));
        Assert.True(Row(session, "account").HasUnmanagedLayer);
        Assert.False(Row(session, "contact").HasUnmanagedLayer);
        Assert.Null(Row(session, "Main form").HasUnmanagedLayer);
        Assert.Null(Row(session, "new_script.js").HasUnmanagedLayer);
        Assert.Equal(["Any / not checked (4)", "Unmanaged layer (1)", "No unmanaged layer (1)", "Not checked (2)"],
            session.LayerFilters.Select(f => f.Label));
        Assert.False(session.IsCheckingLayers);
        Assert.Equal(3, env.Handler.Requests.Count(r => r.Url.Contains("msdyn_componentlayers", StringComparison.Ordinal)));

        session.SelectedLayerFilter = session.LayerFilters.Single(f => f.Name == "Unmanaged layer");
        Assert.Equal("Account", Assert.Single(session.ItemsView.Cast<SolutionComponentItem>()).PrimaryLabel);

        // Checking again takes only what is still unchecked.
        session.SelectedLayerFilter = session.LayerFilters[0];
        await session.CheckUnmanagedLayersCommand.ExecuteAsync(null);
        Assert.Equal("Checked 0 object(s) for an unmanaged layer (1 of this type aren't supported by the layers check; " +
                     "1 could not be read - run the check again to retry them).", session.Status);
        Assert.Equal(4, env.Handler.Requests.Count(r => r.Url.Contains("msdyn_componentlayers", StringComparison.Ordinal)));

        // What was found outlasts a fresh read of the solution.
        await session.RefreshCommand.ExecuteAsync(null);
        Assert.True(Row(session, "account").HasUnmanagedLayer);
        Assert.False(Row(session, "contact").HasUnmanagedLayer);
    });

    [Fact]
    public Task Checking_only_what_is_selected_or_already_checked() => EnvironmentSessionThread.Run(async () =>
    {
        var (env, session) = await LayeredAsync();
        session.SetSelection([Row(session, "contact")]);

        await session.CheckSelectionLayersCommand.ExecuteAsync(null);

        Assert.Equal("Checked 1 object(s) for an unmanaged layer.", session.Status);
        Assert.Single(env.Handler.Requests, r => r.Url.Contains("msdyn_componentlayers", StringComparison.Ordinal));

        await session.CheckSelectionLayersCommand.ExecuteAsync(null);

        Assert.Equal("Every object on screen has already been checked for an unmanaged layer.", session.Status);
    });

    [Fact]
    public Task A_layer_check_can_be_stopped() => EnvironmentSessionThread.Run(async () =>
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, session) = await LayeredAsync((handler, account, _, _) => handler.OnAsync(HttpMethod.Get, $"msdyn_componentid eq '{account}'", async _ =>
        {
            await gate.Task;
            return FakeHttpHandler.Json("{\"value\":[]}");
        }));
        session.SetSelection([Row(session, "account")]);
        Assert.False(session.CancelLayerCheckCommand.CanExecute(null));

        var check = session.CheckSelectionLayersCommand.ExecuteAsync(null);
        Assert.True(session.IsCheckingLayers);
        Assert.True(session.CancelLayerCheckCommand.CanExecute(null));
        Assert.False(session.CheckUnmanagedLayersCommand.CanExecute(null));

        session.CancelLayerCheckCommand.Execute(null);
        gate.SetResult();
        await check;

        Assert.Equal("Layer check stopped.", session.Status);
        Assert.False(session.IsCheckingLayers);
        Assert.Null(Row(session, "account").HasUnmanagedLayer);
    });

    [Fact]
    public void Layers_cannot_be_checked_before_connecting()
    {
        var session = new FakeEnvironment().Session();

        Assert.False(session.CheckUnmanagedLayersCommand.CanExecute(null));
        Assert.False(session.CheckSelectionLayersCommand.CanExecute(null));
    }
}
