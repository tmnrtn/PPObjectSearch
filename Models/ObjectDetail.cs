using PPObjectSearch.Core;

namespace PPObjectSearch.Models;

public enum DependencyDirection
{
    /// <summary>Things that depend on this object - it cannot be deleted while these exist.</summary>
    Dependent,

    /// <summary>Things this object needs in order to work.</summary>
    Required
}

/// <summary>One end of a dependency relationship, resolved to a name where possible.</summary>
public sealed class DependencyRef
{
    public required Guid ObjectId { get; init; }
    public required int ComponentType { get; init; }
    public required string ComponentTypeName { get; init; }
    public required DependencyDirection Direction { get; init; }

    /// <summary>Name from the loaded solution, where the component is one we know about.</summary>
    public string? ResolvedName { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(ResolvedName) ? ObjectId.ToString() : ResolvedName!;

    public string DirectionLabel => Direction == DependencyDirection.Dependent ? "Depends on this" : "Required by this";
}

/// <summary>A solution that contains a given object.</summary>
public sealed class ContainingSolution
{
    public required Guid SolutionId { get; init; }
    public required string FriendlyName { get; init; }
    public required string UniqueName { get; init; }
    public bool IsManaged { get; init; }
    public string? Version { get; init; }

    public string StateLabel => IsManaged ? "Managed" : "Unmanaged";
}

/// <summary>The kind of difference one property shows between two component definitions.</summary>
public enum DefinitionChangeKind
{
    Added,
    Modified,
    Removed
}

/// <summary>
/// One property-level difference between two component definitions - either a solution layer
/// against the layer beneath it, or the same component in two environments. Where one side has
/// no definition at all, every property of the other reads as Added or Removed.
/// </summary>
public sealed class DefinitionChange
{
    public required string PropertyName { get; init; }
    public required DefinitionChangeKind Kind { get; init; }
    public string? PreviousValue { get; init; }
    public string? CurrentValue { get; init; }

    public string KindLabel => Kind switch
    {
        DefinitionChangeKind.Added => "Added",
        DefinitionChangeKind.Removed => "Removed",
        _ => "Changed"
    };

    private string? _previousText;
    private string? _currentText;
    private IReadOnlyList<DiffRow>? _diff;

    /// <summary>The before value, indented where it is JSON or XML so it can actually be read.</summary>
    public string PreviousText => _previousText ??= TextDiff.Prettify(PreviousValue);

    public string CurrentText => _currentText ??= TextDiff.Prettify(CurrentValue);

    /// <summary>Built on demand - a component's whole form definition is not worth diffing until
    /// someone selects that row.</summary>
    public IReadOnlyList<DiffRow> Diff => _diff ??= TextDiff.Compare(PreviousText, CurrentText);
}

/// <summary>
/// One row of a component's solution layer stack, top (most recently applied) first. The
/// unmanaged layer is a single, environment-wide layer shared by every unmanaged customization -
/// Dataverse's own maker portal labels it "Active" rather than naming a solution.
/// </summary>
public sealed class ComponentLayer
{
    public required string SolutionName { get; init; }
    public string? PublisherName { get; init; }
    public int Order { get; init; }

    /// <summary>
    /// The component's whole definition as it stands in this layer (msdyn_componentjson), which
    /// is what makes a layer-to-layer diff possible. Null when Dataverse returns none.
    /// </summary>
    public string? ComponentJson { get; init; }

    /// <summary>Dataverse's own list of the properties this layer touched (msdyn_changes), in
    /// whatever shape it chose to return - see <see cref="ChangedProperties"/>.</summary>
    public string? ChangesRaw { get; init; }

    public bool IsUnmanagedLayer => string.Equals(SolutionName, "Active", StringComparison.OrdinalIgnoreCase);

    public string StateLabel => IsUnmanagedLayer ? "Unmanaged (Active)" : "Managed";

    public bool HasDefinition => !string.IsNullOrWhiteSpace(ComponentJson);

    private IReadOnlyList<string>? _changedProperties;

    /// <summary>The properties named in msdyn_changes, which arrives either as a JSON array or as
    /// a delimited string depending on the component type.</summary>
    public IReadOnlyList<string> ChangedProperties => _changedProperties ??= ParseChanges(ChangesRaw);

    /// <summary>What the layers grid shows without opening the diff.</summary>
    public string ChangeSummary
    {
        get
        {
            var count = ChangedProperties.Count;
            if (count > 0) return count == 1 ? "1 property" : $"{count} properties";

            return HasDefinition ? "Full definition" : "-";
        }
    }

    private static IReadOnlyList<string> ParseChanges(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        var trimmed = raw.Trim();

        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray()
                        .Select(e => e.ValueKind == System.Text.Json.JsonValueKind.String
                            ? e.GetString()
                            : e.ToString())
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall through to the delimited reading below.
            }
        }

        return trimmed
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
