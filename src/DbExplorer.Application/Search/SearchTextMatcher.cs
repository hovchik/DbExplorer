using System.Text.RegularExpressions;

namespace DbExplorer.Application.Search;

/// <summary>
/// Builds/evaluates the regular expression used by <see cref="MetadataSearchService"/> so the
/// same matching rules (case sensitivity, whole word, regex mode) can be reused to highlight
/// matches when a result is opened for viewing.
/// </summary>
public static class SearchTextMatcher
{
    public static Regex BuildRegex(MetadataSearchQuery query)
    {
        var pattern = query.UseRegex ? query.Text : Regex.Escape(query.Text);
        if (query.WholeWord) pattern = $@"\b(?:{pattern})\b";

        var options = RegexOptions.CultureInvariant;
        if (!query.MatchCase) options |= RegexOptions.IgnoreCase;

        return new Regex(pattern, options, TimeSpan.FromSeconds(2));
    }

    public static bool IsMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Where <paramref name="regex"/> matches in <paramref name="text"/> (empty matches skipped), to highlight
    /// them; empty when the pattern takes too long.</summary>
    public static IReadOnlyList<(int Start, int Length)> Spans(Regex regex, string text, int max = 2000)
    {
        var spans = new List<(int, int)>();
        try
        {
            for (var m = regex.Match(text); m.Success && spans.Count < max; m = m.NextMatch())
                if (m.Length > 0) spans.Add((m.Index, m.Length));
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return spans;
    }

    /// <summary><paramref name="text"/> cut into plain and matching runs, in order; one plain run when nothing matches.</summary>
    public static IReadOnlyList<(string Text, bool IsMatch)> Segments(Regex regex, string text)
    {
        var segments = new List<(string, bool)>();
        var last = 0;
        foreach (var (start, length) in Spans(regex, text, 200))
        {
            if (start > last) segments.Add((text[last..start], false));
            segments.Add((text.Substring(start, length), true));
            last = start + length;
        }
        if (last < text.Length || segments.Count == 0) segments.Add((text[last..], false));
        return segments;
    }
}
