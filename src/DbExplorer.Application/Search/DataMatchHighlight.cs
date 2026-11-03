using DbExplorer.Core.Search;

namespace DbExplorer.Application.Search;

/// <summary>A found value split around the searched text, so the part that matched can be shown highlighted.</summary>
public sealed record DataMatchHighlight(string Before, string Hit, string After)
{
    /// <summary>Longest value shown in one result line; the rest is cut with an ellipsis (the found row shows it all).</summary>
    public const int MaxLength = 300;

    /// <summary>
    /// Splits <paramref name="value"/> at the first case-insensitive occurrence of <paramref name="term"/> that fits
    /// <paramref name="mode"/>. A value that matched another way (a number or GUID compared exactly, a collation that
    /// ignores accents) is highlighted whole. Line breaks become spaces and long values are cut around the hit.
    /// </summary>
    public static DataMatchHighlight Split(string? value, string term, SearchMatchMode mode)
    {
        if (string.IsNullOrEmpty(value)) return new("", "", "");
        var text = OneLine(value);
        term = term.Trim();
        if (term.Length == 0) return Cut(text, 0, 0);

        var index = mode switch
        {
            SearchMatchMode.StartsWith => text.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 0 : -1,
            SearchMatchMode.EndsWith => text.EndsWith(term, StringComparison.OrdinalIgnoreCase) ? text.Length - term.Length : -1,
            SearchMatchMode.Exact => string.Equals(text.Trim(), term, StringComparison.OrdinalIgnoreCase) ? 0 : -1,
            _ => text.IndexOf(term, StringComparison.OrdinalIgnoreCase)
        };
        if (mode == SearchMatchMode.Exact && index == 0) return Cut(text, 0, text.Length);
        return index < 0 ? Cut(text, 0, text.Length) : Cut(text, index, term.Length);
    }

    private static DataMatchHighlight Cut(string text, int start, int length)
    {
        if (text.Length <= MaxLength) return new(text[..start], text.Substring(start, length), text[(start + length)..]);

        // Keep some context before the hit, then as much after it as fits.
        var hitLength = Math.Min(length, MaxLength);
        var from = Math.Max(0, Math.Min(start - 40, text.Length - MaxLength));
        var to = Math.Min(text.Length, Math.Max(from + MaxLength, start + hitLength));
        var before = (from > 0 ? "…" : "") + text[from..start];
        var hit = text.Substring(start, hitLength);
        var after = text[(start + hitLength)..to] + (to < text.Length ? "…" : "");
        return new(before, hit, after);
    }

    private static string OneLine(string value) =>
        value.IndexOfAny(['\r', '\n', '\t']) < 0 ? value : value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
