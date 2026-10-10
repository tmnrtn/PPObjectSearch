using System.Runtime.CompilerServices;
using PPObjectSearch.Auth;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>
/// Environment tabs for tool-window tests: connected to a fake handler without signing in, with
/// whatever solutions and loaded rows the test needs.
/// </summary>
public static class TestSessions
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_client")]
    private static extern ref DataverseClient? ClientField(EnvironmentSessionViewModel session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isConnected")]
    private static extern ref bool ConnectedField(EnvironmentSessionViewModel session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_allItems")]
    private static extern ref List<SolutionComponentItem> ItemsField(EnvironmentSessionViewModel session);

    /// <summary>A tab that has never connected.</summary>
    public static EnvironmentSessionViewModel Disconnected(string url = Fakes.EnvironmentUrl) =>
        new(new AuthenticationService(), new AppSettings(), new TabState { EnvironmentUrl = url });

    /// <summary>
    /// A tab connected through <paramref name="handler"/>, showing <paramref name="solution"/> with
    /// <paramref name="items"/> loaded. The solution is chosen before the client is in place, so
    /// choosing it reads nothing.
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
        ClientField(session) = Fakes.Dataverse(handler, url);
        ConnectedField(session) = true;
        return session;
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
