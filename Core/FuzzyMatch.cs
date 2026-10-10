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

            var found = Find(wanted, text, at, preferWordStarts);
            if (found < 0) return null;

            score += LetterScore(text, found, previous);
            previous = found;
            at = found + 1;
        }

        // Among equal matches, the shorter label is the closer one.
        return score * 100 - Math.Min(text.Length, 99);
    }

    /// <summary>
    /// Where the letter next appears from <paramref name="at"/> on: the first word start holding it
    /// when word starts are preferred (falling back to its first appearance), otherwise its first
    /// appearance. -1 when it does not appear.
    /// </summary>
    private static int Find(char wanted, string text, int at, bool preferWordStarts)
    {
        var first = -1;
        for (var i = at; i < text.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(wanted)) continue;

            if (!preferWordStarts || IsWordStart(text, i)) return i;
            if (first < 0) first = i;
        }

        return first;
    }

    /// <summary>One for the letter, more when it runs on from the last one, starts a word or starts the label.</summary>
    private static int LetterScore(string text, int found, int previous)
    {
        var score = 1;
        if (found == previous + 1) score += 5;
        if (IsWordStart(text, found)) score += 8;
        if (found == 0) score += 4;
        return score;
    }

    private static bool IsWordStart(string text, int i) =>
        i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
}
