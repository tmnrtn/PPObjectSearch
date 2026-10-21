using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.Services;

/// <summary>Comparing table settings across environments, from snapshots already read.</summary>
public class TableSettingsReviewTests
{
    private static readonly Guid Synapse = Guid.Parse("50000000-0000-0000-0000-000000000005");
    private static readonly Guid Fabric = Guid.Parse("50000000-0000-0000-0000-000000000006");
    private static readonly HashSet<string> NoneIgnored = new(StringComparer.OrdinalIgnoreCase);

    private static TableSetting Table(string name, bool audit = true, bool tracking = true, bool search = false, bool custom = true) =>
        new(name, name.ToUpperInvariant(), Guid.NewGuid(), custom, false, audit, tracking, search, true);

    private static TableSettingsSnapshot Env(string name, params TableSetting[] tables) => new()
    {
        Environment = name,
        Tables = tables.ToDictionary(t => t.LogicalName, StringComparer.OrdinalIgnoreCase),
        Settings = new EnvironmentDataSettings(true, true, false, 90, true),
        Synapse = SynapseLinkSetup.Empty
    };

    private static SynapseLinkSetup Links(params (Guid Profile, string Table, int Sync)[] tables) => new(
        [
            new SynapseLinkProfile(Synapse, "Lake", 1, "Active", "SynapseLink", "contosolake", null, null),
            new SynapseLinkProfile(Fabric, "Fabric", 1, "Active", "SynapseLink", "cds_workspace", null, null)
        ],
        tables.Select(t => new SynapseLinkTable(t.Profile, t.Table, t.Sync, t.Sync == 4 ? "Completed" : "InProgress",
            null, null, 10, 10, null, true)).ToList());

    [Fact]
    public void A_table_audited_everywhere_is_consistent()
    {
        var rows = TableSettingsReview.Tables([Env("Dev", Table("account")), Env("Prod", Table("account"))],
            ["account"], TableSettingKind.Audit, NoneIgnored);

        var row = Assert.Single(rows);
        Assert.True(row.IsConsistent);
        Assert.Equal(["On", "On"], row.Cells.Select(c => c.Text));
        Assert.Equal(string.Empty, row.Difference);
    }

    [Fact]
    public void The_minority_is_what_the_difference_names()
    {
        var rows = TableSettingsReview.Tables(
        [
            Env("Dev", Table("account")), Env("QA", Table("account")),
            Env("UAT", Table("account")), Env("Prod", Table("account", audit: false))
        ], ["account"], TableSettingKind.Audit, NoneIgnored);

        var row = Assert.Single(rows);
        Assert.False(row.IsConsistent);
        Assert.Equal("Off in Prod", row.Difference);
    }

    [Fact]
    public void A_table_missing_from_an_environment_is_not_reported_as_off()
    {
        var rows = TableSettingsReview.Tables([Env("Dev", Table("account")), Env("QA")],
            ["account"], TableSettingKind.Audit, NoneIgnored);

        var row = Assert.Single(rows);
        Assert.Equal(SettingCellState.Absent, row.Cells[1].State);
        Assert.False(row.IsConsistent);
        Assert.Equal("missing from QA", row.Difference);
    }

    [Fact]
    public void An_environment_whose_tables_could_not_be_read_is_unknown_not_missing()
    {
        var unread = new TableSettingsSnapshot { Environment = "QA", TablesRead = false };

        var rows = TableSettingsReview.Tables([Env("Dev", Table("account")), unread], ["account"], TableSettingKind.Audit, NoneIgnored);

        Assert.Equal(SettingCellState.Unknown, rows[0].Cells[1].State);
        Assert.Equal("unknown in QA", rows[0].Difference);
    }

    [Fact]
    public void Table_audit_has_no_effect_while_the_environment_is_not_audited()
    {
        var off = new TableSettingsSnapshot
        {
            Environment = "Dev",
            Tables = new Dictionary<string, TableSetting> { ["account"] = Table("account") },
            Settings = new EnvironmentDataSettings(false, null, null, null, null)
        };

        var row = Assert.Single(TableSettingsReview.Tables([off], ["account"], TableSettingKind.Audit, NoneIgnored));

        Assert.Equal(SettingCellState.Inactive, row.Cells[0].State);
    }

    [Fact]
    public void A_table_counts_as_linked_only_through_an_active_profile_in_a_counted_folder()
    {
        var dev = Env("Dev", Table("account"), Table("contact"));
        dev = new TableSettingsSnapshot
        {
            Environment = dev.Environment, Tables = dev.Tables, Settings = dev.Settings,
            Synapse = Links((Synapse, "account", 4), (Fabric, "contact", 4))
        };

        var all = TableSettingsReview.Tables([dev], ["account", "contact"], TableSettingKind.SynapseLink, NoneIgnored);
        Assert.Equal(["On", "On"], all.Select(r => r.Cells[0].Text));

        var ignored = new HashSet<string>(["cds_workspace"], StringComparer.OrdinalIgnoreCase);
        var synapseOnly = TableSettingsReview.Tables([dev], ["account", "contact"], TableSettingKind.SynapseLink, ignored);
        Assert.Equal(["On", "Off"], synapseOnly.Select(r => r.Cells[0].Text));
    }

    [Fact]
    public void A_linked_table_without_change_tracking_is_a_problem()
    {
        var dev = new TableSettingsSnapshot
        {
            Environment = "Dev",
            Tables = new Dictionary<string, TableSetting> { ["account"] = Table("account", tracking: false) },
            Synapse = Links((Synapse, "account", 4))
        };

        var row = Assert.Single(TableSettingsReview.Tables([dev], ["account"], TableSettingKind.SynapseLink, NoneIgnored));

        Assert.Equal(SettingCellState.Problem, row.Cells[0].State);
        Assert.True(row.HasProblem);
        Assert.True(row.IsConsistent);
        Assert.Equal("linked, but change tracking is off in Dev", row.Difference);
    }

    [Fact]
    public void Links_that_could_not_be_read_are_unknown()
    {
        var dev = new TableSettingsSnapshot
        {
            Environment = "Dev", Tables = new Dictionary<string, TableSetting> { ["account"] = Table("account") }, Synapse = null
        };

        var row = Assert.Single(TableSettingsReview.Tables([dev], ["account"], TableSettingKind.SynapseLink, NoneIgnored));

        Assert.Equal(SettingCellState.Unknown, row.Cells[0].State);
    }

    [Fact]
    public void All_settings_pack_into_one_comparable_cell()
    {
        var dev = new TableSettingsSnapshot
        {
            Environment = "Dev",
            Tables = new Dictionary<string, TableSetting> { ["account"] = Table("account", audit: true, tracking: true, search: true) },
            Synapse = Links((Synapse, "account", 4))
        };
        var prod = Env("Prod", Table("account", audit: true, tracking: false, search: false));

        var row = Assert.Single(TableSettingsReview.Tables([dev, prod], ["account"], TableSettingKind.All, NoneIgnored));

        Assert.Equal("A C L S", row.Cells[0].Text);
        Assert.Equal("A · · ·", row.Cells[1].Text);
        Assert.False(row.IsConsistent);
        Assert.Equal("change tracking Off in Prod; link Off in Prod; search Off in Prod", row.Difference);
    }

    [Fact]
    public void Search_on_but_not_in_the_index_is_a_problem()
    {
        var dev = new TableSettingsSnapshot
        {
            Environment = "Dev",
            Tables = new Dictionary<string, TableSetting>
            {
                ["account"] = Table("account", search: true),
                ["contact"] = Table("contact", search: true)
            },
            Search = new SearchIndexStatus("provisioned", null, null,
                [new SearchTableStatus("account", "EntitySyncComplete", null, ["name"])], null, null)
        };

        var rows = TableSettingsReview.Tables([dev], ["account", "contact"], TableSettingKind.Search, NoneIgnored);

        Assert.Equal(SettingCellState.On, rows[0].Cells[0].State);
        Assert.Equal(SettingCellState.Problem, rows[1].Cells[0].State);

        var index = TableSettingsReview.SearchIndex([dev]);
        Assert.Equal(2, index.Count);
        Assert.False(index[0].HasProblem);
        Assert.Equal("Search is on for the table, but it is not in the index", index[1].Problem);
    }

    [Fact]
    public void Columns_compare_across_environments_and_say_where_the_table_is_not_audited()
    {
        var dev = Env("Dev", Table("account"));
        dev.Columns["account"] = [new ColumnAudit("contoso_tier", "Tier", true, false), new ColumnAudit("contoso_code", null, true, false)];

        var prod = Env("Prod", Table("account", audit: false));
        prod.Columns["account"] = [new ColumnAudit("contoso_tier", "Tier", false, true)];

        var rows = TableSettingsReview.Columns([dev, prod], ["account"]);

        Assert.Equal(["contoso_code", "contoso_tier"], rows.Select(r => r.Name));
        Assert.Equal(SettingCellState.Absent, rows[0].Cells[1].State);
        Assert.Equal(SettingCellState.Inactive, rows[1].Cells[1].State);
        Assert.Equal("Tier · account", rows[1].Detail);
        Assert.Equal("account|contoso_tier", rows[1].Key);
    }

    [Fact]
    public void Gaps_are_unaudited_columns_on_audited_tables_only()
    {
        var dev = Env("Dev", Table("account"), Table("contact", audit: false));
        dev.Columns["account"] = [new ColumnAudit("contoso_tier", "Tier", false, true), new ColumnAudit("contoso_code", null, true, false)];
        dev.Columns["contact"] = [new ColumnAudit("contoso_pet", null, false, false)];

        var gap = Assert.Single(TableSettingsReview.Gaps([dev], ["account", "contact"]));

        Assert.Equal("Dev", gap.Environment);
        Assert.Equal("contoso_tier", gap.Column);
        Assert.True(gap.IsManaged);
    }

    [Fact]
    public void A_link_row_says_why_it_needs_a_look()
    {
        var dev = new TableSettingsSnapshot
        {
            Environment = "Dev", Synapse = Links((Synapse, "account", 4), (Synapse, "contact", 2))
        };

        var rows = TableSettingsReview.Links([dev], NoneIgnored);

        Assert.False(rows[0].HasProblem);
        Assert.Equal("Sync is InProgress", rows[1].Problem);
        Assert.True(rows.All(r => r.IsCounted));
    }

    [Fact]
    public void Folders_are_listed_once_across_environments()
    {
        var dev = new TableSettingsSnapshot { Environment = "Dev", Synapse = Links() };
        var prod = new TableSettingsSnapshot { Environment = "Prod", Synapse = Links() };

        Assert.Equal(["cds_workspace", "contosolake"], TableSettingsReview.Folders([dev, prod]));
    }

    [Fact]
    public void Environment_settings_line_up_side_by_side()
    {
        var dev = Env("Dev", Table("account"));
        var prod = new TableSettingsSnapshot
        {
            Environment = "Prod",
            Tables = dev.Tables,
            Settings = new EnvironmentDataSettings(false, true, false, -1, true)
        };

        var rows = TableSettingsReview.Environments([dev, prod]);

        var auditing = rows.Single(r => r.Name == "Auditing");
        Assert.Equal("Off in Prod", auditing.Difference);

        var retention = rows.Single(r => r.Name == "Retain audit logs");
        Assert.Equal(["90 days", "Forever"], retention.Cells.Select(c => c.Text));

        var links = rows.Single(r => r.Name == "Link profiles");
        Assert.Equal(SettingCellState.Unknown, links.Cells[1].State);
    }
}
