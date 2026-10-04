using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One result, tagged with the environment it came from.</summary>
public sealed class GlobalSearchRow : ObservableObject
{
    public required EnvironmentSessionViewModel Source { get; init; }
    public required string Environment { get; init; }
    public required string Solution { get; init; }
    public required SolutionComponentItem Item { get; init; }

    /// <summary>The tab's position, so a name group lists its environments in sidebar order.</summary>
    public int SessionOrder { get; init; }

    public EnvironmentSku EnvironmentSku => Source.EnvironmentSku;

    private bool _isGroupEnd;
    /// <summary>The last row of a run with the same name, while the list is sorted by name - the
    /// grid draws a stronger line under it so each object's environments read as one group.</summary>
    public bool IsGroupEnd
    {
        get => _isGroupEnd;
        set => SetProperty(ref _isGroupEnd, value);
    }
}

/// <summary>One "In" pill: every environment, or a single one.</summary>
public sealed class SearchScope : ObservableObject
{
    public EnvironmentSessionViewModel? Session { get; init; }
    public required string Label { get; init; }
    public bool IsAll => Session is null;
    public bool IsConnected { get; init; } = true;
    public EnvironmentSku EnvironmentSku => Session?.EnvironmentSku ?? EnvironmentSku.Unknown;

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(CountLabel));
        }
    }

    /// <summary>A disconnected environment has nothing to count.</summary>
    public string CountLabel => IsConnected ? Count.ToString("N0") : "—";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// One keyword across every connected tab at once - "which environments have this thing?".
/// Runs entirely against the already-loaded lists, so it costs no requests.
/// </summary>
public sealed class GlobalSearchViewModel : ObservableObject
{
    private readonly List<GlobalSearchRow> _all = new();
    private readonly DispatcherTimer _debounce;
    private string[] _terms = Array.Empty<string>();

    public GlobalSearchViewModel(IEnumerable<EnvironmentSessionViewModel> sessions)
    {
        var sessionList = sessions.ToList();

        _selectedScope = new SearchScope { Label = "All", IsSelected = true };
        Scopes.Add(_selectedScope);

        foreach (var (session, order) in sessionList.Select((s, i) => (s, i)))
        {
            Scopes.Add(new SearchScope { Session = session, Label = session.Title, IsConnected = session.IsConnected });
            if (!session.IsConnected) continue;

            foreach (var item in session.AllItems)
            {
                _all.Add(new GlobalSearchRow
                {
                    Source = session,
                    Environment = session.Title,
                    Solution = session.SelectedSolution?.FriendlyName ?? string.Empty,
                    Item = item,
                    SessionOrder = order
                });
            }
        }

        var skipped = sessionList.Where(s => !s.IsConnected).Select(s => s.Title).ToList();
        SkippedMessage = skipped.Count switch
        {
            0 => string.Empty,
            1 => $"{skipped[0]} is not connected and was skipped.",
            _ => $"{string.Join(", ", skipped)} are not connected and were skipped."
        };

        Rows = new ObservableCollection<GlobalSearchRow>(_all);
        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = Filter;

        // Name first, so one object's copies in each environment sit together.
        RowsView.SortDescriptions.Add(new SortDescription("Item.PrimaryLabel", ListSortDirection.Ascending));
        RowsView.SortDescriptions.Add(new SortDescription(nameof(GlobalSearchRow.SessionOrder), ListSortDirection.Ascending));
        ((System.Collections.Specialized.INotifyCollectionChanged)RowsView.SortDescriptions).CollectionChanged += (_, _) => MarkGroupEnds();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Apply();
        };

        OpenLinkCommand = new RelayCommand(
            // Each row opens in its own environment's browser profile.
            p => (p as GlobalSearchRow)?.Source.OpenUrl((p as GlobalSearchRow)?.Item.MakerUrl),
            p => (p as GlobalSearchRow)?.Item.MakerUrl is not null);

        ExportCommand = new RelayCommand(_ => Export(), _ => RowsView.Count > 0);

        SelectScopeCommand = new RelayCommand(p =>
        {
            if (p is SearchScope { IsConnected: true } scope) SelectedScope = scope;
        });

        Apply();
    }

    public ObservableCollection<GlobalSearchRow> Rows { get; }
    public ListCollectionView RowsView { get; }

    /// <summary>"All" followed by one entry per tab, connected or not.</summary>
    public ObservableCollection<SearchScope> Scopes { get; } = new();

    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand SelectScopeCommand { get; }

    public int EnvironmentCount => _all.Select(r => r.Source).Distinct().Count();

    /// <summary>Which tabs were left out because they are not connected.</summary>
    public string SkippedMessage { get; }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private SearchScope _selectedScope;
    /// <summary>Narrows the results to one environment; the text match on environment names still works too.</summary>
    public SearchScope SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (!SetProperty(ref _selectedScope, value)) return;
            foreach (var scope in Scopes) scope.IsSelected = ReferenceEquals(scope, value);
            Apply();
        }
    }

    private string _summary = string.Empty;
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    private void Apply()
    {
        _terms = SearchText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToArray();

        // The pill counts ignore the scope itself, so each one says what picking it would show.
        var matching = _all.Where(MatchesTerms).ToList();
        Scopes[0].Count = matching.Count;
        foreach (var scope in Scopes.Where(s => !s.IsAll))
        {
            scope.Count = matching.Count(r => ReferenceEquals(r.Source, scope.Session));
        }

        RowsView.Refresh();
        MarkGroupEnds();

        var shown = RowsView.Count;
        var environments = RowsView.Cast<GlobalSearchRow>().Select(r => r.Source).Distinct().Count();

        Summary = string.IsNullOrWhiteSpace(SearchText) && SelectedScope.IsAll
            ? $"{shown:N0} objects across {EnvironmentCount} environment(s)"
            : $"{shown:N0} matches across {environments} of {EnvironmentCount} environment(s)";

        ExportCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Only meaningful while the list is sorted by name; any other order clears it.</summary>
    private void MarkGroupEnds()
    {
        var byName = RowsView.SortDescriptions.Count > 0 &&
                     RowsView.SortDescriptions[0].PropertyName == "Item.PrimaryLabel";

        GlobalSearchRow? previous = null;
        foreach (GlobalSearchRow row in RowsView)
        {
            if (previous is not null)
            {
                previous.IsGroupEnd = byName && !string.Equals(
                    previous.Item.PrimaryLabel, row.Item.PrimaryLabel, StringComparison.CurrentCultureIgnoreCase);
            }

            previous = row;
        }

        if (previous is not null) previous.IsGroupEnd = false;
    }

    private bool Filter(object obj) =>
        obj is GlobalSearchRow row &&
        (SelectedScope.IsAll || ReferenceEquals(row.Source, SelectedScope.Session)) &&
        MatchesTerms(row);

    private bool MatchesTerms(GlobalSearchRow row)
    {
        foreach (var term in _terms)
        {
            if (!row.Item.SearchIndex.Contains(term, StringComparison.Ordinal) &&
                !row.Environment.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = "object-search-all-environments.csv"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            // The same object in three environments is three rows that only these columns tell apart.
            var rows = RowsView.Cast<GlobalSearchRow>().ToList();
            CsvExporter.Write(
                dialog.FileName,
                rows.Select(r => (r.Item, (IReadOnlyList<string?>)new[] { r.Environment, r.Source.EnvironmentUrl, r.Solution })),
                new[] { "Environment", "Environment URL", "Solution" });

            Summary = $"Exported {rows.Count:N0} row(s) to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
