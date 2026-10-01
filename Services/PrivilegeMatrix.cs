using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>One table's row in a role's privilege grid: how far each of its eight privileges reaches.</summary>
public sealed class PrivilegeMatrixRow
{
    public required string Table { get; init; }

    public PrivilegeDepth Create { get; init; }
    public PrivilegeDepth Read { get; init; }
    public PrivilegeDepth Write { get; init; }
    public PrivilegeDepth Delete { get; init; }
    public PrivilegeDepth Append { get; init; }
    public PrivilegeDepth AppendTo { get; init; }
    public PrivilegeDepth Assign { get; init; }
    public PrivilegeDepth Share { get; init; }

    /// <summary>The widest reach of any of the table's privileges - "Organization" for a role that reads everything.</summary>
    public PrivilegeDepth Widest => new[] { Create, Read, Write, Delete, Append, AppendTo, Assign, Share }.Max();
}

/// <summary>A privilege that is not one of a table's eight: Export to Excel, Bulk delete and the like.</summary>
public sealed record MiscPrivilege(string Name, PrivilegeDepth Depth)
{
    public string DepthLabel => PrivilegeMatrix.DepthLabel(Depth);
}

/// <summary>
/// Turns a role's flat privilege list into the role editor's grid. Table privileges are named
/// prv + action + table schema name - prvAppendToAccount - so the name says both.
/// </summary>
public static class PrivilegeMatrix
{
    // AppendTo before Append, or prvAppendToAccount would read as Append on "ToAccount".
    private static readonly string[] Actions = ["AppendTo", "Append", "Create", "Read", "Write", "Delete", "Assign", "Share"];

    public static (IReadOnlyList<PrivilegeMatrixRow> Tables, IReadOnlyList<MiscPrivilege> Misc) Build(IEnumerable<RolePrivilege> privileges)
    {
        var tables = new Dictionary<string, Dictionary<string, PrivilegeDepth>>(StringComparer.OrdinalIgnoreCase);
        var misc = new List<MiscPrivilege>();

        foreach (var privilege in privileges)
        {
            if (Parse(privilege.Name) is { } parsed)
            {
                if (!tables.TryGetValue(parsed.Table, out var actions))
                {
                    actions = new Dictionary<string, PrivilegeDepth>(StringComparer.Ordinal);
                    tables[parsed.Table] = actions;
                }

                // The same privilege twice (it should not happen) keeps its wider reach.
                actions[parsed.Action] = actions.TryGetValue(parsed.Action, out var existing) && existing > privilege.Depth
                    ? existing
                    : privilege.Depth;
            }
            else
            {
                misc.Add(new MiscPrivilege(Friendly(privilege.Name), privilege.Depth));
            }
        }

        var rows = tables
            .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Select(t => new PrivilegeMatrixRow
            {
                Table = t.Key,
                Create = t.Value.GetValueOrDefault("Create"),
                Read = t.Value.GetValueOrDefault("Read"),
                Write = t.Value.GetValueOrDefault("Write"),
                Delete = t.Value.GetValueOrDefault("Delete"),
                Append = t.Value.GetValueOrDefault("Append"),
                AppendTo = t.Value.GetValueOrDefault("AppendTo"),
                Assign = t.Value.GetValueOrDefault("Assign"),
                Share = t.Value.GetValueOrDefault("Share")
            })
            .ToList();

        return (rows, misc.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>"prvAppendToAccount" is (AppendTo, Account); anything else is not a table privilege.</summary>
    internal static (string Action, string Table)? Parse(string name)
    {
        if (!name.StartsWith("prv", StringComparison.Ordinal)) return null;

        var rest = name[3..];
        foreach (var action in Actions)
        {
            if (rest.StartsWith(action, StringComparison.Ordinal) && rest.Length > action.Length)
            {
                return (action, rest[action.Length..]);
            }
        }

        return null;
    }

    /// <summary>"prvExportToExcel" reads as "ExportToExcel".</summary>
    internal static string Friendly(string name) => name.StartsWith("prv", StringComparison.Ordinal) ? name[3..] : name;

    public static string DepthLabel(PrivilegeDepth depth) => depth switch
    {
        PrivilegeDepth.User => "User",
        PrivilegeDepth.BusinessUnit => "Business unit",
        PrivilegeDepth.ParentChild => "Parent: child BUs",
        PrivilegeDepth.Organization => "Organization",
        _ => "None"
    };
}
