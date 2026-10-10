using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>Turning flows, processes and plug-in steps on and off from an environment tab, behind the write guard and a confirmation.</summary>
[Collection(nameof(WriteConfirmation))]
public sealed class EnvironmentSessionQuickActionsTests : IDisposable
{
    private static readonly TimeSpan InsightDebounce = TimeSpan.FromMilliseconds(250);

    private readonly Func<EnvironmentSessionViewModel, string, string, bool> _prompt = WriteConfirmation.Prompt;
    private readonly Action<string, string> _refuse = WriteConfirmation.Refuse;
    private readonly List<(string Title, string Action)> _asked = new();
    private readonly List<string> _refused = new();

    public EnvironmentSessionQuickActionsTests()
    {
        WriteConfirmation.Prompt = (_, title, action) =>
        {
            lock (_asked) _asked.Add((title, action));
            return Answer;
        };
        WriteConfirmation.Refuse = (message, _) => _refused.Add(message);
    }

    private bool Answer { get; set; } = true;

    public void Dispose()
    {
        WriteConfirmation.Prompt = _prompt;
        WriteConfirmation.Refuse = _refuse;
    }

    private static string States(params (Guid Id, int State)[] states) =>
        JsonSerializer.Serialize(new { value = states.Select(s => new { workflowid = s.Id, statecode = s.State }) });

    private static SolutionComponentItem Row(EnvironmentSessionViewModel session, string name) =>
        session.AllItems.Single(i => i.Name == name);

    private static IEnumerable<RecordedRequest> Patches(FakeEnvironment env) =>
        env.Handler.Requests.Where(r => r.Method == HttpMethod.Patch);

    [Fact]
    public Task Turning_off_the_selected_flow_asks_then_writes_and_reads_its_state_again() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        var flow = env.AddComponent(solution, 29, "Notify owner", workflowCategory: 5);
        env.Handler
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,statecode", States((flow, 1)))
            .OnJson(HttpMethod.Get, "flowruns?", "{\"value\":[]}")
            .OnStatus(HttpMethod.Patch, $"workflows({flow})", HttpStatusCode.NoContent);
        var session = await env.ConnectedAsync();
        session.SelectedItem = Row(session, "Notify owner");
        EnvironmentSessionThread.Elapse(session, InsightDebounce);
        await EnvironmentSessionThread.Until(() => session.SelectedSwitchedOn is not null, "the flow's state");
        Assert.True(session.SelectedSwitchedOn);
        Assert.False(session.ShowSwitchOn);
        Assert.True(session.HasQuickActions);
        Assert.Equal("Turn off", session.SwitchOffHeader);
        Assert.Equal("Turn off…", session.SwitchOffLabel);
        Assert.Equal("Turns the flow off in Contoso Dev. Asks first; recorded in the run log.", session.SwitchOffToolTip);
        Assert.True(session.SwitchOffCommand.CanExecute(null));

        await session.SwitchOffCommand.ExecuteAsync(null);

        var (title, action) = Assert.Single(_asked);
        Assert.Equal("Turn off", title);
        Assert.Equal("Turn off 1 component(s)?\n\n  Notify owner", action);
        var patch = Assert.Single(Patches(env));
        Assert.EndsWith($"workflows({flow})", patch.Url);
        Assert.Contains("\"statecode\":0", patch.Body);
        Assert.Equal("Turn off - 1 done. Recorded in the run log.", session.Status);
        Assert.True(session.SwitchOnCommand.CanExecute(null));

        // What the pane knew about it is stale now, so it is read again.
        Assert.Null(session.SelectedSwitchedOn);
        Assert.True(EnvironmentSessionThread.Timer(session, InsightDebounce).IsEnabled);
    });

    [Fact]
    public Task Saying_no_writes_nothing() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 92, "account create step");
        var session = await env.ConnectedAsync();
        session.SelectedItem = Row(session, "account create step");
        Answer = false;

        await session.SwitchOnCommand.ExecuteAsync(null);

        Assert.Equal("Enable", Assert.Single(_asked).Title);
        Assert.Empty(Patches(env));
        Assert.StartsWith("Loaded 1 objects", session.Status);
    });

    [Fact]
    public Task A_production_environment_not_allowlisted_is_refused_before_asking() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment("Production");
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 92, "account create step");
        var session = await env.ConnectedAsync();
        session.SelectedItem = Row(session, "account create step");

        await session.SwitchOffCommand.ExecuteAsync(null);

        Assert.Single(_refused);
        Assert.Empty(_asked);
        Assert.Empty(Patches(env));
    });

    [Fact]
    public Task Many_components_are_listed_in_part_and_failures_are_summed_up() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        for (var i = 0; i < 16; i++) env.AddComponent(solution, 29, $"Workflow {i:00}", workflowCategory: 0);
        env.Handler
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,statecode", "{\"value\":[]}")
            .OnError(HttpMethod.Patch, "workflows(", HttpStatusCode.BadRequest, "The workflow has errors");
        var session = await env.ConnectedAsync();
        session.SetSelection(session.AllItems);
        Assert.Equal("Activate 16 selected", session.SwitchOnHeader);
        Assert.Equal("Turns on the 16 selected in Contoso Dev. Asks first; recorded in the run log.", session.SwitchOnToolTip);
        Assert.True(session.ShowSwitchOn);
        Assert.True(session.ShowSwitchOff);

        await session.SwitchOnCommand.ExecuteAsync(null);

        var (title, action) = Assert.Single(_asked);
        Assert.Equal("Turn on", title);
        Assert.EndsWith("  Workflow 14\n  ... and 1 more", action);
        Assert.Equal(16, Patches(env).Count());
        Assert.StartsWith("Turn on - 0 done, 16 failed: Workflow 00: ", session.Status);
        Assert.EndsWith(" ...", session.Status);
    });

    [Fact]
    public Task Different_kinds_together_are_turned_on_and_off_by_count() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 29, "Notify owner", workflowCategory: 5);
        env.AddComponent(solution, 92, "account create step");
        env.AddComponent(solution, 1, "account", "Account");
        var session = await env.ConnectedAsync();

        session.SetSelection([Row(session, "account")]);
        Assert.False(session.HasSwitchableSelection);
        Assert.Equal("Turn on", session.SwitchOnHeader);
        Assert.Equal("Turn off", session.SwitchOffHeader);
        Assert.Equal(string.Empty, session.SwitchOnToolTip);
        Assert.False(session.ShowSwitchOn);
        Assert.False(session.SwitchOnCommand.CanExecute(null));

        session.SetSelection(session.AllItems);
        Assert.Equal("Turn on 2 selected", session.SwitchOnHeader);
        Assert.Equal("Turn off 2 selected", session.SwitchOffHeader);
        Assert.Equal("Turn off 2 selected…", session.SwitchOffLabel);
    });

    [Fact]
    public Task Turning_on_the_solutions_flows_turns_on_only_those_that_are_off() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        var on = env.AddComponent(solution, 29, "Already on", workflowCategory: 5);
        var off = env.AddComponent(solution, 29, "Switched off", workflowCategory: 5);
        env.AddComponent(solution, 29, "Classic", workflowCategory: 0);
        env.Handler
            .OnJson(HttpMethod.Get, "workflows?$select=workflowid,statecode", States((on, 1), (off, 0)))
            .OnStatus(HttpMethod.Patch, "workflows(", HttpStatusCode.NoContent);
        var session = await env.ConnectedAsync();
        Assert.True(session.TurnOnSolutionFlowsCommand.CanExecute(null));

        await session.TurnOnSolutionFlowsCommand.ExecuteAsync(null);

        Assert.Equal("Turn on", Assert.Single(_asked).Title);
        Assert.EndsWith($"workflows({off})", Assert.Single(Patches(env)).Url);
        Assert.Equal("Turn on - 1 done. Recorded in the run log.", session.Status);
    });

    [Fact]
    public Task Flows_that_are_all_on_already_need_nothing() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        var a = env.AddComponent(solution, 29, "Flow A", workflowCategory: 5);
        var b = env.AddComponent(solution, 29, "Flow B", workflowCategory: 5);
        env.Handler.OnJson(HttpMethod.Get, "workflows?$select=workflowid,statecode", States((a, 1), (b, 1)));
        var session = await env.ConnectedAsync();

        await session.TurnOnSolutionFlowsCommand.ExecuteAsync(null);

        Assert.Equal("All 2 flow(s) here are already on.", session.Status);
        Assert.Empty(_asked);
    });

    [Fact]
    public Task Flows_whose_states_cannot_be_read_are_left_alone() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 29, "Flow A", workflowCategory: 5);
        env.Handler.OnError(HttpMethod.Get, "workflows?$select=workflowid,statecode", HttpStatusCode.Forbidden, "No read access to workflows");
        var session = await env.ConnectedAsync();

        await session.TurnOnSolutionFlowsCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not read the flows' states - ", session.Status);
        Assert.Contains("No read access to workflows", session.Status);
    });

    [Fact]
    public Task A_solution_without_flows_has_none_to_turn_on() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 1, "account", "Account");
        var session = await env.ConnectedAsync();

        await session.TurnOnSolutionFlowsCommand.ExecuteAsync(null);

        Assert.Equal("There are no cloud flows in the list.", session.Status);
    });

    [Fact]
    public Task Nothing_is_switched_before_connecting_or_while_loading() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("Default Solution", "Default");
        env.AddComponent(solution, 29, "Flow A", workflowCategory: 5);
        var session = env.Session();

        await session.TurnOnSolutionFlowsCommand.ExecuteAsync(null);
        Assert.Equal("Connect first.", session.Status);

        await session.ConnectAsync();
        var gate = env.Gate(solution);
        var refresh = session.RefreshCommand.ExecuteAsync(null);
        Assert.True(session.IsBusy);

        await session.TurnOnSolutionFlowsCommand.ExecuteAsync(null);
        Assert.Equal("Wait for the current load to finish first.", session.Status);

        gate.SetResult();
        await refresh;
        Assert.Empty(_asked);
    });
}
