using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// Everything worth knowing about one object beyond its row: which solutions carry it (the
/// layering question that bites during deployments), what depends on it in both directions
/// (the question worth asking before deleting anything), and - for a table - what it owns,
/// down to the properties of each column, relationship, key, form, view, chart and dashboard.
/// </summary>
public sealed class ObjectDetailsViewModel : ObservableObject
{
    /// <summary>solutioncomponent type code for a table.</summary>
    private const int TableComponentType = 1;

    private readonly DataverseClient _client;
    private readonly IReadOnlyDictionary<Guid, SolutionComponentItem> _known;

    /// <summary>
    /// Bumped on every child selection. Arrowing down a long column list starts a fetch per row,
    /// and only the last one asked for may write its results.
    /// </summary>
    private int _propertyRequest;

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
        ViewLayerChangesCommand = new RelayCommand(
            p => ShowLayerChanges(p as ComponentLayer ?? SelectedLayer),
            p => (p as ComponentLayer ?? SelectedLayer) is not null);
    }

    public SolutionComponentItem Item { get; }

    public RelayCommand OpenLinkCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ViewLayerChangesCommand { get; }

    public ObservableCollection<ContainingSolution> Solutions { get; } = new();
    public ObservableCollection<DependencyRef> Dependents { get; } = new();
    public ObservableCollection<DependencyRef> Required { get; } = new();
    public ObservableCollection<ComponentLayer> Layers { get; } = new();

    /// <summary>What the table owns, one group per kind. Empty for anything that is not a table.</summary>
    public ObservableCollection<TableChildGroup> ChildGroups { get; } = new();

    /// <summary>The selected group's children, after the filter box.</summary>
    public ObservableCollection<TableChild> Children { get; } = new();

    /// <summary>Every property of the selected child, fetched the first time it is selected.</summary>
    public ObservableCollection<ComponentProperty> ChildProperties { get; } = new();

    /// <summary>Only a table has child components; every other type hides the whole section.</summary>
    public bool IsTable => Item.ComponentType == TableComponentType;

    private ComponentLayer? _selectedLayer;
    public ComponentLayer? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (SetProperty(ref _selectedLayer, value)) ViewLayerChangesCommand.RaiseCanExecuteChanged();
        }
    }

    private TableChildGroup? _selectedChildGroup;
    public TableChildGroup? SelectedChildGroup
    {
        get => _selectedChildGroup;
        set
        {
            if (SetProperty(ref _selectedChildGroup, value)) ApplyChildFilter();
        }
    }

    private string _childFilter = string.Empty;
    /// <summary>Narrows the selected group - a table with 400 columns needs it.</summary>
    public string ChildFilter
    {
        get => _childFilter;
        set
        {
            if (SetProperty(ref _childFilter, value)) ApplyChildFilter();
        }
    }

    private TableChild? _selectedChild;
    public TableChild? SelectedChild
    {
        get => _selectedChild;
        set
        {
            if (SetProperty(ref _selectedChild, value)) _ = ShowChildPropertiesAsync(value);
        }
    }

    private bool _isLoadingProperties;
    public bool IsLoadingProperties
    {
        get => _isLoadingProperties;
        private set => SetProperty(ref _isLoadingProperties, value);
    }

    private string _childStatus = "Select a component to see its properties.";
    /// <summary>Stands in for the property grid whenever there is nothing in it.</summary>
    public string ChildStatus
    {
        get => _childStatus;
        private set => SetProperty(ref _childStatus, value);
    }

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
            ClearChildComponents();

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

            if (IsTable) await LoadChildComponentsAsync(problems);

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

            var summary = $"{Solutions.Count} solution(s), {Dependents.Count} dependent, {Required.Count} required";
            if (IsTable) summary += $", {ChildGroups.Sum(g => g.Count)} child component(s)";

            Status = problems.Count == 0
                ? summary + "."
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

    /// <summary>
    /// Reads everything the table owns. The six reads are independent, so they run together and
    /// each is allowed to fail on its own - a tenant that blocks charts should not cost the user
    /// their columns.
    /// </summary>
    private async Task LoadChildComponentsAsync(List<string> problems)
    {
        TableIdentity? table;

        try
        {
            table = await _client.GetTableIdentityAsync(Item.ObjectId, Item.Name);
        }
        catch (Exception ex)
        {
            problems.Add("table metadata: " + ex.Message);
            return;
        }

        if (table is null)
        {
            problems.Add("table metadata: this component could not be matched to a table.");
            return;
        }

        var columns = _client.GetTableColumnsAsync(table);
        var relationships = _client.GetTableRelationshipsAsync(table);
        var keys = _client.GetTableKeysAsync(table);
        var forms = _client.GetTableFormsAsync(table);
        var views = _client.GetTableViewsAsync(table);
        var charts = _client.GetTableChartsAsync(table);

        var all = new List<TableChild>();
        all.AddRange(await GatherAsync(columns, "columns", problems));
        all.AddRange(await GatherAsync(relationships, "relationships", problems));
        all.AddRange(await GatherAsync(keys, "keys", problems));
        all.AddRange(await GatherAsync(forms, "forms", problems));
        all.AddRange(await GatherAsync(views, "views", problems));
        all.AddRange(await GatherAsync(charts, "charts", problems));

        // Every kind is listed even when it came back empty: "Views 0" is an answer, whereas a
        // missing section only raises the question of whether it was looked for.
        foreach (var kind in Enum.GetValues<TableChildKind>())
        {
            ChildGroups.Add(new TableChildGroup
            {
                Kind = kind,
                Label = GroupLabel(kind),
                Children = all
                    .Where(c => c.Kind == kind)
                    .OrderBy(c => c.PrimaryLabel, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            });
        }

        SelectedChildGroup = ChildGroups.FirstOrDefault(g => !g.IsEmpty) ?? ChildGroups.FirstOrDefault();
    }

    private static async Task<IReadOnlyList<TableChild>> GatherAsync(
        Task<IReadOnlyList<TableChild>> read,
        string what,
        List<string> problems)
    {
        try
        {
            return await read;
        }
        catch (Exception ex)
        {
            problems.Add($"{what}: {ex.Message}");
            return Array.Empty<TableChild>();
        }
    }

    private static string GroupLabel(TableChildKind kind) => kind switch
    {
        TableChildKind.Column => "Columns",
        TableChildKind.Relationship => "Relationships",
        TableChildKind.Key => "Keys",
        TableChildKind.Form => "Forms",
        TableChildKind.View => "Views",
        TableChildKind.Chart => "Charts",
        _ => "Dashboards"
    };

    private void ClearChildComponents()
    {
        // Nothing may still be in flight against the old list once it is gone.
        _propertyRequest++;

        ChildGroups.Clear();
        Children.Clear();
        ChildProperties.Clear();

        _selectedChildGroup = null;
        OnPropertyChanged(nameof(SelectedChildGroup));

        _selectedChild = null;
        OnPropertyChanged(nameof(SelectedChild));

        IsLoadingProperties = false;
        ChildStatus = "Select a component to see its properties.";
    }

    private void ApplyChildFilter()
    {
        // Clearing the list makes the grid report a null selection, so the one to restore has to
        // be remembered before that happens.
        var previous = SelectedChild;

        Children.Clear();

        var children = SelectedChildGroup?.Children ?? Array.Empty<TableChild>();
        var terms = ChildFilter.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var child in children)
        {
            if (terms.All(t => child.FilterIndex.Contains(t, StringComparison.Ordinal))) Children.Add(child);
        }

        // Typing in the filter box should not throw away what is already on screen, but a
        // selection the filter has just excluded - or one from another group - has to go.
        SelectedChild = previous is not null && Children.Contains(previous)
            ? previous
            : Children.Count == 1 ? Children[0] : null;
    }

    /// <summary>
    /// Fills the property grid for one child, from its cache where it has one. A form or view
    /// record carries its whole XML definition, which is why this is not read up front.
    /// </summary>
    private async Task ShowChildPropertiesAsync(TableChild? child)
    {
        var request = ++_propertyRequest;

        ChildProperties.Clear();

        if (child is null)
        {
            IsLoadingProperties = false;
            ChildStatus = "Select a component to see its properties.";
            return;
        }

        if (child.Properties is null)
        {
            IsLoadingProperties = true;
            ChildStatus = "Loading properties...";

            try
            {
                child.Properties = await _client.GetChildPropertiesAsync(child);
            }
            catch (Exception ex)
            {
                if (request != _propertyRequest) return;

                IsLoadingProperties = false;
                ChildStatus = "Properties could not be read - " + ex.Message;
                return;
            }

            // The user moved on while this was in flight; their current selection owns the grid.
            if (request != _propertyRequest) return;

            IsLoadingProperties = false;
        }

        foreach (var property in child.Properties) ChildProperties.Add(property);

        ChildStatus = ChildProperties.Count == 0
            ? "Dataverse returned no properties for this component."
            : string.Empty;
    }

    /// <summary>
    /// Opens the diff for one layer. <see cref="Layers"/> runs top of the stack first, so the
    /// layer a given one sits on top of - the "before" side of the diff - is the next row down.
    /// </summary>
    private void ShowLayerChanges(ComponentLayer? layer)
    {
        if (layer is null) return;

        var index = Layers.IndexOf(layer);
        var below = index >= 0 && index + 1 < Layers.Count ? Layers[index + 1] : null;

        var window = new Views.LayerChangesWindow
        {
            DataContext = new LayerChangesViewModel(Item.PrimaryLabel, layer, below),
            Owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => ReferenceEquals(w.DataContext, this)) ?? Application.Current.MainWindow
        };

        window.Show();
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
