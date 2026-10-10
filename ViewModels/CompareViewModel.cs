using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// Diffs the objects loaded in two tabs - what is missing from one side. Works from the loaded lists, so it costs no requests.
///
/// Objects are matched on object id first, since solution deployment preserves ids, and fall back
/// to type plus name for anything created independently in each environment.
/// </summary>
public sealed class CompareViewModel : ObservableObject, IDisposable
{
    private readonly List<CompareRow> _all = new();

    public CompareViewModel(IEnumerable<EnvironmentSessionViewModel> sessions)
    {
        Sessions = new ObservableCollection<EnvironmentSessionViewModel>(sessions.Where(s => s.IsConnected));

        Rows = new ObservableCollection<CompareRow>();
        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = Filter;

        TypeFiltersView = CollectionViewSource.GetDefaultView(TypeFilters);
        TypeFiltersView.Filter = FilterTypeOption;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RefreshView();
        };

        CompareCommand = new RelayCommand(_ => Compare(), _ => Left is not null && Right is not null && Left != Right);
        SwapCommand = new RelayCommand(_ => Swap(), _ => Left is not null || Right is not null);

        // Keeps "Compared 2 min ago" honest while the window sits open.
        _agoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _agoTimer.Tick += (_, _) => OnPropertyChanged(nameof(ComparedAgo));
        _agoTimer.Start();
        ExportCommand = new RelayCommand(_ => Export(), _ => Rows.Count > 0);
        CompareDefinitionCommand = new RelayCommand(
            p => CompareDefinition(p as CompareRow ?? SelectedRow),
            p => (p as CompareRow ?? SelectedRow) is { ExistsOnBothSides: true });

        _left = Sessions.FirstOrDefault();
        _right = Sessions.Skip(1).FirstOrDefault();

        if (Left is not null && Right is not null) Compare();
    }

    public ObservableCollection<EnvironmentSessionViewModel> Sessions { get; }
    public ObservableCollection<CompareRow> Rows { get; }
    public ListCollectionView RowsView { get; }

    public RelayCommand CompareCommand { get; }
    public RelayCommand SwapCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CompareDefinitionCommand { get; }

    private readonly DispatcherTimer _agoTimer;
    private DateTimeOffset? _comparedAt;

    /// <summary>"Compared 2 min ago" - empty until the first comparison.</summary>
    public string ComparedAgo
    {
        get
        {
            if (_comparedAt is not { } at) return string.Empty;

            var elapsed = DateTimeOffset.Now - at;
            return elapsed switch
            {
                { TotalMinutes: < 1 } => "Compared just now",
                { TotalHours: < 1 } => $"Compared {(int)elapsed.TotalMinutes} min ago",
                _ => $"Compared at {at:HH:mm}"
            };
        }
    }

    /// <summary>How rows were paired, and from which solutions - the status bar's left side.</summary>
    public string MatchDescription
    {
        get
        {
            var rule = "Matched on object id, falling back to type and name";
            var left = Left?.SelectedSolution?.FriendlyName;
            var right = Right?.SelectedSolution?.FriendlyName;

            if (left is null || right is null) return rule;

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                ? $"{rule} · {left} on both sides"
                : $"{rule} · {left} vs {right}";
        }
    }

    private CompareStatusFilter _statusFilter;
    /// <summary>Replaces the old "Show items in both" check box.</summary>
    public CompareStatusFilter StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetProperty(ref _statusFilter, value)) RefreshView();
        }
    }

    // Counts for the segmented filter, after the type and search filters but before the status one.
    private int _countAll, _countOnlyLeft, _countOnlyRight, _countBoth;
    public int CountAll { get => _countAll; private set => SetProperty(ref _countAll, value); }
    public int CountOnlyLeft { get => _countOnlyLeft; private set => SetProperty(ref _countOnlyLeft, value); }
    public int CountOnlyRight { get => _countOnlyRight; private set => SetProperty(ref _countOnlyRight, value); }
    public int CountBoth { get => _countBoth; private set => SetProperty(ref _countBoth, value); }

    private string _resultSummary = string.Empty;
    /// <summary>"47 differences · 2,904 in both".</summary>
    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    /// <summary>Left becomes right and the comparison runs again, so the status labels follow.</summary>
    private void Swap()
    {
        (_left, _right) = (_right, _left);
        OnPropertyChanged(nameof(Left));
        OnPropertyChanged(nameof(Right));
        CompareCommand.RaiseCanExecuteChanged();

        if (CompareCommand.CanExecute(null)) Compare();
    }

    private CompareRow? _selectedRow;
    public CompareRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value)) CompareDefinitionCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Opens the same component's definition in both environments side by side. Unlike
    /// <see cref="Compare"/>, which works entirely from the loaded lists, this does cost two
    /// requests - one layer lookup per environment - so it is per-row and on demand.
    /// </summary>
    private void CompareDefinition(CompareRow? row)
    {
        if (row is not { Left: { } leftItem, Right: { } rightItem }) return;
        if (Left?.Client is not { } leftClient || Right?.Client is not { } rightClient) return;

        var viewModel = new EnvironmentDiffViewModel(
            row.Name,
            row.ComponentTypeName,
            new EnvironmentDiffSide(LeftHeader, leftClient, leftItem, Left.EnvironmentSku),
            new EnvironmentDiffSide(RightHeader, rightClient, rightItem, Right.EnvironmentSku));

        var window = new Views.EnvironmentDiffWindow
        {
            DataContext = viewModel,
            Owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => ReferenceEquals(w.DataContext, this)) ?? Application.Current.MainWindow
        };

        window.Show();
        _ = viewModel.LoadAsync();
    }

    private EnvironmentSessionViewModel? _left;
    public EnvironmentSessionViewModel? Left
    {
        get => _left;
        set
        {
            if (!SetProperty(ref _left, value)) return;
            CompareCommand.RaiseCanExecuteChanged();
            SwapCommand.RaiseCanExecuteChanged();
        }
    }

    private EnvironmentSessionViewModel? _right;
    public EnvironmentSessionViewModel? Right
    {
        get => _right;
        set
        {
            if (!SetProperty(ref _right, value)) return;
            CompareCommand.RaiseCanExecuteChanged();
            SwapCommand.RaiseCanExecuteChanged();
        }
    }

    private string _summary = "Pick two environments to compare.";
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ObservableCollection<TypeFilterOption> TypeFilters { get; } = new();

    private TypeFilterOption? _selectedTypeFilter;
    public TypeFilterOption? SelectedTypeFilter
    {
        get => _selectedTypeFilter;
        set
        {
            if (SetProperty(ref _selectedTypeFilter, value)) RefreshView();
        }
    }

    private string _typeFilterSearchText = string.Empty;
    public string TypeFilterSearchText
    {
        get => _typeFilterSearchText;
        set
        {
            if (SetProperty(ref _typeFilterSearchText, value))
            {
                TypeFiltersView?.Refresh();
                
                if (SelectedTypeFilter != null && value != SelectedTypeFilter.Label)
                {
                    SelectedTypeFilter = TypeFilters.FirstOrDefault(t => t.IsAll);
                }
            }
        }
    }

    public ICollectionView TypeFiltersView { get; private set; }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchDebounce.Stop();
                _searchDebounce.Start();
            }
        }
    }

    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce;

    /// <summary>
    /// A running DispatcherTimer is held by the dispatcher, and through its handler holds this view
    /// model - every compare row and both environments' item lists. Stopping it when the window
    /// closes is what lets all of that be collected.
    /// </summary>
    public void Dispose()
    {
        _agoTimer.Stop();
        _searchDebounce.Stop();
    }

    public string LeftHeader => Left is null ? "Left" : Left.Title;
    public string RightHeader => Right is null ? "Right" : Right.Title;

    /// <summary>The sides as they were when the rows were built - the column headers follow
    /// these rather than the pickers, which may have moved on since.</summary>
    public EnvironmentSessionViewModel? ComparedLeft { get; private set; }
    public EnvironmentSessionViewModel? ComparedRight { get; private set; }

    private void Compare()
    {
        if (Left is null || Right is null) return;

        _all.Clear();
        ComparedLeft = Left;
        ComparedRight = Right;

        var left = Left.AllItems;
        var right = Right.AllItems;

        // Matched on id, then type and name; sorted by status, type and name.
        _all.AddRange(ComponentDiff.Diff(left, right));

        TypeFilters.Clear();
        TypeFilters.Add(new TypeFilterOption { Name = " all", Count = _all.Count, IsAll = true, AllLabel = "All types" });

        foreach (var group in _all.GroupBy(r => r.ComponentTypeName)
                                  .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            TypeFilters.Add(new TypeFilterOption { Name = group.Key, Count = group.Count() });
        }

        SelectedTypeFilter = TypeFilters[0];

        _comparedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(ComparedAgo));
        OnPropertyChanged(nameof(MatchDescription));
        OnPropertyChanged(nameof(LeftHeader));
        OnPropertyChanged(nameof(RightHeader));
        OnPropertyChanged(nameof(ComparedLeft));
        OnPropertyChanged(nameof(ComparedRight));
        RefreshView();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private void RefreshView()
    {
        Rows.Clear();
        foreach (var row in _all) Rows.Add(row);

        RowsView.Refresh();

        var onlyLeft = _all.Count(r => r.Status == CompareStatus.OnlyInLeft);
        var onlyRight = _all.Count(r => r.Status == CompareStatus.OnlyInRight);
        var same = _all.Count(r => r.Status == CompareStatus.Same);

        Summary = $"{onlyLeft:N0} only in {LeftHeader}  |  {onlyRight:N0} only in {RightHeader}  |  " +
                  $"{same:N0} in both";
        ResultSummary = $"{onlyLeft + onlyRight:N0} differences · {same:N0} in both";

        var narrowed = _all.Where(MatchesTypeAndSearch).ToList();
        CountAll = narrowed.Count;
        CountOnlyLeft = narrowed.Count(r => r.Status == CompareStatus.OnlyInLeft);
        CountOnlyRight = narrowed.Count(r => r.Status == CompareStatus.OnlyInRight);
        CountBoth = narrowed.Count(r => r.Status == CompareStatus.Same);
    }

    private bool FilterTypeOption(object obj)
    {
        if (string.IsNullOrWhiteSpace(_typeFilterSearchText)) return true;

        if (SelectedTypeFilter != null && 
            string.Equals(_typeFilterSearchText, SelectedTypeFilter.Label, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        if (obj is not TypeFilterOption option) return false;
        
        if (option.IsAll) return true;

        return option.Label.Contains(_typeFilterSearchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool Filter(object obj)
    {
        if (obj is not CompareRow row) return false;

        var statusMatches = StatusFilter switch
        {
            CompareStatusFilter.OnlyLeft => row.Status == CompareStatus.OnlyInLeft,
            CompareStatusFilter.OnlyRight => row.Status == CompareStatus.OnlyInRight,
            CompareStatusFilter.Both => row.Status == CompareStatus.Same,
            _ => true
        };

        return statusMatches && MatchesTypeAndSearch(row);
    }

    private bool MatchesTypeAndSearch(CompareRow row)
    {
        if (SelectedTypeFilter is { IsAll: false } type &&
            !string.Equals(row.ComponentTypeName, type.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"compare-{LeftHeader}-{RightHeader}.csv".Replace(' ', '-')
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = new List<string> { "Status,Name,Object type,Sub type,Left modified,Right modified" };

            lines.AddRange(RowsView.Cast<CompareRow>().Select(r => CsvExporter.Line(
                r.StatusLabel, r.Name, r.ComponentTypeName, r.SubType,
                r.LeftModified?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                r.RightModified?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))));

            CsvExporter.WriteLines(dialog.FileName, lines);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
