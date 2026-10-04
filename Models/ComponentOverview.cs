namespace PPObjectSearch.Models;

/// <summary>A labelled value on the Overview tab.</summary>
public sealed record OverviewProperty(string Label, string? Value)
{
    public string Shown => string.IsNullOrWhiteSpace(Value) ? "—" : Value!;
}

/// <summary>A list on the Overview tab - a model-driven app's tables, a custom API's parameters...</summary>
public sealed class OverviewTable
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; }

    /// <summary>Extra lines shown under the title, such as a sitemap rendered as an outline.</summary>
    public string? Note { get; init; }

    public string Heading => $"{Title} ({Rows.Count:N0})";
}

/// <summary>What the Overview tab shows for a component: its key properties and its parts.</summary>
public sealed class ComponentOverview
{
    public List<OverviewProperty> Properties { get; } = new();
    public List<OverviewTable> Tables { get; } = new();

    /// <summary>Parts that could not be read, said rather than left empty.</summary>
    public List<string> Problems { get; } = new();
}
