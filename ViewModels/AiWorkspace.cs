using System.Collections.ObjectModel;
using System.Text;
using PPObjectSearch.Ai;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// The app as the assistant's tools see it: the open screens and environments, each read as the
/// assistant reads the screen it was asked about, and each environment's loaded objects to search.
/// Environments the assistant may not read are left out of the list and refused by name.
/// </summary>
public sealed class AiWorkspace : IAiWorkspace
{
    internal const int MaxSearchResults = 50;
    private const int MaxSummaryRows = 200;

    private readonly ObservableCollection<EnvironmentSessionViewModel> _sessions;
    private readonly ScreenHost _screens;
    private readonly Func<EnvironmentSessionViewModel?> _selected;
    private readonly AiSettings _settings;

    public AiWorkspace(
        ObservableCollection<EnvironmentSessionViewModel> sessions,
        ScreenHost screens,
        Func<EnvironmentSessionViewModel?> selected,
        AiSettings settings)
    {
        _sessions = sessions;
        _screens = screens;
        _selected = selected;
        _settings = settings;
    }

    /// <summary>
    /// Reads a screen's view - an open screen's, or the main window's for the selected environment -
    /// on the UI thread. Set by the main window; null when there is no view to read.
    /// </summary>
    public Func<object, string, AiScreen?> ReadView { get; set; } = (_, _) => null;

    /// <summary>
    /// What a screen - an open screen, or an environment - is, without reading it: its title, the
    /// environments its view models name, and the item selected on it.
    /// </summary>
    public static AiContext Describe(object key)
    {
        switch (key)
        {
            case OpenScreen screen:
            {
                var context = (screen.Content as System.Windows.FrameworkElement)?.DataContext;
                var finder = new AiEnvironmentFinder();
                finder.Add(context);
                return new AiContext(screen.Title, finder.Found, (context as IAiSelection)?.SelectedNameForAi);
            }
            case EnvironmentSessionViewModel session:
                return new AiContext(session.Title,
                    string.IsNullOrWhiteSpace(session.EnvironmentUrl) ? [] : [new AiEnvironment(session.Title, session.EnvironmentUrl, session.EnvironmentSku)],
                    session.SelectedNameForAi);
            default:
                return new AiContext(string.Empty, []);
        }
    }

    public string ListScreens()
    {
        var text = new StringBuilder("Environments:\n");
        foreach (var session in Connected())
        {
            text.Append("- ").Append(session.Title)
                .Append(" (").Append(session.EnvironmentUrl).Append(')');
            if (ReferenceEquals(session, _selected())) text.Append(" - selected");
            if (!_settings.IsEnvironmentAllowed(session.EnvironmentUrl, session.EnvironmentSku)) text.Append(" - AI is off for it: don't ask for it");
            text.Append('\n');
        }

        text.Append("\nOpen screens:\n");
        if (_screens.Open.Count == 0) text.Append("(none)\n");
        foreach (var screen in _screens.Open) text.Append("- ").Append(screen.Title).Append('\n');

        return text.ToString();
    }

    public AiScreen? ReadScreen(string title)
    {
        title = title.Trim();
        AiScreen? read = null;

        if (_screens.Open.FirstOrDefault(s => Same(s.Title, title)) is { } screen)
        {
            read = ReadView(screen, screen.Title);
        }
        else if (FindSession(title) is { } session)
        {
            read = ReadView(session, session.Title) ?? Summary(session);
        }

        if (read is null) return null;

        // The same check as the question itself: a screen showing a turned-off environment is not read.
        var excluded = read.Environments.Where(e => !_settings.IsEnvironmentAllowed(e.Url, e.Sku)).ToList();
        return excluded.Count == 0
            ? read
            : new AiScreen(read.Title, $"Not read: AI is off for {string.Join(" and ", excluded.Select(e => e.Title))}.");
    }

    public string SearchObjects(string query, string? environment)
    {
        var session = string.IsNullOrWhiteSpace(environment) ? _selected() : FindSession(environment);
        if (session is null) return $"No connected environment is called \"{environment}\". Call list_open_screens for their names.";
        if (!_settings.IsEnvironmentAllowed(session.EnvironmentUrl, session.EnvironmentSku)) return $"Not searched: AI is off for {session.Title}.";
        if (!session.IsConnected) return $"{session.Title} isn't connected.";

        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return "Give at least one keyword.";

        var matches = session.AllItems
            .Where(i => terms.All(t => i.SearchIndex.Contains(t, StringComparison.Ordinal)))
            .ToList();

        var solution = session.SelectedSolution?.FriendlyName ?? "the loaded solution";
        if (matches.Count == 0) return $"Nothing in {solution} in {session.Title} matches \"{query}\".";

        var text = new StringBuilder(matches.Count > MaxSearchResults
            ? $"{matches.Count:N0} objects in {solution} in {session.Title} match; the first {MaxSearchResults} follow.\n"
            : $"{matches.Count:N0} object{(matches.Count == 1 ? "" : "s")} in {solution} in {session.Title} match.\n");
        AppendRows(text, matches.Take(MaxSearchResults));
        return text.ToString();
    }

    /// <summary>An environment not on screen: what is loaded for it, and its objects as its filters show them.</summary>
    internal static AiScreen Summary(EnvironmentSessionViewModel session)
    {
        var text = new StringBuilder()
            .Append("Environment: ").Append(session.Title).Append(" (").Append(session.EnvironmentUrl).Append(")\n")
            .Append("Type: ").Append(session.EnvironmentSku).Append('\n');

        if (!session.IsConnected)
        {
            text.Append("Not connected.\n");
        }
        else
        {
            text.Append("Solution: ").Append(session.SelectedSolution?.FriendlyName ?? "(none)").Append('\n');
            if (!string.IsNullOrWhiteSpace(session.SearchText)) text.Append("Search: ").Append(session.SearchText).Append('\n');

            var shown = session.ItemsView.Cast<SolutionComponentItem>().ToList();
            text.Append($"{shown.Count:N0} of {session.AllItems.Count:N0} objects shown");
            text.Append(shown.Count > MaxSummaryRows ? $"; the first {MaxSummaryRows} follow.\n" : ".\n");
            AppendRows(text, shown.Take(MaxSummaryRows));
        }

        return new AiScreen(session.Title, text.ToString())
        {
            Environments = string.IsNullOrWhiteSpace(session.EnvironmentUrl) ? [] : [new AiEnvironment(session.Title, session.EnvironmentUrl, session.EnvironmentSku)]
        };
    }

    private static void AppendRows(StringBuilder text, IEnumerable<SolutionComponentItem> items)
    {
        text.Append("Type\tSub type\tName\tDisplay name\tTable\tState\tModified\tId\n");
        foreach (var i in items)
        {
            text.Append(i.ComponentTypeName).Append('\t')
                .Append(i.SubType).Append('\t')
                .Append(i.Name).Append('\t')
                .Append(i.DisplayName).Append('\t')
                .Append(i.PrimaryEntityName).Append('\t')
                .Append(i.ManagedLabel).Append('\t')
                .Append(i.ModifiedOn?.ToLocalTime().ToString("g")).Append('\t')
                .Append(i.ObjectId == Guid.Empty ? "" : i.ObjectId.ToString()).Append('\n');
        }
    }

    private IEnumerable<EnvironmentSessionViewModel> Connected() =>
        _sessions.Where(s => !string.IsNullOrWhiteSpace(s.EnvironmentUrl));

    private EnvironmentSessionViewModel? FindSession(string name)
    {
        name = name.Trim();
        return Connected().FirstOrDefault(s => Same(s.Title, name))
               ?? Connected().FirstOrDefault(s => Same(AiSettings.Host(s.EnvironmentUrl), AiSettings.Host(name)));
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
