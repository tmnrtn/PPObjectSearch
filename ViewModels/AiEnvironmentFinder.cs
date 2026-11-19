using System.Collections.Concurrent;
using System.Reflection;
using PPObjectSearch.Ai;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// The environments whose data a screen shows, found from the view models it binds to: an
/// environment itself, or a view model's environment properties - its Session, or a comparison's
/// Left and Right. Only where a view model names no environment of its own is a list of them
/// taken to be what it shows, as a search across every open environment does. Read through the
/// declared types alone, so no other property's getter runs.
/// </summary>
public sealed class AiEnvironmentFinder
{
    private static readonly ConcurrentDictionary<Type, (PropertyInfo[] Single, PropertyInfo[] Lists)> Properties = new();

    private readonly Dictionary<string, AiEnvironment> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<AiEnvironment> Found => _found.Values.ToList();

    /// <summary>Adds the environments a view model - a screen's, a row's - shows.</summary>
    public void Add(object? viewModel)
    {
        if (viewModel is null || !_seen.Add(viewModel)) return;

        if (viewModel is EnvironmentSessionViewModel session)
        {
            Add(session);
            return;
        }

        var (single, lists) = Properties.GetOrAdd(viewModel.GetType(), Find);
        if (single.Length == 0 && lists.Length == 0) return;

        var named = single.Select(p => Read(p, viewModel)).OfType<EnvironmentSessionViewModel>().ToList();
        if (named.Count > 0)
        {
            foreach (var s in named) Add(s);
            return;
        }

        foreach (var list in lists)
        {
            if (Read(list, viewModel) is IEnumerable<EnvironmentSessionViewModel> sessions)
            {
                foreach (var s in sessions.ToList()) Add(s);
            }
        }
    }

    private void Add(EnvironmentSessionViewModel session)
    {
        if (string.IsNullOrWhiteSpace(session.EnvironmentUrl)) return;
        _found.TryAdd(session.EnvironmentUrl, new AiEnvironment(session.Title, session.EnvironmentUrl, session.EnvironmentSku));
    }

    private static object? Read(PropertyInfo property, object owner)
    {
        try
        {
            return property.GetValue(owner);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static (PropertyInfo[] Single, PropertyInfo[] Lists) Find(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null)
            .ToList();

        return (
            properties.Where(p => typeof(EnvironmentSessionViewModel).IsAssignableFrom(p.PropertyType)).ToArray(),
            properties.Where(p => typeof(IEnumerable<EnvironmentSessionViewModel>).IsAssignableFrom(p.PropertyType)).ToArray());
    }
}
