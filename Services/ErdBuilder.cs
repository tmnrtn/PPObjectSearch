using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>How much of each table an entity relationship diagram lists.</summary>
public enum ErdColumnDetail
{
    /// <summary>The table's name alone.</summary>
    None,

    /// <summary>The primary key, the primary name and the lookups drawn as lines.</summary>
    Drawn,

    /// <summary>The primary key, the primary name and every lookup - those to tables left out too.</summary>
    Keys,

    /// <summary>The keys, and every custom column.</summary>
    Custom,

    /// <summary>Every column, system ones included.</summary>
    All
}

/// <summary>What an entity relationship diagram shows besides the tables chosen.</summary>
/// <param name="ShowSystemRelationships">The platform's relationships too, not only those the solutions' publishers made.</param>
/// <param name="ShowPeople">Users, teams and business units, and the lines to them.</param>
/// <param name="ShowReferenceTables">Tables that are only looked up - types, codes, statuses - and the lines to them.</param>
/// <param name="Prefixes">The solutions' publisher prefixes, which name what they made; with none, custom stands in.</param>
/// <param name="ShowStandalone">Chosen tables with no line to another - unless none has one, when all are drawn.</param>
public sealed record ErdOptions(
    ErdColumnDetail Columns = ErdColumnDetail.Drawn,
    bool ShowOutsideTables = true,
    bool ShowSystemRelationships = false,
    bool ShowPeople = false,
    IReadOnlyCollection<string>? Prefixes = null,
    bool ShowReferenceTables = false,
    bool ShowStandalone = false)
{
    /// <summary>Made by one of the solutions' publishers: named with its prefix - or, with no prefix known, custom.</summary>
    public bool IsOwn(string name, bool isCustom) => Prefixes is not { Count: > 0 } prefixes
        ? isCustom
        : prefixes.Any(p => name.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One end of a relationship: how many rows of that table take part.</summary>
public enum ErdCardinality
{
    ZeroOrOne,
    ExactlyOne,
    ZeroOrMany
}

/// <summary>A row in a table's box: a column, marked PK or FK, and what a lookup points at.</summary>
/// <param name="References">The tables a lookup is drawn to, comma-separated; null for any other column.</param>
public sealed record ErdRow(string Name, string Type, bool IsPrimaryKey, bool IsForeignKey, bool IsRequired, string? References = null)
{
    public string Marker => IsPrimaryKey ? "PK" : IsForeignKey ? "FK" : string.Empty;
}

/// <summary>A table's box. One outside the solutions is named, but lists no columns.</summary>
public sealed record ErdBox(string LogicalName, string Title, string? Subtitle, IReadOnlyList<ErdRow> Rows, bool IsOutside);

/// <summary>
/// A line between two boxes. For a lookup, <see cref="One"/> is the table looked up and
/// <see cref="Many"/> the table holding the lookup; a many-to-many has many at both ends.
/// </summary>
public sealed record ErdLink(string One, string Many, ErdCardinality OneEnd, ErdCardinality ManyEnd, string Label, bool IsManyToMany);

/// <summary>What the diagram holds: the boxes and the lines between them, before they are placed.</summary>
public sealed record ErdDiagram(IReadOnlyList<ErdBox> Boxes, IReadOnlyList<ErdLink> Links);

/// <summary>
/// Turns the chosen tables' metadata into a diagram: a box per table, a line per lookup and
/// many-to-many relationship between them, and - if asked for - a bare box for each table outside
/// them that their lookups point at.
///
/// By default only the relationships the solutions' publishers made are drawn - lookup columns and
/// many-to-many relationships named with their prefixes. The platform's own (owner, created by, an
/// activity's regarding, an account's parent, a contact's merged-into, Microsoft's apps' columns)
/// are true, but drawn they bury the data model the solutions make. So are lines to users, teams
/// and business units, which nearly every table has: those lookups are listed in their boxes,
/// naming the table, without a line.
/// </summary>
public static class ErdBuilder
{
    /// <summary>Users, teams and business units - left out unless asked for.</summary>
    internal static readonly IReadOnlySet<string> People = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "systemuser", "team", "businessunit"
    };

    public static ErdDiagram Build(
        IReadOnlyList<ErdTable> tables,
        ErdOptions options,
        IReadOnlyDictionary<string, string?>? displayNames = null)
    {
        var chosen = tables
            .Where(t => options.ShowPeople || !People.Contains(t.LogicalName))
            .GroupBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(t => t.LogicalName, StringComparer.OrdinalIgnoreCase);

        var outside = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<ErdLink>();

        // A lookup's targets, by table and column - a customer lookup can reach two. A lookup
        // to a table left out is listed in its box without a line.
        var drawn = new Dictionary<(string Table, string Column), List<string>>();

        void Refer(string table, string column, string target)
        {
            var key = (table, column.ToLowerInvariant());
            if (!drawn.TryGetValue(key, out var targets)) drawn[key] = targets = new List<string>();
            targets.Add(target);
        }

        bool Reaches(string table)
        {
            if (chosen.ContainsKey(table)) return true;
            if (!options.ShowOutsideTables) return false;

            outside.Add(table);
            return true;
        }

        foreach (var table in chosen.Values.OrderBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            var required = table.Columns.Where(c => c.IsRequired).Select(c => c.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var custom = table.Columns.Where(c => c.IsCustom).Select(c => c.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var lookup in table.Lookups.OrderBy(l => l.Column, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Target, StringComparer.OrdinalIgnoreCase))
            {
                if (!options.ShowSystemRelationships && !options.IsOwn(lookup.Column, custom.Contains(lookup.Column))) continue;

                var target = chosen.TryGetValue(lookup.Target, out var t) ? t.LogicalName : lookup.Target.ToLowerInvariant();
                Refer(table.LogicalName, lookup.Column, target);
                if (!options.ShowPeople && People.Contains(target)) continue;
                if (!Reaches(target)) continue;

                links.Add(new ErdLink(
                    target, table.LogicalName,
                    required.Contains(lookup.Column) ? ErdCardinality.ExactlyOne : ErdCardinality.ZeroOrOne,
                    ErdCardinality.ZeroOrMany, lookup.Column, IsManyToMany: false));
            }
        }

        // Each end of a many-to-many lists it; draw it once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in chosen.Values.OrderBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var relationship in table.ManyToMany.OrderBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Contains(relationship.SchemaName)) continue;
                if (!options.ShowSystemRelationships && !options.IsOwn(relationship.SchemaName, relationship.IsCustom)) continue;
                if (!options.ShowPeople && (People.Contains(relationship.Table1) || People.Contains(relationship.Table2))) continue;
                if (!Reaches(relationship.Table1) || !Reaches(relationship.Table2)) continue;

                seen.Add(relationship.SchemaName);
                links.Add(new ErdLink(
                    NameOf(relationship.Table1), NameOf(relationship.Table2),
                    ErdCardinality.ZeroOrMany, ErdCardinality.ZeroOrMany, relationship.SchemaName, IsManyToMany: true));
            }
        }

        // A reference table - a type, code or status that other tables look up and that looks up
        // nothing itself - is listed in the boxes that look it up rather than drawn: a solution's
        // thirty such tables would otherwise stand in a row across the top of the diagram.
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!options.ShowReferenceTables)
        {
            var starts = links.Where(l => l.IsManyToMany || !string.Equals(l.One, l.Many, StringComparison.OrdinalIgnoreCase))
                .SelectMany(l => l.IsManyToMany ? new[] { l.One, l.Many } : [l.Many])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            references.UnionWith(links.Where(l => !l.IsManyToMany && chosen.ContainsKey(l.One) && !starts.Contains(l.One)).Select(l => l.One));
            links.RemoveAll(l => references.Contains(l.One));
        }

        // An outside table reached only by a relationship skipped above has no line; leave it out.
        var linked = links.SelectMany(l => new[] { l.One, l.Many }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        outside.RemoveWhere(t => !linked.Contains(t));

        // A table with no line to another says nothing about the relationships - unless none has
        // one, when the tables are all there is to draw.
        var between = links.Where(l => !string.Equals(l.One, l.Many, StringComparison.OrdinalIgnoreCase)).ToList();
        var related = between.SelectMany(l => new[] { l.One, l.Many }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var standalone = options.ShowStandalone || between.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : chosen.Keys.Where(t => !related.Contains(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        links.RemoveAll(l => standalone.Contains(l.One));

        // The lookups drawn as lines, by table and column, for the rows that list only those.
        var lines = links.Where(l => !l.IsManyToMany).Select(l => (l.Many.ToLowerInvariant(), l.Label.ToLowerInvariant())).ToHashSet();

        var boxes = new List<ErdBox>();
        foreach (var table in chosen.Values.OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            if (references.Contains(table.LogicalName) || standalone.Contains(table.LogicalName)) continue;

            boxes.Add(new ErdBox(table.LogicalName, table.Label, Subtitle(table.Label, table.LogicalName),
                Rows(table, options.Columns, drawn, lines), IsOutside: false));
        }

        foreach (var name in outside)
        {
            var title = displayNames?.GetValueOrDefault(name) is { Length: > 0 } display ? display : name;
            boxes.Add(new ErdBox(name.ToLowerInvariant(), title, Subtitle(title, name), [], IsOutside: true));
        }

        return new ErdDiagram(boxes, links);

        string NameOf(string table) => chosen.TryGetValue(table, out var t) ? t.LogicalName : table.ToLowerInvariant();
    }

    /// <summary>
    /// The tables worth drawing by default: the solutions' own - named with their publishers'
    /// prefixes - the out-of-box tables they add columns to, and the tables their own lookups
    /// point at. An out-of-box table a solution holds only for a form or a view is left out, as
    /// are users, teams and business units. With no prefix known, custom stands in for own.
    /// </summary>
    public static IReadOnlySet<string> Suggested(IReadOnlyList<ErdTable> tables, IReadOnlyCollection<string> prefixes)
    {
        var options = new ErdOptions(Prefixes: prefixes);
        var names = tables.Select(t => t.LogicalName).Where(n => !People.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables.Where(t => !t.IsIntersect && names.Contains(t.LogicalName)))
        {
            if (options.IsOwn(table.LogicalName, table.IsCustom) || table.Columns.Any(c => options.IsOwn(c.LogicalName, c.IsCustom)))
            {
                suggested.Add(table.LogicalName);
            }
        }

        foreach (var table in tables.Where(t => suggested.Contains(t.LogicalName)).ToList())
        {
            var own = table.Columns.Where(c => options.IsOwn(c.LogicalName, c.IsCustom)).Select(c => c.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var lookup in table.Lookups.Where(l => own.Contains(l.Column) && names.Contains(l.Target)))
            {
                suggested.Add(names.First(n => string.Equals(n, lookup.Target, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return suggested;
    }

    /// <summary>
    /// A table and the tables joined to it either way by a relationship the diagram would draw,
    /// with these options - to narrow a large diagram to one table's neighbourhood.
    /// </summary>
    public static IReadOnlySet<string> Neighbours(string table, IReadOnlyList<ErdTable> tables, ErdOptions options)
    {
        var diagram = Build(tables, options with { Columns = ErdColumnDetail.None, ShowOutsideTables = false, ShowReferenceTables = true, ShowStandalone = true });
        var neighbours = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table };

        foreach (var link in diagram.Links)
        {
            if (string.Equals(link.One, table, StringComparison.OrdinalIgnoreCase)) neighbours.Add(link.Many);
            else if (string.Equals(link.Many, table, StringComparison.OrdinalIgnoreCase)) neighbours.Add(link.One);
        }

        return neighbours;
    }

    /// <summary>The logical name under the display name, unless it would say the same again.</summary>
    private static string? Subtitle(string title, string logicalName) =>
        string.Equals(title, logicalName, StringComparison.OrdinalIgnoreCase) ? null : logicalName;

    /// <summary>The primary key, then the primary name, then the rest by logical name.</summary>
    private static IReadOnlyList<ErdRow> Rows(ErdTable table, ErdColumnDetail detail,
        Dictionary<(string Table, string Column), List<string>> drawn, HashSet<(string Table, string Column)> lines)
    {
        if (detail == ErdColumnDetail.None) return [];

        var rows = new List<ErdRow>();
        foreach (var column in table.Columns
                     .OrderByDescending(c => c.IsPrimaryId)
                     .ThenByDescending(c => c.IsPrimaryName)
                     .ThenBy(c => c.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            var targets = drawn.GetValueOrDefault((table.LogicalName, column.LogicalName.ToLowerInvariant()));
            var shown = detail switch
            {
                ErdColumnDetail.All => true,
                ErdColumnDetail.Custom => column.IsPrimaryId || column.IsPrimaryName || targets is not null || column.IsCustom,
                ErdColumnDetail.Keys => column.IsPrimaryId || column.IsPrimaryName || targets is not null,
                _ => column.IsPrimaryId || column.IsPrimaryName ||
                     lines.Contains((table.LogicalName.ToLowerInvariant(), column.LogicalName.ToLowerInvariant()))
            };
            if (!shown) continue;

            rows.Add(new ErdRow(column.LogicalName, column.Type, column.IsPrimaryId, targets is not null, column.IsRequired,
                targets is null ? null : string.Join(", ", targets.Distinct(StringComparer.OrdinalIgnoreCase))));
        }

        return rows;
    }
}
