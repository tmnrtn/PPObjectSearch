using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

public enum CompareStatus
{
    OnlyInLeft,
    OnlyInRight,
    Same
}

public sealed class CompareRow
{
    public required string Name { get; init; }
    public required string ComponentTypeName { get; init; }
    public string? SubType { get; init; }
    public required CompareStatus Status { get; init; }
    public DateTimeOffset? LeftModified { get; init; }
    public DateTimeOffset? RightModified { get; init; }

    /// <summary>Whichever side's row can supply a maker portal link.</summary>
    public SolutionComponentItem? Link { get; init; }

    public string StatusLabel => Status switch
    {
        CompareStatus.OnlyInLeft => "Only in left",
        CompareStatus.OnlyInRight => "Only in right",
        _ => "Same"
    };
}

/// <summary>
/// Diffs the objects loaded in two tabs - what is missing from one side. Works from the loaded lists, so it costs no requests.
///
/// Objects are matched on object id first, since solution deployment preserves ids, and fall back
/// to type plus name for anything created independently in each environment.
/// </summary>
public sealed class CompareViewModel : ObservableObject
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
        ExportCommand = new RelayCommand(_ => Export(), _ => Rows.Count > 0);

        _left = Sessions.FirstOrDefault();
        _right = Sessions.Skip(1).FirstOrDefault();

        if (Left is not null && Right is not null) Compare();
    }

    public ObservableCollection<EnvironmentSessionViewModel> Sessions { get; }
    public ObservableCollection<CompareRow> Rows { get; }
    public ListCollectionView RowsView { get; }

    public RelayCommand CompareCommand { get; }
    public RelayCommand ExportCommand { get; }

    private EnvironmentSessionViewModel? _left;
    public EnvironmentSessionViewModel? Left
    {
        get => _left;
        set
        {
            if (SetProperty(ref _left, value)) CompareCommand.RaiseCanExecuteChanged();
        }
    }

    private EnvironmentSessionViewModel? _right;
    public EnvironmentSessionViewModel? Right
    {
        get => _right;
        set
        {
            if (SetProperty(ref _right, value)) CompareCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _showIdentical;
    public bool ShowIdentical
    {
        get => _showIdentical;
        set
        {
            if (SetProperty(ref _showIdentical, value)) RefreshView();
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

    public string LeftHeader => Left is null ? "Left" : Left.Title;
    public string RightHeader => Right is null ? "Right" : Right.Title;

    private void Compare()
    {
        if (Left is null || Right is null) return;

        _all.Clear();

        var left = Left.AllItems;
        var right = Right.AllItems;

        var rightById = new Dictionary<Guid, SolutionComponentItem>();
        var rightByName = new Dictionary<(int, string), SolutionComponentItem>();

        foreach (var item in right)
        {
            if (item.ObjectId != Guid.Empty) rightById[item.ObjectId] = item;
            rightByName[(item.ComponentType, item.PrimaryLabel.ToLowerInvariant())] = item;
        }

        var matchedRight = new HashSet<SolutionComponentItem>();

        foreach (var item in left)
        {
            var match = FindMatch(item, rightById, rightByName);

            if (match is null)
            {
                _all.Add(Row(item, null, CompareStatus.OnlyInLeft));
                continue;
            }

            matchedRight.Add(match);

            _all.Add(Row(item, match, CompareStatus.Same));
        }

        foreach (var item in right.Where(i => !matchedRight.Contains(i)))
        {
            _all.Add(Row(null, item, CompareStatus.OnlyInRight));
        }

        _all.Sort((a, b) =>
        {
            var byStatus = a.Status.CompareTo(b.Status);
            if (byStatus != 0) return byStatus;

            var byType = string.Compare(a.ComponentTypeName, b.ComponentTypeName, StringComparison.CurrentCultureIgnoreCase);
            return byType != 0 ? byType : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        TypeFilters.Clear();
        TypeFilters.Add(new TypeFilterOption { Name = " all", Count = _all.Count, IsAll = true, AllLabel = "All types" });

        foreach (var group in _all.GroupBy(r => r.ComponentTypeName)
                                  .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            TypeFilters.Add(new TypeFilterOption { Name = group.Key, Count = group.Count() });
        }

        SelectedTypeFilter = TypeFilters[0];

        OnPropertyChanged(nameof(LeftHeader));
        OnPropertyChanged(nameof(RightHeader));
        RefreshView();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private static SolutionComponentItem? FindMatch(
        SolutionComponentItem item,
        Dictionary<Guid, SolutionComponentItem> byId,
        Dictionary<(int, string), SolutionComponentItem> byName)
    {
        if (item.ObjectId != Guid.Empty && byId.TryGetValue(item.ObjectId, out var byIdMatch)) return byIdMatch;

        return byName.TryGetValue((item.ComponentType, item.PrimaryLabel.ToLowerInvariant()), out var byNameMatch)
            ? byNameMatch
            : null;
    }

    private static CompareRow Row(SolutionComponentItem? left, SolutionComponentItem? right, CompareStatus status)
    {
        var source = left ?? right!;

        return new CompareRow
        {
            Name = source.PrimaryLabel,
            ComponentTypeName = source.ComponentTypeName,
            SubType = source.SubType,
            Status = status,
            LeftModified = left?.ModifiedOn,
            RightModified = right?.ModifiedOn,
            Link = source
        };
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
                  $"{same:N0} identical";
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

        if (!ShowIdentical && row.Status == CompareStatus.Same) return false;

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

            lines.AddRange(RowsView.Cast<CompareRow>().Select(r => string.Join(",", new[]
            {
                r.StatusLabel, r.Name, r.ComponentTypeName, r.SubType ?? string.Empty,
                r.LeftModified?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                r.RightModified?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty
            }.Select(v => v.Contains(',') || v.Contains('"') ? '"' + v.Replace("\"", "\"\"") + '"' : v))));

            System.IO.File.WriteAllLines(dialog.FileName, lines, new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
