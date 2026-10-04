using PPObjectSearch.Models;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Services;

/// <summary>
/// What is missing from one side of two component lists. Objects are matched on object id first,
/// since solution deployment preserves ids, and fall back to type plus name for anything created
/// independently in each environment.
/// </summary>
public static class ComponentDiff
{
    public static List<CompareRow> Diff(IEnumerable<SolutionComponentItem> left, IEnumerable<SolutionComponentItem> right)
    {
        var rows = new List<CompareRow>();
        var rightList = right.ToList();

        var rightById = new Dictionary<Guid, SolutionComponentItem>();
        var rightByName = new Dictionary<(int, string), SolutionComponentItem>();

        foreach (var item in rightList)
        {
            if (item.ObjectId != Guid.Empty) rightById[item.ObjectId] = item;
            rightByName[(item.ComponentType, item.PrimaryLabel.ToLowerInvariant())] = item;
        }

        var matchedRight = new HashSet<SolutionComponentItem>();

        foreach (var item in left)
        {
            var match = FindMatch(item, rightById, rightByName);

            if (match is null)
            {
                rows.Add(Row(item, null, CompareStatus.OnlyInLeft));
                continue;
            }

            matchedRight.Add(match);
            rows.Add(Row(item, match, CompareStatus.Same));
        }

        foreach (var item in rightList.Where(i => !matchedRight.Contains(i)))
        {
            rows.Add(Row(null, item, CompareStatus.OnlyInRight));
        }

        rows.Sort((a, b) =>
        {
            var byStatus = a.Status.CompareTo(b.Status);
            if (byStatus != 0) return byStatus;

            var byType = string.Compare(a.ComponentTypeName, b.ComponentTypeName, StringComparison.CurrentCultureIgnoreCase);
            return byType != 0 ? byType : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        return rows;
    }

    private static SolutionComponentItem? FindMatch(
        SolutionComponentItem item,
        Dictionary<Guid, SolutionComponentItem> byId,
        Dictionary<(int, string), SolutionComponentItem> byName)
    {
        if (item.ObjectId != Guid.Empty && byId.TryGetValue(item.ObjectId, out var byIdMatch)) return byIdMatch;

        return byName.TryGetValue((item.ComponentType, item.PrimaryLabel.ToLowerInvariant()), out var byNameMatch)
            ? byNameMatch
            : null;
    }

    private static CompareRow Row(SolutionComponentItem? left, SolutionComponentItem? right, CompareStatus status)
    {
        var source = left ?? right!;

        return new CompareRow
        {
            Name = source.PrimaryLabel,
            ComponentTypeName = source.ComponentTypeName,
            SubType = source.SubType,
            Status = status,
            LeftModified = left?.ModifiedOn,
            RightModified = right?.ModifiedOn,
            Link = source,
            Left = left,
            Right = right
        };
    }
}
