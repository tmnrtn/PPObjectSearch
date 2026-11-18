using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>
/// A role as it reaches a user: its name, the business unit of the copy assigned - where a
/// Business unit or Parent depth reaches from - and the team it comes through, if any.
/// </summary>
public sealed record EffectiveGrant(string Role, string? BusinessUnit, string? Team)
{
    /// <summary>"Salesperson", or "Sales Manager via team EMEA" - as Who can words a path.</summary>
    public string Label => Team is null ? Role : $"{Role} via team {Team}";
}

/// <summary>
/// One cell of an effective matrix: the widest depth, and the roles that grant it. A Business
/// unit or Parent depth reaches from the unit each granting role is held in, so roles held in
/// two units grant it in both.
/// </summary>
public sealed record EffectiveCell(PrivilegeDepth Depth, IReadOnlyList<EffectiveGrant> Grants)
{
    public static readonly EffectiveCell None = new(PrivilegeDepth.None, Array.Empty<EffectiveGrant>());

    public string Label => Depth == PrivilegeDepth.None ? string.Empty : PrivilegeMatrix.DepthLabel(Depth);

    /// <summary>The depth is bounded by a business unit - Business unit or Parent; User and Organization are not.</summary>
    private bool InUnits => Depth is PrivilegeDepth.BusinessUnit or PrivilegeDepth.ParentChild;

    /// <summary>The business units a Business unit or Parent depth covers, as first granted; none for any other depth.</summary>
    public IReadOnlyList<string> BusinessUnits => InUnits
        ? Grants.Select(g => g.BusinessUnit).OfType<string>().Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : Array.Empty<string>();

    /// <summary>The roles granting the depth, each once - with the unit it is held in where the depth reaches from it.</summary>
    public IReadOnlyList<string> GrantedBy => Grants
        .Select(g => InUnits && !string.IsNullOrWhiteSpace(g.BusinessUnit) ? $"{g.Label} in {g.BusinessUnit}" : g.Label)
        .Distinct()
        .ToList();

    public string GrantedByLabel => string.Join(", ", GrantedBy);

    /// <summary>The depth, and for Business unit or Parent the units it reaches from: "Business unit (North; South)".</summary>
    public string ExportLabel => BusinessUnits.Count == 0 ? Label : $"{Label} ({string.Join("; ", BusinessUnits)})";

    /// <summary>
    /// Whose records the depth covers: "records in each of Probation North and Sales", "records
    /// owned by them or team EMEA" - User depth through a team covers what the team owns.
    /// </summary>
    public string Reach
    {
        get
        {
            var units = SecurityLookup.Listed(BusinessUnits);
            var owners = Grants.Where(g => g.Team is null).Select(_ => "them")
                .Concat(Grants.Select(g => g.Team).OfType<string>().Select(t => $"team {t}"))
                .Distinct().ToList();

            return Depth switch
            {
                PrivilegeDepth.User => "records owned by " + SecurityLookup.Listed(owners, "or"),
                PrivilegeDepth.BusinessUnit when BusinessUnits.Count == 0 => "records in their business unit",
                PrivilegeDepth.BusinessUnit when BusinessUnits.Count == 1 => $"records in {units}",
                PrivilegeDepth.BusinessUnit => $"records in each of {units}",
                PrivilegeDepth.ParentChild when BusinessUnits.Count == 0 => "records in their business unit and the units beneath it",
                PrivilegeDepth.ParentChild when BusinessUnits.Count == 1 => $"records in {units} and the units beneath it",
                PrivilegeDepth.ParentChild => $"records in each of {units} and the units beneath them",
                PrivilegeDepth.Organization => "every record in the organization",
                _ => string.Empty
            };
        }
    }

    /// <summary>The cell's hover: the depth and what it reaches, then a line per role granting it. None for an empty cell.</summary>
    public string? ToolTip => Depth == PrivilegeDepth.None
        ? null
        : string.Join(Environment.NewLine, GrantedBy.Prepend($"{Label} - {Reach}"));
}

/// <summary>One table of a user's effective privileges.</summary>
public sealed class EffectiveRow
{
    public required string Table { get; init; }
    public required IReadOnlyDictionary<string, EffectiveCell> Cells { get; init; }

    public EffectiveCell this[string action] => Cells.TryGetValue(action, out var cell) ? cell : EffectiveCell.None;

    public EffectiveCell Create => this["Create"];
    public EffectiveCell Read => this["Read"];
    public EffectiveCell Write => this["Write"];
    public EffectiveCell Delete => this["Delete"];
    public EffectiveCell Append => this["Append"];
    public EffectiveCell AppendTo => this["AppendTo"];
    public EffectiveCell Assign => this["Assign"];
    public EffectiveCell Share => this["Share"];
}

/// <summary>A privilege two roles grant differently.</summary>
public sealed record RoleDifference(string Table, string Action, PrivilegeDepth Left, PrivilegeDepth Right)
{
    public string LeftLabel => PrivilegeMatrix.DepthLabel(Left);
    public string RightLabel => PrivilegeMatrix.DepthLabel(Right);
    public string Privilege => Table.Length == 0 ? Action : $"{Action} {Table}";
}

/// <summary>Someone who can do something, and how they come to.</summary>
public sealed record AccessHolder(string Name, string Kind, PrivilegeDepth Depth, IReadOnlyList<string> Paths)
{
    public string DepthLabel => PrivilegeMatrix.DepthLabel(Depth);
    public string Via => string.Join("; ", Paths);
}

/// <summary>A business unit a user holds roles in: their own or not, directly, and through which teams.</summary>
public sealed record RoleUnit(string Name, bool IsOwn, bool Direct, IReadOnlyList<string> Teams)
{
    /// <summary>"Sales (their own)", "EMEA (direct)", "Probation North (team Probation North)", "Sales (their own; teams A and B)".</summary>
    public string Label
    {
        get
        {
            var how = new List<string>();
            if (IsOwn) how.Add("their own");
            else if (Direct) how.Add("direct");
            if (Teams.Count > 0) how.Add((Teams.Count == 1 ? "team " : "teams ") + SecurityLookup.Listed(Teams));
            return $"{Name} ({string.Join("; ", how)})";
        }
    }
}

/// <summary>A role holder as the lookup needs it: who, and through which team if any.</summary>
public sealed record HeldRole(string RoleName, PrivilegeDepth Depth, string Principal, bool IsTeam, string? BusinessUnit, string? ViaTeam);

/// <summary>The reasoning behind the security lookups - pure, so it can be checked without an environment.</summary>
public static class SecurityLookup
{
    public static readonly string[] Actions = ["Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share"];

    /// <summary>
    /// A user's effective privileges: per table and action, the widest depth any of their roles
    /// grants - directly or through a team - and which roles, held in which business units, grant
    /// that depth.
    /// </summary>
    public static IReadOnlyList<EffectiveRow> Effective(IEnumerable<(EffectiveGrant Grant, IReadOnlyList<RolePrivilege> Privileges)> roles)
    {
        var cells = new Dictionary<string, Dictionary<string, (PrivilegeDepth Depth, List<EffectiveGrant> By)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (grant, privileges) in roles)
        {
            foreach (var privilege in privileges)
            {
                if (PrivilegeMatrix.Parse(privilege.Name) is not { } parsed || privilege.Depth == PrivilegeDepth.None) continue;

                if (!cells.TryGetValue(parsed.Table, out var actions))
                {
                    actions = new Dictionary<string, (PrivilegeDepth, List<EffectiveGrant>)>(StringComparer.Ordinal);
                    cells[parsed.Table] = actions;
                }

                if (!actions.TryGetValue(parsed.Action, out var cell) || privilege.Depth > cell.Depth)
                {
                    actions[parsed.Action] = (privilege.Depth, [grant]);
                }
                else if (privilege.Depth == cell.Depth && !cell.By.Contains(grant))
                {
                    cell.By.Add(grant);
                }
            }
        }

        return cells
            .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Select(t => new EffectiveRow
            {
                Table = t.Key,
                Cells = t.Value.ToDictionary(a => a.Key, a => new EffectiveCell(a.Value.Depth, a.Value.By))
            })
            .ToList();
    }

    /// <summary>
    /// The business units a user holds roles in, and how: their own unit first, then units they
    /// hold roles in directly - modernized business units allow it - then the ones only teams
    /// bring. A role with no unit read is left out.
    /// </summary>
    public static IReadOnlyList<RoleUnit> UnitsHeldIn(IEnumerable<EffectiveGrant> grants, string? ownUnit)
    {
        return grants
            .Where(g => !string.IsNullOrWhiteSpace(g.BusinessUnit))
            .GroupBy(g => g.BusinessUnit!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RoleUnit(g.Key,
                string.Equals(g.Key, ownUnit, StringComparison.OrdinalIgnoreCase),
                g.Any(r => r.Team is null),
                g.Select(r => r.Team).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(u => u.IsOwn)
            .ThenByDescending(u => u.Direct)
            .ThenBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>How many units <see cref="HeldInLabel"/> names before it counts the rest.</summary>
    private const int UnitsNamed = 3;

    /// <summary>
    /// "Roles held in Sales (their own) and Probation North (team Probation North)" - past four
    /// units, the first three and a count. Empty for none.
    /// </summary>
    public static string HeldInLabel(IReadOnlyList<RoleUnit> units)
    {
        if (units.Count == 0) return string.Empty;

        var labels = units.Select(u => u.Label).ToList();
        if (labels.Count > UnitsNamed + 1)
        {
            labels = [.. labels.Take(UnitsNamed), $"{units.Count - UnitsNamed:N0} more"];
        }
        return "Roles held in " + Listed(labels);
    }

    /// <summary>Every unit, one a line, under what they mean - none for no units.</summary>
    public static string? HeldInToolTip(IReadOnlyList<RoleUnit> units) => units.Count == 0
        ? null
        : string.Join(Environment.NewLine, units.Select(u => u.Label)
            .Prepend("Business unit and Parent access reaches from each unit a role is held in:"));

    /// <summary>"A", "A and B", "A, B and C" - or with "or".</summary>
    public static string Listed(IReadOnlyList<string> items, string conjunction = "and") => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} {conjunction} {items[^1]}"
    };

    /// <summary>Every privilege two roles grant at different depths - the second side missing reads as None.</summary>
    public static IReadOnlyList<RoleDifference> Diff(IReadOnlyList<RolePrivilege> left, IReadOnlyList<RolePrivilege> right)
    {
        // Matched without regard to case - environments disagree on a schema name's casing - but
        // shown as first spelled.
        static Dictionary<(string Table, string Action), (string Shown, PrivilegeDepth Depth)> Index(IEnumerable<RolePrivilege> privileges)
        {
            var index = new Dictionary<(string, string), (string, PrivilegeDepth)>();
            foreach (var p in privileges)
            {
                var (table, action) = PrivilegeMatrix.Parse(p.Name) is { } parsed
                    ? (parsed.Table, parsed.Action)
                    : (string.Empty, PrivilegeMatrix.Friendly(p.Name));
                var key = (table.ToLowerInvariant(), action);

                // The deepest grant wins; the spelling stays the first one seen.
                if (!index.TryGetValue(key, out var existing)) index[key] = (table, p.Depth);
                else if (p.Depth > existing.Item2) index[key] = (existing.Item1, p.Depth);
            }
            return index;
        }

        var a = Index(left);
        var b = Index(right);

        return a.Keys.Union(b.Keys)
            .Select(k =>
            {
                var shown = a.TryGetValue(k, out var l) ? l.Shown : b[k].Shown;
                return new RoleDifference(shown, k.Item2,
                    a.TryGetValue(k, out var left) ? left.Depth : PrivilegeDepth.None,
                    b.TryGetValue(k, out var right) ? right.Depth : PrivilegeDepth.None);
            })
            .Where(d => d.Left != d.Right)
            .OrderBy(d => d.Table.Length == 0)
            .ThenBy(d => d.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => Array.IndexOf(Actions, d.Action))
            .ThenBy(d => d.Action, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Everyone who holds a privilege, once each: users directly and through their teams, and the
    /// teams themselves. A user reached several ways keeps the widest depth and every path.
    /// </summary>
    public static IReadOnlyList<AccessHolder> Holders(IEnumerable<HeldRole> held)
    {
        var byPrincipal = new Dictionary<(string Name, bool IsTeam), (PrivilegeDepth Depth, List<string> Paths)>();

        foreach (var h in held)
        {
            var path = (h.ViaTeam is null ? h.RoleName : $"{h.RoleName} via team {h.ViaTeam}") +
                       (string.IsNullOrWhiteSpace(h.BusinessUnit) ? string.Empty : $" in {h.BusinessUnit}");
            var key = (h.Principal, h.IsTeam);

            if (!byPrincipal.TryGetValue(key, out var entry))
            {
                byPrincipal[key] = (h.Depth, [path]);
                continue;
            }

            if (!entry.Paths.Contains(path)) entry.Paths.Add(path);
            if (h.Depth > entry.Depth) byPrincipal[key] = (h.Depth, entry.Paths);
        }

        return byPrincipal
            .Select(p => new AccessHolder(p.Key.Name, p.Key.IsTeam ? "Team" : "User", p.Value.Depth, p.Value.Paths))
            .OrderByDescending(h => h.Depth)
            .ThenBy(h => h.Kind)
            .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
