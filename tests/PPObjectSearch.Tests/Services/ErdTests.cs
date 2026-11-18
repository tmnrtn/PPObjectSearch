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

    private static ErdDiagram Build(ErdOptions? options = null) =>
        ErdBuilder.Build(Tables(), options ?? new ErdOptions(), new Dictionary<string, string?> { ["account"] = "Account" });

    // ---- builder ----

    [Fact]
    public void Lookups_between_the_tables_and_to_tables_outside_are_drawn_from_their_one_end()
    {
        var diagram = Build();

        Assert.Equal(["Case", "Contact", "Account"], diagram.Boxes.Select(b => b.Title));
        Assert.True(diagram.Boxes.Single(b => b.LogicalName == "account").IsOutside);

        var lookups = diagram.Links.Where(l => !l.IsManyToMany).Select(l => $"{l.One}<-{l.Many}.{l.Label}").ToList();
        Assert.Equal(
            ["account<-contact.parentcustomerid", "account<-ect_case.ect_accountid", "contact<-contact.parentcustomerid",
             "contact<-ect_case.ect_contactid", "ect_case<-ect_case.ect_parentcaseid"],
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
    public void System_lookups_are_left_out_unless_asked_for()
    {
        Assert.DoesNotContain(Build().Boxes, b => b.LogicalName == "systemuser");

        var diagram = Build(new ErdOptions(ShowSystemRelationships: true));

        Assert.Contains(diagram.Boxes, b => b.LogicalName == "systemuser" && b.IsOutside);
        Assert.Contains(diagram.Boxes, b => b.LogicalName == "team");
        Assert.Equal(2, diagram.Links.Count(l => l.Label == "ownerid"));
    }

    [Fact]
    public void Without_outside_tables_only_lines_between_the_chosen_tables_are_drawn()
    {
        var diagram = Build(new ErdOptions(ShowOutsideTables: false));

        Assert.Equal(["ect_case", "contact"], diagram.Boxes.Select(b => b.LogicalName));
        Assert.DoesNotContain(diagram.Links, l => l.One == "account");
    }

    [Fact]
    public void A_many_to_many_listed_by_both_its_tables_is_drawn_once()
    {
        var manyToMany = Assert.Single(Build().Links, l => l.IsManyToMany);

        Assert.Equal(("ect_case", "contact", "ect_case_contact_watchers"), (manyToMany.One, manyToMany.Many, manyToMany.Label));
        Assert.Equal(ErdCardinality.ZeroOrMany, manyToMany.OneEnd);
    }

    [Fact]
    public void Keys_list_the_primary_key_the_primary_name_and_the_lookups_drawn()
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
        var row = Build().Boxes.Single(b => b.LogicalName == "contact").Rows.Single(r => r.Name == "parentcustomerid");

        Assert.Equal("account, contact", row.References);
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

    [Fact]
    public void Layout_places_every_box_without_overlap_inside_the_diagram()
    {
        var placed = ErdLayout.Arrange(Build(new ErdOptions(ShowSystemRelationships: true)), Measure, "Core", "ECT-Prod");

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
    public void Layout_puts_a_table_looked_up_above_the_table_that_looks_it_up()
    {
        var placed = ErdLayout.Arrange(Build(new ErdOptions(ShowOutsideTables: false)), Measure);

        var contact = placed.Boxes.Single(b => b.Box.LogicalName == "contact");
        var @case = placed.Boxes.Single(b => b.Box.LogicalName == "ect_case");
        Assert.True(contact.Y + contact.Height < @case.Y);
    }

    [Fact]
    public void Each_line_runs_from_its_one_end_to_its_many_end_with_its_marks()
    {
        var placed = ErdLayout.Arrange(Build(new ErdOptions(ShowOutsideTables: false)), Measure);

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
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var svg = ErdSvgWriter.ToSvg(placed);

            Assert.DoesNotMatch("[0-9],[0-9]+\"", svg.Replace("\" ", "\""));
            XDocument.Parse(svg);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }
}
