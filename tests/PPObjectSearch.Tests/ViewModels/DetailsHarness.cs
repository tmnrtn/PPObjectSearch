using System.Diagnostics;
using System.Net.Http;
using PPObjectSearch.Auth;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>What the details window tests share: the object, its environment, and the reads every object makes.</summary>
internal static class DetailsHarness
{
    public static readonly Guid Id = Guid.Parse("0b1ec700-0000-0000-0000-000000000001");
    public static readonly Guid FlowUniqueId = Guid.Parse("0b1ec700-0000-0000-0000-0000000000aa");

    public const string EnvironmentId = "env-1";

    public const string Empty = """{"value":[]}""";

    public static SolutionComponentItem Item(int type, int? category = null, string? logicalName = null, string name = "thing",
        string? displayName = null) => new()
    {
        Name = name, DisplayName = displayName, ComponentType = type, ComponentTypeName = "Type", ComponentLogicalName = logicalName,
        ObjectId = Id, ProcessCategory = category, WorkflowIdUnique = type == 29 ? FlowUniqueId : null
    };

    public static SolutionComponentItem CloudFlow(string displayName = "Notify on case") => Item(29, 5, displayName: displayName);

    /// <summary>The reads every object makes - its solutions, dependencies and layers - answered with nothing.</summary>
    public static FakeHttpHandler Quiet(this FakeHttpHandler handler) => handler
        .OnJson(HttpMethod.Get, "solutioncomponents?", Empty)
        .OnJson(HttpMethod.Get, "RetrieveDependentComponents(", Empty)
        .OnJson(HttpMethod.Get, "RetrieveRequiredComponents(", Empty)
        .OnJson(HttpMethod.Get, "msdyn_componentlayers?", Empty);

    public static ObjectDetailsViewModel Details(
        FakeHttpHandler handler,
        SolutionComponentItem item,
        string? environmentId = null,
        EnvironmentSessionViewModel? session = null,
        Action<string?>? openUrl = null,
        IReadOnlyDictionary<Guid, SolutionComponentItem>? known = null,
        DetailsShortcut? openOn = null) =>
        new(Fakes.Dataverse(handler), item, known ?? new Dictionary<Guid, SolutionComponentItem>(), environmentId,
            openUrl ?? (_ => { }), session, openOn);

    public static EnvironmentSessionViewModel Session() =>
        new(new AuthenticationService(), new AppSettings(), new TabState { EnvironmentUrl = Fakes.EnvironmentUrl });

    /// <summary>
    /// Starts work whose progress is reported through <see cref="Progress{T}"/>. In the app the
    /// dispatcher delivers each report before the result; here, without one, a report could land
    /// after it. Posting inline keeps that order.
    /// </summary>
    public static Task StartInOrder(Func<Task> start)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            return start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    /// <summary>For work the view model starts without handing back a task.</summary>
    public static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The condition was never met.");
            await Task.Delay(5);
        }
    }
}
