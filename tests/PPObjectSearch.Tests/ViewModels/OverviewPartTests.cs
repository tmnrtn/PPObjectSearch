using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>The Overview tab's parts in their own shapes: a rule's outline, a role's pills, a sitemap's tree.</summary>
public class OverviewPartTests
{
    private static OverviewTable Table(string title) => new() { Title = title, Columns = ["A"], Rows = [] };

    [Fact]
    public void A_business_rule_reads_as_an_outline_with_then_before_the_first_action()
    {
        var part = OverviewPart.From(new OverviewTable
        {
            Title = "Steps", Columns = ["Step"], Rows = [],
            Steps =
            [
                new BusinessRuleStep(0, BusinessRuleReader.If, "If Priority equals High"),
                new BusinessRuleStep(1, BusinessRuleReader.Action, "Make Escalation tier required"),
                new BusinessRuleStep(1, BusinessRuleReader.Action, "Show Escalate after"),
                new BusinessRuleStep(0, BusinessRuleReader.Else, "Else"),
                new BusinessRuleStep(1, BusinessRuleReader.Action, "Lock Escalation tier")
            ]
        });

        var steps = Assert.IsType<OverviewStepsPart>(part);
        Assert.Equal(5, steps.Count);
        Assert.Equal(
            [("If", "Priority equals High", true), ("Then", "Make Escalation tier required", false), ("", "Show Escalate after", false),
             ("Else", "", true), ("Then", "Lock Escalation tier", false)],
            steps.Lines.Select(l => (l.Keyword, l.Text, l.IsCondition)));
        Assert.Equal(32, steps.Lines[1].Margin.Left);
        Assert.Equal(12, steps.Lines[0].Margin.Left);
    }

    [Fact]
    public void Table_privileges_show_the_first_rows_until_all_are_asked_for_and_filter_by_table()
    {
        var rows = Enumerable.Range(1, OverviewPrivilegesPart.FirstRows + 5)
            .Select(i => new PrivilegeMatrixRow { Table = $"table{i:D2}", Read = PrivilegeDepth.Organization })
            .ToList();
        var table = Table("Table privileges");
        var part = Assert.IsType<OverviewPrivilegesPart>(OverviewPart.From(new OverviewTable
        {
            Title = table.Title, Columns = table.Columns, Rows = table.Rows, Privileges = rows
        }));

        Assert.Equal(OverviewPrivilegesPart.FirstRows, part.RowsView.Cast<object>().Count());
        Assert.True(part.HasMore);
        Assert.Equal("Show all 30 tables", part.ShowAllLabel);

        part.Filter = "table3";
        Assert.Equal(["table30"], part.RowsView.Cast<PrivilegeMatrixRow>().Select(r => r.Table));
        Assert.False(part.HasMore);

        part.Filter = "nothing";
        Assert.True(part.NothingMatches);

        part.Filter = string.Empty;
        part.ShowAllCommand.Execute(null);
        Assert.Equal(30, part.RowsView.Cast<object>().Count());
        Assert.False(part.HasMore);
        Assert.False(part.ShowAllCommand.CanExecute(null));
    }

    [Fact]
    public void A_sitemap_is_a_tree_counted_by_its_items()
    {
        var part = Assert.IsType<OverviewTreePart>(OverviewPart.From(new OverviewTable
        {
            Title = "Navigation (sitemap)", Columns = ["Navigation", "Opens"], Rows = [],
            Tree = [new(0, "Sales", null), new(1, "Customers", null), new(2, "Accounts", "Table: account"), new(2, "Contacts", "Table: contact")]
        }));

        Assert.Equal(2, part.Count);
        Assert.Equal("2", part.CountText);
        Assert.True(part.Lines[0].IsArea);
        Assert.True(part.Lines[1].IsGroup);
        Assert.Equal(52, part.Lines[2].Margin.Left);
    }

    [Fact]
    public void Other_parts_are_grids_with_the_count_beside_the_title()
    {
        var part = Assert.IsType<OverviewGridPart>(OverviewPart.From(new OverviewTable
        {
            Title = "Options", Columns = ["Value", ""], Rows = [["1", "Red"], ["2", null]], Note = "Global choice"
        }));

        Assert.Equal("Options", part.Title);
        Assert.Equal("2", part.CountText);
        Assert.Equal("Global choice", part.Note);
        Assert.Equal(["Value", "Column 2"], part.Rows.Table!.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
    }
}
