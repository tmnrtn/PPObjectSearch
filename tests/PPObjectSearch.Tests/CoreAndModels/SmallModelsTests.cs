using PPObjectSearch.Models;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>Labels and flags derived on connection reference, compare, membership and table child rows.</summary>
public class SmallModelsTests
{
    private static ConnectionReferenceInfo Reference(string? connection, string? connector = "/providers/Microsoft.PowerApps/apis/shared_sql", string? display = null) => new()
    {
        Id = Guid.NewGuid(), LogicalName = "new_sql", DisplayName = display, ConnectorId = connector, ConnectionId = connection
    };

    [Fact]
    public void A_connection_reference_row_passes_through_what_its_reference_and_connection_say()
    {
        var row = new ConnectionReferenceRow
        {
            Reference = Reference("conn-1", display: "SQL"),
            Connection = new ConnectionInfo { Name = "conn-1", DisplayName = "Contoso SQL", Owner = "ada@contoso.com", Status = "Connected" }
        };

        Assert.Equal("SQL", row.Label);
        Assert.Equal("new_sql", row.LogicalName);
        Assert.Equal("shared_sql", row.Connector);
        Assert.Equal("conn-1", row.ConnectionId);
        Assert.Equal("Contoso SQL", row.ConnectionName);
        Assert.Equal("ada@contoso.com", row.Owner);
        Assert.False(row.IsBroken);
    }

    [Fact]
    public void A_reference_without_a_connector_or_connection_has_neither()
    {
        var row = new ConnectionReferenceRow { Reference = Reference(null, connector: null) };

        Assert.Equal(string.Empty, row.Connector);
        Assert.Equal("new_sql", row.Label);
        Assert.Null(row.ConnectionName);
        Assert.Null(row.Owner);
        Assert.True(row.IsBroken);
    }

    [Theory]
    [InlineData("Error", null, "Error")]
    [InlineData(null, null, "Error")]
    [InlineData(null, "Expired", "Error - Expired")]
    public void A_failing_connection_names_its_status_and_message(string? status, string? message, string expected)
    {
        var row = new ConnectionReferenceRow
        {
            Reference = Reference("c"),
            Connection = new ConnectionInfo { Name = "c", Status = status, StatusMessage = message }
        };

        Assert.Equal(ConnectionHealth.Error, row.Health);
        Assert.Equal(expected, row.HealthLabel);
        Assert.True(row.IsBroken);
    }

    [Theory]
    [InlineData(true, "Bound to a connection not shared with you")]
    [InlineData(false, "Bound - status not read")]
    public void A_bound_connection_that_was_not_found_is_described_by_whether_the_list_was_read(bool known, string expected)
    {
        var row = new ConnectionReferenceRow { Reference = Reference("c"), ConnectionsKnown = known };

        Assert.Equal(expected, row.HealthLabel);
        Assert.False(row.IsBroken);
    }

    private static SolutionComponentItem Component() => new() { Name = "x", ComponentTypeName = "T" };

    [Theory]
    [InlineData(CompareStatus.OnlyInLeft, "Only in left")]
    [InlineData(CompareStatus.OnlyInRight, "Only in right")]
    [InlineData(CompareStatus.Same, "In both")]
    public void A_compare_row_labels_its_status(CompareStatus status, string expected)
    {
        Assert.Equal(expected, new CompareRow { Name = "x", ComponentTypeName = "T", Status = status }.StatusLabel);
    }

    [Fact]
    public void Only_a_row_on_both_sides_has_two_definitions_to_compare()
    {
        Assert.True(new CompareRow { Name = "x", ComponentTypeName = "T", Status = CompareStatus.Same, Left = Component(), Right = Component() }.ExistsOnBothSides);
        Assert.False(new CompareRow { Name = "x", ComponentTypeName = "T", Status = CompareStatus.OnlyInLeft, Left = Component() }.ExistsOnBothSides);
        Assert.False(new CompareRow { Name = "x", ComponentTypeName = "T", Status = CompareStatus.OnlyInRight, Right = Component() }.ExistsOnBothSides);
    }

    [Theory]
    [InlineData("Owner", 0, "Owner")]
    [InlineData(null, 2, "2")]
    [InlineData(null, null, "Unknown")]
    public void A_team_shows_its_type_label_number_or_unknown(string? label, int? type, string expected)
    {
        var team = new TeamInfo(Guid.NewGuid(), "Sales", type, label, "Contoso", null, null, null, false);

        Assert.Equal(expected, team.TypeDisplay);
        Assert.Equal($"Sales Contoso {expected}", team.SearchText);
        Assert.False(team.IsEntraGroupTeam);
    }

    [Fact]
    public void A_team_linked_to_an_entra_group_is_one_and_queues_and_users_are_searchable()
    {
        var team = new TeamInfo(Guid.NewGuid(), "Group", 2, "AAD Security Group", "Contoso", Guid.NewGuid(), 0, "Members and guests", false);
        var queue = new QueueInfo(Guid.NewGuid(), "Support", "Public", "Ada", "support@contoso.com");
        var app = new MemberUser(Guid.NewGuid(), "# Integration", null, null, null, false, "Non-interactive", Guid.NewGuid());
        var person = app with { ApplicationId = null };

        Assert.True(team.IsEntraGroupTeam);
        Assert.Equal("Support support@contoso.com Ada", queue.SearchText);
        Assert.True(app.IsApplicationUser);
        Assert.False(person.IsApplicationUser);
    }

    [Fact]
    public void A_table_child_labels_itself_and_builds_a_lower_case_filter_index()
    {
        var named = new TableChild { Kind = TableChildKind.Column, Name = "new_Code", DisplayName = "Code", Detail = "String", IsManaged = true, PropertiesQuery = "q" };
        var bare = new TableChild { Kind = TableChildKind.Key, Name = "new_key", DisplayName = " ", PropertiesQuery = "q" };

        named.BuildFilterIndex();
        bare.BuildFilterIndex();

        Assert.Equal(("Code", "Managed"), (named.PrimaryLabel, named.ManagedLabel));
        Assert.Equal(("new_key", "Unmanaged"), (bare.PrimaryLabel, bare.ManagedLabel));
        Assert.Equal("new_code code string", named.FilterIndex);
        Assert.Equal("new_key", bare.FilterIndex);
    }

    [Fact]
    public void A_table_child_group_counts_its_children()
    {
        var empty = new TableChildGroup { Kind = TableChildKind.View, Label = "Views", Children = [] };
        var one = new TableChildGroup
        {
            Kind = TableChildKind.Form, Label = "Forms",
            Children = [new TableChild { Kind = TableChildKind.Form, Name = "Main", PropertiesQuery = "q" }]
        };

        Assert.Equal((0, true), (empty.Count, empty.IsEmpty));
        Assert.Equal((1, false), (one.Count, one.IsEmpty));
    }
}
