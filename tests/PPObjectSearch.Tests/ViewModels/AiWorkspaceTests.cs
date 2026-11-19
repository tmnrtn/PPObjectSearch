using System.Collections.ObjectModel;
using PPObjectSearch.Ai;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

public class AiWorkspaceTests
{
    private const string ProdUrl = "https://prod.crm11.dynamics.com";

    private static (AiWorkspace Workspace, EnvironmentSessionViewModel Dev, EnvironmentSessionViewModel Prod, ScreenHost Screens, AiSettings Settings) Workspace()
    {
        var dev = Session(Fakes.EnvironmentUrl, EnvironmentSku.Developer,
            [TestSessions.Item("new_invoice"), TestSessions.Item("Invoice approval", 29, "Workflow")]);
        var prod = Session(ProdUrl, EnvironmentSku.Sandbox, [TestSessions.Item("new_invoice")]);
        var screens = new ScreenHost(new FakeScreenWindows());
        var settings = new AiSettings();
        var workspace = new AiWorkspace(new ObservableCollection<EnvironmentSessionViewModel> { dev, prod }, screens, () => dev, settings);
        return (workspace, dev, prod, screens, settings);
    }

    /// <summary>A connected tab whose type is known, as one restored from settings is.</summary>
    private static EnvironmentSessionViewModel Session(string url, EnvironmentSku sku, IEnumerable<PPObjectSearch.Models.SolutionComponentItem> items)
    {
        var session = new EnvironmentSessionViewModel(new PPObjectSearch.Auth.AuthenticationService(), new AppSettings(),
            new TabState { EnvironmentUrl = url, EnvironmentType = sku });
        TestSessions.Connect(session, new FakeHttpHandler(), items);
        return session;
    }

    [Fact]
    public void Searching_finds_the_selected_environments_objects_by_every_word()
    {
        var (workspace, _, _, _, _) = Workspace();

        var found = workspace.SearchObjects("invoice workflow", null);

        Assert.Contains("1 object", found);
        Assert.Contains("Invoice approval", found);
        Assert.DoesNotContain("new_invoice", found);
    }

    [Fact]
    public void A_turned_off_environment_is_not_searched_or_read_and_is_marked_in_the_list()
    {
        var (workspace, _, prod, _, settings) = Workspace();
        settings.SetEnvironmentAllowed(ProdUrl, false, EnvironmentSku.Sandbox);

        Assert.StartsWith("Not searched: AI is off", workspace.SearchObjects("invoice", prod.Title));
        Assert.Contains("Not read: AI is off", workspace.ReadScreen(prod.Title)!.Content);
        Assert.Contains("AI is off for it", workspace.ListScreens());
    }

    [Fact]
    public void An_environment_not_on_screen_is_read_from_what_is_loaded_for_it()
    {
        var (workspace, _, prod, _, _) = Workspace();

        var read = workspace.ReadScreen(prod.Title)!;

        Assert.Contains("new_invoice", read.Content);
        Assert.Equal(ProdUrl, Assert.Single(read.Environments).Url);
    }

    [Fact]
    public void An_open_screen_is_read_through_its_view_and_refused_if_it_shows_a_turned_off_environment()
    {
        var (workspace, dev, prod, screens, settings) = Workspace();
        screens.Show(new OpenScreen(new object(), "Compare"), ScreenPlacement.Window);
        workspace.ReadView = (_, title) => new AiScreen(title, "differences")
        {
            Environments = [new(dev.Title, dev.EnvironmentUrl), new(prod.Title, ProdUrl)]
        };

        Assert.Equal("differences", workspace.ReadScreen("compare")!.Content);

        settings.SetEnvironmentAllowed(ProdUrl, false, EnvironmentSku.Sandbox);
        Assert.StartsWith("Not read", workspace.ReadScreen("Compare")!.Content);
        Assert.Null(workspace.ReadScreen("No such screen"));
    }

    private sealed class Comparison(EnvironmentSessionViewModel left, EnvironmentSessionViewModel right, IEnumerable<EnvironmentSessionViewModel> all)
    {
        public EnvironmentSessionViewModel Left { get; } = left;
        public EnvironmentSessionViewModel? Right { get; } = right;
        public IEnumerable<EnvironmentSessionViewModel> Sessions { get; } = all;
    }

    private sealed class SearchEverywhere(IEnumerable<EnvironmentSessionViewModel> all)
    {
        public ObservableCollection<EnvironmentSessionViewModel> Sessions { get; } = new(all);
    }

    [Fact]
    public void A_screen_shows_the_environments_it_names_and_only_without_any_every_one_it_holds()
    {
        var (_, dev, prod, _, _) = Workspace();
        var third = TestSessions.Disconnected("https://test.crm11.dynamics.com");

        var comparison = new AiEnvironmentFinder();
        comparison.Add(new Comparison(dev, prod, [dev, prod, third]));
        Assert.Equal([dev.EnvironmentUrl, ProdUrl], comparison.Found.Select(e => e.Url));

        var everywhere = new AiEnvironmentFinder();
        everywhere.Add(new SearchEverywhere([dev, prod, third]));
        Assert.Equal(3, everywhere.Found.Count);
    }

    [Fact]
    public void A_production_tab_starts_with_AI_off()
    {
        var session = new EnvironmentSessionViewModel(new PPObjectSearch.Auth.AuthenticationService(), new AppSettings { Ai = new AiSettings() },
            new TabState { EnvironmentUrl = ProdUrl, EnvironmentType = EnvironmentSku.Production });

        Assert.False(session.IsAiAllowed);
    }

    [Fact]
    public void Turning_AI_off_from_the_environment_menu_is_saved_for_that_environment()
    {
        var settings = new AppSettings { Ai = new AiSettings() };
        var session = new EnvironmentSessionViewModel(new PPObjectSearch.Auth.AuthenticationService(), settings,
            new TabState { EnvironmentUrl = ProdUrl, EnvironmentType = EnvironmentSku.Sandbox });
        var previous = AiService.Current;
        AiService.Current = new AiService(settings.Ai, () => { }, new AiKeyStore());
        try
        {
            Assert.True(session.IsAiAllowed);

            session.ToggleAiAllowedCommand.Execute(null);

            Assert.False(session.IsAiAllowed);
            Assert.Equal(["prod.crm11.dynamics.com"], settings.Ai.ExcludedEnvironments);
        }
        finally
        {
            AiService.Current = previous;
        }
    }
}
