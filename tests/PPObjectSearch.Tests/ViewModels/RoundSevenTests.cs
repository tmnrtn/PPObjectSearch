using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// What the seventh review round changed behind the views: questions asked in the app's own
/// dialog rather than a message box, with buttons named for what they do.
/// </summary>
public class RoundSevenTests
{
    [Fact]
    public Task Opening_many_details_windows_asks_with_the_count_on_the_button() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await EnvironmentSessionFilterTests.ConnectedAsync();
        var rows = Enumerable.Range(0, 14).Select(i => new SolutionComponentItem { Name = $"row {i}", ComponentTypeName = "Table" }).ToList();
        session.SetSelection(rows);
        AskRequest? asked = null;
        session.Ask = request => { asked = request; return false; };

        session.OpenSelectionDetailsCommand.Execute(null);

        Assert.NotNull(asked);
        Assert.Equal("Open 14 details windows?", asked.Heading);
        Assert.Equal("Open 14 windows", asked.Confirm);
        Assert.False(asked.ConfirmIsDefault);
    });

    [Theory]
    [InlineData(EnvironmentSku.Sandbox, false, "Writes permitted.")]
    [InlineData(EnvironmentSku.Production, true, "Writes allowed to production.")]
    [InlineData(EnvironmentSku.Unknown, true, "Writes allowed to an environment of unknown type.")]
    public void The_guard_box_names_a_protected_environment_that_was_allowed(EnvironmentSku sku, bool allowlisted, string label)
    {
        var permission = new WritePermission(true, new EnvironmentTypeInfo(sku, null, null), "reason") { IsAllowlisted = allowlisted };

        Assert.Equal(label, permission.AllowedLabel);
    }

    [Fact]
    public void The_runs_pane_can_show_only_the_failed_runs()
    {
        var details = DetailsHarness.Details(new Infrastructure.FakeHttpHandler().Quiet(), DetailsHarness.CloudFlow());
        details.Runs.Add(new ProcessRun { Name = "a", Status = "Succeeded", Outcome = RunOutcome.Succeeded });
        details.Runs.Add(new ProcessRun { Name = "b", Status = "Failed", Outcome = RunOutcome.Failed });

        Assert.Equal(1, details.FailedRunCount);
        Assert.Equal(2, details.RunsView.Count);

        details.FailedRunsOnly = true;

        Assert.Equal("b", Assert.Single(details.RunsView.Cast<ProcessRun>()).Name);
    }

    [Fact]
    public void Stopping_a_run_names_both_choices()
    {
        Assert.Equal("Stop after this change", Asking.StopRun.Confirm);
        Assert.Equal("Keep running", Asking.StopRun.Cancel);
    }
}
