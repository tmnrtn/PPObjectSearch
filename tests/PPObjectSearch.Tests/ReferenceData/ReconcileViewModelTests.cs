using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

/// <summary>The reconcile window's own decisions: what it offers, what it will send, and when.</summary>
public class ReconcileViewModelTests
{
    private static readonly EntityColumn Code = Col("new_code");
    private static readonly EntityColumn Name = Col("new_name");

    private static readonly WritePermission Allowed =
        WriteGuard.Evaluate(new AppSettings(), Fakes.EnvironmentUrl, new EnvironmentTypeInfo(EnvironmentSku.Sandbox, null, null));

    private static readonly WritePermission Blocked =
        WriteGuard.Evaluate(new AppSettings(), Fakes.EnvironmentUrl, new EnvironmentTypeInfo(EnvironmentSku.Production, null, null));

    /// <summary>One row to create (A), one to update (B), one to delete (C).</summary>
    private static IReadOnlyList<RecordComparison> Rows(bool sourceTruncated = false)
    {
        var plan = Plan(new[] { Code }, new[] { Name });

        return ReferenceDataComparer.Compare(plan,
            new[]
            {
                Row(G(1)).With("new_code", "A").With("new_name", "new").Build(),
                Row(G(2)).With("new_code", "B").With("new_name", "changed").Build()
            },
            new[]
            {
                Row(G(2)).With("new_code", "B").With("new_name", "old").Build(),
                Row(G(3)).With("new_code", "C").With("new_name", "gone").Build()
            },
            sourceTruncated: sourceTruncated).Rows;
    }

    private static FakeHttpHandler Accepting() => new FakeHttpHandler()
        .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent)
        .OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent)
        .OnStatus(HttpMethod.Delete, "new_things(", HttpStatusCode.NoContent);

    private static ReconcileViewModel Window(FakeHttpHandler handler, WritePermission? permission = null, bool sourceTruncated = false) =>
        new(Rows(sourceTruncated), "Dev", "Test", Fakes.Dataverse(handler),
            new Dictionary<string, EntitySummary>(StringComparer.OrdinalIgnoreCase) { [Table] = Entity() },
            permission ?? Allowed);

    [Fact]
    public void Delete_is_off_until_switched_on_and_then_needs_its_own_acknowledgement()
    {
        var vm = Window(Accepting());

        Assert.False(vm.Delete);
        Assert.False(vm.HasDeletes);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        vm.Delete = true;

        Assert.True(vm.HasDeletes);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal("Confirm deletion to apply", vm.ApplyLabel);

        vm.DeleteAcknowledged = true;

        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.IsDestructiveApply);

        vm.Delete = false;
        Assert.False(vm.DeleteAcknowledged);
    }

    [Fact]
    public async Task An_unacknowledged_delete_is_refused_even_when_the_run_is_started_directly()
    {
        var handler = Accepting();
        var vm = Window(handler);
        vm.Delete = true;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(handler.Requests);
        Assert.False(vm.HasRun);
    }

    [Fact]
    public async Task Actions_switched_off_are_never_sent()
    {
        var handler = Accepting();
        var vm = Window(handler);
        vm.Update = false;

        await vm.ApplyCommand.ExecuteAsync(null);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("Skipped", vm.Rows.Single(r => r.Action == ReconcileAction.Update).ResultLabel);
        Assert.True(vm.AnyWritesSucceeded);
    }

    [Fact]
    public async Task A_target_the_guard_refuses_gets_nothing_at_all()
    {
        var handler = Accepting();
        var vm = Window(handler, Blocked);

        Assert.True(vm.IsBlocked);
        Assert.False(vm.ApplyCommand.CanExecute(null));

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rows_a_truncated_read_cannot_vouch_for_are_listed_but_never_written()
    {
        var handler = Accepting();
        var vm = Window(handler, sourceTruncated: true);
        vm.Delete = true;

        // The only delete rests on a source read cut short at the cap, so there is nothing to acknowledge.
        var delete = vm.Rows.Single(r => r.Action == ReconcileAction.Delete);
        Assert.True(delete.IsBlocked);
        Assert.False(delete.IsIncluded);
        Assert.False(vm.HasDeletes);
        Assert.Contains("row cap", delete.ResultLabel);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Stopping_ends_the_run_after_the_row_being_written()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new FakeHttpHandler()
            .OnAsync(HttpMethod.Post, "new_things", _ => release.Task)
            .OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent);
        var vm = Window(handler);

        var run = vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);

        vm.CancelRunCommand.Execute(null);
        release.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        await run;

        Assert.Single(handler.Requests);
        Assert.StartsWith("Stopped after 1 row(s)", vm.Status);
        Assert.False(vm.IsRunning);
        Assert.True(vm.HasRun);
    }

    [Fact]
    public async Task Failed_rows_can_be_retried_and_only_they_are_sent_again()
    {
        var patches = 0;
        var handler = new FakeHttpHandler()
            .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent)
            .On(HttpMethod.Patch, "new_things(", _ => ++patches == 1
                ? FakeHttpHandler.Json(FakeHttpHandler.ErrorJson("throttled"), HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        var vm = Window(handler);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.HasFailures);
        Assert.True(vm.RetryFailedCommand.CanExecute(null));

        await vm.RetryFailedCommand.ExecuteAsync(null);

        Assert.False(vm.HasFailures);
        Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Post));
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Patch));
    }
}
