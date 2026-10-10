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

    /// <summary>The target row as read just before a write: B before its update, C before its delete.</summary>
    private static string Snapshot(RecordedRequest request) =>
        request.Url.Contains(G(3).ToString())
            ? $$"""{"@odata.etag":"W/\"9\"","{{PrimaryId}}":"{{G(3)}}","new_code":"C","new_name":"gone","createdon":"2026-01-01T00:00:00Z"}"""
            : $$"""{"@odata.etag":"W/\"8\"","{{PrimaryId}}":"{{G(2)}}","new_code":"B","new_name":"old"}""";

    private const string TargetColumns = """
        {"value":[
          {"LogicalName":"new_thingid","AttributeTypeName":{"Value":"UniqueidentifierType"},"IsPrimaryId":true,"IsValidForCreate":true},
          {"LogicalName":"new_code","AttributeTypeName":{"Value":"StringType"},"IsValidForCreate":true},
          {"LogicalName":"new_name","AttributeTypeName":{"Value":"StringType"},"IsValidForCreate":true},
          {"LogicalName":"createdon","AttributeTypeName":{"Value":"DateTimeType"},"IsValidForCreate":false}
        ]}
        """;

    private static FakeHttpHandler Accepting(string relationships = """{"value":[]}""") => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "/OneToManyRelationships", relationships)
        .On(HttpMethod.Get, "new_things(", r => FakeHttpHandler.Json(Snapshot(r)))
        .OnJson(HttpMethod.Get, "/Attributes?", TargetColumns)
        .OnStatus(HttpMethod.Post, "new_things", HttpStatusCode.NoContent)
        .OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent)
        .OnStatus(HttpMethod.Delete, "new_things(", HttpStatusCode.NoContent);

    private static ReconcileViewModel Window(FakeHttpHandler handler, WritePermission? permission = null, bool sourceTruncated = false) =>
        new(Rows(sourceTruncated), "Dev",
            new ReconcileTarget("Test", Fakes.Dataverse(handler),
                new Dictionary<string, EntitySummary>(StringComparer.OrdinalIgnoreCase) { [Table] = Entity() }),
            permission ?? Allowed, writeLogFolder: LogFolder)
        {
            Confirm = _ => true
        };

    private static readonly string LogFolder =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ppobjectsearch-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Delete_is_off_until_switched_on_and_then_needs_its_own_acknowledgement()
    {
        var vm = Window(Accepting());

        Assert.False(vm.Delete);
        Assert.False(vm.HasDeletes);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        vm.Delete = true;
        await (vm.ImpactCheck ?? Task.CompletedTask);

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
        await (vm.ImpactCheck ?? Task.CompletedTask);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
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
        await (vm.ImpactCheck ?? Task.CompletedTask);

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
            .On(HttpMethod.Get, "new_things(", r => FakeHttpHandler.Json(Snapshot(r)))
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

    [Fact]
    public async Task Every_write_is_logged_with_the_row_as_it_was()
    {
        var vm = Window(Accepting());
        vm.Delete = true;
        await (vm.ImpactCheck ?? Task.CompletedTask);
        vm.DeleteAcknowledged = true;

        await vm.ApplyCommand.ExecuteAsync(null);

        var entries = WriteLog.Read(vm.RunLogPath!);
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.True(e.Succeeded));

        var update = entries.Single(e => e.Action == "Update");
        Assert.Equal("old", update.Before!["new_name"]!.GetValue<string>());
        Assert.Equal("changed", update.After!["new_name"]!.GetValue<string>());
        Assert.Equal(new[] { "new_name" }, update.Columns);

        var delete = entries.Single(e => e.Action == "Delete");
        Assert.Equal(G(3), delete.Id);
        Assert.Equal("gone", delete.Before!["new_name"]!.GetValue<string>());
        Assert.Equal(UndoMethod.Create, delete.Undo!.Method);
    }

    [Fact]
    public async Task Undo_reverses_each_write_newest_first()
    {
        var handler = Accepting();
        var vm = Window(handler);
        vm.Delete = true;
        await (vm.ImpactCheck ?? Task.CompletedTask);
        vm.DeleteAcknowledged = true;

        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.CanUndo);
        Assert.Equal(3, vm.UndoCount);

        var before = handler.Requests.Count;
        await vm.UndoCommand.ExecuteAsync(null);

        var undo = handler.Requests.Skip(before).ToList();
        Assert.Equal(3, undo.Count);

        // The delete ran last, so it is put back first: same id, every column the target accepts on a create.
        Assert.Equal(HttpMethod.Post, undo[0].Method);
        Assert.Contains($"\"{PrimaryId}\":\"{G(3)}\"", undo[0].Body);
        Assert.Contains("\"new_name\":\"gone\"", undo[0].Body);
        Assert.DoesNotContain("createdon", undo[0].Body);

        Assert.Equal(HttpMethod.Patch, undo[1].Method);
        Assert.Contains(G(2).ToString(), undo[1].Url);
        Assert.Equal("{\"new_name\":\"old\"}", undo[1].Body);

        Assert.Equal(HttpMethod.Delete, undo[2].Method);
        Assert.Contains(G(1).ToString(), undo[2].Url);

        Assert.StartsWith("Undone - 3 write(s)", vm.Status);
        Assert.False(vm.CanUndo);
        Assert.Contains(WriteLog.Read(vm.RunLogPath!), e => e.Action == "UndoCreate" && e.Succeeded);
    }

    [Fact]
    public async Task A_row_that_cannot_be_copied_first_is_not_written()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "new_things(", HttpStatusCode.Forbidden, "no read")
            .OnStatus(HttpMethod.Patch, "new_things(", HttpStatusCode.NoContent);
        var vm = Window(handler);
        vm.Create = false;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("copy of the row", vm.Rows.Single(r => r.Action == ReconcileAction.Update).Result);
    }

    [Fact]
    public void The_plan_exports_one_line_per_changed_column()
    {
        var vm = Window(Accepting());

        var lines = ReconcilePlanExport.Lines(vm.Rows.Select(r => (r.Item, r.IsIncluded, r.ResultLabel)), "Dev", "Test");

        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.StartsWith("new_thing,Update,Yes,B,") && l.Contains(",new_name,changed,old,"));
        Assert.Contains(lines, l => l.StartsWith("new_thing,Delete,No,C,"));
    }

    [Fact]
    public async Task A_column_left_out_of_the_run_is_not_written_and_an_update_with_nothing_left_is_skipped()
    {
        var handler = Accepting();
        var vm = Window(handler);

        var name = Assert.Single(vm.Columns);
        Assert.Equal("new_name", name.Column.LogicalName);

        name.IsIncluded = false;

        var update = vm.Rows.Single(r => r.Action == ReconcileAction.Update);
        Assert.Equal("Skipped", update.ResultLabel);
        Assert.Equal("Apply 1 change", vm.ApplyLabel);
        Assert.Contains("1 of 1 left out", vm.ColumnsSummary);

        await vm.ApplyCommand.ExecuteAsync(null);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.DoesNotContain("new_name", sent.Body);
    }

    [Fact]
    public async Task What_else_a_delete_reaches_is_counted_before_it_can_be_acknowledged()
    {
        const string relationships = """
            {"value":[
              {"ReferencingEntity":"new_child","ReferencingAttribute":"new_thingid","CascadeConfiguration":{"Delete":"Cascade"}},
              {"ReferencingEntity":"new_note","ReferencingAttribute":"new_thingid","CascadeConfiguration":{"Delete":"NoCascade"}}
            ]}
            """;
        var handler = Accepting(relationships)
            .OnJson(HttpMethod.Get, "new_children?", """{"@odata.count":7,"value":[]}""");
        var vm = new ReconcileViewModel(Rows(), "Dev",
            new ReconcileTarget("Test", Fakes.Dataverse(handler),
                new Dictionary<string, EntitySummary>(StringComparer.OrdinalIgnoreCase)
                {
                    [Table] = Entity(),
                    ["new_child"] = Entity("new_child", "new_children", "new_childid")
                }),
            Allowed, writeLogFolder: LogFolder);

        vm.Delete = true;
        Assert.True(vm.IsCheckingImpact || vm.HasDeleteImpact);
        await vm.ImpactCheck!;

        Assert.False(vm.IsCheckingImpact);
        Assert.Equal("Dataverse will also delete 7 new_child (via new_thingid).", vm.DeleteImpactText);
        var count = Assert.Single(handler.Requests, r => r.Url.Contains("new_children?"));
        Assert.Contains($"_new_thingid_value eq {G(3)}", Uri.UnescapeDataString(count.Url));
    }
}
