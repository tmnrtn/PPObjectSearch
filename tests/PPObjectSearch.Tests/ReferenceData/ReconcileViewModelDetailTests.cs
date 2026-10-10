using System.IO;
using System.Net;
using System.Net.Http;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

/// <summary>What the reconcile window shows about the run it offers, and how it reports trouble.</summary>
public sealed class ReconcileViewModelDetailTests : IDisposable
{
    private static readonly EntityColumn Code = Col("new_code");
    private static readonly EntityColumn Name = Col("new_name", displayName: "Name");

    private readonly string _logFolder = Path.Combine(Path.GetTempPath(), "ppos-reconcile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_logFolder, recursive: true); } catch (IOException) { /* best effort: a temp folder left behind is harmless */ }
    }

    private static WritePermission Permission(EnvironmentSku sku, AppSettings? settings = null) =>
        WriteGuard.Evaluate(settings ?? new AppSettings(), Fakes.EnvironmentUrl, new EnvironmentTypeInfo(sku, null, null));

    /// <summary>Creates A and E, updates B, deletes C, and leaves D alone.</summary>
    private static IReadOnlyList<RecordComparison> Rows(bool sourceTruncated = false)
    {
        var plan = Plan(new[] { Code }, new[] { Name });

        return ReferenceDataComparer.Compare(plan,
            new[]
            {
                Row(G(1)).With("new_code", "A").With("new_name", "new").Named("Alpha").Build(),
                Row(G(2)).With("new_code", "B").With("new_name", "changed").Build(),
                Row(G(4)).With("new_code", "D").With("new_name", "same").Build(),
                Row(G(5)).With("new_code", "E").With("new_name", "also new").Build()
            },
            new[]
            {
                Row(G(2)).With("new_code", "B").With("new_name", "old").Build(),
                Row(G(3)).With("new_code", "C").With("new_name", "gone").Build(),
                Row(G(4)).With("new_code", "D").With("new_name", "same").Build()
            },
            sourceTruncated: sourceTruncated).Rows;
    }

    private const string TargetColumns = """
        {"value":[
          {"LogicalName":"new_thingid","AttributeTypeName":{"Value":"UniqueidentifierType"},"IsPrimaryId":true},
          {"LogicalName":"new_code","AttributeTypeName":{"Value":"StringType"}},
          {"LogicalName":"new_name","AttributeTypeName":{"Value":"StringType"}}
        ]}
        """;

    /// <summary>Accepts every write until told to refuse them.</summary>
    private sealed class Target
    {
        /// <summary>The target row as read just before a write: B before its update, C before its delete.</summary>
        private static string Snapshot(RecordedRequest request) =>
            request.Url.Contains(G(3).ToString())
                ? $$"""{"@odata.etag":"W/\"9\"","{{PrimaryId}}":"{{G(3)}}","new_code":"C","new_name":"gone"}"""
                : $$"""{"@odata.etag":"W/\"8\"","{{PrimaryId}}":"{{G(2)}}","new_code":"B","new_name":"old"}""";

        public bool Refusing { get; set; }

        public FakeHttpHandler Handler { get; }

        public Target(string relationships = """{"value":[]}""")
        {
            HttpResponseMessage Write() => Refusing
                ? FakeHttpHandler.Json(FakeHttpHandler.ErrorJson("refused"), HttpStatusCode.BadRequest)
                : new HttpResponseMessage(HttpStatusCode.NoContent);

            Handler = new FakeHttpHandler()
                .OnJson(HttpMethod.Get, "/OneToManyRelationships", relationships)
                .On(HttpMethod.Get, "new_things(", r => FakeHttpHandler.Json(Snapshot(r)))
                .OnJson(HttpMethod.Get, "/Attributes?", TargetColumns)
                .On(HttpMethod.Post, "new_things", _ => Write())
                .On(HttpMethod.Patch, "new_things(", _ => Write())
                .On(HttpMethod.Delete, "new_things(", _ => Write());
        }
    }

    private ReconcileViewModel Window(FakeHttpHandler handler, WritePermission? permission = null, bool sourceTruncated = false) =>
        new(Rows(sourceTruncated), "Dev",
            new ReconcileTarget("Test", Fakes.Dataverse(handler),
                new Dictionary<string, EntitySummary>(StringComparer.OrdinalIgnoreCase) { [Table] = Entity() }, "maria@contoso.com"),
            permission ?? Permission(EnvironmentSku.Sandbox), EnvironmentSku.Developer, _logFolder)
        {
            Confirm = _ => true
        };

    [Fact]
    public void The_header_names_both_environments_and_what_is_on_offer()
    {
        var vm = Window(new Target().Handler);

        Assert.Equal("Reconcile — Dev → Test", vm.Title);
        Assert.Equal("Dev", vm.SourceName);
        Assert.Equal(EnvironmentSku.Developer, vm.SourceSku);
        Assert.Equal(EnvironmentSku.Sandbox, vm.TargetSku);
        Assert.Equal("contoso.crm11.dynamics.com", vm.TargetHost);
        Assert.StartsWith("Test  -  ", vm.TargetDescription);
        Assert.DoesNotContain("allowlisted", vm.TargetDescription);
        Assert.Equal(vm.Permission.Reason, vm.GuardMessage);
        Assert.Equal(2, vm.AvailableCreates);
        Assert.Equal(1, vm.AvailableUpdates);
        Assert.Equal(1, vm.AvailableDeletes);
        Assert.True(vm.CanEditActions);
        Assert.True(vm.IsNotRunning);
        Assert.True(vm.CanAcknowledgeDelete);
        Assert.True(vm.ExportPlanCommand.CanExecute(null));
        Assert.True(vm.OpenRunLogCommand.CanExecute(null));
        Assert.Null(vm.RunLogPath);
    }

    [Fact]
    public void An_allowlisted_production_target_says_why_it_may_be_written()
    {
        var settings = new AppSettings { AllowProductionWrites = new List<string> { Fakes.EnvironmentUrl } };

        var vm = Window(new Target().Handler, Permission(EnvironmentSku.Production, settings));

        Assert.True(vm.Permission.Allowed);
        Assert.EndsWith("  (allowlisted for writes)", vm.TargetDescription);
    }

    [Fact]
    public void The_footer_summarises_the_run_and_what_was_left_out_of_it()
    {
        var vm = Window(new Target().Handler);

        Assert.Equal(
            "3 writes: 2 create · 1 update · 0 delete. Only compared columns are written; rows are written one at a time. " +
            "1 of the selected rows need nothing.",
            vm.Summary);
        Assert.Equal(vm.Summary, vm.FooterText);
        Assert.Equal("Apply 3 changes", vm.ApplyLabel);

        vm.Create = false;

        Assert.StartsWith("1 write: 0 create · 1 update", vm.Summary);
        Assert.Equal("Apply 1 change", vm.ApplyLabel);
    }

    [Fact]
    public void Rows_and_columns_describe_themselves_for_the_grid()
    {
        var vm = Window(new Target().Handler);

        var create = vm.Rows.First(r => r.Action == ReconcileAction.Create);
        Assert.Equal("A", create.Key);
        Assert.Equal("Alpha", create.Name);
        Assert.False(string.IsNullOrEmpty(create.Detail));
        Assert.False(create.IsDelete);
        Assert.Equal("Pending", create.ResultLabel);

        var column = Assert.Single(vm.Columns);
        Assert.True(vm.HasColumns);
        Assert.Equal("new_thing · Name (new_name)", column.Label);
        Assert.Equal("Columns: all 1 written", vm.ColumnsSummary);
    }

    [Fact]
    public void Rows_a_capped_read_cannot_vouch_for_are_counted_in_the_summary()
    {
        var vm = Window(new Target().Handler, sourceTruncated: true);

        Assert.EndsWith(" 1 row(s) cannot be written safely - see Result.", vm.Summary);
    }

    [Fact]
    public async Task The_delete_acknowledgement_names_how_many_rows_go_and_from_where()
    {
        var vm = Window(new Target().Handler);

        vm.Delete = true;
        await vm.ImpactCheck!;

        Assert.StartsWith("Yes, delete 1 row(s) from Test.", vm.DeleteAcknowledgement);
        Assert.Equal("Confirm deletion to apply", vm.ApplyLabel);
    }

    [Fact]
    public async Task A_failed_impact_check_is_shown_rather_than_hidden()
    {
        var handler = new FakeHttpHandler().OnError(HttpMethod.Get, "/OneToManyRelationships", HttpStatusCode.Forbidden, "no metadata");
        var vm = Window(handler);

        vm.Delete = true;
        await vm.ImpactCheck!;

        Assert.StartsWith("Could not check what else these deletes would reach - ", vm.DeleteImpactText);
        Assert.Contains("no metadata", vm.DeleteImpactText);
        Assert.True(vm.HasDeleteImpact);
        Assert.False(vm.IsCheckingImpact);
    }

    [Fact]
    public async Task Switching_delete_off_mid_check_drops_the_check()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler().OnAsync(HttpMethod.Get, "/OneToManyRelationships", _ => release.Task);
        var vm = Window(handler);

        vm.Delete = true;
        var check = vm.ImpactCheck!;
        Assert.True(vm.IsCheckingImpact);
        Assert.False(vm.CanAcknowledgeDelete);

        vm.Delete = false;
        release.SetResult(FakeHttpHandler.Json("""{"value":[]}"""));
        await check;

        Assert.Equal(string.Empty, vm.DeleteImpactText);
        Assert.False(vm.HasDeleteImpact);
        Assert.False(vm.IsCheckingImpact);
    }

    [Fact]
    public async Task Undo_does_nothing_unless_confirmed()
    {
        var target = new Target();
        var vm = Window(target.Handler);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.False(vm.CanEditActions);
        var before = target.Handler.Requests.Count;
        string? question = null;
        vm.Confirm = message => { question = message; return false; };

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.StartsWith("Undo 3 write(s) in Test?", question);
        Assert.Equal(before, target.Handler.Requests.Count);
        Assert.True(vm.CanUndo);
    }

    [Fact]
    public async Task Undo_steps_that_fail_are_listed_and_logged()
    {
        var target = new Target();
        var vm = Window(target.Handler);
        vm.Delete = true;
        await vm.ImpactCheck!;
        vm.DeleteAcknowledged = true;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.UndoCount);
        Assert.StartsWith("Done - 4 row(s) written to Test.", vm.FooterText);

        target.Refusing = true;
        await vm.UndoCommand.ExecuteAsync(null);

        Assert.StartsWith("Undo finished with problems - 4 of 4 could not be reversed: ", vm.Status);
        Assert.EndsWith(" ...", vm.Status);
        Assert.False(vm.CanUndo);
        Assert.True(vm.AnyWritesSucceeded);

        var undo = WriteLog.Read(vm.RunLogPath!).Where(e => e.Action.StartsWith("Undo")).ToList();
        Assert.Equal(4, undo.Count);
        Assert.All(undo, e => Assert.False(e.Succeeded));
        Assert.All(undo, e => Assert.Equal("maria@contoso.com", e.Account));
    }
}
