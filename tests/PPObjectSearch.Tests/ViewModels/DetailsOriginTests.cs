using PPObjectSearch.Auth;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>A details window says which environment it came from.</summary>
public class DetailsOriginTests
{
    private static SolutionComponentItem Flow() => new()
    {
        Name = "Get Defect Report", ComponentType = 29, ComponentTypeName = "Process", ObjectId = Guid.NewGuid()
    };

    private static EnvironmentSessionViewModel Session(string url) =>
        new(new AuthenticationService(), new AppSettings(), new TabState { EnvironmentUrl = url });

    private static ObjectDetailsViewModel Details(EnvironmentSessionViewModel? session) =>
        new(Fakes.Dataverse(new FakeHttpHandler()), Flow(), new Dictionary<Guid, SolutionComponentItem>(), session: session);

    [Fact]
    public void The_window_title_names_the_environment()
    {
        var session = Session("https://ect-preprod.crm11.dynamics.com");

        var details = Details(session);

        Assert.Equal($"Get Defect Report — Process · {session.Title}", details.Title);
        Assert.True(details.HasSession);
        Assert.Same(session, details.Session);
    }

    [Fact]
    public void Without_a_tab_the_title_is_the_object_alone()
    {
        var details = Details(null);

        Assert.Equal("Get Defect Report — Process", details.Title);
        Assert.False(details.HasSession);
    }

    [Fact]
    public void Two_windows_for_the_same_object_in_different_environments_tell_apart()
    {
        var dev = Details(Session("https://contoso-dev.crm11.dynamics.com"));
        var prod = Details(Session("https://contoso-prod.crm11.dynamics.com"));

        Assert.NotEqual(dev.Title, prod.Title);
    }

    [Fact]
    public void A_closed_window_stops_following_its_tab()
    {
        var session = Session("https://contoso-dev.crm11.dynamics.com");
        var details = Details(session);
        var raised = 0;
        details.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ObjectDetailsViewModel.Title)) raised++; };

        session.EnvironmentUrl = "https://contoso-test.crm11.dynamics.com";   // renames the tab
        var whileOpen = raised;

        details.Detach();
        session.EnvironmentUrl = "https://contoso-uat.crm11.dynamics.com";

        Assert.True(whileOpen > 0);
        Assert.Equal(whileOpen, raised);
    }
}
