using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>One cell of an effective matrix: the widest depth, and the roles that grant it.</summary>
public sealed record EffectiveCell(PrivilegeDepth Depth, IReadOnlyList<string> GrantedBy)
{
    public static readonly EffectiveCell None = new(PrivilegeDepth.None, Array.Empty<string>());

    public string Label => Depth == PrivilegeDepth.None ? string.Empty : PrivilegeMatrix.DepthLabel(Depth);
    public string GrantedByLabel => string.Join(", ", GrantedBy);
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

/// <summary>A role holder as the lookup needs it: who, and through which team if any.</summary>
public sealed record HeldRole(string RoleName, PrivilegeDepth Depth, string Principal, bool IsTeam, string? BusinessUnit, string? ViaTeam);

/// <summary>The reasoning behind the security lookups - pure, so it can be checked without an environment.</summary>
public static class SecurityLookup
{
    public static readonly string[] Actions = ["Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share"];

    /// <summary>
    /// A user's effective privileges: per table and action, the widest depth any of their roles
    /// grants - directly or through a team - and which roles grant that depth.
    /// </summary>
    public static IReadOnlyList<EffectiveRow> Effective(IEnumerable<(string Role, IReadOnlyList<RolePrivilege> Privileges)> roles)
    {
        var cells = new Dictionary<string, Dictionary<string, (PrivilegeDepth Depth, List<string> By)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (role, privileges) in roles)
        {
            foreach (var privilege in privileges)
            {
                if (PrivilegeMatrix.Parse(privilege.Name) is not { } parsed || privilege.Depth == PrivilegeDepth.None) continue;

                if (!cells.TryGetValue(parsed.Table, out var actions))
                {
                    actions = new Dictionary<string, (PrivilegeDepth, List<string>)>(StringComparer.Ordinal);
                    cells[parsed.Table] = actions;
                }

                if (!actions.TryGetValue(parsed.Action, out var cell) || privilege.Depth > cell.Depth)
                {
                    actions[parsed.Action] = (privilege.Depth, [role]);
                }
                else if (privilege.Depth == cell.Depth && !cell.By.Contains(role))
                {
                    cell.By.Add(role);
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
