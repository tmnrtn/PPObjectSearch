using System.Xml.Linq;
using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.Services;

public class ErdTests
{
    /// <summary>A rough Segoe UI: wide enough that nothing measured here is cut by accident.</summary>
    internal static double Measure(string text, double size, bool bold) => text.Length * size * (bold ? 0.6 : 0.55);

    private static ErdColumn Id(string name) => new(name, null, "Uniqueidentifier", IsPrimaryId: true, IsRequired: true);
    private static ErdColumn Name(string name) => new(name, "Name", "String", IsPrimaryName: true, IsRequired: true);
    private static ErdColumn Lookup(string name, bool required = false, bool custom = true) => new(name, null, "Lookup", IsRequired: required, IsCustom: custom);

    /// <summary>
    /// Case looks up contact (required), account (optional) and itself (parent case), and has the
    /// usual system lookups; contact looks up account; case and contact share a many-to-many.
    /// </summary>
    private static IReadOnlyList<ErdTable> Tables() =>
    [
        new("ect_case", "Case",
            [Id("ect_caseid"), Name("ect_name"), Lookup("ect_contactid", required: true), Lookup("ect_accountid"), Lookup("ect_parentcaseid"),
             new("ect_notes", "Notes", "Memo", IsCustom: true), new("statecode", "Status", "State"),
             Lookup("ownerid", required: true, custom: false), Lookup("createdby", custom: false)],
            [new("ect_contact_case", "ect_contactid", "contact"), new("ect_account_case", "ect_accountid", "account"),
             new("ect_case_parent", "ect_parentcaseid", "ect_case"), new("owner_cases", "ownerid", "systemuser"),
             new("team_cases", "ownerid", "team"), new("lk_case_createdby", "createdby", "systemuser")],
            [new("ect_case_contact_watchers", "ect_case", "contact", "ect_case_contact_watchers")],
            IsCustom: true),
        new("contact", "Contact",
            [Id("contactid"), Name("fullname"), Lookup("parentcustomerid", custom: false)],
            [new("contact_customer_accounts", "parentcustomerid", "account"), new("contact_customer_contacts", "parentcustomerid", "contact")],
            [new("ect_case_contact_watchers", "ect_case", "contact", "ect_case_contact_watchers")])
    ];

    /// <summary>The shared tables, drawn for the ect publisher - with reference tables, as contact is one here.</summary>
    private static ErdDiagram Build(ErdOptions? options = null)
    {
        var shaped = options ?? new ErdOptions();
        shaped = shaped with { Prefixes = shaped.Prefixes ?? ["ect"], ShowReferenceTables = true };
        return ErdBuilder.Build(Tables(), shaped, new Dictionary<string, string?> { ["account"] = "Account" });
    }

    // ---- builder ----

    [Fact]
    public void The_solutions_own_lookups_are_drawn_from_their_one_end()
    {
        var diagram = Build();

        Assert.Equal(["Case", "Contact", "Account"], diagram.Boxes.Select(b => b.Title));
        Assert.True(diagram.Boxes.Single(b => b.LogicalName == "account").IsOutside);

        var lookups = diagram.Links.Where(l => !l.IsManyToMany).Select(l => $"{l.One}<-{l.Many}.{l.Label}").ToList();
        Assert.Equal(
            ["account<-ect_case.ect_accountid", "contact<-ect_case.ect_contactid", "ect_case<-ect_case.ect_parentcaseid"],
            lookups.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_required_lookup_is_exactly_one_and_an_optional_one_zero_or_one()
    {
        var links = Build().Links;

        Assert.Equal(ErdCardinality.ExactlyOne, links.Single(l => l.Label == "ect_contactid").OneEnd);
        Assert.Equal(ErdCardinality.ZeroOrOne, links.Single(l => l.Label == "ect_accountid").OneEnd);
        Assert.All(links, l => Assert.Equal(ErdCardinality.ZeroOrMany, l.ManyEnd));
    }

    [Fact]
    public void The_platforms_lookups_are_left_out_unless_asked_for()
    {
        var diagram = Build();
        Assert.DoesNotContain(diagram.Links, l => l.Label is "parentcustomerid" or "ownerid" or "createdby");
        Assert.DoesNotContain(diagram.Boxes.Single(b => b.LogicalName == "ect_case").Rows, r => r.Name == "ownerid");

        var platform = Build(new ErdOptions(ShowSystemRelationships: true));
        Assert.Equal(["account", "contact"], platform.Links.Where(l => l.Label == "parentcustomerid").Select(l => l.One).Order(StringComparer.Ordinal));
        // Users and teams are still left out: the owner lookup is listed, naming them, without a line.
        Assert.DoesNotContain(platform.Boxes, b => b.LogicalName is "systemuser" or "team");
        Assert.Equal("systemuser, team", platform.Boxes.Single(b => b.LogicalName == "ect_case").Rows.Single(r => r.Name == "ownerid").References);

        var people = Build(new ErdOptions(ShowSystemRelationships: true, ShowPeople: true));
        Assert.Contains(people.Boxes, b => b.LogicalName == "systemuser" && b.IsOutside);
        Assert.Contains(people.Boxes, b => b.LogicalName == "team");
        Assert.Equal(2, people.Links.Count(l => l.Label == "ownerid"));
    }

    [Fact]
    public void A_lookup_to_a_user_team_or_business_unit_is_listed_without_a_line()
    {
        IReadOnlyList<ErdTable> tables =
        [
            new("ect_case", "Case", [Id("ect_caseid"), Lookup("ect_approver")], [new("ect_case_approver", "ect_approver", "systemuser")], [], IsCustom: true),
            new("systemuser", "User", [Id("systemuserid"), Lookup("ect_caseid")], [new("ect_user_case", "ect_caseid", "ect_case")], [])
        ];

        var diagram = ErdBuilder.Build(tables, new ErdOptions(Prefixes: ["ect"], ShowReferenceTables: true));

        Assert.Equal(["ect_case"], diagram.Boxes.Select(b => b.LogicalName));
        Assert.Empty(diagram.Links);
        var row = diagram.Boxes[0].Rows.Single(r => r.Name == "ect_approver");
        Assert.Equal(("FK", "systemuser"), (row.Marker, row.References));

        var people = ErdBuilder.Build(tables, new ErdOptions(Prefixes: ["ect"], ShowReferenceTables: true, ShowPeople: true));
        Assert.Equal(2, people.Links.Count);
    }

    [Fact]
    public void Without_outside_tables_a_lookup_to_one_is_listed_without_a_line()
    {
        var diagram = Build(new ErdOptions(ShowOutsideTables: false));

        Assert.Equal(["ect_case", "contact"], diagram.Boxes.Select(b => b.LogicalName));
        Assert.DoesNotContain(diagram.Links, l => l.One == "account");
        Assert.Equal("account", diagram.Boxes[0].Rows.Single(r => r.Name == "ect_accountid").References);
    }

    [Fact]
    public void A_many_to_many_listed_by_both_its_tables_is_drawn_once()
    {
        var manyToMany = Assert.Single(Build().Links, l => l.IsManyToMany);

        Assert.Equal(("ect_case", "contact", "ect_case_contact_watchers"), (manyToMany.One, manyToMany.Many, manyToMany.Label));
        Assert.Equal(ErdCardinality.ZeroOrMany, manyToMany.OneEnd);
    }

    [Fact]
    public void Keys_list_the_primary_key_the_primary_name_and_the_lookups()
    {
        var rows = Build().Boxes.Single(b => b.LogicalName == "ect_case").Rows;

        Assert.Equal(["ect_caseid", "ect_name", "ect_accountid", "ect_contactid", "ect_parentcaseid"], rows.Select(r => r.Name));
        Assert.Equal("PK", rows[0].Marker);
        Assert.Equal("FK", rows.Single(r => r.Name == "ect_accountid").Marker);
        Assert.Equal("account", rows.Single(r => r.Name == "ect_accountid").References);
        Assert.True(rows.Single(r => r.Name == "ect_contactid").IsRequired);
    }

    [Fact]
    public void A_lookup_reaching_two_tables_names_both()
    {
        var row = Build(new ErdOptions(ShowSystemRelationships: true)).Boxes.Single(b => b.LogicalName == "contact").Rows.Single(r => r.Name == "parentcustomerid");

        Assert.Equal("account, contact", row.References);
    }

    [Fact]
    public void Microsofts_columns_are_not_the_solutions_own_though_dataverse_calls_them_custom()
    {
        IReadOnlyList<ErdTable> tables =
        [
            new("account", "Account", [Id("accountid"), Lookup("msdyn_segmentid"), Lookup("ect_tierid")],
                [new("msdyn_segment_account", "msdyn_segmentid", "msdyn_segment"), new("ect_tier_account", "ect_tierid", "ect_tier")],
                [new("msdyn_warranty_account", "msdyn_warranty", "account", null), new("ect_account_region", "account", "ect_region", null)])
        ];

        var own = ErdBuilder.Build(tables, new ErdOptions(Prefixes: ["ect"], ShowReferenceTables: true));
        Assert.Equal(["ect_account_region", "ect_tierid"], own.Links.Select(l => l.Label).Order(StringComparer.Ordinal));

        // With no prefix known, custom stands in for own - and Microsoft's columns count.
        Assert.Equal(4, ErdBuilder.Build(tables, new ErdOptions(ShowReferenceTables: true)).Links.Count);
    }

    [Fact]
    public void An_out_of_box_lookup_to_almost_any_table_is_the_platforms_own()
    {
        var targets = new[] { "ect_case", "ect_hearing", "ect_court", "ect_offence" };
        IReadOnlyList<ErdTable> tables =
        [
            new("task", "Task", [Id("activityid"), Lookup("regardingobjectid", custom: false), Lookup("ect_subjectid")],
                [.. targets.Select(t => new ErdLookup("task_" + t, "regardingobjectid", t)), .. targets.Select(t => new ErdLookup("ect_task_" + t, "ect_subjectid", t))], []),
            .. targets.Select(t => new ErdTable(t, t, [Id(t + "id")], [], [], IsCustom: true))
        ];

        var diagram = ErdBuilder.Build(tables, new ErdOptions(Prefixes: ["ect"], ShowReferenceTables: true));

        Assert.DoesNotContain(diagram.Links, l => l.Label == "regardingobjectid");
        Assert.Equal(4, diagram.Links.Count(l => l.Label == "ect_subjectid"));
        Assert.Equal(8, ErdBuilder.Build(tables, new ErdOptions(ShowSystemRelationships: true, ShowReferenceTables: true)).Links.Count);
    }

    [Fact]
    public void A_reference_table_is_listed_in_the_tables_that_look_it_up_rather_than_drawn()
    {
        IReadOnlyList<ErdTable> tables =
        [
            new("ect_case", "Case", [Id("ect_caseid"), Lookup("ect_casetypeid"), Lookup("ect_vehicleid")],
                [new("ect_casetype_case", "ect_casetypeid", "ect_casetype"), new("ect_vehicle_case", "ect_vehicleid", "ect_vehicle")], [], IsCustom: true),
            new("ect_casetype", "Case Type", [Id("ect_casetypeid"), Lookup("ect_parenttypeid")], [new("ect_casetype_parent", "ect_parenttypeid", "ect_casetype")], [], IsCustom: true),
            new("ect_vehicle", "Vehicle", [Id("ect_vehicleid"), Lookup("ect_makeid")], [new("ect_make_vehicle", "ect_makeid", "ect_make")], [], IsCustom: true),
            new("ect_make", "Make", [Id("ect_makeid")], [], [], IsCustom: true)
        ];

        var diagram = ErdBuilder.Build(tables, new ErdOptions(Prefixes: ["ect"]));

        // Case type looks up only itself and make nothing; vehicle looks up make, so it stays.
        Assert.Equal(["ect_case", "ect_vehicle"], diagram.Boxes.Select(b => b.LogicalName));
        Assert.Equal(["ect_vehicleid"], diagram.Links.Select(l => l.Label));
        Assert.Equal("ect_casetype", diagram.Boxes[0].Rows.Single(r => r.Name == "ect_casetypeid").References);
        Assert.Equal("ect_make", diagram.Boxes[1].Rows.Single(r => r.Name == "ect_makeid").References);

        Assert.Equal(4, ErdBuilder.Build(tables, new ErdOptions(Prefixes: ["ect"], ShowReferenceTables: true)).Boxes.Count);
    }

    [Fact]
    public void A_platform_many_to_many_to_a_table_outside_is_left_out()
    {
        IReadOnlyList<ErdTable> tables =
        [
            new("contact", "Contact", [Id("contactid")], [],
                [new("adx_invitation_invitecontacts", "adx_invitation", "contact", null),
                 new("ect_contact_region", "contact", "ect_region", null)])
        ];

        var diagram = ErdBuilder.Build(tables, new ErdOptions(Prefixes: ["ect"]));

        Assert.Equal(["ect_contact_region"], diagram.Links.Select(l => l.Label));
        Assert.Equal(2, ErdBuilder.Build(tables, new ErdOptions(ShowSystemRelationships: true, Prefixes: ["ect"])).Links.Count);
    }

    // ---- suggested tables and neighbours ----

    private static IReadOnlyList<ErdTable> Solution() =>
    [
        new("ect_case", "Case", [Id("ect_caseid"), Lookup("ect_contactid")], [new("ect_contact_case", "ect_contactid", "contact")], [], IsCustom: true),
        new("contact", "Contact", [Id("contactid"), Lookup("parentcustomerid", custom: false)], [new("contact_account", "parentcustomerid", "account")], []),
        new("account", "Account", [Id("accountid"), new("ect_tier", "Tier", "Picklist", IsCustom: true)], [], []),
        new("audit", "Audit", [Id("auditid"), new("msdyn_flag", "Flag", "Boolean", IsCustom: true)], [], []),
        new("appointment", "Appointment", [Id("activityid")], [], []),
        new("ect_case_contact", null, [Id("ect_case_contactid")], [], [], IsCustom: true, IsIntersect: true),
        new("msdyn_widget", "Widget", [Id("msdyn_widgetid")], [], [], IsCustom: true)
    ];

    [Fact]
    public void Suggested_tables_are_the_solutions_own_those_they_extend_and_those_their_lookups_reach()
    {
        var suggested = ErdBuilder.Suggested(Solution(), ["ect"]);

        // ect_case its own; account extended with ect_tier; contact looked up by ect_contactid.
        // Audit's column is another publisher's; appointment is held for a form; the intersect table is a line.
        Assert.Equal(["account", "contact", "ect_case"], suggested.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Without_a_publisher_prefix_custom_tables_and_columns_stand_in()
    {
        var suggested = ErdBuilder.Suggested(Solution(), []);

        Assert.Equal(["account", "audit", "contact", "ect_case", "msdyn_widget"], suggested.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_tables_neighbours_are_joined_to_it_either_way()
    {
        var options = new ErdOptions(Prefixes: ["ect"]);

        // Contact is only looked up, but a neighbourhood draws it all the same.
        Assert.Equal(["contact", "ect_case"], ErdBuilder.Neighbours("ect_case", Solution(), options).Order(StringComparer.Ordinal));
        Assert.Equal(["contact", "ect_case"], ErdBuilder.Neighbours("contact", Solution(), options).Order(StringComparer.Ordinal));
        Assert.Equal(["account", "contact", "ect_case"],
            ErdBuilder.Neighbours("contact", Solution(), options with { ShowSystemRelationships = true }).Order(StringComparer.Ordinal));
        Assert.Equal(["appointment"], ErdBuilder.Neighbours("appointment", Solution(), options));
    }

    [Theory]
    [InlineData(ErdColumnDetail.None, new string[0])]
    [InlineData(ErdColumnDetail.Custom, new[] { "ect_caseid", "ect_name", "ect_accountid", "ect_contactid", "ect_notes", "ect_parentcaseid" })]
    [InlineData(ErdColumnDetail.All, new[] { "ect_caseid", "ect_name", "createdby", "ect_accountid", "ect_contactid", "ect_notes", "ect_parentcaseid", "ownerid", "statecode" })]
    public void Column_detail_decides_the_rows(ErdColumnDetail detail, string[] expected)
    {
        var rows = Build(new ErdOptions(detail)).Boxes.Single(b => b.LogicalName == "ect_case").Rows;

        Assert.Equal(expected, rows.Select(r => r.Name));
    }

    [Fact]
    public void A_table_outside_lists_no_columns_and_is_titled_by_its_display_name()
    {
        var account = Build(new ErdOptions(ErdColumnDetail.All)).Boxes.Single(b => b.LogicalName == "account");

        Assert.Empty(account.Rows);
        Assert.Equal(("Account", null), (account.Title, account.Subtitle));
    }

    // ---- Mermaid ----

    [Fact]
    public void Mermaid_draws_each_line_with_its_cardinality_and_label()
    {
        var mermaid = ErdMermaidWriter.ToMermaid(Build(), "Core tables");

        Assert.StartsWith("%% Core tables - drawn by PPObjectSearch\nerDiagram\n", mermaid.ReplaceLineEndings("\n"));
        Assert.Contains("    contact ||--o{ ect_case : \"ect_contactid\"", mermaid);
        Assert.Contains("    account |o--o{ ect_case : \"ect_accountid\"", mermaid);
        Assert.Contains("    ect_case }o--o{ contact : \"ect_case_contact_watchers\"", mermaid);
    }

    [Fact]
    public void Mermaid_lists_columns_with_their_type_and_keys()
    {
        var mermaid = ErdMermaidWriter.ToMermaid(Build());

        Assert.Contains("    ect_case {\n        Uniqueidentifier ect_caseid PK\n        String ect_name\n        Lookup ect_accountid FK \"account\"\n",
            mermaid.ReplaceLineEndings("\n"));
        // A table outside is declared by its lines; it has no block.
        Assert.DoesNotContain("account {", mermaid);
    }

    [Fact]
    public void Mermaid_declares_a_table_with_no_lines_and_no_columns_bare()
    {
        var diagram = ErdBuilder.Build([new ErdTable("ect_lonely", "Lonely", [Id("ect_lonelyid")], [], [])], new ErdOptions(ErdColumnDetail.None));

        Assert.Contains("\n    ect_lonely\n", ErdMermaidWriter.ToMermaid(diagram).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Mermaid_names_are_made_safe()
    {
        var diagram = new ErdDiagram([new ErdBox("1 odd.name", "Odd", null, [new ErdRow("col name", "Multi Select", false, false, false)], false)], []);

        var mermaid = ErdMermaidWriter.ToMermaid(diagram);

        Assert.Contains("_1_odd_name {", mermaid);
        Assert.Contains("Multi_Select col_name", mermaid);
    }

    // ---- layout ----

    [Theory]
    [InlineData(ErdLayoutStyle.Grouped)]
    [InlineData(ErdLayoutStyle.Layered)]
    public void Layout_places_every_box_without_overlap_inside_the_diagram(ErdLayoutStyle style)
    {
        var placed = ErdLayout.Arrange(Build(new ErdOptions(ShowSystemRelationships: true, ShowPeople: true)), Measure, "Core", "ECT-Prod", style: style);

        Assert.Equal(5, placed.Boxes.Count);
        foreach (var box in placed.Boxes)
        {
            Assert.InRange(box.X, 0, placed.Width - box.Width);
            Assert.InRange(box.Y, ErdLayout.Margin + 1, placed.Height - box.Height);
            foreach (var other in placed.Boxes.Where(o => o != box))
            {
                var apart = box.X + box.Width <= other.X || other.X + other.Width <= box.X ||
                            box.Y + box.Height <= other.Y || other.Y + other.Height <= box.Y;
                Assert.True(apart, $"{box.Box.LogicalName} overlaps {other.Box.LogicalName}");
            }
        }
    }

    [Fact]
    public void Layered_puts_a_table_looked_up_above_the_table_that_looks_it_up()
    {
        var placed = ErdLayout.Arrange(Build(new ErdOptions(ShowOutsideTables: false)), Measure, style: ErdLayoutStyle.Layered);

        var contact = placed.Boxes.Single(b => b.Box.LogicalName == "contact");
        var @case = placed.Boxes.Single(b => b.Box.LogicalName == "ect_case");
        Assert.True(contact.Y + contact.Height < @case.Y);
    }

    [Fact]
    public void Each_line_runs_from_its_one_end_to_its_many_end_with_its_marks()
    {
        var placed = ErdLayout.Arrange(Build(new ErdOptions(ShowOutsideTables: false)), Measure, style: ErdLayoutStyle.Layered);

        var link = placed.Links.Single(l => l.Link.Label == "ect_contactid");
        var contact = placed.Boxes.Single(b => b.Box.LogicalName == "contact");
        var @case = placed.Boxes.Single(b => b.Box.LogicalName == "ect_case");

        Assert.InRange(link.Path[0].Start.Y, contact.Y + contact.Height - 1, contact.Y + contact.Height + 1);
        Assert.InRange(link.Path[^1].End.Y, @case.Y - 1, @case.Y + 1);

        // Exactly one: two bars. Zero or many: a three-stroke crow's foot and a circle.
        Assert.Equal(5, link.Strokes.Count);
        Assert.Single(link.Circles);
    }

    [Fact]
    public void Layout_draws_a_table_that_looks_itself_up()
    {
        var placed = ErdLayout.Arrange(Build(new ErdOptions(ShowOutsideTables: false)), Measure);

        Assert.NotEmpty(placed.Links.Single(l => l.Link.Label == "ect_parentcaseid").Path);
    }

    [Fact]
    public void Lookups_from_one_table_to_another_are_drawn_as_one_line()
    {
        IReadOnlyList<ErdTable> tables =
        [
            new("ect_mivr", "Visit", [Id("ect_mivrid"), Lookup("ect_tyres", required: true), Lookup("ect_brakes"), Lookup("ect_lights")],
                [new("a", "ect_tyres", "ect_scoredresponse"), new("b", "ect_brakes", "ect_scoredresponse"), new("c", "ect_lights", "ect_scoredresponse")], [], IsCustom: true),
            new("ect_scoredresponse", "Response", [Id("ect_scoredresponseid")], [], [], IsCustom: true)
        ];
        var diagram = ErdBuilder.Build(tables, new ErdOptions(Prefixes: ["ect"], ShowReferenceTables: true));

        var line = Assert.Single(ErdLayout.Arrange(diagram, Measure).Links);

        Assert.Equal(3, line.Merged.Count);
        Assert.Equal("ect_brakes, ect_lights, ect_tyres", line.Link.Label);
        // One of them is optional, so the line says zero or one.
        Assert.Equal(ErdCardinality.ZeroOrOne, line.Link.OneEnd);
        Assert.Equal("ect_mivr looks up ect_scoredresponse in 3 columns: ect_brakes, ect_lights, ect_tyres", ErdSvgWriter.Describe(line));
        // Each is still listed in the box, and in the Mermaid.
        Assert.Equal(3, diagram.Boxes.Single(b => b.LogicalName == "ect_mivr").Rows.Count(r => r.IsForeignKey));
        Assert.Equal(3, ErdMermaidWriter.ToMermaid(diagram).Split('\n').Count(l => l.Contains("--o{")));
    }

    /// <summary>Thirty tables: a hub, ten tables looking it up, each looked up by two more of its own.</summary>
    private static ErdDiagram Large()
    {
        var boxes = new List<ErdBox> { new("hub", "Hub", null, [], false) };
        var links = new List<ErdLink>();
        for (var i = 0; i < 10; i++)
        {
            boxes.Add(new ErdBox($"t{i}", $"Table {i}", null, [], false));
            links.Add(new ErdLink("hub", $"t{i}", ErdCardinality.ZeroOrOne, ErdCardinality.ZeroOrMany, "hubid", false));
            for (var j = 0; j < 2; j++)
            {
                boxes.Add(new ErdBox($"t{i}_{j}", $"Table {i}.{j}", null, [], false));
                links.Add(new ErdLink($"t{i}", $"t{i}_{j}", ErdCardinality.ZeroOrOne, ErdCardinality.ZeroOrMany, "parentid", false));
            }
        }

        return new ErdDiagram(boxes, links);
    }

    [Fact]
    public void Grouped_places_a_large_diagram_the_same_every_time_and_squarer_than_layers()
    {
        var grouped = ErdLayout.Arrange(Large(), Measure);
        var again = ErdLayout.Arrange(Large(), Measure);
        var layered = ErdLayout.Arrange(Large(), Measure, style: ErdLayoutStyle.Layered);

        Assert.Equal(grouped.Boxes.Select(b => (b.X, b.Y)), again.Boxes.Select(b => (b.X, b.Y)));
        Assert.True(grouped.Width / grouped.Height < layered.Width / layered.Height,
            $"grouped {grouped.Width:0}x{grouped.Height:0}, layered {layered.Width:0}x{layered.Height:0}");
    }

    [Fact]
    public void Grouped_lays_a_few_tables_out_in_layers()
    {
        var diagram = Build(new ErdOptions(ShowOutsideTables: false));

        Assert.Equal(ErdLayout.Arrange(diagram, Measure, style: ErdLayoutStyle.Layered).Boxes.Select(b => (b.X, b.Y)),
            ErdLayout.Arrange(diagram, Measure).Boxes.Select(b => (b.X, b.Y)));
    }

    [Fact]
    public void A_box_is_as_wide_as_its_text_within_limits_and_cuts_what_does_not_fit()
    {
        var name = new string('x', 120);
        var diagram = new ErdDiagram(
            [new ErdBox("a", "A", null, [], false), new ErdBox("b", name, "b", [new ErdRow(name, "String", false, false, false)], false)], []);

        var placed = ErdLayout.Arrange(diagram, Measure);

        Assert.Equal(150, placed.Boxes[0].Width);
        var wide = placed.Boxes[1];
        Assert.Equal(440, wide.Width);
        Assert.EndsWith("…", wide.Title);
        Assert.EndsWith("…", wide.Rows[0].Name);
        Assert.True(Measure(wide.Rows[0].Name, ErdLayout.RowSize, false) <= wide.Width - 2 * ErdLayout.Padding);
    }

    [Fact]
    public void An_empty_diagram_is_just_its_heading()
    {
        var placed = ErdLayout.Arrange(new ErdDiagram([], []), Measure, "Nothing");

        Assert.Empty(placed.Boxes);
        Assert.True(placed.Height > ErdLayout.Margin);
    }

    [Fact]
    public void Layout_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ErdLayout.Arrange(Build(), Measure, ct: cts.Token));
    }

    // ---- SVG ----

    [Fact]
    public void Svg_is_well_formed_with_a_box_per_table_and_a_path_per_line()
    {
        var placed = ErdLayout.Arrange(Build(new ErdOptions(ErdColumnDetail.All)), Measure, "Core & more <tables>", "ECT-Prod");

        var svg = XDocument.Parse(ErdSvgWriter.ToSvg(placed));
        XNamespace ns = "http://www.w3.org/2000/svg";

        Assert.Equal(ns + "svg", svg.Root!.Name);
        Assert.Equal(placed.Links.Count, svg.Descendants(ns + "path").Count(p => p.Attribute("fill") is null));
        Assert.Contains(svg.Descendants(ns + "text"), t => t.Value == "Core & more <tables>");
        Assert.Contains(svg.Descendants(ns + "text"), t => t.Value == "→ account");
        Assert.Contains(svg.Descendants(ns + "title"), t => t.Value == "ect_case.ect_accountid looks up account");
        // A many-to-many is dashed.
        Assert.Single(svg.Descendants(ns + "path"), p => p.Attribute("stroke-dasharray") is not null);
    }

    [Fact]
    public void Svg_numbers_are_written_the_same_in_every_culture()
    {
        var placed = ErdLayout.Arrange(Build(), Measure);
        var before = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var invariant = ErdSvgWriter.ToSvg(placed);
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.Equal(invariant, ErdSvgWriter.ToSvg(placed));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }
}
