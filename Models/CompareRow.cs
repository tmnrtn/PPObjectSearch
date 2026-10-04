using PPObjectSearch.Models;

// Kept in the view-model namespace, where the compare window's XAML refers to them; they hold no
// WPF types, so the command line and the tests use them too.
namespace PPObjectSearch.ViewModels;

public enum CompareStatus
{
    OnlyInLeft,
    OnlyInRight,
    Same
}

/// <summary>The segmented filter above the grid.</summary>
public enum CompareStatusFilter
{
    All,
    OnlyLeft,
    OnlyRight,
    Both
}

public sealed class CompareRow
{
    public required string Name { get; init; }
    public required string ComponentTypeName { get; init; }
    public string? SubType { get; init; }
    public required CompareStatus Status { get; init; }
    public DateTimeOffset? LeftModified { get; init; }
    public DateTimeOffset? RightModified { get; init; }

    /// <summary>Whichever side's row can supply a maker portal link.</summary>
    public SolutionComponentItem? Link { get; init; }

    /// <summary>
    /// Both sides are kept, not just the one that supplies the link, because comparing the
    /// definition needs to ask each environment about its own object - matched-by-name rows
    /// carry a different id on each side.
    /// </summary>
    public SolutionComponentItem? Left { get; init; }
    public SolutionComponentItem? Right { get; init; }

    /// <summary>Only a component present on both sides has two definitions to compare.</summary>
    public bool ExistsOnBothSides => Left is not null && Right is not null;

    public string StatusLabel => Status switch
    {
        CompareStatus.OnlyInLeft => "Only in left",
        CompareStatus.OnlyInRight => "Only in right",
        _ => "In both"
    };
}

