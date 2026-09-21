namespace PPObjectSearch.Models;

/// <summary>
/// The kinds of thing a table owns, in the order the maker portal's table page lists them -
/// schema first (columns, relationships, keys), then the data experiences built on top.
/// </summary>
public enum TableChildKind
{
    Column,
    Relationship,
    Key,
    Form,
    View,
    Chart,
    Dashboard
}

/// <summary>One property of a component, flattened out of its Dataverse record.</summary>
public sealed class ComponentProperty
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// One thing owned by a table - a column, relationship, key, form, view, chart or dashboard.
/// The list row is read cheaply up front; the full property set waits until the child is
/// selected, because a form or view record carries its entire XML definition.
/// </summary>
public sealed class TableChild
{
    public required TableChildKind Kind { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>
    /// What separates this child from its siblings - a column's data type, a relationship's
    /// other end, a form's type. Null where the kind has nothing worth summarising.
    /// </summary>
    public string? Detail { get; init; }

    public bool IsManaged { get; init; }
    public Guid Id { get; init; }

    /// <summary>
    /// The Web API query returning this child's full record, relative to the API root. Built when
    /// the child is listed, because only the client knows the route a kind needs - a column is
    /// addressed through its concrete metadata type, a form through its entity set.
    /// </summary>
    public required string PropertiesQuery { get; init; }

    /// <summary>Cached after the first fetch - re-selecting a child should not re-query.</summary>
    public IReadOnlyList<ComponentProperty>? Properties { get; set; }

    public string PrimaryLabel => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;

    public string ManagedLabel => IsManaged ? "Managed" : "Unmanaged";

    /// <summary>Lower-case haystack for the filter box, matching the search box on the main list.</summary>
    public string FilterIndex { get; private set; } = string.Empty;

    public void BuildFilterIndex() =>
        FilterIndex = string.Join(" ", new[] { Name, DisplayName, Detail }
            .Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();
}

/// <summary>One kind's worth of children, listed even when empty - "Views 0" is an answer.</summary>
public sealed class TableChildGroup
{
    public required TableChildKind Kind { get; init; }
    public required string Label { get; init; }
    public required IReadOnlyList<TableChild> Children { get; init; }

    public int Count => Children.Count;

    public bool IsEmpty => Children.Count == 0;
}
