using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.ViewModels;

/// <summary>
/// One part of the Overview tab: its heading and count, a note, and its rows in the shape that
/// suits them - a grid, a business rule's outline, a role's privilege pills or a sitemap's tree.
/// </summary>
public abstract class OverviewPart : ObservableObject
{
    public required string Title { get; init; }
    public required int Count { get; init; }
    public string? Note { get; init; }

    /// <summary>"Steps (9)" - the title and count in one, for the grid's accessible name.</summary>
    public string Heading => $"{Title} ({Count:N0})";

    public string CountText => Count.ToString("N0");

    /// <summary>The part as the tab draws it.</summary>
    public static OverviewPart From(OverviewTable table) => table switch
    {
        { Steps: { } steps } => new OverviewStepsPart { Title = table.Title, Count = table.Count, Note = table.Note, Lines = OverviewStepsPart.Outline(steps) },
        { Privileges: { } privileges } => new OverviewPrivilegesPart(privileges) { Title = table.Title, Count = table.Count, Note = table.Note },
        { OtherPrivileges: { } other } => new OverviewOtherPrivilegesPart { Title = table.Title, Count = table.Count, Note = table.Note, Rows = other },
        { Tree: { } tree } => new OverviewTreePart
        {
            Title = table.Title, Count = table.Count, Note = table.Note,
            Lines = tree.Select(l => new OverviewTreeRow(l.Text, l.Opens, l.Depth, new Thickness(12 + l.Depth * 20, 3, 12, 3))).ToList()
        },
        _ => new OverviewGridPart { Title = table.Title, Count = table.Count, Note = table.Note, Rows = Grid(table) }
    };

    /// <summary>The columns differ by type, so the grid is handed a DataView.</summary>
    private static System.Data.DataView Grid(OverviewTable table)
    {
        var data = new System.Data.DataTable();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            // Column names must be unique and non-empty; the header is what is shown.
            var name = string.IsNullOrWhiteSpace(table.Columns[i]) ? $"Column {i + 1}" : table.Columns[i];
            data.Columns.Add(UniqueColumnName(data, name), typeof(string));
        }

        foreach (var row in table.Rows)
        {
            data.Rows.Add(row.Take(table.Columns.Count).Select(v => (object?)v ?? DBNull.Value).ToArray());
        }

        return data.DefaultView;
    }

    /// <summary>The name with as few trailing spaces as make it unique; the header still reads the same.</summary>
    private static string UniqueColumnName(System.Data.DataTable data, string name)
    {
        var padding = 0;
        while (data.Columns.Contains(name + new string(' ', padding))) padding++;
        return name + new string(' ', padding);
    }
}

/// <summary>A part with no shape of its own: a grid of its rows, at full height.</summary>
public sealed class OverviewGridPart : OverviewPart
{
    public required System.Data.DataView Rows { get; init; }
}

/// <summary>
/// One line of a business rule's outline: the keyword (If, Else if, Else, or Then before the first
/// thing a condition does), the rest of the step, and its indent.
/// </summary>
public sealed record OverviewStepLine(string Keyword, string Text, bool IsCondition, Thickness Margin);

/// <summary>A business rule's steps as the designer reads them: conditions, then what each does, indented.</summary>
public sealed class OverviewStepsPart : OverviewPart
{
    public required IReadOnlyList<OverviewStepLine> Lines { get; init; }

    internal static IReadOnlyList<OverviewStepLine> Outline(IReadOnlyList<BusinessRuleStep> steps)
    {
        var lines = new List<OverviewStepLine>(steps.Count);
        BusinessRuleStep? previous = null;

        foreach (var step in steps)
        {
            var condition = step.Kind is BusinessRuleReader.If or BusinessRuleReader.ElseIf or BusinessRuleReader.Else;
            string keyword, text;

            if (condition)
            {
                keyword = step.Kind;
                text = step.Text == step.Kind ? string.Empty
                    : step.Text.StartsWith(step.Kind + " ", StringComparison.Ordinal) ? step.Text[(step.Kind.Length + 1)..]
                    : step.Text;
            }
            else
            {
                // "Then" on the first thing a condition does; the rest line up under it.
                keyword = previous is { Kind: BusinessRuleReader.If or BusinessRuleReader.ElseIf or BusinessRuleReader.Else } p && p.Depth < step.Depth
                    ? "Then"
                    : string.Empty;
                text = step.Text;
            }

            lines.Add(new OverviewStepLine(keyword, text, condition, new Thickness(12 + step.Depth * 20, 3, 12, 3)));
            previous = step;
        }

        return lines;
    }
}

/// <summary>One line of a sitemap: an area, a group or an item, and what the item opens.</summary>
public sealed record OverviewTreeRow(string Text, string? Opens, int Depth, Thickness Margin)
{
    public bool IsArea => Depth == 0;
    public bool IsGroup => Depth == 1;
}

/// <summary>A sitemap as an indented tree, top to bottom as the app's navigation shows it.</summary>
public sealed class OverviewTreePart : OverviewPart
{
    public required IReadOnlyList<OverviewTreeRow> Lines { get; init; }
}

/// <summary>A role's other privileges - Export to Excel, Bulk delete - with each one's reach as a pill.</summary>
public sealed class OverviewOtherPrivilegesPart : OverviewPart
{
    public required IReadOnlyList<MiscPrivilege> Rows { get; init; }
}

/// <summary>
/// A role's table privileges as depth pills, the same as Environment admin: filtered by table, and
/// the first <see cref="FirstRows"/> shown until the rest are asked for.
/// </summary>
public sealed class OverviewPrivilegesPart : OverviewPart
{
    /// <summary>Enough to see what the role is about without a page of pills.</summary>
    public const int FirstRows = 25;

    private readonly IReadOnlyList<PrivilegeMatrixRow> _all;
    private readonly HashSet<object> _first;

    public OverviewPrivilegesPart(IReadOnlyList<PrivilegeMatrixRow> rows)
    {
        _all = rows;
        _first = rows.Take(FirstRows).ToHashSet<object>(ReferenceEqualityComparer.Instance);
        RowsView = new ListCollectionView(rows.ToList()) { Filter = Matches };
        ShowAllCommand = new RelayCommand(_ => ShowingAll = true, _ => !ShowingAll);
    }

    public ICollectionView RowsView { get; }

    private string _filter = string.Empty;
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? string.Empty)) return;
            Refresh();
        }
    }

    private bool _showingAll;
    public bool ShowingAll
    {
        get => _showingAll;
        private set
        {
            if (!SetProperty(ref _showingAll, value)) return;
            ShowAllCommand.RaiseCanExecuteChanged();
            Refresh();
        }
    }

    public RelayCommand ShowAllCommand { get; }

    /// <summary>Rows held back: none while filtering, or once all are shown.</summary>
    public bool HasMore => !ShowingAll && Filter.Length == 0 && _all.Count > FirstRows;

    public string ShowAllLabel => $"Show all {_all.Count:N0} tables";

    /// <summary>Said where the grid would be when the filter matches nothing.</summary>
    public bool NothingMatches => Filter.Length > 0 && RowsView.IsEmpty;

    private bool Matches(object row)
    {
        if (row is not PrivilegeMatrixRow privilege) return false;
        if (Filter.Length > 0) return privilege.Table.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase);

        return ShowingAll || _first.Contains(privilege);
    }

    private void Refresh()
    {
        RowsView.Refresh();
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(NothingMatches));
    }
}
