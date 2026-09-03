using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// Everything worth knowing about one object beyond its row: which solutions carry it (the
/// layering question that bites during deployments) and what depends on it in both directions
/// (the question worth asking before deleting anything).
/// </summary>
public sealed class ObjectDetailsViewModel : ObservableObject
{
    private readonly DataverseClient _client;
    private readonly IReadOnlyDictionary<Guid, SolutionComponentItem> _known;

    public ObjectDetailsViewModel(
        DataverseClient client,
        SolutionComponentItem item,
        IReadOnlyDictionary<Guid, SolutionComponentItem> known)
    {
        _client = client;
        _known = known;
        Item = item;

        OpenLinkCommand = new RelayCommand(_ => OpenUrl(item.MakerUrl), _ => item.MakerUrl is not null);
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());
    }

    public SolutionComponentItem Item { get; }

    public RelayCommand OpenLinkCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public ObservableCollection<ContainingSolution> Solutions { get; } = new();
    public ObservableCollection<DependencyRef> Dependents { get; } = new();
    public ObservableCollection<DependencyRef> Required { get; } = new();
    public ObservableCollection<ComponentLayer> Layers { get; } = new();

    private bool _layersSupported = true;
    /// <summary>False when this object's type has no known mapping into the layers API - the
    /// section is hidden rather than shown empty, which would read as "no unmanaged layer".</summary>
    public bool LayersSupported
    {
        get => _layersSupported;
        private set => SetProperty(ref _layersSupported, value);
    }

    private bool _hasUnmanagedLayer;
    public bool HasUnmanagedLayer
    {
        get => _hasUnmanagedLayer;
        private set => SetProperty(ref _hasUnmanagedLayer, value);
    }

    public string Title => $"{Item.PrimaryLabel} - {Item.ComponentTypeName}";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Loading...";

        var problems = new List<string>();

        try
        {
            Solutions.Clear();
            Dependents.Clear();
            Required.Clear();
            Layers.Clear();

            // Each is useful on its own, so one failing must not hide the others.
            try
            {
                foreach (var solution in await _client.GetContainingSolutionsAsync(Item.ObjectId))
                {
                    Solutions.Add(solution);
                }
            }
            catch (Exception ex)
            {
                problems.Add("solutions: " + ex.Message);
            }

            await LoadDependenciesAsync(DependencyDirection.Dependent, Dependents, problems);
            await LoadDependenciesAsync(DependencyDirection.Required, Required, problems);

            try
            {
                var layers = await _client.GetComponentLayersAsync(Item.ObjectId, Item.ComponentType);
                LayersSupported = layers is not null;

                if (layers is not null)
                {
                    foreach (var layer in layers) Layers.Add(layer);
                }

                HasUnmanagedLayer = Layers.Any(l => l.IsUnmanagedLayer);
            }
            catch (Exception ex)
            {
                problems.Add("layers: " + ex.Message);
            }

            Status = problems.Count == 0
                ? $"{Solutions.Count} solution(s), {Dependents.Count} dependent, {Required.Count} required."
                : "Some details could not be read - " + string.Join("; ", problems);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadDependenciesAsync(
        DependencyDirection direction,
        ObservableCollection<DependencyRef> target,
        List<string> problems)
    {
        try
        {
            foreach (var dependency in await _client.GetDependenciesAsync(Item.ObjectId, Item.ComponentType, direction))
            {
                // Most dependencies point at something already loaded, so a name is usually free.
                if (_known.TryGetValue(dependency.ObjectId, out var match))
                {
                    dependency.ResolvedName = match.PrimaryLabel;
                }

                target.Add(dependency);
            }
        }
        catch (Exception ex)
        {
            problems.Add($"{direction.ToString().ToLowerInvariant()} components: {ex.Message}");
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
