using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ReferenceData.RefData;

namespace PPObjectSearch.Tests.ReferenceData;

/// <summary>The reference data window: reading both sides, judging the rows, and keeping configurations.</summary>
public sealed class ReferenceDataCompareViewModelTests : IDisposable
{
    private const string DevUrl = "https://dev.crm11.dynamics.com";
    private const string TestUrl = "https://test.crm11.dynamics.com";
    private const string EntityList = "EntityDefinitions?$select=LogicalName";
    private const string Attributes = "EntityDefinitions(LogicalName='new_thing')/Attributes";
    private const string RowsUrl = "new_things?$select=";
    private const string BapList = "BusinessAppPlatform/environments?";

    private const string ColumnsJson = """
        {"value":[
          {"LogicalName":"new_thingid","AttributeTypeName":{"Value":"UniqueidentifierType"},"IsPrimaryId":true},
          {"LogicalName":"new_code","AttributeTypeName":{"Value":"StringType"}},
          {"LogicalName":"new_name","AttributeTypeName":{"Value":"StringType"},"IsPrimaryName":true},
          {"LogicalName":"new_parentid","AttributeTypeName":{"Value":"LookupType"}},
          {"LogicalName":"createdon","AttributeTypeName":{"Value":"DateTimeType"}}
        ]}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ppos-refdata-" + Guid.NewGuid().ToString("N"));

    public ReferenceDataCompareViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort: a temp folder left behind is harmless */ }
    }

    // ---------------------------------------------------------------- arranging

    /// <summary>Settings read from a broken file, which the app never saves over - so nothing here
    /// reaches the real settings file.</summary>
    private AppSettings Settings(params ReferenceDataConfig[] configs)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");

        var settings = AppSettings.Load(path);
        settings.ReferenceDataConfigurations = configs.ToList();
        return settings;
    }

    private static EnvironmentSessionViewModel Session(string url, FakeHttpHandler? handler)
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(), new TabState { EnvironmentUrl = url });
        if (handler is not null) session.UseConnectedClient(Fakes.Dataverse(handler, url));
        return session;
    }

    private static string EntitiesJson(params string[] names) => JsonSerializer.Serialize(new
    {
        value = names.Select(n => new
        {
            LogicalName = n,
            DisplayName = new { UserLocalizedLabel = new { Label = n == Table ? "Thing" : "Other" } },
            EntitySetName = n + "s",
            PrimaryIdAttribute = n + "id",
            PrimaryNameAttribute = "new_name"
        })
    });

    private static Dictionary<string, object?> Record(int id, string code, string name, int parent) => new()
    {
        ["@odata.etag"] = "W/\"1\"",
        [PrimaryId] = G(id),
        ["new_code"] = code,
        ["new_name"] = name,
        ["_new_parentid_value"] = G(parent),
        ["_new_parentid_value@OData.Community.Display.V1.FormattedValue"] = "Parent",
        ["createdon"] = "2026-01-0" + id + "T00:00:00Z"
    };

    private static string RowsJson(params Dictionary<string, object?>[] rows) => JsonSerializer.Serialize(new { value = rows });

    /// <summary>Only in source (A), different (B), the same (D); the parent lookups agree by label but not by id.</summary>
    private static readonly string SourceRows = RowsJson(Record(1, "A", "Alpha", 10), Record(2, "B", "Beta", 10), Record(4, "D", "Delta", 10));

    /// <summary>Different (B), only in target (C), the same (D).</summary>
    private static readonly string TargetRows = RowsJson(Record(2, "B", "Beta old", 11), Record(3, "C", "Gamma", 11), Record(4, "D", "Delta", 11));

    private static FakeHttpHandler Side(string rows, params string[] tables) => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, EntityList, EntitiesJson(tables.Length == 0 ? new[] { Table, "new_other" } : tables))
        .OnJson(HttpMethod.Get, Attributes, ColumnsJson)
        .OnJson(HttpMethod.Get, RowsUrl, rows);

    private static string BapEnvironments(string sku) => JsonSerializer.Serialize(new
    {
        value = new[]
        {
            new { name = "env-test", properties = new { environmentSku = sku, linkedEnvironmentMetadata = new { instanceApiUrl = TestUrl + "/" } } }
        }
    });

    private static ReferenceEntityConfig Thing(Action<ReferenceEntityConfig>? configure = null)
    {
        var config = new ReferenceEntityConfig { LogicalName = Table, DisplayName = "Thing" };
        configure?.Invoke(config);
        return config;
    }

    private static ReferenceDataConfig Config(string name = "Lookups", params ReferenceEntityConfig[] entities) => new()
    {
        Name = name,
        Entities = entities.Length == 0 ? new List<ReferenceEntityConfig> { Thing() } : entities.ToList()
    };

    private ReferenceDataCompareViewModel Window(FakeHttpHandler? source, FakeHttpHandler? target, params ReferenceDataConfig[] configs) =>
        Window(source, target, Settings(configs));

    private static ReferenceDataCompareViewModel Window(FakeHttpHandler? source, FakeHttpHandler? target, AppSettings settings) =>
        new(new[] { Session(DevUrl, source), Session(TestUrl, target) }, settings);

    private async Task<ReferenceDataCompareViewModel> Compared(FakeHttpHandler? source = null, FakeHttpHandler? target = null)
    {
        var vm = Window(source ?? Side(SourceRows), target ?? Side(TargetRows), Config());
        await vm.CompareCommand.ExecuteAsync(null);
        return vm;
    }

    private static List<RecordComparison> Visible(ReferenceDataCompareViewModel vm) => vm.RowsView.Cast<RecordComparison>().ToList();

    private static RecordComparison Row(ReferenceDataCompareViewModel vm, RecordCompareStatus status) =>
        vm.Rows.Single(r => r.Status == status);

    /// <summary>One row of each kind, without reading anything.</summary>
    private static List<RecordComparison> Selection()
    {
        var plan = Plan(new[] { Col("new_code") }, new[] { Col("new_name") });

        return new List<RecordComparison>
        {
            Comparison(plan, RecordCompareStatus.OnlyInSource, source: RefData.Row(G(1)).With("new_code", "A").Build(), key: "A"),
            Comparison(plan, RecordCompareStatus.Different,
                source: RefData.Row(G(2)).With("new_code", "B").With("new_name", "new").Build(),
                target: RefData.Row(G(2)).With("new_code", "B").With("new_name", "old").Build(), key: "B",
                differences: new[] { new ColumnComparison { Column = Col("new_name"), SourceValue = "new", TargetValue = "old", IsDifferent = true } }),
            Comparison(plan, RecordCompareStatus.OnlyInTarget, target: RefData.Row(G(3)).With("new_code", "C").Build(), key: "C"),
            Comparison(plan, RecordCompareStatus.Same,
                source: RefData.Row(G(4)).With("new_code", "D").Build(),
                target: RefData.Row(G(4)).With("new_code", "D").Build(), key: "D")
        };
    }

    // ---------------------------------------------------------------- opening

    private static readonly string[] ConnectedTitles = ["dev", "test"];

    [Fact]
    public void Only_connected_environments_are_offered_and_the_first_saved_configuration_is_loaded()
    {
        var sessions = new[] { Session(DevUrl, Side(SourceRows)), Session("https://offline.crm11.dynamics.com", null), Session(TestUrl, Side(TargetRows)) };
        var config = Config("Lookups", Thing(), Thing(c => { c.LogicalName = "new_other"; c.IsEnabled = false; }));
        config.MatchLookupsByName = false;
        config.MaxRowsPerEntity = 250;

        var vm = new ReferenceDataCompareViewModel(sessions, Settings(config, Config("Second")));

        Assert.Equal(ConnectedTitles, vm.Sessions.Select(s => s.Title));
        Assert.Equal("dev", vm.SourceHeader);
        Assert.Equal("test", vm.TargetHeader);
        Assert.Equal(2, vm.Configurations.Count);
        Assert.Equal("Lookups", vm.SelectedConfiguration!.Name);
        Assert.Equal("Lookups", vm.ConfigurationName);
        Assert.False(vm.MatchLookupsByName);
        Assert.Equal(250, vm.MaxRowsPerEntity);
        Assert.Equal(new[] { Table, "new_other" }, vm.Entities.Select(e => e.LogicalName));
        Assert.Equal("Tables · 1 of 2 checked", vm.TablesHeading);
        Assert.False(vm.IsDirty);
        Assert.True(vm.IsIdle);
        Assert.True(vm.CompareCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.WriteStatus);
        Assert.StartsWith("Pick a source and a target", vm.Status);
    }

    [Fact]
    public void Compare_waits_for_two_different_environments_and_a_ticked_table()
    {
        var vm = Window(Side(SourceRows), null, Config());

        Assert.Null(vm.Target);
        Assert.Equal("Target", vm.TargetHeader);
        Assert.False(vm.CompareCommand.CanExecute(null));

        vm.Target = vm.Source;
        Assert.False(vm.CompareCommand.CanExecute(null));

        var empty = Window(null, null);
        Assert.Equal("Source", empty.SourceHeader);
        Assert.Equal("Tables", empty.TablesHeading);
        Assert.False(empty.AddEntitiesCommand.CanExecute(null));
        Assert.False(empty.RenameConfigurationCommand.CanExecute(null));
        Assert.False(empty.DeleteConfigurationCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- comparing

    [Fact]
    public async Task Compare_reads_both_sides_and_sorts_every_row_by_what_it_needs()
    {
        var source = Side(SourceRows);
        var target = Side(TargetRows);

        var vm = await Compared(source, target);

        Assert.False(vm.IsBusy);
        Assert.Equal(4, vm.Rows.Count);
        Assert.Equal(1, vm.CountOnlySource);
        Assert.Equal(1, vm.CountOnlyTarget);
        Assert.Equal(1, vm.CountDifferent);
        Assert.Equal(1, vm.CountMatches);
        Assert.Equal(3, vm.CountDifferences);
        Assert.Equal(3, Visible(vm).Count);
        Assert.Equal("1 only in dev  |  1 only in test  |  1 with different values  |  1 matching", vm.Summary);
        Assert.StartsWith("Compared 1 table in ", vm.Status);
        Assert.EndsWith(" · 3 rows read from each side", vm.Status);
        Assert.Equal(new[] { "All tables", Table }, vm.EntityFilters);
        Assert.True(vm.ExportCommand.CanExecute(null));

        var thing = Assert.Single(vm.Entities);
        Assert.Equal(3, thing.DifferenceCount);
        Assert.Equal("Differences", thing.Result);
        Assert.Equal("3 diff", thing.ResultLabel);

        var read = Assert.Single(source.Requests, r => r.Url.Contains(RowsUrl));
        Assert.Contains("$orderby=new_thingid", read.Url);
        Assert.Contains("_new_parentid_value", read.Url);
        Assert.DoesNotContain("createdon", read.Url);
        Assert.Single(target.Requests, r => r.Url.Contains(RowsUrl));
    }

    [Fact]
    public async Task The_status_filter_narrows_the_grid_to_one_kind_of_row()
    {
        var vm = await Compared();

        vm.StatusFilter = RecordStatusFilter.OnlySource;
        Assert.Equal(G(1), Assert.Single(Visible(vm)).Source!.Id);

        vm.StatusFilter = RecordStatusFilter.OnlyTarget;
        Assert.Equal(G(3), Assert.Single(Visible(vm)).Target!.Id);

        vm.StatusFilter = RecordStatusFilter.Different;
        Assert.Equal(G(2), Assert.Single(Visible(vm)).Source!.Id);

        vm.StatusFilter = RecordStatusFilter.Matches;
        Assert.Equal(G(4), Assert.Single(Visible(vm)).Source!.Id);

        vm.StatusFilter = RecordStatusFilter.Differences;
        Assert.Equal(3, Visible(vm).Count);
    }

    [Fact]
    public async Task Searching_and_picking_a_table_narrow_the_rows_and_their_counts()
    {
        var vm = await Compared();

        vm.SearchText = "beta";
        Assert.Equal(G(2), Assert.Single(Visible(vm)).Source!.Id);
        Assert.Equal(1, vm.CountDifferent);
        Assert.Equal(0, vm.CountOnlySource);
        Assert.Equal(1, vm.CountDifferences);

        vm.SearchText = G(3).ToString();
        Assert.Equal(G(3), Assert.Single(Visible(vm)).Target!.Id);

        vm.SearchText = "new_name";
        Assert.Equal(G(2), Assert.Single(Visible(vm)).Source!.Id);

        vm.SearchText = "nothing like this";
        Assert.Empty(Visible(vm));
        Assert.Equal(0, vm.CountDifferences);

        vm.SearchText = string.Empty;
        vm.SelectedEntityFilter = "new_other";
        Assert.Empty(Visible(vm));
        Assert.Equal(0, vm.CountMatches);

        vm.SelectedEntityFilter = Table;
        Assert.Equal(3, Visible(vm).Count);
    }

    [Fact]
    public async Task A_selected_row_shows_its_differing_columns_and_can_show_them_all()
    {
        var vm = await Compared();

        Assert.Equal("Select a row to see its columns.", vm.DetailHeading);
        Assert.Equal(string.Empty, vm.DetailCounts);
        Assert.Equal(string.Empty, vm.DetailKey);

        vm.SelectedRow = Row(vm, RecordCompareStatus.Different);

        Assert.Equal($"new_thing  |  new_thingid = {G(2)}", vm.DetailHeading);
        Assert.Equal($"new_thing · {G(2)}", vm.DetailKey);
        var different = Assert.Single(vm.DetailColumns);
        Assert.Equal("new_name", different.Column.LogicalName);

        vm.DifferencesOnly = false;

        Assert.True(vm.DetailColumns.Count > 1);
        Assert.Equal($"1 of {vm.DetailColumns.Count} columns differ", vm.DetailCounts);

        vm.SelectedRow = null;
        Assert.Empty(vm.DetailColumns);
    }

    [Fact]
    public async Task Judging_lookups_by_id_instead_of_label_recompares_without_reading_again()
    {
        var source = Side(SourceRows);
        var target = Side(TargetRows);
        var vm = await Compared(source, target);
        var requests = source.Requests.Count + target.Requests.Count;
        vm.SelectedRow = vm.Rows[0];

        vm.MatchLookupsByName = false;

        Assert.Equal(requests, source.Requests.Count + target.Requests.Count);
        Assert.True(vm.IsDirty);
        Assert.Null(vm.SelectedRow);
        Assert.Equal(2, vm.CountDifferent);
        Assert.Equal(0, vm.CountMatches);
        Assert.Equal(4, Assert.Single(vm.Entities).DifferenceCount);
    }

    [Fact]
    public async Task Every_row_matching_says_so()
    {
        var rows = RowsJson(Record(4, "D", "Delta", 10));

        var vm = await Compared(Side(rows), Side(rows));

        Assert.EndsWith(" · 1 rows read from each side · every row matches.", vm.Status);
        Assert.Equal("Match", vm.Entities[0].Result);
        Assert.Equal("Match", vm.Entities[0].ResultLabel);
        Assert.Empty(Visible(vm));
    }

    [Fact]
    public async Task A_table_cut_short_by_the_row_cap_is_warned_about()
    {
        var vm = Window(Side(SourceRows), Side(RowsJson(Record(4, "D", "Delta", 10))), Config());
        vm.MaxRowsPerEntity = 2;

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Contains("2 source and 1 target rows read", vm.Status);
        Assert.Contains(vm.Warnings, w => w.StartsWith("new_thing: hit the 2 row cap"));
        Assert.Equal("1 warning — the result may be partial", vm.WarningsTitle);
        Assert.StartsWith("new_thing: hit the 2 row cap", vm.WarningsPreview);
    }

    [Fact]
    public void The_row_cap_stays_within_bounds_and_marks_the_configuration_changed()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows), Config());

        vm.MaxRowsPerEntity = 0;
        Assert.Equal(1, vm.MaxRowsPerEntity);
        Assert.True(vm.IsDirty);

        vm.MaxRowsPerEntity = 1_000_000;
        Assert.Equal(100_000, vm.MaxRowsPerEntity);
    }

    [Fact]
    public async Task A_table_that_cannot_be_planned_is_skipped_with_a_warning()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows),
            Config("Missing", Thing(c => c.LogicalName = "new_missing")));

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Equal("new_missing: skipped - Not in the source environment.", Assert.Single(vm.Warnings));
        Assert.Equal("1 warning — the result may be partial", vm.WarningsTitle);
        Assert.Equal("new_missing: skipped - Not in the source environment.", vm.WarningsPreview);
        Assert.StartsWith("Compared 0 tables in ", vm.Status);
        Assert.EndsWith(" - no rows read.", vm.Status);
        Assert.Null(vm.Entities[0].DifferenceCount);
        Assert.Equal("None", vm.Entities[0].Result);
        Assert.Equal(string.Empty, vm.Entities[0].ResultLabel);

        vm.ToggleWarningsCommand.Execute(null);
        Assert.True(vm.ShowAllWarnings);
        vm.ToggleWarningsCommand.Execute(null);
        Assert.False(vm.ShowAllWarnings);
    }

    [Fact]
    public async Task A_failed_read_is_reported_and_the_window_is_usable_again()
    {
        var source = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, EntityList, EntitiesJson(Table))
            .OnJson(HttpMethod.Get, Attributes, ColumnsJson)
            .OnError(HttpMethod.Get, RowsUrl, HttpStatusCode.Forbidden, "no read privilege");
        var vm = Window(source, Side(TargetRows), Config());

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.StartsWith("Comparison failed - ", vm.Status);
        Assert.Contains("no read privilege", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Cancelling_stops_before_the_next_table_and_shows_what_was_read()
    {
        ReferenceDataCompareViewModel? vm = null;
        var cancellable = false;

        // Cancel is pressed while the first table's rows are being read.
        var source = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, EntityList, EntitiesJson(Table, "new_other"))
            .OnJson(HttpMethod.Get, Attributes, ColumnsJson)
            .On(HttpMethod.Get, RowsUrl, _ =>
            {
                cancellable = vm!.IsBusy && vm.CancelCommand.CanExecute(null);
                vm.CancelCommand.Execute(null);
                return FakeHttpHandler.Json(SourceRows);
            });
        vm = Window(source, Side(TargetRows), Config("Two", Thing(), Thing(c => c.LogicalName = "new_other")));

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.True(cancellable);
        Assert.Equal("Cancelled - showing what had been read.", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.DoesNotContain(source.Requests, r => r.Url.Contains("LogicalName='new_other'"));
    }

    // ---------------------------------------------------------------- selecting and reconciling

    [Fact]
    public void The_selection_is_described_by_what_reconciling_it_would_do()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows), Config());
        var rows = Selection();

        Assert.Equal("No rows selected", vm.SelectionSummary);
        Assert.Equal(string.Empty, vm.SelectionBreakdown);
        Assert.Equal("Reconcile...", vm.ReconcileLabel);
        Assert.False(vm.ReconcileCommand.CanExecute(null));

        vm.SetSelectedRows(rows);

        Assert.Equal("4 rows selected, 3 need a change", vm.SelectionSummary);
        Assert.Equal(" · 1 create, 1 update, 1 delete", vm.SelectionBreakdown);
        Assert.Equal("Reconcile 4 rows...", vm.ReconcileLabel);
        Assert.True(vm.ReconcileCommand.CanExecute(null));

        vm.SetSelectedRows(rows.Take(1));

        Assert.Equal("1 row selected", vm.SelectionSummary);
        Assert.Equal(" · 1 create", vm.SelectionBreakdown);
        Assert.Equal("Reconcile 1 row...", vm.ReconcileLabel);
    }

    [Fact]
    public async Task Reconciling_rows_that_already_match_does_nothing()
    {
        var target = Side(TargetRows);
        var vm = Window(Side(SourceRows), target, Config());
        vm.SetSelectedRows(Selection().Where(r => r.Status == RecordCompareStatus.Same));
        vm.ShowReconcile = _ => Assert.Fail("No reconcile window should open.");

        await vm.ReconcileCommand.ExecuteAsync(null);

        Assert.Equal("Every selected row already matches, so there is nothing to reconcile.", vm.Status);
        Assert.Empty(target.Requests);
    }

    [Fact]
    public async Task Reconcile_opens_on_the_rows_needing_a_change_once_the_target_is_cleared()
    {
        var target = Side(TargetRows).OnJson(HttpMethod.Get, BapList, BapEnvironments("Sandbox"));
        var vm = Window(Side(SourceRows), target, Config());
        vm.SetSelectedRows(Selection());
        var opened = new List<ReconcileViewModel>();
        vm.ShowReconcile = opened.Add;

        await vm.ReconcileCommand.ExecuteAsync(null);
        await vm.ReconcileCommand.ExecuteAsync(null);

        Assert.Equal(2, opened.Count);
        var reconcile = opened[0];
        Assert.Equal("dev", reconcile.SourceName);
        Assert.Equal("test", reconcile.TargetName);
        Assert.True(reconcile.Permission.Allowed);
        Assert.Equal(3, reconcile.Rows.Count);
        Assert.Equal("Sandbox environment - writes are allowed.", vm.WriteStatus);
        Assert.Equal(reconcile.Status, vm.Status);
        Assert.False(vm.IsBusy);

        // The target's table list is read once and kept; its type is asked afresh each time.
        Assert.Single(target.Requests, r => r.Url.Contains(EntityList));
        Assert.Equal(2, target.Requests.Count(r => r.Url.Contains(BapList)));

        vm.Target = Session("https://other.crm11.dynamics.com", Side(TargetRows));
        Assert.Equal(string.Empty, vm.WriteStatus);
    }

    [Fact]
    public async Task A_protected_target_is_named_as_blocked_after_the_window_closes()
    {
        var target = Side(TargetRows).OnJson(HttpMethod.Get, BapList, BapEnvironments("Production"));
        var vm = Window(Side(SourceRows), target, Config());
        vm.SetSelectedRows(Selection());
        ReconcileViewModel? opened = null;
        vm.ShowReconcile = r => opened = r;

        await vm.ReconcileCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.True(opened.IsBlocked);
        Assert.StartsWith("Writing here is blocked.", vm.Status);
        Assert.Equal(vm.Status, vm.WriteStatus);
    }

    [Fact]
    public async Task A_target_whose_tables_cannot_be_read_gets_no_reconcile_window()
    {
        var target = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, BapList, BapEnvironments("Sandbox"))
            .OnError(HttpMethod.Get, EntityList, HttpStatusCode.Forbidden, "no metadata");
        var vm = Window(Side(SourceRows), target, Config());
        vm.SetSelectedRows(Selection());
        vm.ShowReconcile = _ => Assert.Fail("No reconcile window should open.");

        await vm.ReconcileCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not prepare the write - ", vm.Status);
        Assert.Contains("no metadata", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Reconcile_needs_a_connected_target()
    {
        var vm = Window(Side(SourceRows), null, Config());
        vm.SetSelectedRows(Selection());
        vm.ShowReconcile = _ => Assert.Fail("No reconcile window should open.");
        var before = vm.Status;

        await vm.ReconcileCommand.ExecuteAsync(null);

        Assert.Equal(before, vm.Status);
    }

    // ---------------------------------------------------------------- configurations

    private static readonly string[] LastComparedColumns = ["new_code", "new_name", "new_parentid"];

    [Fact]
    public async Task Saving_keeps_the_tables_and_the_columns_the_last_comparison_used()
    {
        var settings = Settings(Config());
        var vm = Window(Side(SourceRows), Side(TargetRows), settings);
        await vm.CompareCommand.ExecuteAsync(null);
        vm.MaxRowsPerEntity = 300;
        vm.ConfigurationName = "  Lookups  ";

        vm.SaveConfigurationCommand.Execute(null);

        var saved = Assert.Single(vm.Configurations);
        Assert.Same(saved, vm.SelectedConfiguration);
        Assert.Equal(300, saved.MaxRowsPerEntity);
        Assert.Equal(LastComparedColumns, saved.Entities![0].ComparedColumns!.OrderBy(c => c));
        Assert.False(vm.IsDirty);
        Assert.Equal("Saved configuration 'Lookups'.", vm.Status);

        var persisted = Assert.Single(settings.ReferenceDataConfigurations!);
        Assert.Equal("Lookups", persisted.Name);
        Assert.NotSame(saved, persisted);
    }

    private static readonly string[] ChosenColumns = ["new_code"];

    [Fact]
    public void Columns_already_chosen_are_saved_as_they_are()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows), Config("Chosen", Thing(c => c.ComparedColumns = new List<string> { "new_code" })));

        vm.SaveConfigurationCommand.Execute(null);

        Assert.Equal(ChosenColumns, vm.Configurations[0].Entities![0].ComparedColumns);
    }

    private static readonly string[] AskedOnce = ["Save configuration"];
    private static readonly string[] ExistingThenFresh = ["Lookups", "Fresh"];

    [Fact]
    public void A_configuration_without_a_name_is_asked_for_one_first()
    {
        var settings = Settings(Config());
        var vm = Window(Side(SourceRows), Side(TargetRows), settings);
        var asked = new List<string>();
        vm.NewConfigurationCommand.Execute(null);

        vm.AskName = (title, _, _) => { asked.Add(title); return null; };
        vm.SaveConfigurationCommand.Execute(null);

        vm.AskName = (_, _, _) => "   ";
        vm.SaveConfigurationCommand.Execute(null);

        Assert.Equal(AskedOnce, asked);
        Assert.Single(vm.Configurations);

        vm.AskName = (_, _, _) => "Fresh";
        vm.SaveConfigurationCommand.Execute(null);

        Assert.Equal(ExistingThenFresh, vm.Configurations.Select(c => c.Name));
        Assert.Equal("Fresh", vm.ConfigurationName);
        Assert.Equal(2, settings.ReferenceDataConfigurations!.Count);
    }

    [Fact]
    public void A_new_configuration_starts_empty()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows), Config());
        vm.MaxRowsPerEntity = 10;

        vm.NewConfigurationCommand.Execute(null);

        Assert.Null(vm.SelectedConfiguration);
        Assert.Equal(string.Empty, vm.ConfigurationName);
        Assert.Empty(vm.Entities);
        Assert.False(vm.IsDirty);
        Assert.False(vm.CompareCommand.CanExecute(null));
        Assert.False(vm.DeleteConfigurationCommand.CanExecute(null));
    }

    [Fact]
    public void Picking_another_configuration_loads_its_tables()
    {
        var second = Config("Second", Thing(c => c.LogicalName = "new_other"));
        second.MaxRowsPerEntity = 42;
        var vm = Window(Side(SourceRows), Side(TargetRows), Config(), second);

        vm.SelectedConfiguration = vm.Configurations[1];

        Assert.Equal("Second", vm.ConfigurationName);
        Assert.Equal(42, vm.MaxRowsPerEntity);
        Assert.Equal("new_other", Assert.Single(vm.Entities).LogicalName);
        Assert.False(vm.IsDirty);
    }

    private static readonly string[] RenamedInPlace = ["Renamed", "Other"];

    [Fact]
    public void Renaming_replaces_the_configuration_under_its_new_name()
    {
        var settings = Settings(Config(), Config("Other"));
        var vm = Window(Side(SourceRows), Side(TargetRows), settings);
        string? offered = null;
        vm.AskName = (_, _, initial) => { offered = initial; return "Renamed"; };

        vm.RenameConfigurationCommand.Execute(null);

        Assert.Equal("Lookups", offered);
        Assert.Equal(RenamedInPlace, vm.Configurations.Select(c => c.Name));
        Assert.Same(vm.Configurations[0], vm.SelectedConfiguration);
        Assert.Equal("Renamed", vm.ConfigurationName);
        Assert.Equal("Renamed the configuration to 'Renamed'.", vm.Status);
        Assert.Equal("Renamed", settings.ReferenceDataConfigurations![0].Name);
    }

    private static readonly string[] NamesUnchanged = ["Lookups", "Other"];

    [Fact]
    public void Renaming_to_a_name_already_taken_or_unchanged_changes_nothing()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows), Config(), Config("Other"));
        var selected = vm.SelectedConfiguration;

        vm.AskName = (_, _, _) => "other";
        vm.RenameConfigurationCommand.Execute(null);

        Assert.Equal("There is already a configuration called 'other'.", vm.Status);

        vm.AskName = (_, _, _) => "Lookups";
        vm.RenameConfigurationCommand.Execute(null);
        vm.AskName = (_, _, _) => null;
        vm.RenameConfigurationCommand.Execute(null);

        Assert.Same(selected, vm.SelectedConfiguration);
        Assert.Equal(NamesUnchanged, vm.Configurations.Select(c => c.Name));

        vm.NewConfigurationCommand.Execute(null);
        vm.AskName = (_, _, _) => throw new InvalidOperationException("Nothing is selected to rename.");
        vm.RenameConfigurationCommand.Execute(null);
    }

    [Fact]
    public void Deleting_a_configuration_asks_first()
    {
        var settings = Settings(Config(), Config("Other"));
        var vm = Window(Side(SourceRows), Side(TargetRows), settings);
        string? question = null;
        vm.Confirm = message => { question = message; return false; };

        vm.DeleteConfigurationCommand.Execute(null);

        Assert.Equal("Delete the saved configuration 'Lookups'?", question);
        Assert.Equal(2, vm.Configurations.Count);

        vm.Confirm = _ => true;
        vm.DeleteConfigurationCommand.Execute(null);

        Assert.Equal("Other", Assert.Single(vm.Configurations).Name);
        Assert.Null(vm.SelectedConfiguration);
        Assert.Equal("Deleted configuration 'Lookups'.", vm.Status);
        Assert.Equal("Other", Assert.Single(settings.ReferenceDataConfigurations!).Name);

        vm.Confirm = _ => throw new InvalidOperationException("Nothing is selected to delete.");
        vm.DeleteConfigurationCommand.Execute(null);
    }

    // ---------------------------------------------------------------- tables

    [Fact]
    public void Unticking_the_last_table_disables_compare_and_marks_the_configuration_changed()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows), Config());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Entities[0].IsEnabled = false;

        Assert.False(vm.CompareCommand.CanExecute(null));
        Assert.True(vm.IsDirty);
        Assert.Equal("Tables · 0 of 1 checked", vm.TablesHeading);
        Assert.Contains(nameof(vm.TablesHeading), changed);
        Assert.Equal("Off", vm.Entities[0].ResultLabel);
    }

    [Fact]
    public void Removing_a_table_takes_the_one_given_or_else_the_selected_one()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows), Config("Two", Thing(), Thing(c => c.LogicalName = "new_other")));

        Assert.False(vm.RemoveEntityCommand.CanExecute(null));
        Assert.True(vm.RemoveEntityCommand.CanExecute(vm.Entities[1]));

        vm.RemoveEntityCommand.Execute(vm.Entities[1]);

        Assert.Equal(Table, Assert.Single(vm.Entities).LogicalName);
        Assert.True(vm.IsDirty);

        vm.SelectedEntity = vm.Entities[0];
        Assert.True(vm.RemoveEntityCommand.CanExecute(null));
        vm.RemoveEntityCommand.Execute(null);

        Assert.Empty(vm.Entities);
    }

    [Fact]
    public async Task Tables_picked_from_the_source_list_are_added_and_the_list_is_read_once()
    {
        var source = Side(SourceRows);
        var vm = Window(source, Side(TargetRows), Config());
        EntityPickerViewModel? offered = null;
        vm.PickEntities = picker =>
        {
            offered = picker;
            picker.Items.Single(i => i.LogicalName == "new_other").IsSelected = true;
            return true;
        };

        await vm.AddEntitiesCommand.ExecuteAsync(null);

        Assert.True(offered!.Items.Single(i => i.LogicalName == Table).IsAlreadyAdded);
        var added = vm.Entities[1];
        Assert.Equal("new_other", added.LogicalName);
        Assert.Equal("Other (new_other)", added.Label);
        Assert.NotNull(added.Entity);
        Assert.Equal("2 table(s) configured.", vm.Status);
        Assert.True(vm.IsDirty);

        vm.PickEntities = _ => false;
        await vm.AddEntitiesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Entities.Count);
        Assert.Single(source.Requests, r => r.Url.Contains(EntityList));
    }

    [Fact]
    public async Task Picking_nothing_leaves_the_configuration_unchanged()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows), Config());
        vm.PickEntities = _ => true;

        await vm.AddEntitiesCommand.ExecuteAsync(null);

        Assert.Single(vm.Entities);
        Assert.False(vm.IsDirty);
        Assert.Equal("1 table(s) configured.", vm.Status);
    }

    [Fact]
    public async Task A_table_list_that_cannot_be_read_is_reported_instead_of_offering_a_picker()
    {
        var source = new FakeHttpHandler().OnError(HttpMethod.Get, EntityList, HttpStatusCode.Forbidden, "no metadata");
        var vm = Window(source, Side(TargetRows), Config());
        vm.PickEntities = _ => throw new InvalidOperationException("No picker should open.");

        await vm.AddEntitiesCommand.ExecuteAsync(null);

        Assert.StartsWith("Could not read the table list - ", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Another_source_has_its_own_table_list()
    {
        var first = Side(SourceRows);
        var second = Side(SourceRows);
        var vm = Window(first, Side(TargetRows), Config());
        vm.PickEntities = _ => false;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.AddEntitiesCommand.ExecuteAsync(null);
        vm.Source = Session("https://prod.crm11.dynamics.com", second);
        await vm.AddEntitiesCommand.ExecuteAsync(null);

        Assert.Contains(nameof(vm.SourceHeader), changed);
        Assert.Single(first.Requests, r => r.Url.Contains(EntityList));
        Assert.Single(second.Requests, r => r.Url.Contains(EntityList));
    }

    [Fact]
    public async Task Editing_a_table_opens_its_settings_and_marks_the_configuration_changed_when_kept()
    {
        var source = Side(SourceRows)
            .OnJson(HttpMethod.Get, "EntityDefinitions(LogicalName='new_thing')/Keys", """{"value":[]}""");
        var vm = Window(source, Side(TargetRows), Config());
        var entity = vm.Entities[0];
        vm.SelectedEntity = entity;
        ReferenceEntitySettingsViewModel? opened = null;
        vm.EditSettings = settings => { opened = settings; return false; };

        Assert.True(vm.EditEntityCommand.CanExecute(null));
        await vm.EditEntityCommand.ExecuteAsync(null);

        Assert.Equal("Thing (new_thing) - comparison settings", opened!.Title);
        Assert.NotNull(entity.Entity);
        Assert.False(vm.IsDirty);

        vm.EditSettings = _ => true;
        await vm.EditEntityCommand.ExecuteAsync(entity);

        Assert.True(vm.IsDirty);
        Assert.Single(source.Requests, r => r.Url.Contains(EntityList));
    }

    [Fact]
    public async Task A_table_the_source_does_not_have_cannot_be_edited()
    {
        var vm = Window(Side(SourceRows, "new_other"), Side(TargetRows), Config());
        vm.EditSettings = _ => throw new InvalidOperationException("No settings should open.");

        await vm.EditEntityCommand.ExecuteAsync(vm.Entities[0]);

        Assert.Equal("new_thing is not in dev, so its settings cannot be read.", vm.Status);
        Assert.Null(vm.Entities[0].Entity);
    }

    [Fact]
    public async Task A_table_list_that_fails_while_editing_is_reported()
    {
        var source = new FakeHttpHandler().OnError(HttpMethod.Get, EntityList, HttpStatusCode.Forbidden, "no metadata");
        var vm = Window(source, Side(TargetRows), Config());
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Status)) statuses.Add(vm.Status); };

        await vm.EditEntityCommand.ExecuteAsync(vm.Entities[0]);

        Assert.Contains(statuses, s => s.StartsWith("Could not read the table list - "));
        Assert.Equal("new_thing is not in dev, so its settings cannot be read.", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Editing_needs_a_source_and_a_table()
    {
        var vm = Window(Side(SourceRows), Side(TargetRows), Config());
        vm.Source = null;

        Assert.False(vm.EditEntityCommand.CanExecute(vm.Entities[0]));
        Assert.False(vm.AddEntitiesCommand.CanExecute(null));

        var connected = Window(Side(SourceRows), Side(TargetRows), Config());
        Assert.False(connected.EditEntityCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- export

    [Fact]
    public async Task Export_writes_a_line_per_difference_from_the_rows_on_screen()
    {
        var vm = await Compared();
        var path = Path.Combine(_dir, "export.csv");
        string? suggested = null;
        vm.PickExportPath = name => { suggested = name; return path; };

        vm.ExportCommand.Execute(null);

        Assert.Equal("reference-data-dev-test.csv", suggested);
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("Table,Key column(s),Key,Name,Status,Column,dev value,test value,Source id,Target id", lines[0]);
        Assert.Contains(lines, l => l.StartsWith($"new_thing,new_thingid,{G(2)},Beta,") && l.Contains(",new_name,Beta,Beta old,"));
        Assert.Contains(lines, l => l.StartsWith($"new_thing,new_thingid,{G(3)},Gamma,"));
        Assert.Equal($"Exported 3 line(s) to {path}.", vm.Status);
    }

    [Fact]
    public async Task Cancelling_the_export_writes_nothing()
    {
        var vm = await Compared();
        var status = vm.Status;
        vm.PickExportPath = _ => null;

        vm.ExportCommand.Execute(null);

        Assert.Equal(status, vm.Status);
        Assert.Empty(Directory.GetFiles(_dir, "*.csv"));
    }
}
