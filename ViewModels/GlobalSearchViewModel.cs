using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>One result, tagged with the environment it came from.</summary>
public sealed class GlobalSearchRow
{
    public required string Environment { get; init; }
    public required string Solution { get; init; }
    public required SolutionComponentItem Item { get; init; }
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
        foreach (var session in sessions.Where(s => s.IsConnected))
        {
            foreach (var item in session.AllItems)
            {
                _all.Add(new GlobalSearchRow
                {
                    Environment = session.Title,
                    Solution = session.SelectedSolution?.FriendlyName ?? string.Empty,
                    Item = item
                });
            }
        }

        Rows = new ObservableCollection<GlobalSearchRow>(_all);
        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = Filter;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Apply();
        };

        OpenLinkCommand = new RelayCommand(
            p => OpenUrl((p as GlobalSearchRow)?.Item.MakerUrl),
            p => (p as GlobalSearchRow)?.Item.MakerUrl is not null);

        ExportCommand = new RelayCommand(_ => Export(), _ => RowsView.Count > 0);

        Apply();
    }

    public ObservableCollection<GlobalSearchRow> Rows { get; }
    public ListCollectionView RowsView { get; }

    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand ExportCommand { get; }

    public int EnvironmentCount => _all.Select(r => r.Environment).Distinct().Count();

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

        RowsView.Refresh();

        var shown = RowsView.Count;
        var environments = RowsView.Cast<GlobalSearchRow>().Select(r => r.Environment).Distinct().Count();

        Summary = $"{shown:N0} of {_all.Count:N0} objects across {environments} of {EnvironmentCount} environment(s)";
        ExportCommand.RaiseCanExecuteChanged();
    }

    private bool Filter(object obj)
    {
        if (obj is not GlobalSearchRow row) return false;

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
            CsvExporter.Write(dialog.FileName, RowsView.Cast<GlobalSearchRow>().Select(r => r.Item));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
