using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Admin;

public class MembershipApplyViewModelTests
{
    private static readonly string LogFolder =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ppobjectsearch-tests", Guid.NewGuid().ToString("N"));

    private static readonly WritePermission Allowed =
        WriteGuard.Evaluate(new AppSettings(), "https://dev.crm11.dynamics.com", new EnvironmentTypeInfo(EnvironmentSku.Sandbox, null, null));

    private static readonly WritePermission Blocked =
        WriteGuard.Evaluate(new AppSettings(), "https://prod.crm11.dynamics.com", new EnvironmentTypeInfo(EnvironmentSku.Production, null, null));

    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    /// <summary>Add A, remove B, and remove C which is unticked by default.</summary>
    private static List<MembershipChange> Changes() =>
    [
        new(MembershipChangeKind.Add, A, "Add A", "a@x", "in team"),
        new(MembershipChangeKind.Remove, B, "Remove B", "b@x", "not in team"),
        new(MembershipChangeKind.Remove, C, "Remove C", null, "application user", IncludedByDefault: false),
    ];

    private static MembershipApplyViewModel PerRow(
        WritePermission permission,
        Func<MembershipChange, CancellationToken, Task> applyEach,
        List<MembershipChange>? changes = null) =>
        new(new MembershipApplyRequest
        {
            Operation = "Sync queue members from team",
            TargetKind = "queue",
            TargetName = "Q",
            SourceKind = "team",
            SourceName = "T",
            EnvironmentName = "Env",
            EnvironmentHost = "env.crm11.dynamics.com",
            WriteLogFolder = LogFolder,
            Permission = permission,
            Changes = changes ?? Changes(),
            ApplyEach = applyEach,
            ReadMemberIds = _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())
        });

    private static MembershipApplyViewModel Forecast(
        WritePermission permission,
        Func<CancellationToken, Task> applyAll,
        Func<CancellationToken, Task<IReadOnlySet<Guid>>> readMembers,
        List<MembershipChange>? changes = null) =>
        new(new MembershipApplyRequest
        {
            Operation = "Sync team from Entra group",
            TargetKind = "team",
            TargetName = "Team",
            SourceKind = "Entra group",
            SourceName = "Group",
            EnvironmentName = "Env",
            EnvironmentHost = "env.crm11.dynamics.com",
            WriteLogFolder = LogFolder,
            Permission = permission,
            Changes = changes ?? Changes(),
            ApplyAll = applyAll,
            ReadMemberIds = readMembers
        });

    private static async Task RunAsync(MembershipApplyViewModel vm)
    {
        Assert.True(vm.ApplyCommand.CanExecute(null), "Apply should be available");
        await vm.ApplyCommand.ExecuteAsync(null);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!vm.HasRun)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The run did not finish.");
            await Task.Delay(10);
        }
    }

    private static MembershipChangeRow Row(MembershipApplyViewModel vm, Guid id) =>
        vm.Rows.Single(r => r.Change.SystemUserId == id);

    private static Func<MembershipChange, CancellationToken, Task> Record(List<Guid> into) => (change, _) =>
    {
        lock (into) into.Add(change.SystemUserId);
        return Task.CompletedTask;
    };

    // ---------------------------------------------------------------- the guard

    [Fact]
    public void A_blocked_environment_can_never_apply()
    {
        var vm = PerRow(Blocked, Record([]));
        vm.RemoveAcknowledged = true;

        Assert.True(vm.IsBlocked);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.False(vm.CanEditActions);
        Assert.False(vm.CanAcknowledge);
        Assert.DoesNotContain(vm.Rows, r => r.IsEditable);
        Assert.Contains("AllowProductionWrites", vm.GuardMessage);
    }

    [Fact]
    public void A_blocked_forecast_can_never_apply()
    {
        var vm = Forecast(Blocked, _ => Task.CompletedTask, _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));
        vm.RemoveAcknowledged = true;

        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- what is included

    [Fact]
    public void Rows_unticked_by_default_are_not_counted()
    {
        var vm = PerRow(Allowed, Record([]));

        Assert.Equal(1, vm.AddCount);
        Assert.Equal(1, vm.RemoveCount);
        Assert.Equal(1, vm.AvailableAdds);
        Assert.Equal(2, vm.AvailableRemoves);
        Assert.False(Row(vm, C).IsIncluded);
        Assert.Equal("Skipped", Row(vm, C).ResultLabel);
    }

    [Fact]
    public void Rows_are_listed_adds_first()
    {
        var vm = PerRow(Allowed, Record([]));

        Assert.Equal(MembershipChangeKind.Add, vm.Rows[0].Kind);
    }

    [Fact]
    public void Switching_an_action_off_excludes_its_rows_without_unticking_them()
    {
        var vm = PerRow(Allowed, Record([]));

        vm.Add = false;

        Assert.Equal(0, vm.AddCount);
        Assert.True(Row(vm, A).IsChecked);
        Assert.False(Row(vm, A).IsIncluded);

        vm.Add = true;
        Assert.True(Row(vm, A).IsIncluded);
    }

    [Fact]
    public void Ticking_a_row_includes_it()
    {
        var vm = PerRow(Allowed, Record([]));

        Row(vm, C).IsChecked = true;

        Assert.Equal(2, vm.RemoveCount);
    }

    // ---------------------------------------------------------------- confirming removals

    [Fact]
    public void Removals_need_their_own_acknowledgement()
    {
        var vm = PerRow(Allowed, Record([]));

        Assert.True(vm.HasRemoves);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal("Confirm removals to apply", vm.ApplyLabel);

        vm.RemoveAcknowledged = true;

        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.IsDestructiveApply);
        Assert.Equal("Add 1 · remove 1", vm.ApplyLabel);
    }

    [Fact]
    public void Acknowledgement_names_the_count_and_target()
    {
        var vm = PerRow(Allowed, Record([]));

        Assert.Equal("Yes, remove 1 user(s) from Q.", vm.RemoveAcknowledgement);
    }

    [Fact]
    public void Adds_alone_need_no_acknowledgement()
    {
        var vm = PerRow(Allowed, Record([]), [new(MembershipChangeKind.Add, A, "A", null, "r")]);

        Assert.False(vm.HasRemoves);
        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.Equal("Add 1", vm.ApplyLabel);
    }

    [Fact]
    public void Turning_remove_off_withdraws_the_acknowledgement()
    {
        var vm = PerRow(Allowed, Record([]));
        vm.RemoveAcknowledged = true;

        vm.Remove = false;
        Assert.False(vm.RemoveAcknowledged);
        Assert.Equal("Add 1", vm.ApplyLabel);

        vm.Remove = true;
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Unticking_the_last_removal_withdraws_the_acknowledgement()
    {
        var vm = PerRow(Allowed, Record([]));
        vm.RemoveAcknowledged = true;

        Row(vm, B).IsChecked = false;

        Assert.False(vm.RemoveAcknowledged);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        // Ticking a removal back in needs a fresh acknowledgement.
        Row(vm, B).IsChecked = true;
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Nothing_ticked_means_nothing_to_apply()
    {
        var vm = PerRow(Allowed, Record([]));

        Row(vm, A).IsChecked = false;
        Row(vm, B).IsChecked = false;

        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal("Nothing to apply", vm.ApplyLabel);
    }

    // ---------------------------------------------------------------- per-row runs

    [Fact]
    public async Task Only_ticked_rows_in_switched_on_actions_are_written()
    {
        var written = new List<Guid>();
        var vm = PerRow(Allowed, Record(written));
        vm.RemoveAcknowledged = true;

        await RunAsync(vm);

        Assert.Equal([A, B], written);
        Assert.True(vm.AnyWritesAttempted);
    }

    [Fact]
    public async Task A_switched_off_action_is_never_written()
    {
        var written = new List<Guid>();
        var vm = PerRow(Allowed, Record(written));
        vm.Remove = false;

        await RunAsync(vm);

        Assert.Equal([A], written);
    }

    [Fact]
    public async Task Each_row_reports_its_own_outcome_and_a_failure_does_not_stop_the_run()
    {
        var vm = PerRow(Allowed, (change, _) =>
            change.SystemUserId == A ? throw new InvalidOperationException("boom") : Task.CompletedTask);
        vm.RemoveAcknowledged = true;

        await RunAsync(vm);

        Assert.Equal(false, Row(vm, A).Succeeded);
        Assert.Equal("boom", Row(vm, A).Result);
        Assert.True(Row(vm, A).HasFailed);
        Assert.Equal(true, Row(vm, B).Succeeded);
        Assert.Equal("Removed", Row(vm, B).Result);
        Assert.Contains("1 made, 1 failed", vm.Status);
    }

    [Fact]
    public async Task A_window_runs_once()
    {
        var vm = PerRow(Allowed, Record([]));
        vm.RemoveAcknowledged = true;

        await RunAsync(vm);

        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.False(vm.CanEditActions);
        Assert.DoesNotContain(vm.Rows, r => r.IsEditable);
    }

    [Fact]
    public async Task Stopping_a_run_leaves_the_rest_unwritten()
    {
        var written = new List<Guid>();
        MembershipApplyViewModel? vm = null;

        vm = PerRow(Allowed, async (change, ct) =>
        {
            written.Add(change.SystemUserId);
            vm!.CancelRunCommand.Execute(null);
            await Task.Yield();
        });
        vm.RemoveAcknowledged = true;

        await RunAsync(vm);

        Assert.Equal([A], written);
        Assert.Contains("Stopped", vm.Status);
    }

    [Fact]
    public async Task Closing_the_window_lets_the_change_in_flight_finish_and_writes_no_more()
    {
        var written = new List<Guid>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = PerRow(Allowed, async (change, _) =>
        {
            written.Add(change.SystemUserId);
            started.TrySetResult();
            await release.Task;
        });
        vm.RemoveAcknowledged = true;
        var run = vm.ApplyCommand.ExecuteAsync(null);
        await started.Task;

        vm.Dispose();
        release.SetResult();
        await run;

        Assert.Equal([A], written);
        Assert.Equal("Added", Row(vm, A).Result);
        Assert.Null(Row(vm, B).Succeeded);
        Assert.Equal("Pending", Row(vm, B).Result);
        Assert.StartsWith("Stopped. Changes already made have not been undone.", vm.Status);
        Assert.False(vm.IsRunning);
        Assert.True(vm.HasRun);
        Assert.False(vm.CancelRunCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_request_can_name_its_add_verb_and_success_label()
    {
        var vm = new MembershipApplyViewModel(new MembershipApplyRequest
        {
            Operation = "Pull in group members",
            TargetKind = "team",
            TargetName = "T",
            SourceKind = "Entra group",
            SourceName = "G",
            EnvironmentName = "Env",
            EnvironmentHost = "env.crm11.dynamics.com",
            WriteLogFolder = LogFolder,
            Permission = Allowed,
            Changes = [new(MembershipChangeKind.Add, A, "A", null, "r")],
            AddVerb = "Pull in",
            AddedResult = "User record resolved",
            ApplyEach = (_, _) => Task.CompletedTask,
            ReadMemberIds = _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())
        });

        Assert.Equal("Pull in 1", vm.ApplyLabel);

        await RunAsync(vm);

        Assert.Equal("User record resolved", Row(vm, A).Result);
    }

    private static MembershipApplyViewModel WithFollowUp(
        Func<MembershipChange, CancellationToken, Task> applyEach, Func<CancellationToken, Task> afterAll) =>
        new(new MembershipApplyRequest
        {
            Operation = "Pull in group members",
            TargetKind = "team",
            TargetName = "T",
            SourceKind = "Entra group",
            SourceName = "G",
            EnvironmentName = "Env",
            EnvironmentHost = "env.crm11.dynamics.com",
            WriteLogFolder = LogFolder,
            Permission = Allowed,
            Changes = [new(MembershipChangeKind.Add, A, "A", null, "r"), new(MembershipChangeKind.Add, B, "B", null, "r")],
            ApplyEach = applyEach,
            AfterAll = afterAll,
            AfterAllLabel = "SyncGroupMembersToTeam",
            ReadMemberIds = _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())
        });

    [Fact]
    public async Task The_follow_up_runs_once_after_the_rows()
    {
        var order = new List<string>();
        var vm = WithFollowUp(
            (change, _) => { order.Add(change.Name); return Task.CompletedTask; },
            _ => { order.Add("sync"); return Task.CompletedTask; });

        await RunAsync(vm);

        Assert.Equal(["A", "B", "sync"], order);
        Assert.EndsWith("Then SyncGroupMembersToTeam succeeded.", vm.Status);
    }

    [Fact]
    public async Task The_follow_up_is_skipped_when_every_row_failed()
    {
        var ran = false;
        var vm = WithFollowUp((_, _) => throw new InvalidOperationException("no"), _ => { ran = true; return Task.CompletedTask; });

        await RunAsync(vm);

        Assert.False(ran);
        Assert.DoesNotContain("SyncGroupMembersToTeam", vm.Status);
    }

    [Fact]
    public async Task A_failed_follow_up_is_reported_without_undoing_the_rows()
    {
        var vm = WithFollowUp((_, _) => Task.CompletedTask, _ => throw new InvalidOperationException("sync refused"));

        await RunAsync(vm);

        Assert.All(vm.Rows, r => Assert.Equal(true, r.Succeeded));
        Assert.EndsWith("Then SyncGroupMembersToTeam failed - sync refused", vm.Status);
    }

    // ---------------------------------------------------------------- forecasts (SyncGroupMembersToTeam)

    [Fact]
    public void Forecast_rows_cannot_be_ticked_and_all_count()
    {
        var vm = Forecast(Allowed, _ => Task.CompletedTask, _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));

        Assert.True(vm.IsForecast);
        Assert.False(vm.CanEditActions);
        Assert.DoesNotContain(vm.Rows, r => r.IsEditable);
        Assert.All(vm.Rows, r => Assert.True(r.IsIncluded));
        Assert.Equal(2, vm.RemoveCount);
        Assert.StartsWith("Yes, let Dataverse change the membership of Team", vm.RemoveAcknowledgement);
    }

    [Fact]
    public void Forecast_with_nothing_expected_can_still_run()
    {
        var vm = Forecast(Allowed, _ => Task.CompletedTask, _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), []);

        Assert.True(vm.HasNoRows);
        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.Equal("Run sync", vm.ApplyLabel);
    }

    [Fact]
    public async Task Forecast_calls_the_sync_once_and_reports_which_expected_changes_landed()
    {
        var members = new HashSet<Guid> { B, C };
        var calls = 0;

        var vm = Forecast(Allowed,
            _ =>
            {
                calls++;
                members.Add(A);     // the add landed
                members.Remove(B);  // one removal landed; C stays
                return Task.CompletedTask;
            },
            _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>(members)));
        vm.RemoveAcknowledged = true;

        await RunAsync(vm);

        Assert.Equal(1, calls);
        Assert.Equal("Added", Row(vm, A).Result);
        Assert.Equal("Removed", Row(vm, B).Result);
        Assert.True(Row(vm, C).IsPending);
        Assert.Null(Row(vm, C).Succeeded);
        Assert.Equal("Still a member", Row(vm, C).Result);
        Assert.Contains("2 → 2 members (1 added, 1 removed)", vm.Status);
        Assert.Contains("1 expected change(s) have not landed", vm.Status);
    }

    [Fact]
    public async Task Forecast_failure_is_reported_and_ends_the_run()
    {
        var vm = Forecast(Allowed,
            _ => throw new InvalidOperationException("sync refused"),
            _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), []);

        await RunAsync(vm);

        Assert.Equal("Failed - sync refused", vm.Status);
        Assert.True(vm.AnyWritesAttempted);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- audit

    [Fact]
    public async Task Each_change_made_is_logged_and_the_plan_can_be_exported_first()
    {
        var vm = PerRow(Allowed, (change, _) => change.SystemUserId == B
            ? Task.FromException(new InvalidOperationException("denied"))
            : Task.CompletedTask);
        vm.RemoveAcknowledged = true;

        var plan = vm.PlanLines();
        Assert.Equal(4, plan.Count);
        Assert.Contains(plan, l => l.Contains(",Remove,No,Remove C,"));

        await RunAsync(vm);

        var entries = WriteLog.Read(vm.RunLogPath!);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Action == "Add" && e.Id == A && e.Succeeded);
        Assert.Contains(entries, e => e.Action == "Remove" && e.Id == B && !e.Succeeded && e.Message == "denied");
        Assert.All(entries, e => Assert.Equal("membership", e.Tool));
    }
}
