using System.Collections.ObjectModel;
using System.Diagnostics;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// "Where is this used?" - a term searched inside the definitions of the components listed in a
/// tab: flows, scripts, forms, views, plug-in steps, classic workflows and sitemaps. Bodies are read
/// once and kept for the tab, so searching again is instant.
/// </summary>
public sealed class ContentSearchViewModel : ObservableObject
{
    private readonly EnvironmentSessionViewModel _session;
    private readonly DataverseClient _client;
    private readonly DefinitionBodyCache _cache;
    private readonly IReadOnlyList<SolutionComponentItem> _scope;
    private CancellationTokenSource? _cts;

    public ContentSearchViewModel(
        EnvironmentSessionViewModel session,
        DataverseClient client,
        DefinitionBodyCache cache,
        IReadOnlyList<SolutionComponentItem> scope,
        string? scopeLabel,
        string? term = null)
    {
        _session = session;
        _client = client;
        _cache = cache;
        _scope = scope.Where(DataverseClient.HasSearchableBody).ToList();
        ScopeLabel = scopeLabel ?? "the loaded list";
        _term = term ?? string.Empty;

        SearchCommand = new AsyncRelayCommand(_ => SearchAsync(), _ => !IsSearching && Term.Trim().Length >= 2);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsSearching);
        OpenHitCommand = new RelayCommand(p => OpenHit(p as ContentHit ?? SelectedHit), p => (p as ContentHit ?? SelectedHit) is not null);

        // How much there is to search is in the header (ScopeLine); the status bar waits for a search.
        _status = string.Empty;
    }

    public string Title => $"Search inside definitions — {_session.Title}";
    public string ScopeLabel { get; }

    /// <summary>The tab the window was opened from, for its environment line.</summary>
    public EnvironmentSessionViewModel Session => _session;

    /// <summary>"Across 412 definitions in Contoso Core" - the line under the heading.</summary>
    public string ScopeLine => $"Across {_scope.Count:N0} definition{(_scope.Count == 1 ? string.Empty : "s")} in {ScopeLabel}";

    public ObservableCollection<ContentHit> Hits { get; } = new();

    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenHitCommand { get; }

    private string _term;
    public string Term
    {
        get => _term;
        set
        {
            if (SetProperty(ref _term, value)) SearchCommand.RaiseCanExecuteChanged();
        }
    }

    private ContentHit? _selectedHit;
    public ContentHit? SelectedHit
    {
        get => _selectedHit;
        set
        {
            if (SetProperty(ref _selectedHit, value)) OpenHitCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (!SetProperty(ref _isSearching, value)) return;
            SearchCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private string _status;
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    private string _readSummary = string.Empty;
    /// <summary>"Searched 412 definitions in 1.3 s, found in 12 components - double-click a match to open it".</summary>
    public string ReadSummary
    {
        get => _readSummary;
        private set
        {
            if (SetProperty(ref _readSummary, value)) OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>The status bar's left side: what is happening, else what the last search covered.</summary>
    public string StatusLine => string.IsNullOrEmpty(Status) ? ReadSummary : Status;

    // The term the hits are for - the box may have changed since. Null until the first search.
    private string? _searchedTerm;

    /// <summary>"37 matches" once searched; empty before.</summary>
    public string MatchCountLabel => _searchedTerm is null
        ? string.Empty
        : $"{Hits.Count:N0} match{(Hits.Count == 1 ? string.Empty : "es")}";

    public bool HasHits => Hits.Count > 0;

    /// <summary>Why the list is empty and what to change - shown in its place.</summary>
    public string EmptyHeading => _scope.Count == 0 ? "Nothing to search"
        : _searchedTerm is null ? "Search the definitions"
        : "No matches";

    public string EmptyText => _scope.Count == 0
        ? $"Nothing in {ScopeLabel} has a definition to search - flows, scripts, forms, views, plug-in steps, classic workflows or sitemaps. Load another solution."
        : _searchedTerm is null
            ? "Type a column, table, variable, connector or any text of two characters or more, and press Enter."
            : $"'{_searchedTerm}' does not appear in any of the {_scope.Count:N0} definition(s) in {ScopeLabel}. Try a shorter or different term.";

    private void ShowHits()
    {
        OnPropertyChanged(nameof(HasHits));
        OnPropertyChanged(nameof(MatchCountLabel));
        OnPropertyChanged(nameof(EmptyHeading));
        OnPropertyChanged(nameof(EmptyText));
    }

    internal async Task SearchAsync()
    {
        var term = Term.Trim();
        if (term.Length < 2 || IsSearching) return;

        _cts = new CancellationTokenSource();
        IsSearching = true;
        var clock = Stopwatch.StartNew();

        try
        {
            var stale = _cache.Stale(_scope);
            if (stale.Count > 0)
            {
                Status = $"Reading {stale.Count:N0} definition(s)...";
                var progress = new Progress<int>(done => Status = $"Reading definitions... {done:N0} of {stale.Count:N0}");
                var bodies = await _client.GetDefinitionBodiesAsync(stale, progress, _cts.Token);
                _cache.Store(stale, bodies);
            }

            var hits = ContentSearch.Search(_cache.Bodies(_scope), term);

            Hits.Clear();
            foreach (var hit in hits) Hits.Add(hit);
            _searchedTerm = term;
            ShowHits();

            // The outcome goes in the summary; an empty list explains itself in its place.
            var components = hits.Select(h => h.Item.ObjectId).Distinct().Count();
            ReadSummary = Describe(_scope.Count, clock.Elapsed) + (hits.Count == 0
                ? string.Empty
                : $", found in {components:N0} component{(components == 1 ? string.Empty : "s")} - double-click a match to open it");
            Status = string.Empty;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. Definitions read so far are kept for the next search.";
        }
        catch (Exception ex)
        {
            Status = "Could not search - " + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    internal static string Describe(int definitions, TimeSpan took) =>
        $"Searched {definitions:N0} definition{(definitions == 1 ? string.Empty : "s")} in {took.TotalSeconds:0.0} s";

    private void OpenHit(ContentHit? hit)
    {
        if (hit is null) return;

        // Flows and scripts open where their text is; the rest on their usual tab.
        var tab = hit.Item.ComponentType is 61 || DetailsTabs.KindOf(hit.Item) == ObjectKind.CloudFlow
            ? DetailsTab.Source
            : DetailsTab.Default;

        _session.OpenDetails(hit.Item, tab);
    }
}
