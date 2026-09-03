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

    public bool IsUnmanagedLayer => string.Equals(SolutionName, "Active", StringComparison.OrdinalIgnoreCase);

    public string StateLabel => IsUnmanagedLayer ? "Unmanaged (Active)" : "Managed";
}
