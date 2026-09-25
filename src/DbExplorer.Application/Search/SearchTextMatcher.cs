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
}
