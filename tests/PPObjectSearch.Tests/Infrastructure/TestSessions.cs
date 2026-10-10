using PPObjectSearch.Auth;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>Environment tabs for the windows that work from one.</summary>
public static class TestSessions
{
    public static SolutionInfo Solution(string friendly = "Contoso Core", string unique = "contoso_core") => new()
    {
        SolutionId = Guid.NewGuid(), UniqueName = unique, FriendlyName = friendly, Version = "1.0.0.0", PublisherName = "Contoso"
    };

    public static SolutionComponentItem Item(string name, int type = 1, string typeName = "Entity", Guid? id = null, string? makerUrl = null)
    {
        var item = new SolutionComponentItem
        {
            Name = name, ComponentType = type, ComponentTypeName = typeName, ObjectId = id ?? Guid.NewGuid(), MakerUrl = makerUrl
        };
        item.BuildSearchIndex();
        return item;
    }

    /// <summary>A tab that has never connected, on the given environment.</summary>
    public static EnvironmentSessionViewModel Disconnected(string url = Fakes.EnvironmentUrl, AppSettings? settings = null) =>
        new(new AuthenticationService(), settings ?? new AppSettings(), new TabState { EnvironmentUrl = url });

    /// <summary>A tab connected through the handler, with the solution and objects loaded.</summary>
    public static EnvironmentSessionViewModel Connected(
        FakeHttpHandler handler, IEnumerable<SolutionComponentItem> items, string url = Fakes.EnvironmentUrl,
        SolutionInfo? solution = null, AppSettings? settings = null)
    {
        var session = Disconnected(url, settings);
        Connect(session, handler, items, solution);
        return session;
    }

    /// <summary>Connects an existing tab through the handler and lists the solution's objects in it.</summary>
    public static void Connect(EnvironmentSessionViewModel session, FakeHttpHandler handler,
        IEnumerable<SolutionComponentItem> items, SolutionInfo? solution = null)
    {
        session.UseConnectedClient(Fakes.Dataverse(handler, session.EnvironmentUrl));
        session.UseLoadedSolution(solution ?? Solution(), items);
    }
}
