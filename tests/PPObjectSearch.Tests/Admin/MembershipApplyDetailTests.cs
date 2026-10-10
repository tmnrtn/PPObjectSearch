using System.IO;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Admin;

/// <summary>The membership confirmation window's header, labels and footer, and runs stopped from inside a change.</summary>
public sealed class MembershipApplyDetailTests : IDisposable
{
    private readonly string _logFolder = Path.Combine(Path.GetTempPath(), "ppobjectsearch-tests", Guid.NewGuid().ToString("N"));

    private static readonly WritePermission Allowed =
        WriteGuard.Evaluate(new AppSettings(), "https://dev.crm11.dynamics.com", new EnvironmentTypeInfo(EnvironmentSku.Sandbox, null, null));

    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    public void Dispose()
    {
        try { Directory.Delete(_logFolder, recursive: true); } catch (DirectoryNotFoundException) { /* Nothing was written. */ }
    }

    private MembershipApplyViewModel PerRow(
        Func<MembershipChange, CancellationToken, Task> applyEach,
        string? note = null,
        Func<CancellationToken, Task>? afterAll = null) =>
        new(new MembershipApplyRequest
        {
            Operation = "Sync queue members from team",
            TargetKind = "queue",
            TargetName = "Support",
            SourceKind = "team",
            SourceName = "Service Desk",
            EnvironmentName = "Contoso Dev",
            EnvironmentHost = "dev.crm11.dynamics.com",
            WriteLogFolder = _logFolder,
            Permission = Allowed,
            Note = note,
            Changes =
            [
                new(MembershipChangeKind.Add, A, "Alice", "alice@contoso.com", "In the team."),
                new(MembershipChangeKind.Remove, B, "Bob", "bob@contoso.com", "Not in the team.")
            ],
            ApplyEach = applyEach,
            AfterAll = afterAll,
            AfterAllLabel = "Team sync",
            ReadMemberIds = _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())
        });

    [Fact]
    public void The_header_names_the_change_the_target_and_the_environment()
    {
        var vm = PerRow((_, _) => Task.CompletedTask, note: "Check the plan first.");

        Assert.Equal("Sync queue members from team — Support", vm.Title);
        Assert.Equal(("queue", "team", "Service Desk"), (vm.TargetKind, vm.SourceKind, vm.SourceName));
        Assert.Equal(("Contoso Dev", "dev.crm11.dynamics.com"), (vm.EnvironmentName, vm.EnvironmentHost));
        Assert.Equal("Check the plan first.", vm.Note);
        Assert.True(vm.HasNote);
        Assert.True(vm.IsPerRow);
        Assert.False(vm.IsForecast);
        Assert.True(vm.IsNotRunning);
        Assert.False(vm.HasNoRows);
        Assert.Null(vm.RunLogPath);
        Assert.Equal((1, 1), (vm.AvailableAdds, vm.AvailableRemoves));
    }

    [Fact]
    public void A_request_without_a_note_shows_none()
    {
        var vm = PerRow((_, _) => Task.CompletedTask, note: "  ");

        Assert.False(vm.HasNote);
    }

    [Fact]
    public void The_apply_button_says_what_it_will_do()
    {
        var vm = PerRow((_, _) => Task.CompletedTask);

        Assert.Equal("Confirm removals to apply", vm.ApplyLabel);

        vm.RemoveAcknowledged = true;
        Assert.Equal("Add 1 · remove 1", vm.ApplyLabel);
        Assert.True(vm.IsDestructiveApply);

        vm.Add = false;
        Assert.Equal("Remove 1", vm.ApplyLabel);

        vm.Remove = false;
        Assert.Equal("Nothing to apply", vm.ApplyLabel);
        Assert.False(vm.IsDestructiveApply);
    }

    [Fact]
    public void The_footer_shows_the_summary_until_a_run_says_something()
    {
        var vm = PerRow((_, _) => Task.CompletedTask);

        Assert.Equal(string.Empty, vm.Status);
        Assert.Equal(vm.Summary, vm.FooterText);
        Assert.Equal("1 to add · 1 to remove, one user at a time. Untick anyone to leave them as they are.", vm.FooterText);
    }

    [Fact]
    public async Task A_change_that_is_cancelled_stops_the_run()
    {
        var written = new List<Guid>();
        var vm = PerRow((change, _) =>
        {
            written.Add(change.SystemUserId);
            throw new OperationCanceledException();
        });
        vm.RemoveAcknowledged = true;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal([A], written);
        Assert.Equal("Stopped. Changes already made have not been undone.", vm.Status);
        Assert.Equal(vm.Status, vm.FooterText);
        Assert.True(vm.HasRun);
        Assert.True(vm.AnyWritesAttempted);
        Assert.NotNull(vm.RunLogPath);
    }

    [Fact]
    public async Task A_follow_up_that_is_cancelled_stops_the_run()
    {
        var vm = PerRow((_, _) => Task.CompletedTask, afterAll: _ => throw new OperationCanceledException());
        vm.Remove = false;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Stopped. Changes already made have not been undone.", vm.Status);
        Assert.Equal("Added", vm.Rows[0].ResultLabel);
        Assert.Equal("Skipped", vm.Rows[1].ResultLabel);
    }
}
