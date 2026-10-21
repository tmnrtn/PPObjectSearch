namespace PPObjectSearch.Models;

/// <summary>A labelled value on the Overview tab.</summary>
public sealed record OverviewProperty(string Label, string? Value)
{
    public string Shown => string.IsNullOrWhiteSpace(Value) ? "—" : Value;
}

/// <summary>One line of a tree on the Overview tab: a sitemap's area (0), group (1) or item (2), and what it opens.</summary>
public sealed record OverviewTreeLine(int Depth, string Text, string? Opens);

/// <summary>A list on the Overview tab - a model-driven app's tables, a custom API's parameters...</summary>
public sealed class OverviewTable
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; }

    /// <summary>A line or two about the part, shown in Muted under its heading.</summary>
    public string? Note { get; init; }

    /// <summary>A business rule's steps: drawn as an outline rather than a grid.</summary>
    public IReadOnlyList<Services.BusinessRuleStep>? Steps { get; init; }

    /// <summary>A role's table privileges: drawn with a depth pill for each.</summary>
    public IReadOnlyList<Services.PrivilegeMatrixRow>? Privileges { get; init; }

    /// <summary>A role's other privileges, each with its depth.</summary>
    public IReadOnlyList<Services.MiscPrivilege>? OtherPrivileges { get; init; }

    /// <summary>A sitemap: drawn as an indented tree.</summary>
    public IReadOnlyList<OverviewTreeLine>? Tree { get; init; }

    public int Count => Steps?.Count ?? Privileges?.Count ?? OtherPrivileges?.Count ?? Tree?.Count(l => l.Depth == 2) ?? Rows.Count;

    public string Heading => $"{Title} ({Count:N0})";
}

/// <summary>What the Overview tab shows for a component: its key properties and its parts.</summary>
public sealed class ComponentOverview
{
    public List<OverviewProperty> Properties { get; } = new();
    public List<OverviewTable> Tables { get; } = new();

    /// <summary>Parts that could not be read, said rather than left empty.</summary>
    public List<string> Problems { get; } = new();
}
