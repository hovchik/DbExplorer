namespace DbExplorer.Application.Search;

/// <summary>
/// Subsequence matching for quick-open style pickers: "custord" matches "dbo.CustomerOrders".
/// Higher scores for prefix hits, consecutive characters and matches at word starts
/// (after . _ - space, or a lower→upper case change).
/// </summary>
public static class FuzzyMatcher
{
    /// <returns>A score (higher is better), or null when <paramref name="query"/> is not a subsequence of <paramref name="candidate"/>.</returns>
    public static int? Score(string candidate, string query)
    {
        if (query.Length == 0) return 0;
        if (query.Length > candidate.Length) return null;

        // Greedy left-to-right, and a pass that prefers word starts ("ol" → Order_Lines); keep the better.
        var greedy = Pass(candidate, query, preferWordStarts: false);
        if (greedy is null) return null;
        var acronym = Pass(candidate, query, preferWordStarts: true) ?? int.MinValue;

        var score = Math.Max(greedy.Value, acronym);
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 10;
        if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase)) score += 50;
        return score - (candidate.Length - query.Length) / 8;
    }

    private static int? Pass(string candidate, string query, bool preferWordStarts)
    {
        var score = 0;
        var previous = -2;
        var start = 0;

        for (var qi = 0; qi < query.Length; qi++)
        {
            // Continuing a contiguous run always beats jumping ahead to a later word start.
            var continues = qi > 0 && start < candidate.Length && SameLetter(candidate[start], query[qi]);
            var ci = continues ? start : preferWordStarts ? NextWordStart(candidate, query[qi], start) : -1;
            if (ci < 0) ci = Next(candidate, query[qi], start);
            if (ci < 0) return null;

            score += 1;
            if (ci == previous + 1) score += 5;
            if (IsWordStart(candidate, ci)) score += 12;
            if (candidate[ci] == query[qi]) score += 1;
            if (qi == 0 && (ci == 0 || candidate[ci - 1] == '.')) score += 10;
            previous = ci;
            start = ci + 1;
        }
        return score;
    }

    private static bool SameLetter(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    private static int Next(string s, char c, int from)
    {
        for (var i = from; i < s.Length; i++)
            if (char.ToUpperInvariant(s[i]) == char.ToUpperInvariant(c)) return i;
        return -1;
    }

    private static int NextWordStart(string s, char c, int from)
    {
        for (var i = from; i < s.Length; i++)
            if (IsWordStart(s, i) && char.ToUpperInvariant(s[i]) == char.ToUpperInvariant(c)) return i;
        return -1;
    }

    private static bool IsWordStart(string s, int i) =>
        i == 0
        || s[i - 1] is '.' or '_' or '-' or ' ' or '[' or '/'
        || (char.IsUpper(s[i]) && char.IsLower(s[i - 1]));
}
