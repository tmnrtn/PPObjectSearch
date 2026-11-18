using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>How much of each table an entity relationship diagram lists.</summary>
public enum ErdColumnDetail
{
    /// <summary>The table's name alone.</summary>
    None,

    /// <summary>The primary key, the primary name and the lookups drawn.</summary>
    Keys,

    /// <summary>The keys, and every custom column.</summary>
    Custom,

    /// <summary>Every column, system ones included.</summary>
    All
}

/// <summary>What an entity relationship diagram shows besides the tables chosen.</summary>
public sealed record ErdOptions(
    ErdColumnDetail Columns = ErdColumnDetail.Keys,
    bool ShowOutsideTables = true,
    bool ShowSystemRelationships = false);

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
/// them that their custom lookups point at.
///
/// The platform's own relationships are left out unless asked for: the lookups every table has
/// (owner, created by), lookups that can point at almost any table (an activity's regarding,
/// an audit's object), and an out-of-box table's own lookups and many-to-many relationships to
/// tables outside - an account's territory, SLA or price list. They are true, but drawn they bury
/// the relationships that make this data model its own.
/// </summary>
public static class ErdBuilder
{
    /// <summary>
    /// The lookups every table has - who created and changed a row, who owns it, its currency,
    /// its business process stage. Drawn, they tie every table to systemuser and businessunit and
    /// bury the relationships that make this data model its own.
    /// </summary>
    /// <summary>Past this many tables, an out-of-box lookup is the platform's plumbing - regarding, an audit's object.</summary>
    internal const int MaxPlatformTargets = 3;

    internal static readonly IReadOnlySet<string> SystemLookups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "createdby", "createdonbehalfby", "modifiedby", "modifiedonbehalfby",
        "createdbyexternalparty", "modifiedbyexternalparty",
        "ownerid", "owninguser", "owningteam", "owningbusinessunit", "organizationid",
        "transactioncurrencyid", "stageid", "processid", "slaid", "slainvokedid"
    };

    public static ErdDiagram Build(
        IReadOnlyList<ErdTable> tables,
        ErdOptions options,
        IReadOnlyDictionary<string, string?>? displayNames = null)
    {
        var chosen = tables
            .GroupBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(t => t.LogicalName, StringComparer.OrdinalIgnoreCase);

        var outside = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<ErdLink>();

        // A lookup's drawn targets, by table and column - a customer lookup can reach two.
        var drawn = new Dictionary<(string Table, string Column), List<string>>();

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
            var targetCounts = table.Lookups.GroupBy(l => l.Column, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var lookup in table.Lookups.OrderBy(l => l.Column, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Target, StringComparer.OrdinalIgnoreCase))
            {
                var isCustom = custom.Contains(lookup.Column);
                if (!options.ShowSystemRelationships)
                {
                    if (IsSystemLookup(lookup.Column, isCustom, targetCounts[lookup.Column])) continue;
                    if (!isCustom && !chosen.ContainsKey(lookup.Target)) continue;
                }

                if (!Reaches(lookup.Target)) continue;

                var target = chosen.TryGetValue(lookup.Target, out var t) ? t.LogicalName : lookup.Target.ToLowerInvariant();
                links.Add(new ErdLink(
                    target, table.LogicalName,
                    required.Contains(lookup.Column) ? ErdCardinality.ExactlyOne : ErdCardinality.ZeroOrOne,
                    ErdCardinality.ZeroOrMany, lookup.Column, IsManyToMany: false));

                var key = (table.LogicalName, lookup.Column.ToLowerInvariant());
                if (!drawn.TryGetValue(key, out var targets)) drawn[key] = targets = new List<string>();
                targets.Add(target);
            }
        }

        // Each end of a many-to-many lists it; draw it once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in chosen.Values.OrderBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var relationship in table.ManyToMany.OrderBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Contains(relationship.SchemaName)) continue;
                if (!options.ShowSystemRelationships && !relationship.IsCustom &&
                    !(chosen.ContainsKey(relationship.Table1) && chosen.ContainsKey(relationship.Table2))) continue;
                if (!Reaches(relationship.Table1) || !Reaches(relationship.Table2)) continue;

                seen.Add(relationship.SchemaName);
                links.Add(new ErdLink(
                    NameOf(relationship.Table1), NameOf(relationship.Table2),
                    ErdCardinality.ZeroOrMany, ErdCardinality.ZeroOrMany, relationship.SchemaName, IsManyToMany: true));
            }
        }

        // An outside table reached only by a relationship skipped above has no line; leave it out.
        var linked = links.SelectMany(l => new[] { l.One, l.Many }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        outside.RemoveWhere(t => !linked.Contains(t));

        var boxes = new List<ErdBox>();
        foreach (var table in chosen.Values.OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase))
        {
            boxes.Add(new ErdBox(table.LogicalName, table.Label, Subtitle(table.Label, table.LogicalName),
                Rows(table, options.Columns, drawn), IsOutside: false));
        }

        foreach (var name in outside)
        {
            var title = displayNames?.GetValueOrDefault(name) is { Length: > 0 } display ? display : name;
            boxes.Add(new ErdBox(name.ToLowerInvariant(), title, Subtitle(title, name), [], IsOutside: true));
        }

        return new ErdDiagram(boxes, links);

        string NameOf(string table) => chosen.TryGetValue(table, out var t) ? t.LogicalName : table.ToLowerInvariant();
    }

    /// <summary>A lookup every table has, or an out-of-box one that can point at almost any table.</summary>
    internal static bool IsSystemLookup(string column, bool isCustom, int targets) =>
        SystemLookups.Contains(column) || (!isCustom && targets > MaxPlatformTargets);

    /// <summary>
    /// The tables worth drawing by default: the solutions' own - named with their publishers'
    /// prefixes - the out-of-box tables they add columns to, and the tables their own lookups
    /// point at. An out-of-box table a solution holds only for a form or a view is left out.
    /// With no prefix known, custom tables and columns stand in for the solutions' own.
    /// </summary>
    public static IReadOnlySet<string> Suggested(IReadOnlyList<ErdTable> tables, IReadOnlyCollection<string> prefixes)
    {
        bool Own(string name, bool isCustom) => prefixes.Count == 0
            ? isCustom
            : prefixes.Any(p => name.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase));

        var names = tables.Select(t => t.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables.Where(t => !t.IsIntersect))
        {
            if (Own(table.LogicalName, table.IsCustom) || table.Columns.Any(c => Own(c.LogicalName, c.IsCustom))) suggested.Add(table.LogicalName);
        }

        foreach (var table in tables.Where(t => suggested.Contains(t.LogicalName)).ToList())
        {
            var own = table.Columns.Where(c => Own(c.LogicalName, c.IsCustom)).Select(c => c.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var lookup in table.Lookups.Where(l => own.Contains(l.Column) && names.Contains(l.Target)))
            {
                suggested.Add(names.First(n => string.Equals(n, lookup.Target, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return suggested;
    }

    /// <summary>
    /// A table and the tables joined to it either way by a relationship the diagram would draw -
    /// to narrow a large diagram to one table's neighbourhood.
    /// </summary>
    public static IReadOnlySet<string> Neighbours(string table, IReadOnlyList<ErdTable> tables, bool showSystemRelationships = false)
    {
        var diagram = Build(tables, new ErdOptions(ErdColumnDetail.None, ShowOutsideTables: false, showSystemRelationships));
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
    private static IReadOnlyList<ErdRow> Rows(ErdTable table, ErdColumnDetail detail, Dictionary<(string Table, string Column), List<string>> drawn)
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
                _ => column.IsPrimaryId || column.IsPrimaryName || targets is not null
            };
            if (!shown) continue;

            rows.Add(new ErdRow(column.LogicalName, column.Type, column.IsPrimaryId, targets is not null, column.IsRequired,
                targets is null ? null : string.Join(", ", targets.Distinct(StringComparer.OrdinalIgnoreCase))));
        }

        return rows;
    }
}
