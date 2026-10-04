namespace PPObjectSearch.Core;

/// <summary>
/// Matches typed letters against a label in order, not necessarily adjacent - "cmpd" finds
/// "Compare data". Letters at the start of a word and runs of adjacent letters score higher, so
/// the closest readings come first.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>The match's score, higher is better; null when the letters do not all appear in order.</summary>
    public static int? Score(string query, string text)
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        if (text.Length == 0) return null;

        // The earliest letters can miss a better reading - "rc" takes the c of "Recent" rather
        // than the C of "Changes" - so a pass that prefers word starts is scored too.
        var greedy = Pass(query, text, preferWordStarts: false);
        if (greedy is null) return null;

        var starts = Pass(query, text, preferWordStarts: true);
        return starts is { } s && s > greedy ? s : greedy;
    }

    private static int? Pass(string query, string text, bool preferWordStarts)
    {
        var score = 0;
        var at = 0;
        var previous = -2;

        foreach (var wanted in query)
        {
            if (char.IsWhiteSpace(wanted)) continue;

            var found = -1;
            for (var i = at; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(wanted)) continue;

                if (found < 0) found = i;
                if (!preferWordStarts || IsWordStart(text, i)) { found = i; break; }
            }

            if (found < 0) return null;

            score += 1;
            if (found == previous + 1) score += 5;
            if (IsWordStart(text, found)) score += 8;
            if (found == 0) score += 4;

            previous = found;
            at = found + 1;
        }

        // Among equal matches, the shorter label is the closer one.
        return score * 100 - Math.Min(text.Length, 99);
    }

    private static bool IsWordStart(string text, int i) =>
        i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
}
