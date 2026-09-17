using System.Collections.ObjectModel;
using PPObjectSearch.Core;
using PPObjectSearch.Dataverse;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// The same component as it stands in two environments. Dataverse has no "give me this
/// component's definition" call, so the definition is taken from the top of each environment's
/// solution layer stack - that layer is by definition the one in effect there. The stacks
/// themselves are kept alongside, because they are usually what explains the difference.
/// </summary>
public sealed class EnvironmentDiffViewModel : DefinitionDiffViewModel
{
    private readonly DataverseClient _leftClient;
    private readonly DataverseClient _rightClient;
    private readonly SolutionComponentItem _leftItem;
    private readonly SolutionComponentItem _rightItem;

    public EnvironmentDiffViewModel(
        string componentLabel,
        string componentTypeName,
        string leftEnvironment,
        string rightEnvironment,
        DataverseClient leftClient,
        DataverseClient rightClient,
        SolutionComponentItem leftItem,
        SolutionComponentItem rightItem)
        : base(leftEnvironment, rightEnvironment)
    {
        ComponentLabel = componentLabel;
        ComponentTypeName = componentTypeName;
        LeftEnvironment = leftEnvironment;
        RightEnvironment = rightEnvironment;

        _leftClient = leftClient;
        _rightClient = rightClient;
        _leftItem = leftItem;
        _rightItem = rightItem;

        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());
    }

    public string ComponentLabel { get; }
    public string ComponentTypeName { get; }
    public string LeftEnvironment { get; }
    public string RightEnvironment { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public ObservableCollection<ComponentLayer> LeftLayers { get; } = new();
    public ObservableCollection<ComponentLayer> RightLayers { get; } = new();

    public string Title => $"{ComponentLabel} - {LeftEnvironment} vs {RightEnvironment}";

    /// <summary>Spelled out because the two sides can carry different object ids, where the
    /// component was created separately in each environment rather than deployed.</summary>
    public string IdentityLabel => _leftItem.ObjectId == _rightItem.ObjectId
        ? $"Matched on object id {_leftItem.ObjectId}"
        : $"Matched by name - ids differ ({_leftItem.ObjectId} / {_rightItem.ObjectId})";

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
        Status = "Loading both environments...";

        try
        {
            LeftLayers.Clear();
            RightLayers.Clear();
            ReplaceChanges(null);

            var problems = new List<string>();

            // One call per environment, run together - neither side depends on the other.
            var left = LoadLayersAsync(_leftClient, _leftItem, LeftEnvironment, problems);
            var right = LoadLayersAsync(_rightClient, _rightItem, RightEnvironment, problems);

            await Task.WhenAll(left, right);

            var leftLayers = await left;
            var rightLayers = await right;

            foreach (var layer in leftLayers ?? Enumerable.Empty<ComponentLayer>()) LeftLayers.Add(layer);
            foreach (var layer in rightLayers ?? Enumerable.Empty<ComponentLayer>()) RightLayers.Add(layer);

            // The layer lists arrive top of stack first, so the effective definition is the head.
            var leftJson = LeftLayers.FirstOrDefault()?.ComponentJson;
            var rightJson = RightLayers.FirstOrDefault()?.ComponentJson;

            ReplaceChanges(DefinitionDiff.Compare(leftJson, rightJson));

            Status = BuildStatus(leftLayers, rightLayers, leftJson, rightJson, problems);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task<IReadOnlyList<ComponentLayer>?> LoadLayersAsync(
        DataverseClient client,
        SolutionComponentItem item,
        string environment,
        List<string> problems)
    {
        try
        {
            return await client.GetComponentLayersAsync(item.ObjectId, item.ComponentType);
        }
        catch (Exception ex)
        {
            lock (problems) problems.Add($"{environment}: {ex.Message}");
            return null;
        }
    }

    private string BuildStatus(
        IReadOnlyList<ComponentLayer>? leftLayers,
        IReadOnlyList<ComponentLayer>? rightLayers,
        string? leftJson,
        string? rightJson,
        List<string> problems)
    {
        if (problems.Count > 0) return "Could not read layers - " + string.Join("; ", problems);

        // Null layers means the type has no mapping into the layers API at all, which is not the
        // same as a component that simply has no definition recorded.
        if (leftLayers is null || rightLayers is null)
        {
            return $"{ComponentTypeName} is not a type Dataverse exposes solution layers for, so its definition cannot be compared.";
        }

        if (leftJson is null && rightJson is null)
        {
            return "Neither environment returned a definition for this component.";
        }

        if (leftJson is null) return $"{LeftEnvironment} returned no definition, so there is nothing to compare against.";
        if (rightJson is null) return $"{RightEnvironment} returned no definition, so there is nothing to compare against.";

        if (Changes.Count == 0)
        {
            return $"The definitions match. {LeftEnvironment}: {LeftLayers.Count} layer(s), {RightEnvironment}: {RightLayers.Count} layer(s).";
        }

        var properties = Changes.Count == 1 ? "1 property differs" : $"{Changes.Count} properties differ";
        return $"{properties}. {LeftEnvironment}: {LeftLayers.Count} layer(s), {RightEnvironment}: {RightLayers.Count} layer(s).";
    }
}
