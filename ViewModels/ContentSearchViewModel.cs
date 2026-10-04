using System.Collections.ObjectModel;
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

        _status = $"{_scope.Count:N0} component(s) in {ScopeLabel} have definitions to search.";
    }

    public string Title => $"Search inside definitions — {_session.Title}";
    public string ScopeLabel { get; }

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
        private set => SetProperty(ref _status, value);
    }

    internal async Task SearchAsync()
    {
        var term = Term.Trim();
        if (term.Length < 2 || IsSearching) return;

        _cts = new CancellationTokenSource();
        IsSearching = true;

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

            var components = hits.Select(h => h.Item.ObjectId).Distinct().Count();
            Status = hits.Count == 0
                ? $"'{term}' does not appear in any of the {_scope.Count:N0} definition(s) in {ScopeLabel}."
                : $"'{term}' appears {hits.Count:N0} time(s) in {components:N0} component(s). Double-click a hit to open it.";
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
