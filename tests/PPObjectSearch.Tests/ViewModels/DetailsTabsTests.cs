using System.Net.Http;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// The details window's tabs - type-specific first, one selection - and the detail pane's
/// shortcuts that open it on a given tab.
/// </summary>
public class DetailsTabsTests
{
    private static SolutionComponentItem Item(int type, int? category = null, string? subType = null) => new()
    {
        Name = "thing", ComponentType = type, ComponentTypeName = "Type", ObjectId = Guid.NewGuid(),
        ProcessCategory = category, SubType = subType, WorkflowIdUnique = Guid.NewGuid()
    };

    private static SolutionComponentItem CloudFlow() => Item(29, 5);

    private static ObjectDetailsViewModel Details(SolutionComponentItem item, DetailsShortcut? openOn = null, string? environmentId = "env-1") =>
        new(Fakes.Dataverse(new FakeHttpHandler()), item, new Dictionary<Guid, SolutionComponentItem>(),
            environmentId: environmentId, openOn: openOn);

    // ---------------------------------------------------------------- kinds and tabs

    [Theory]
    [InlineData(1, null, null, ObjectKind.Table)]
    [InlineData(61, null, null, ObjectKind.WebResource)]
    [InlineData(90, null, null, ObjectKind.Plugin)]
    [InlineData(92, null, null, ObjectKind.Plugin)]
    [InlineData(380, null, null, ObjectKind.EnvironmentVariable)]
    [InlineData(381, null, null, ObjectKind.EnvironmentVariable)]
    [InlineData(29, 5, null, ObjectKind.CloudFlow)]
    [InlineData(29, 0, null, ObjectKind.ClassicWorkflow)]
    [InlineData(29, null, "Modern Flow", ObjectKind.CloudFlow)]
    [InlineData(29, null, "Workflow (classic)", ObjectKind.ClassicWorkflow)]
    [InlineData(29, 2, null, ObjectKind.Other)]
    [InlineData(26, null, null, ObjectKind.Other)]
    public void An_object_is_told_apart_by_type_and_category(int type, int? category, string? subType, ObjectKind expected)
    {
        Assert.Equal(expected, DetailsTabs.KindOf(Item(type, category, subType)));
    }

    [Fact]
    public void A_cloud_flow_lists_its_own_tabs_before_the_generic_ones()
    {
        Assert.Equal(
            [DetailsTab.Design, DetailsTab.Runs, DetailsTab.Source, DetailsTab.Connections, DetailsTab.Layers, DetailsTab.Dependencies],
            DetailsTabs.TabsFor(ObjectKind.CloudFlow));
    }

    [Theory]
    [InlineData(ObjectKind.CloudFlow, DetailsTab.Design)]
    [InlineData(ObjectKind.ClassicWorkflow, DetailsTab.Runs)]
    [InlineData(ObjectKind.Plugin, DetailsTab.TraceLog)]
    [InlineData(ObjectKind.WebResource, DetailsTab.Source)]
    [InlineData(ObjectKind.EnvironmentVariable, DetailsTab.Value)]
    [InlineData(ObjectKind.Table, DetailsTab.Components)]
    [InlineData(ObjectKind.ConnectionReference, DetailsTab.Connections)]
    [InlineData(ObjectKind.Other, DetailsTab.Layers)]
    public void Each_kind_opens_on_its_own_first_tab(ObjectKind kind, DetailsTab expected)
    {
        Assert.Equal(expected, DetailsTabs.DefaultFor(kind));
        Assert.Equal(expected, DetailsTabs.TabsFor(kind)[0]);
    }

    [Fact]
    public void A_tab_the_kind_does_not_have_falls_back_to_its_default()
    {
        Assert.Equal(DetailsTab.Components, DetailsTabs.Resolve(ObjectKind.Table, DetailsTab.Design));
        Assert.Equal(DetailsTab.Dependencies, DetailsTabs.Resolve(ObjectKind.Table, DetailsTab.Dependencies));
    }

    [Fact]
    public void A_connection_reference_is_known_by_its_table_not_its_type_code()
    {
        var item = new SolutionComponentItem
        {
            Name = "new_outlook", ComponentType = 10132, ComponentTypeName = "Connection Reference",
            ComponentLogicalName = "connectionreference", ObjectId = Guid.NewGuid()
        };

        Assert.Equal(ObjectKind.ConnectionReference, DetailsTabs.KindOf(item));
        Assert.Equal(["Connection", "Used by"], DetailsTabs.ShortcutsFor(item).Select(s => s.Label));
    }

    // ---------------------------------------------------------------- shortcuts

    [Fact]
    public void A_cloud_flow_offers_design_run_history_and_definition()
    {
        var shortcuts = DetailsTabs.ShortcutsFor(CloudFlow());

        Assert.Equal(["Design", "Run history", "Definition"], shortcuts.Select(s => s.Label));
        Assert.Equal([DetailsTab.Design, DetailsTab.Runs, DetailsTab.Source], shortcuts.Select(s => s.Tab));
    }

    [Fact]
    public void A_table_offers_its_component_groups()
    {
        var shortcuts = DetailsTabs.ShortcutsFor(Item(1));

        Assert.Equal(["Columns", "Relationships", "Forms", "Views"], shortcuts.Select(s => s.Label));
        Assert.All(shortcuts, s => Assert.Equal(DetailsTab.Components, s.Tab));
        Assert.Equal(TableChildKind.Form, shortcuts[2].Group);
    }

    [Theory]
    [InlineData(29, 0, "System jobs,Layers")]
    [InlineData(91, null, "Trace log,Layers")]
    [InlineData(61, null, "Source,Layers")]
    [InlineData(380, null, "Value")]
    [InlineData(26, null, "Layers,Dependencies")]
    public void Other_kinds_offer_their_own_tab_and_a_generic_one(int type, int? category, string expected)
    {
        Assert.Equal(expected, string.Join(",", DetailsTabs.ShortcutsFor(Item(type, category)).Select(s => s.Label)));
    }

    // ---------------------------------------------------------------- the window's selection

    [Fact]
    public void The_window_opens_on_the_types_own_tab()
    {
        Assert.Equal(DetailsTab.Design, Details(CloudFlow()).SelectedTab);
        Assert.Equal(DetailsTab.Components, Details(Item(1)).SelectedTab);
        Assert.Equal(DetailsTab.Layers, Details(Item(26)).SelectedTab);
    }

    [Fact]
    public void A_shortcut_opens_the_window_on_its_tab()
    {
        var details = Details(CloudFlow(), new DetailsShortcut("Run history", DetailsTab.Runs));

        Assert.Equal(DetailsTab.Runs, details.SelectedTab);
    }

    [Fact]
    public async Task A_table_opened_on_its_views_selects_that_group()
    {
        var table = Item(1);
        var handler = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "EntityDefinitions(", """{"LogicalName":"account","ObjectTypeCode":1}""")
            .OnJson(HttpMethod.Get, "", """{"value":[]}""");
        var details = new ObjectDetailsViewModel(Fakes.Dataverse(handler), table, new Dictionary<Guid, SolutionComponentItem>(),
            openOn: new DetailsShortcut("Views", DetailsTab.Components, TableChildKind.View));

        await details.LoadAsync();

        Assert.NotEmpty(details.ChildGroups);
        Assert.Equal(TableChildKind.View, details.SelectedChildGroup?.Kind);
    }

    [Fact]
    public void A_classic_workflows_runs_are_its_system_jobs()
    {
        Assert.Equal("System jobs", Details(Item(29, 0)).RunsTabHeader);
        Assert.Equal("Run history", Details(CloudFlow()).RunsTabHeader);
    }

    [Fact]
    public void A_flows_source_tab_is_its_definition_and_a_web_resources_its_source()
    {
        Assert.Equal("Definition", Details(CloudFlow()).SourceTabHeader);
        Assert.Equal("Source", Details(Item(61)).SourceTabHeader);
    }

    // ---------------------------------------------------------------- the flow in Power Automate

    [Fact]
    public void A_cloud_flow_links_to_its_page_in_power_automate()
    {
        var flow = CloudFlow();

        var details = Details(flow);

        Assert.Equal($"https://make.powerautomate.com/environments/env-1/flows/{flow.WorkflowIdUnique}/details", details.FlowUrl);
        Assert.True(details.OpenInPowerAutomateCommand.CanExecute(null));
    }

    [Fact]
    public void Without_an_environment_id_there_is_no_power_automate_link()
    {
        var details = Details(CloudFlow(), environmentId: null);

        Assert.Null(details.FlowUrl);
        Assert.False(details.OpenInPowerAutomateCommand.CanExecute(null));
    }

    [Fact]
    public void Only_a_cloud_flow_has_a_power_automate_link()
    {
        Assert.Null(Details(Item(61)).FlowUrl);
    }

    [Fact]
    public void The_flow_url_builder_needs_both_ids()
    {
        Assert.Null(MakerPortalLinkBuilder.BuildFlowUrl(null, "f"));
        Assert.Null(MakerPortalLinkBuilder.BuildFlowUrl("e", " "));
        Assert.Equal("https://make.powerautomate.com/environments/e%201/flows/f/details", MakerPortalLinkBuilder.BuildFlowUrl("e 1", "f"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task The_flow_state_is_read_with_its_definition(int statecode, bool on)
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, $"workflows({id})?$select=clientdata,statecode",
            $$"""{"clientdata":"{}","statecode":{{statecode}}}""");

        var flow = await Fakes.Dataverse(handler).GetCloudFlowAsync(id);

        Assert.Equal("{}", flow.Definition);
        Assert.Equal(on, flow.IsOn);
    }

    [Fact]
    public async Task A_flow_with_no_state_says_nothing_about_it()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "workflows(", """{"clientdata":null}""");

        var flow = await Fakes.Dataverse(handler).GetCloudFlowAsync(Guid.NewGuid());

        Assert.Null(flow.IsOn);
    }

    // ---------------------------------------------------------------- the session's detail pane

    [Fact]
    public void The_detail_pane_offers_the_selected_objects_shortcuts()
    {
        var session = new EnvironmentSessionViewModel(new AuthenticationService(), new AppSettings(),
            new TabState { EnvironmentUrl = "https://contoso.crm11.dynamics.com" });

        Assert.Empty(session.DetailShortcuts);

        session.SelectedItem = Item(1);

        Assert.Equal(4, session.DetailShortcuts.Count);
        Assert.False(session.IsSelectedEnvironmentVariable);

        session.SelectedItem = Item(380);

        Assert.True(session.IsSelectedEnvironmentVariable);
        Assert.Null(session.LatestRun);
    }
}
