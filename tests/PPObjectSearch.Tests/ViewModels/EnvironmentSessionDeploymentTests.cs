using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The tab's per-solution tools - documentation, deployment settings, content search - and what they need before they open.</summary>
public class EnvironmentSessionDeploymentTests
{
    [Fact]
    public void Before_connecting_the_tools_say_what_they_need()
    {
        var session = new FakeEnvironment().Session();

        session.DocumentSolutionCommand.Execute(null);
        Assert.Equal("Choose a solution first - the documentation is of one solution.", session.Status);

        session.ContentSearchCommand.Execute(null);
        Assert.Equal("Connect first.", session.Status);
    }

    [Fact]
    public Task Before_connecting_the_deployment_settings_need_a_solution() => EnvironmentSessionThread.Run(async () =>
    {
        var session = new FakeEnvironment().Session();

        await session.ExportDeploymentSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Choose a solution first - the settings file is for one solution.", session.Status);
    });

    [Fact]
    public void Before_connecting_the_windows_of_the_environment_do_not_open()
    {
        var session = new FakeEnvironment().Session();
        var status = session.Status;

        session.RecentChangesCommand.Execute(null);
        session.FailuresCommand.Execute(null);
        session.SecurityLookupCommand.Execute(null);
        session.SolutionHistoryCommand.Execute(null);
        session.EntraTeamSyncCommand.Execute(null);
        session.QueueSyncCommand.Execute(null);
        session.EnvironmentAdminCommand.Execute("Roles");
        session.OpenDetails(new SolutionComponentItem { Name = "account", ComponentTypeName = "Table", ObjectId = Guid.NewGuid() });
        session.OpenDetails(new SolutionComponentItem { Name = "flow", ComponentTypeName = "Process", ObjectId = Guid.NewGuid() }, DetailsTab.Runs);

        Assert.Equal(status, session.Status);
        Assert.False(session.SolutionHistoryCommand.CanExecute(null));
        Assert.False(session.EnvironmentAdminCommand.CanExecute(null));
        Assert.Empty(session.RecentObjects);
    }

    [Fact]
    public Task A_connected_tab_with_no_solution_has_nothing_to_document() => EnvironmentSessionThread.Run(async () =>
    {
        var session = await new FakeEnvironment().ConnectedAsync();

        session.DocumentSolutionCommand.Execute(null);
        Assert.Equal("Choose a solution first - the documentation is of one solution.", session.Status);

        await session.ExportDeploymentSettingsCommand.ExecuteAsync(null);
        Assert.Equal("Choose a solution first - the settings file is for one solution.", session.Status);
    });

    [Fact]
    public Task A_solution_without_variables_or_connection_references_needs_no_settings_file() => EnvironmentSessionThread.Run(async () =>
    {
        var env = new FakeEnvironment();
        var solution = env.AddSolution("ECT Core", "ect_core");
        env.AddComponent(solution, 1, "account", "Account");
        env.AddComponent(solution, 61, "new_script.js");
        var session = await env.ConnectedAsync();

        await session.ExportDeploymentSettingsCommand.ExecuteAsync(null);

        Assert.Equal($"{session.SelectedSolution!.DisplayLabel} has no environment variables or connection references, so it needs no settings file.",
            session.Status);
    });

    [Fact]
    public void A_recent_object_needs_a_connection_to_open()
    {
        var session = new FakeEnvironment().Session();

        Assert.False(session.OpenRecentCommand.CanExecute(new RecentObject { ObjectId = Guid.NewGuid(), Label = "Case" }));
        Assert.False(session.ApplySavedSearchCommand.CanExecute("not a search"));
    }
}
