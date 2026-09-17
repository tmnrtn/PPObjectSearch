using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// What one solution layer actually did to a component: the properties it set, and for each one
/// the value it replaced. Dataverse names the touched properties in msdyn_changes but does not
/// say what they were before, so the "before" side comes from diffing this layer's
/// msdyn_componentjson against the layer immediately beneath it in the stack.
/// </summary>
public sealed class LayerChangesViewModel : DefinitionDiffViewModel
{
    public LayerChangesViewModel(string componentLabel, ComponentLayer layer, ComponentLayer? below)
        : base(below?.SolutionName ?? "Nothing beneath", layer.SolutionName)
    {
        ComponentLabel = componentLabel;
        Layer = layer;
        Below = below;

        Definition = TextDiff.Prettify(layer.ComponentJson);

        ReplaceChanges(BuildChanges(layer, below));
        Summary = BuildSummary();
    }

    public string ComponentLabel { get; }
    public ComponentLayer Layer { get; }

    /// <summary>The layer this one sits on top of, or null when this is the bottom of the stack.</summary>
    public ComponentLayer? Below { get; }

    /// <summary>This layer's whole component definition, indented so it can be read.</summary>
    public string Definition { get; }

    public string Summary { get; }

    public string Title => $"{ComponentLabel} - changes in '{Layer.SolutionName}'";

    public string BelowLabel => Below is null
        ? "This is the bottom layer, so everything in it is new."
        : $"Compared against the layer beneath it: '{Below.SolutionName}'.";

    public bool HasDefinition => !string.IsNullOrWhiteSpace(Definition);

    private string BuildSummary()
    {
        if (Changes.Count == 0)
        {
            return HasDefinition
                ? "Dataverse reported no property-level differences for this layer."
                : "Dataverse returned no definition for this layer, so its changes cannot be shown.";
        }

        return Changes.Count == 1 ? "1 property changed by this layer." : $"{Changes.Count} properties changed by this layer.";
    }

    /// <summary>
    /// Where no definition can be read - some component types return none - the properties
    /// Dataverse named in msdyn_changes are still listed, just without values, which is more
    /// useful than an empty window.
    /// </summary>
    private static IReadOnlyList<DefinitionChange> BuildChanges(ComponentLayer layer, ComponentLayer? below)
        => DefinitionDiff.Compare(below?.ComponentJson, layer.ComponentJson, layer.ChangedProperties)
           ?? layer.ChangedProperties
               .Select(name => new DefinitionChange { PropertyName = name, Kind = DefinitionChangeKind.Modified })
               .ToList();
}
