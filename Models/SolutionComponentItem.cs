namespace PPObjectSearch.Models;

public sealed class SolutionInfo
{
    public required Guid SolutionId { get; init; }
    public required string UniqueName { get; init; }
    public required string FriendlyName { get; init; }
    public bool IsManaged { get; init; }
    public string? Version { get; init; }
    public string? PublisherName { get; init; }

    public bool IsDefaultSolution =>
        string.Equals(UniqueName, "Default", StringComparison.OrdinalIgnoreCase);

    public string DisplayLabel => IsManaged ? $"{FriendlyName}  (managed)" : FriendlyName;

    public override string ToString() => DisplayLabel;
}

/// <summary>One row of the solution's object list.</summary>
public sealed class SolutionComponentItem
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public required string ComponentTypeName { get; init; }
    public int ComponentType { get; init; }
    public string? ComponentLogicalName { get; init; }
    public Guid ObjectId { get; init; }
    public string? SchemaName { get; init; }
    public string? PrimaryEntityName { get; init; }
    public bool IsManaged { get; init; }
    public bool IsCustomizable { get; init; }
    public string? Owner { get; init; }
    public DateTimeOffset? ModifiedOn { get; init; }
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>Absolute maker portal URL for this object, or null when no link can be built.</summary>
    public string? MakerUrl { get; set; }

    /// <summary>Best label for the Name column - display name where there is one.</summary>
    public string PrimaryLabel =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : Name;

    /// <summary>Secondary identifier shown alongside the label (logical / schema name).</summary>
    public string SecondaryLabel
    {
        get
        {
            var secondary = !string.IsNullOrWhiteSpace(SchemaName) ? SchemaName! : Name;
            return string.Equals(secondary, PrimaryLabel, StringComparison.Ordinal) ? string.Empty : secondary;
        }
    }

    public string ManagedLabel => IsManaged ? "Managed" : "Unmanaged";

    /// <summary>Pre-computed lower-case haystack so keyword filtering stays allocation free.</summary>
    public string SearchIndex { get; private set; } = string.Empty;

    public void BuildSearchIndex()
    {
        var parts = new[]
        {
            Name,
            DisplayName,
            SchemaName,
            ComponentTypeName,
            ComponentLogicalName,
            PrimaryEntityName,
            Owner,
            ObjectId == Guid.Empty ? null : ObjectId.ToString()
        };

        SearchIndex = string.Join(" ", parts.Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();
    }
}
