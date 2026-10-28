using System.Net;
using System.Net.Http;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The table settings window: choosing what to read, reading it, and cutting the views.</summary>
public class TableSettingsViewModelTests
{
    private const string DevUrl = "https://dev.crm11.dynamics.com";
    private const string ProdUrl = "https://prod.crm11.dynamics.com";

    private static readonly Guid CaseId = Guid.Parse("60000000-0000-0000-0000-000000000006");
    private static readonly Guid Profile = Guid.Parse("50000000-0000-0000-0000-000000000005");
    private static readonly Guid Fabric = Guid.Parse("50000000-0000-0000-0000-000000000007");

    private static readonly SolutionInfo Core = new()
    {
        SolutionId = Guid.Parse("10000000-0000-0000-0000-000000000001"), UniqueName = "core", FriendlyName = "Contoso Core"
    };

    /// <summary>
    /// An environment with two custom tables - only contoso_case in the Core solution - its audit
    /// set as given, one link profile per folder, and search not provisioned.
    /// </summary>
    private static FakeHttpHandler Environment(bool caseAudited, bool caseColumnAudited = true)
    {
        return new FakeHttpHandler()
            .OnError(HttpMethod.Get, "msdyn_componenttype ge 0", HttpStatusCode.BadRequest, "range filters are not supported")
            .OnJson(HttpMethod.Get, "msdyn_solutioncomponentsummaries", $$"""
                {"value":[{"msdyn_componenttype":1,"msdyn_name":"contoso_case","msdyn_objectid":"{{CaseId}}"}]}
                """)
            .OnJson(HttpMethod.Get, "EntityDefinitions?", $$"""
                {"value":[
                  {"LogicalName":"contoso_case","MetadataId":"{{CaseId}}","IsCustomEntity":true,"IsAuditEnabled":{"Value":{{(caseAudited ? "true" : "false")}}},"ChangeTrackingEnabled":true},
                  {"LogicalName":"contoso_note","MetadataId":"{{Guid.NewGuid()}}","IsCustomEntity":true,"IsAuditEnabled":{"Value":true},"ChangeTrackingEnabled":true}
                ]}
                """)
            .OnJson(HttpMethod.Get, "/Attributes", $$$"""
                {"value":[{"LogicalName":"contoso_tier","IsCustomAttribute":true,"IsValidForUpdate":true,"IsAuditEnabled":{"Value":{{{(caseColumnAudited ? "true" : "false")}}}}}]}
                """)
            .OnJson(HttpMethod.Get, "organizations", """{"value":[{"isauditenabled":true,"auditretentionperiodv2":-1}]}""")
            .OnJson(HttpMethod.Get, "synapselinkprofiles?", $$"""
                {"value":[
                  {"synapselinkprofileid":"{{Profile}}","name":"Lake","profilestate":1,"_datalakefolder_value@OData.Community.Display.V1.FormattedValue":"contosolake"},
                  {"synapselinkprofileid":"{{Fabric}}","name":"Fabric","profilestate":1,"_datalakefolder_value@OData.Community.Display.V1.FormattedValue":"cds_workspace"}
                ]}
                """)
            .OnJson(HttpMethod.Get, "synapselinkprofileentitystates", $$"""
                {"value":[{"entityname":"contoso_case","_profile_value":"{{Fabric}}","syncstate":4,"statecode":0}]}
                """)
            .OnJson(HttpMethod.Get, "searchstatus", """{"response":"{\"value\":{\"status\":\"notprovisioned\"}}"}""")
            .OnStatus(HttpMethod.Get, "searchstatistics", HttpStatusCode.BadRequest);
    }

    private sealed record Opened(TableSettingsViewModel Vm, EnvironmentSessionViewModel Dev, EnvironmentSessionViewModel Prod, AppSettings Settings, List<AppSettings> Saves);

    private static Opened Open(AppSettings? settings = null, FakeHttpHandler? prod = null)
    {
        InlineSynchronizationContext.Install();

        var dev = TestSessions.Connected(Environment(caseAudited: true), DevUrl, Core);
        var prodSession = TestSessions.Connected(prod ?? Environment(caseAudited: false), ProdUrl);
        settings ??= new AppSettings();
        var saves = new List<AppSettings>();

        var vm = new TableSettingsViewModel([dev, prodSession], settings, dev, s => saves.Add(s));
        return new Opened(vm, dev, prodSession, settings, saves);
    }

    [Fact]
    public void It_starts_from_the_open_tabs_solution_and_every_connected_environment()
    {
        var offline = TestSessions.Disconnected("https://offline.crm11.dynamics.com");
        InlineSynchronizationContext.Install();
        var dev = TestSessions.Connected(Environment(true), DevUrl, Core);

        var vm = new TableSettingsViewModel([dev, offline], new AppSettings(), dev, _ => { });

        Assert.Equal([dev], vm.Environments.Select(e => e.Session));
        Assert.Same(dev, vm.ScopeSession);
        Assert.Equal("Contoso Core", vm.SolutionsLabel);
        Assert.True(vm.RunCommand.CanExecute(null));
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void Solution_scope_needs_a_solution_and_other_scopes_do_not()
    {
        var o = Open();
        o.Vm.SolutionChoices.Single().IsSelected = false;

        Assert.False(o.Vm.RunCommand.CanExecute(null));
        Assert.Contains("Choose the solutions", o.Vm.EmptyText);

        o.Vm.Scope = TableScope.CustomTables;

        Assert.True(o.Vm.RunCommand.CanExecute(null));
        Assert.Equal(TableScope.CustomTables, o.Settings.TableSettings?.Scope);
    }

    [Fact]
    public async Task A_check_covers_the_solutions_tables_in_every_environment()
    {
        var o = Open();

        await o.Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, o.Vm.Status);
        Assert.Equal([o.Dev.Title, o.Prod.Title], o.Vm.ResultEnvironments);

        var row = Assert.Single(o.Vm.TableRows);
        Assert.Equal("contoso_case", row.Name);

        o.Vm.Kind = TableSettingKind.Audit;
        row = Assert.Single(o.Vm.TableRows);
        Assert.Equal($"Off in {o.Prod.Title}", row.Difference);
        Assert.Equal(1, o.Vm.TableFindings);
    }

    [Fact]
    public async Task Columns_are_read_for_tables_audited_somewhere_and_gaps_listed()
    {
        var o = Open(prod: Environment(caseAudited: true, caseColumnAudited: false));

        await o.Vm.RunCommand.ExecuteAsync(null);

        var column = Assert.Single(o.Vm.ColumnRows);
        Assert.Equal("contoso_tier", column.Name);
        Assert.False(column.IsConsistent);

        var gap = Assert.Single(o.Vm.GapRows);
        Assert.Equal(o.Prod.Title, gap.Environment);

        // Shown as a pivot: a row per column, a cell per environment, grouped by table.
        var row = Assert.Single(o.Vm.GapMatrixRows);
        Assert.Equal("contoso_tier", row.Column);
        Assert.Equal(["Audited", "Not audited"], row.Cells.Select(c => c.Text));
        Assert.Equal($"1 column not audited in {o.Prod.Title}", row.Group.Summary);
    }

    [Fact]
    public async Task Leaving_columns_out_reads_no_columns()
    {
        var prod = Environment(caseAudited: false);
        var o = Open(prod: prod);
        o.Vm.IncludeColumns = false;

        await o.Vm.RunCommand.ExecuteAsync(null);

        Assert.Empty(o.Vm.ColumnRows);
        Assert.DoesNotContain(prod.Requests, r => r.Url.Contains("/Attributes"));
    }

    [Fact]
    public async Task Unticking_a_link_folder_redraws_the_matrix_and_is_remembered()
    {
        var o = Open();
        await o.Vm.RunCommand.ExecuteAsync(null);
        o.Vm.Kind = TableSettingKind.SynapseLink;

        Assert.Equal(["On", "On"], o.Vm.TableRows.Single().Cells.Select(c => c.Text));
        Assert.Equal(["cds_workspace", "contosolake"], o.Vm.Folders.Select(f => f.Name));

        o.Vm.Folders.Single(f => f.Name == "cds_workspace").IsCounted = false;

        Assert.Equal(["Off", "Off"], o.Vm.TableRows.Single().Cells.Select(c => c.Text));
        Assert.All(o.Vm.LinkRows, l => Assert.False(l.IsCounted));
        Assert.Equal(["cds_workspace"], o.Settings.TableSettings?.IgnoredLinkFolders);
        Assert.NotEmpty(o.Saves);
    }

    [Fact]
    public async Task Ignored_folders_carry_over_to_the_next_window()
    {
        var settings = new AppSettings
        {
            TableSettings = new TableSettingsPreferences { IgnoredLinkFolders = ["cds_workspace"], Solutions = ["core"] }
        };
        var o = Open(settings);

        await o.Vm.RunCommand.ExecuteAsync(null);
        o.Vm.Kind = TableSettingKind.SynapseLink;

        Assert.False(o.Vm.Folders.Single(f => f.Name == "cds_workspace").IsCounted);
        Assert.Equal(["Off", "Off"], o.Vm.TableRows.Single().Cells.Select(c => c.Text));
    }

    [Fact]
    public async Task Findings_only_and_the_filter_narrow_every_view()
    {
        var o = Open(prod: Environment(caseAudited: true));
        o.Vm.Scope = TableScope.CustomTables;
        await o.Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(["contoso_case", "contoso_note"], o.Vm.TableRows.Select(r => r.Name));

        o.Vm.FindingsOnly = true;
        Assert.Empty(o.Vm.TableRows);

        o.Vm.FindingsOnly = false;
        o.Vm.FilterText = "note";
        Assert.Equal(["contoso_note"], o.Vm.TableRows.Select(r => r.Name));
    }

    [Fact]
    public async Task What_could_not_be_read_is_said_per_environment()
    {
        var prod = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "synapselinkprofiles", HttpStatusCode.Forbidden, "no read on synapselinkprofile");
        foreach (var route in new[] { "EntityDefinitions?", "organizations", "searchstatus" })
        {
            prod.OnJson(HttpMethod.Get, route, route == "searchstatus"
                ? """{"response":"{\"value\":{\"status\":\"notprovisioned\"}}"}"""
                : """{"value":[]}""");
        }

        var o = Open(prod: prod);
        await o.Vm.RunCommand.ExecuteAsync(null);

        Assert.True(o.Vm.HasProblems);
        Assert.Contains($"{o.Prod.Title}: Synapse Link could not be read", o.Vm.Problems);
    }

    [Fact]
    public async Task Export_writes_the_view_on_screen_with_a_column_per_environment()
    {
        var o = Open();
        await o.Vm.RunCommand.ExecuteAsync(null);
        o.Vm.Kind = TableSettingKind.Audit;

        var lines = o.Vm.ExportLines();

        Assert.Equal($"Table,Display name,{o.Dev.Title},{o.Prod.Title},Consistent,Difference", lines[0]);
        Assert.StartsWith("contoso_case,,On,Off,No,", lines[1]);

        o.Vm.SelectedTab = TableSettingsTab.Links;
        Assert.StartsWith("Environment,Profile,Folder", o.Vm.ExportLines()[0]);
    }

    [Fact]
    public void Sorting_a_grouped_view_sorts_within_the_groups_and_keeps_their_order()
    {
        GapGroup Group(int order, string table) => new() { Order = order, Table = table, Label = table, ColumnCount = 2, Environments = ["Dev"] };
        var (contact, account) = (Group(0, "contact"), Group(1, "account"));
        var rows = new System.Collections.ObjectModel.ObservableCollection<GapRow>
        {
            new(contact, "b_column", null, false, []), new(contact, "z_column", null, false, []),
            new(account, "a_column", null, false, []), new(account, "y_column", null, false, [])
        };
        var view = TableSettingsViewModel.Grouped(rows, nameof(GapRow.Group));

        // What a click on the Column header does: a_column now comes first, but its group does not.
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(GapRow.Column),
            System.ComponentModel.ListSortDirection.Descending));

        var groups = view.Groups.Cast<System.Windows.Data.CollectionViewGroup>().ToList();
        Assert.Equal([contact, account], groups.Select(g => g.Name));
        Assert.Equal(["z_column", "b_column"], groups[0].Items.Cast<GapRow>().Select(r => r.Column));
        Assert.Equal(["y_column", "a_column"], groups[1].Items.Cast<GapRow>().Select(r => r.Column));
    }
}
