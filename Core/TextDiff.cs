using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PPObjectSearch.Core;

public enum DiffKind
{
    Unchanged,
    Added,
    Removed,
    Modified
}

/// <summary>A stretch of one line, flagged when it is part of what differs from the other side.</summary>
public sealed record DiffRun(string Text, bool Changed);

/// <summary>
/// One aligned pair of lines. <see cref="Left"/> or <see cref="Right"/> is null where that side
/// has no line at all, which is what keeps the two panes lined up against each other.
/// </summary>
public sealed class DiffRow
{
    public required DiffKind Kind { get; init; }
    public IReadOnlyList<DiffRun>? Left { get; init; }
    public IReadOnlyList<DiffRun>? Right { get; init; }
}

/// <summary>
/// Line-level diff with word-level highlighting inside the lines that pair up. Solution layer
/// values are frequently one long JSON or XML string, so callers are expected to run values
/// through <see cref="Prettify"/> first - a diff of two 4000-character lines tells you nothing.
/// </summary>
public static class TextDiff
{
    /// <summary>The line-alignment table is O(n*m), so past this size the cheap index-wise
    /// pairing below is used instead. It is worse, but it returns.</summary>
    private const int MaxLinesForAlignment = 1200;

    /// <summary>Same tradeoff one level down: a line with more tokens than this is highlighted
    /// whole rather than word by word.</summary>
    private const int MaxTokensForWordDiff = 800;

    /// <summary>
    /// Whitespace, words, then single punctuation characters. Splitting punctuation off on its own
    /// is what keeps a changed value from dragging its surrounding quotes, colons and commas into
    /// the highlight - in JSON and XML values that is most of the line.
    /// </summary>
    private static readonly Regex Tokenizer = new(@"\s+|\w+|[^\w\s]", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static IReadOnlyList<DiffRow> Compare(string? before, string? after)
    {
        var left = SplitLines(before);
        var right = SplitLines(after);

        if (left.Length == 0 && right.Length == 0) return Array.Empty<DiffRow>();
        if (left.Length == 0) return right.Select(AddedRow).ToList();
        if (right.Length == 0) return left.Select(RemovedRow).ToList();

        return left.Length > MaxLinesForAlignment || right.Length > MaxLinesForAlignment
            ? PairByIndex(left, right)
            : Align(left, right);
    }

    /// <summary>
    /// Indents JSON or XML so a value diffs line by line instead of as one unreadable line.
    /// Anything else is passed through untouched.
    /// </summary>
    public static string Prettify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;

        var trimmed = value.Trim();

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return JsonSerializer.Serialize(doc.RootElement, Indented);
            }
            catch (JsonException)
            {
                return value;
            }
        }

        if (trimmed.StartsWith('<'))
        {
            try
            {
                return XElement.Parse(trimmed).ToString();
            }
            catch (System.Xml.XmlException)
            {
                return value;
            }
        }

        return value;
    }

    private static string[] SplitLines(string? text)
        => string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Replace("\r\n", "\n").Split('\n');

    private static DiffRow AddedRow(string line)
        => new() { Kind = DiffKind.Added, Right = new[] { new DiffRun(line, true) } };

    private static DiffRow RemovedRow(string line)
        => new() { Kind = DiffKind.Removed, Left = new[] { new DiffRun(line, true) } };

    private static DiffRow UnchangedRow(string line)
        => new()
        {
            Kind = DiffKind.Unchanged,
            Left = new[] { new DiffRun(line, false) },
            Right = new[] { new DiffRun(line, false) }
        };

    /// <summary>Fallback for values too large to align properly - lines are matched by position.</summary>
    private static IReadOnlyList<DiffRow> PairByIndex(string[] left, string[] right)
    {
        var rows = new List<DiffRow>(Math.Max(left.Length, right.Length));

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (i >= left.Length) rows.Add(AddedRow(right[i]));
            else if (i >= right.Length) rows.Add(RemovedRow(left[i]));
            else if (string.Equals(left[i], right[i], StringComparison.Ordinal)) rows.Add(UnchangedRow(left[i]));
            else rows.Add(ModifiedRow(left[i], right[i]));
        }

        return rows;
    }

    private static IReadOnlyList<DiffRow> Align(string[] left, string[] right)
    {
        var lcs = BuildLcsTable(left, right);
        var rows = new List<DiffRow>();

        // Removals and insertions are buffered so that a run of each can be paired up into
        // Modified rows - that pairing is what makes a word-level diff possible at all.
        var removed = new List<string>();
        var added = new List<string>();

        void Flush()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                if (i >= removed.Count) rows.Add(AddedRow(added[i]));
                else if (i >= added.Count) rows.Add(RemovedRow(removed[i]));
                else rows.Add(ModifiedRow(removed[i], added[i]));
            }

            removed.Clear();
            added.Clear();
        }

        int x = 0, y = 0;

        while (x < left.Length && y < right.Length)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                Flush();
                rows.Add(UnchangedRow(left[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                removed.Add(left[x]);
                x++;
            }
            else
            {
                added.Add(right[y]);
                y++;
            }
        }

        while (x < left.Length) removed.Add(left[x++]);
        while (y < right.Length) added.Add(right[y++]);
        Flush();

        return rows;
    }

    /// <summary>Longest-common-subsequence lengths, filled from the end so it can be walked forwards.</summary>
    private static int[,] BuildLcsTable(string[] left, string[] right)
    {
        var table = new int[left.Length + 1, right.Length + 1];

        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        return table;
    }

    private static DiffRow ModifiedRow(string before, string after)
    {
        var (left, right) = WordDiff(before, after);
        return new DiffRow { Kind = DiffKind.Modified, Left = left, Right = right };
    }

    /// <summary>
    /// Marks the words that actually differ within a pair of lines, so a one-word change in a
    /// long line does not read as though the whole line was rewritten.
    /// </summary>
    private static (IReadOnlyList<DiffRun> Left, IReadOnlyList<DiffRun> Right) WordDiff(string before, string after)
    {
        var left = Tokenize(before);
        var right = Tokenize(after);

        if (left.Length > MaxTokensForWordDiff || right.Length > MaxTokensForWordDiff)
        {
            return (new[] { new DiffRun(before, true) }, new[] { new DiffRun(after, true) });
        }

        var lcs = BuildLcsTable(left, right);
        var leftRuns = new List<DiffRun>();
        var rightRuns = new List<DiffRun>();

        int x = 0, y = 0;

        while (x < left.Length && y < right.Length)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                leftRuns.Add(new DiffRun(left[x], false));
                rightRuns.Add(new DiffRun(right[y], false));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                leftRuns.Add(new DiffRun(left[x], true));
                x++;
            }
            else
            {
                rightRuns.Add(new DiffRun(right[y], true));
                y++;
            }
        }

        while (x < left.Length) leftRuns.Add(new DiffRun(left[x++], true));
        while (y < right.Length) rightRuns.Add(new DiffRun(right[y++], true));

        return (Merge(leftRuns), Merge(rightRuns));
    }

    private static string[] Tokenize(string line)
        => Tokenizer.Matches(line).Select(m => m.Value).ToArray();

    /// <summary>Collapses neighbouring runs that share a flag, so the view draws a handful of
    /// spans rather than one per word.</summary>
    private static IReadOnlyList<DiffRun> Merge(List<DiffRun> runs)
    {
        if (runs.Count == 0) return Array.Empty<DiffRun>();

        var merged = new List<DiffRun>();
        var text = new StringBuilder(runs[0].Text);
        var changed = runs[0].Changed;

        foreach (var run in runs.Skip(1))
        {
            if (run.Changed == changed)
            {
                text.Append(run.Text);
                continue;
            }

            merged.Add(new DiffRun(text.ToString(), changed));
            text.Clear().Append(run.Text);
            changed = run.Changed;
        }

        merged.Add(new DiffRun(text.ToString(), changed));
        return merged;
    }
}
