using System.Net;
using System.Net.Http;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;
using static PPObjectSearch.Tests.ViewModels.DetailsHarness;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The details window's load: solutions, dependencies and layers, its status line, and its links and commands.</summary>
public class ObjectDetailsViewModelTests
{
    private static readonly Guid FormId = Guid.Parse("f0000000-0000-0000-0000-000000000001");
    private static readonly Guid AppId = Guid.Parse("a0000000-0000-0000-0000-000000000002");

    private const string TwoSolutions = """
        {"value":[
          {"componenttype":26,"solutionid":{"solutionid":"11111111-0000-0000-0000-000000000001","uniquename":"core","friendlyname":"Core","ismanaged":true,"version":"1.0.0.0"}},
          {"componenttype":26,"solutionid":{"solutionid":"11111111-0000-0000-0000-000000000002","uniquename":"patch","friendlyname":"Patch","ismanaged":false}}
        ]}
        """;

    private const string TwoLayers = """
        {"value":[
          {"msdyn_solutionname":"Core","msdyn_publishername":"Contoso","msdyn_order":1,"msdyn_componentjson":"{\"a\":1}"},
          {"msdyn_solutionname":"Active","msdyn_order":2,"msdyn_componentjson":"{\"a\":2}","msdyn_changes":"a"}
        ]}
        """;

    private static string Dependency(string prefix, Guid id) =>
        $$"""{"value":[{"{{prefix}}objectid":"{{id}}","{{prefix}}type":60,"{{prefix}}type@OData.Community.Display.V1.FormattedValue":"System Form"}]}""";

    private static FakeHttpHandler Loaded() => new FakeHttpHandler()
        .OnJson(HttpMethod.Get, "solutioncomponents?", TwoSolutions)
        .OnJson(HttpMethod.Get, "RetrieveDependentComponents(", Dependency("dependentcomponent", FormId))
        .OnJson(HttpMethod.Get, "RetrieveRequiredComponents(", Dependency("requiredcomponent", AppId))
        .OnJson(HttpMethod.Get, "msdyn_componentlayers?", TwoLayers);

    [Fact]
    public async Task Loading_lists_the_solutions_dependencies_and_layers_and_sums_them_up()
    {
        var known = new Dictionary<Guid, SolutionComponentItem>
        {
            [FormId] = new() { Name = "main_form", DisplayName = "Main form", ComponentType = 60, ComponentTypeName = "System Form", ObjectId = FormId }
        };
        var details = Details(Loaded(), Item(26), known: known);
        var changed = new List<string?>();
        details.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await details.LoadAsync();

        Assert.Equal(["Patch", "Core"], details.Solutions.Select(s => s.FriendlyName));
        Assert.Equal("Main form", Assert.Single(details.Dependents).DisplayName);
        Assert.Equal(AppId.ToString(), Assert.Single(details.Required).DisplayName);
        Assert.Equal(2, details.DependencyCount);
        Assert.Equal(["Active", "Core"], details.Layers.Select(l => l.SolutionName));
        Assert.True(details.HasUnmanagedLayer);
        Assert.True(details.LayersSupported);
        Assert.Equal("2 · 2", details.SolutionsTabCount);
        Assert.Equal("2 solution(s), 1 dependent, 1 required.", details.Status);
        Assert.Equal(details.Status, details.StatusText);
        Assert.False(details.IsBusy);
        Assert.Contains(nameof(ObjectDetailsViewModel.SolutionsTabCount), changed);
        Assert.Contains(nameof(ObjectDetailsViewModel.DependencyCount), changed);
    }

    [Fact]
    public async Task A_type_without_layers_hides_them_rather_than_showing_none()
    {
        var handler = new FakeHttpHandler().Quiet();
        var details = Details(handler, Item(3));

        await details.LoadAsync();

        Assert.False(details.LayersSupported);
        Assert.False(details.HasUnmanagedLayer);
        Assert.Equal("0", details.SolutionsTabCount);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("msdyn_componentlayers"));
        Assert.Equal("0 solution(s), 0 dependent, 0 required.", details.Status);
    }

    [Fact]
    public async Task Each_part_that_fails_is_named_and_the_rest_still_load()
    {
        var handler = new FakeHttpHandler()
            .OnError(HttpMethod.Get, "solutioncomponents?", HttpStatusCode.InternalServerError, "No solutions")
            .OnError(HttpMethod.Get, "RetrieveDependentComponents(", HttpStatusCode.Forbidden, "No dependents")
            .OnJson(HttpMethod.Get, "RetrieveRequiredComponents(", Dependency("requiredcomponent", AppId))
            .OnError(HttpMethod.Get, "msdyn_componentlayers?", HttpStatusCode.BadRequest, "No layers");
        var details = Details(handler, Item(26));

        await details.LoadAsync();

        Assert.Single(details.Required);
        Assert.Empty(details.Solutions);
        Assert.StartsWith("Some details could not be read - solutions: ", details.Status);
        Assert.Contains("No solutions", details.Status);
        Assert.Contains("; dependent components: ", details.Status);
        Assert.Contains("No dependents", details.Status);
        Assert.Contains("; layers: ", details.Status);
    }

    [Fact]
    public async Task A_second_load_while_one_is_running_is_ignored()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new FakeHttpHandler()
            .OnAsync(HttpMethod.Get, "solutioncomponents?", _ => slow.Task)
            .Quiet();
        var details = Details(handler, Item(26));

        var first = details.LoadAsync();

        Assert.True(details.IsBusy);
        Assert.Equal("Loading...", details.Status);
        Assert.False(details.RefreshCommand.CanExecute(null));

        await details.LoadAsync();
        slow.SetResult(FakeHttpHandler.Json(TwoSolutions));
        await first;

        Assert.Equal(2, details.Solutions.Count);
        Assert.Single(handler.Requests, r => r.Url.Contains("solutioncomponents?"));
        Assert.True(details.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refreshing_reads_everything_again_without_listing_anything_twice()
    {
        var handler = Loaded();
        var details = Details(handler, Item(26));
        await details.LoadAsync();

        await details.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, details.Solutions.Count);
        Assert.Equal(2, details.Layers.Count);
        Assert.Single(details.Dependents);
        Assert.Equal(2, handler.Requests.Count(r => r.Url.Contains("solutioncomponents?")));
    }

    [Fact]
    public async Task A_window_closed_while_loading_stops_at_its_next_step()
    {
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new FakeHttpHandler()
            .OnAsync(HttpMethod.Get, "solutioncomponents?", _ => slow.Task)
            .Quiet();
        var details = Details(handler, Item(26));

        var load = details.LoadAsync();
        details.Detach();
        slow.SetResult(FakeHttpHandler.Json(TwoSolutions));
        await load;

        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("msdyn_componentlayers"));
        Assert.False(details.IsBusy);

        var before = handler.Requests.Count;
        await details.LoadAsync();

        Assert.Equal(before, handler.Requests.Count);
    }

    [Fact]
    public async Task A_layers_changes_can_be_viewed_for_the_selected_or_a_given_layer()
    {
        var details = Details(Loaded(), Item(26));
        await details.LoadAsync();
        var raised = 0;
        details.ViewLayerChangesCommand.CanExecuteChanged += (_, _) => raised++;

        Assert.False(details.ViewLayerChangesCommand.CanExecute(null));
        Assert.True(details.ViewLayerChangesCommand.CanExecute(details.Layers[1]));

        details.SelectedLayer = details.Layers[0];

        Assert.True(details.ViewLayerChangesCommand.CanExecute(null));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_maker_link_opens_through_the_tabs_browser()
    {
        var opened = new List<string?>();
        var item = Item(26);
        item.MakerUrl = "https://make.powerapps.com/environments/env-1/solutions";

        var details = Details(new FakeHttpHandler(), item, openUrl: opened.Add);
        details.OpenLinkCommand.Execute(null);

        Assert.Equal([item.MakerUrl], opened);
        Assert.False(Details(new FakeHttpHandler(), Item(26)).OpenLinkCommand.CanExecute(null));
    }

    [Fact]
    public void Only_an_object_with_an_id_offers_to_copy_it()
    {
        var withId = Details(new FakeHttpHandler(), Item(26));
        var withoutId = Details(new FakeHttpHandler(), new SolutionComponentItem { Name = "x", ComponentTypeName = "Type", ComponentType = 26 });

        Assert.True(withId.CopyIdCommand.CanExecute(null));
        Assert.False(withoutId.CopyIdCommand.CanExecute(null));
    }

    [Fact]
    public void Find_usages_needs_a_tab_and_a_name_and_asks_the_tab_to_search()
    {
        var session = Session();
        var details = Details(new FakeHttpHandler(), Item(2), session: session);

        Assert.False(Details(new FakeHttpHandler(), Item(2)).FindUsagesCommand.CanExecute("new_status"));
        Assert.False(details.FindUsagesCommand.CanExecute(string.Empty));
        Assert.False(details.FindUsagesCommand.CanExecute(42));
        Assert.True(details.FindUsagesCommand.CanExecute("new_status"));

        details.FindUsagesCommand.Execute("new_status");

        Assert.Equal("Connect first.", session.Status);
    }

    [Theory]
    [InlineData(1, CodeLanguage.Html)]
    [InlineData(2, CodeLanguage.Css)]
    [InlineData(3, CodeLanguage.JavaScript)]
    [InlineData(4, CodeLanguage.Xml)]
    [InlineData(9, CodeLanguage.Xml)]
    [InlineData(11, CodeLanguage.Xml)]
    [InlineData(12, CodeLanguage.Xml)]
    [InlineData(5, CodeLanguage.None)]
    public void A_web_resource_is_highlighted_by_its_type(int type, CodeLanguage expected)
    {
        Assert.Equal(expected, ObjectDetailsViewModel.LanguageFor(type));
    }

    [Theory]
    [InlineData(1, null, null, "table")]
    [InlineData(61, null, null, "source")]
    [InlineData(90, null, null, "trace")]
    [InlineData(91, null, null, "trace")]
    [InlineData(92, null, null, "trace")]
    [InlineData(380, null, null, "variable")]
    [InlineData(381, null, null, "variable")]
    [InlineData(29, 5, null, "runs,source,connections")]
    [InlineData(29, 0, null, "runs")]
    [InlineData(10132, null, "connectionreference", "connections")]
    [InlineData(10101, null, "customapi", "overview")]
    [InlineData(26, null, null, "")]
    public void Each_kind_of_object_reads_what_it_has(int type, int? category, string? logicalName, string expected)
    {
        var details = Details(new FakeHttpHandler(), Item(type, category, logicalName));

        var has = new (bool Has, string Name)[]
        {
            (details.IsTable, "table"), (details.HasRunHistory, "runs"), (details.HasTraceLog, "trace"),
            (details.IsEnvironmentVariable, "variable"), (details.HasSource, "source"), (details.HasConnections, "connections"),
            (details.HasOverview, "overview")
        };

        Assert.Equal(expected, string.Join(",", has.Where(h => h.Has).Select(h => h.Name)));
        Assert.Equal(type == 61, details.IsWebResource);
    }

    [Fact]
    public void A_switch_is_offered_only_from_a_tab_and_says_what_it_does_there()
    {
        var session = Session();
        var fromTab = Details(new FakeHttpHandler(), Item(92), session: session);
        var alone = Details(new FakeHttpHandler(), Item(92));

        Assert.Equal(SwitchableKind.PluginStep, fromTab.SwitchKind);
        Assert.True(fromTab.HasSwitch);
        Assert.False(alone.HasSwitch);
        Assert.Null(alone.SwitchToolTip);
        Assert.Equal($"Enables the plug-in step in {session.Title}. Asks first; recorded in the run log.", fromTab.SwitchToolTip);
        Assert.Equal("Enable", fromTab.SwitchButtonLabel);
        Assert.Equal("Enable…", fromTab.SwitchButtonText);
        Assert.Null(fromTab.SwitchStateLabel);
        Assert.False(fromTab.ToggleSwitchCommand.CanExecute(null));
    }

    [Fact]
    public void Something_with_no_switch_has_no_switch_labels()
    {
        var details = Details(new FakeHttpHandler(), Item(26), session: Session());

        Assert.Null(details.SwitchKind);
        Assert.False(details.HasSwitch);
        Assert.Equal(string.Empty, details.SwitchButtonLabel);
        Assert.Equal(string.Empty, details.SwitchButtonText);
        Assert.Null(details.SwitchStateLabel);
    }

    [Fact]
    public void A_tab_change_moves_the_status_line_only_when_it_is_a_change()
    {
        var details = Details(new FakeHttpHandler(), Item(26));
        var changed = new List<string?>();
        details.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        details.SelectedTab = DetailsTab.Layers;
        details.SelectedTab = DetailsTab.Dependencies;

        Assert.Equal([nameof(ObjectDetailsViewModel.SelectedTab), nameof(ObjectDetailsViewModel.StatusText)], changed);
    }
}
