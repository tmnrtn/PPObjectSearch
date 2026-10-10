using System.Runtime.CompilerServices;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>
/// Environment tabs for the windows that work from one: connected to a fake handler without
/// signing in, with whatever solutions and loaded rows the test needs.
/// </summary>
public static class TestSessions
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_client")]
    private static extern ref DataverseClient? ClientField(EnvironmentSessionViewModel session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_allItems")]
    private static extern ref List<SolutionComponentItem> ItemsField(EnvironmentSessionViewModel session);

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

    /// <summary>A tab connected through the handler, with the solution and objects loaded and listed.</summary>
    public static EnvironmentSessionViewModel Connected(
        FakeHttpHandler handler, IEnumerable<SolutionComponentItem> items, string url = Fakes.EnvironmentUrl,
        SolutionInfo? solution = null, AppSettings? settings = null)
    {
        var session = Disconnected(url, settings);
        Connect(session, handler, items, solution);
        return session;
    }

    /// <summary>
    /// A tab connected through <paramref name="handler"/>, showing <paramref name="solution"/> with
    /// <paramref name="items"/> loaded but not listed. The solution is chosen before the client is in
    /// place, so choosing it reads nothing.
    /// </summary>
    public static EnvironmentSessionViewModel Connected(FakeHttpHandler handler, string url = Fakes.EnvironmentUrl,
        SolutionInfo? solution = null, params SolutionComponentItem[] items)
    {
        var session = Disconnected(url);

        if (solution is not null)
        {
            session.Solutions.Add(solution);
            session.SelectedSolution = solution;
        }

        ItemsField(session).AddRange(items);
        session.UseConnectedClient(Fakes.Dataverse(handler, url));
        return session;
    }

    /// <summary>Connects an existing tab through the handler and lists the solution's objects in it.</summary>
    public static void Connect(EnvironmentSessionViewModel session, FakeHttpHandler handler,
        IEnumerable<SolutionComponentItem> items, SolutionInfo? solution = null)
    {
        session.UseConnectedClient(Fakes.Dataverse(handler, session.EnvironmentUrl));
        session.UseLoadedSolution(solution ?? Solution(), items);
    }

    /// <summary>Takes the client away again - opening details then does nothing rather than open a window.</summary>
    public static void DropClient(EnvironmentSessionViewModel session) => ClientField(session) = null;

    /// <summary>Waits for something a view model started without awaiting it.</summary>
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
