namespace PPObjectSearch.Models;

/// <summary>A column of a table, as an entity relationship diagram needs it.</summary>
public sealed record ErdColumn(
    string LogicalName,
    string? DisplayName,
    string Type,
    bool IsPrimaryId = false,
    bool IsPrimaryName = false,
    bool IsRequired = false,
    bool IsCustom = false);

/// <summary>A lookup from one table to another: the many end looks up the one end.</summary>
/// <param name="Column">The lookup column on the many end.</param>
/// <param name="Target">The table looked up - one of several, for a customer, owner or regarding lookup.</param>
public sealed record ErdLookup(string SchemaName, string Column, string Target);

/// <summary>A many-to-many relationship, held in its intersect table.</summary>
public sealed record ErdManyToMany(string SchemaName, string Table1, string Table2, string? IntersectTable);

/// <summary>A table's metadata for the diagram: its columns and the relationships it starts.</summary>
public sealed record ErdTable(
    string LogicalName,
    string? DisplayName,
    IReadOnlyList<ErdColumn> Columns,
    IReadOnlyList<ErdLookup> Lookups,
    IReadOnlyList<ErdManyToMany> ManyToMany,
    bool IsCustom = false,
    bool IsIntersect = false)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? LogicalName : DisplayName;
}
