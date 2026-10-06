using System.Collections.ObjectModel;
using System.Windows;
using PPObjectSearch.Core;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

public enum SearchSuggestionKind
{
    ContentSearch,
    Saved,
    Recent
}

/// <summary>One row of the search box's dropdown.</summary>
public sealed class SearchSuggestion : ObservableObject
{
    public required SearchSuggestionKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Detail { get; init; }

    /// <summary>The <see cref="SavedSearch"/> or <see cref="RecentObject"/> the row stands for.</summary>
    public object? Payload { get; init; }

    private bool _isHighlighted;
    /// <summary>The row Enter runs - moved with the arrow keys.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }
}

/// <summary>
/// The search box's dropdown: searching inside definitions for the words typed, the saved
/// searches, and the objects opened lately - one place for what used to be three buttons.
/// </summary>
public sealed partial class EnvironmentSessionViewModel
{
    /// <summary>How many recent objects the dropdown lists; the palette has the rest.</summary>
    internal const int DropdownRecentCount = 5;

    private readonly List<SearchSuggestion> _suggestions = new();

    /// <summary>The first row, always there: search inside definitions for the words typed.</summary>
    public SearchSuggestion ContentSuggestion { get; } = new() { Kind = SearchSuggestionKind.ContentSearch };

    /// <summary>"Search inside definitions for " and the words, or a plain "Search inside definitions…".</summary>
    public string SearchInsideLabel => string.IsNullOrWhiteSpace(SearchText)
        ? "Search inside definitions…"
        : "Search inside definitions for ";

    public string SearchInsideTerm => SearchText.Trim();

    public ObservableCollection<SearchSuggestion> SavedSuggestions { get; } = new();
    public ObservableCollection<SearchSuggestion> RecentSuggestions { get; } = new();

    private bool _searchDropdownOpen;
    /// <summary>Open while the box has focus - the view opens it only once the environment is connected.</summary>
    public bool SearchDropdownOpen
    {
        get => _searchDropdownOpen;
        set
        {
            if (!SetProperty(ref _searchDropdownOpen, value) || !value) return;

            RebuildSuggestions();
            Highlight(ContentSuggestion);
        }
    }

    private RelayCommand? _runSuggestionCommand;
    /// <summary>A click on a row; the row is the parameter.</summary>
    public RelayCommand RunSuggestionCommand => _runSuggestionCommand ??= new RelayCommand(
        p => RunSuggestion(p as SearchSuggestion), _ => IsConnected);

    private RelayCommand? _renameSavedSearchCommand;
    public RelayCommand RenameSavedSearchCommand => _renameSavedSearchCommand ??= new RelayCommand(
        p => RenameSavedSearch(p as SavedSearch), p => p is SavedSearch);

    /// <summary>A saved search's words and filters in a line, e.g. "“case” · Plug-in step".</summary>
    internal static string Summary(SavedSearch search)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(search.SearchText)) parts.Add($"“{search.SearchText.Trim()}”");
        if (!string.IsNullOrEmpty(search.Type)) parts.Add(search.Type);
        if (!string.IsNullOrEmpty(search.SubType)) parts.Add(search.SubType);
        if (!string.IsNullOrEmpty(search.State)) parts.Add(search.State);
        if (!string.IsNullOrEmpty(search.Layer)) parts.Add(search.Layer);
        if (search.FavouritesOnly) parts.Add("Favourites");
        return parts.Count == 0 ? "Everything" : string.Join(" · ", parts);
    }

    private void RebuildSuggestions()
    {
        SavedSuggestions.Clear();
        foreach (var search in SavedSearches)
        {
            SavedSuggestions.Add(new SearchSuggestion
            {
                Kind = SearchSuggestionKind.Saved, Title = search.Name, Detail = Summary(search), Payload = search
            });
        }

        RecentSuggestions.Clear();
        foreach (var recent in RecentObjects.Take(DropdownRecentCount))
        {
            RecentSuggestions.Add(new SearchSuggestion
            {
                Kind = SearchSuggestionKind.Recent, Title = recent.Label, Detail = recent.TypeName, Payload = recent
            });
        }

        var highlighted = _suggestions.FirstOrDefault(s => s.IsHighlighted);

        _suggestions.Clear();
        _suggestions.Add(ContentSuggestion);
        _suggestions.AddRange(SavedSuggestions);
        _suggestions.AddRange(RecentSuggestions);

        // A row that went away takes the highlight back to the first.
        Highlight(_suggestions.Contains(highlighted!) ? highlighted : ContentSuggestion);
    }

    private void Highlight(SearchSuggestion? row)
    {
        foreach (var s in _suggestions) s.IsHighlighted = ReferenceEquals(s, row);
    }

    /// <summary>↓ and ↑: opens the dropdown, then walks its rows.</summary>
    public void MoveSuggestion(int by)
    {
        if (!SearchDropdownOpen)
        {
            SearchDropdownOpen = true;
            return;
        }

        var index = _suggestions.FindIndex(s => s.IsHighlighted);
        Highlight(_suggestions[Math.Clamp(index + by, 0, _suggestions.Count - 1)]);
    }

    /// <summary>
    /// Enter, or a click: runs the row - the highlighted one when none is named - and closes.
    /// False when the dropdown was shut, so Enter is left to the box.
    /// </summary>
    public bool RunSuggestion(SearchSuggestion? row = null)
    {
        if (row is null && !SearchDropdownOpen) return false;
        row ??= _suggestions.FirstOrDefault(s => s.IsHighlighted) ?? ContentSuggestion;

        SearchDropdownOpen = false;

        switch (row.Kind)
        {
            case SearchSuggestionKind.Saved:
                ApplySavedSearch(row.Payload as SavedSearch);
                break;
            case SearchSuggestionKind.Recent:
                OpenRecent(row.Payload as RecentObject);
                break;
            default:
                OpenContentSearch(string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim());
                break;
        }

        return true;
    }

    /// <summary>Esc closes the dropdown; a second Esc clears the search.</summary>
    public void Escape()
    {
        if (SearchDropdownOpen) SearchDropdownOpen = false;
        else SearchText = string.Empty;
    }

    private void RenameSavedSearch(SavedSearch? search)
    {
        if (search is null) return;

        SearchDropdownOpen = false;
        var name = Views.NamePromptWindow.Ask(Application.Current?.MainWindow, "Rename saved search",
            "A new name for this search.", search.Name);
        if (string.IsNullOrWhiteSpace(name) || name == search.Name) return;

        var old = search.Name;
        Library.RenameSearch(EnvironmentUrl, old, name);
        RefreshLibrary();
        Status = $"Renamed the saved search '{old}' to '{name}'.";
    }
}
