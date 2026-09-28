using System.Text.RegularExpressions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Compare;

/// <summary>What a text diff treats as "the same" beyond exact equality. Only the comparison is affected;
/// the original text is always what gets displayed.</summary>
public sealed record DiffOptions
{
    public static readonly DiffOptions Default = new();

    /// <summary>Compare case-insensitively (keywords and identifiers are case-insensitive on most engines).</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Line mode: collapse runs of whitespace and ignore leading/trailing whitespace, so re-indented
    /// lines match. Content mode always ignores whitespace.</summary>
    public bool IgnoreWhitespace { get; init; }
}

/// <summary>Headline numbers for a diff: how much changed and how similar the two sides are.</summary>
public sealed record DiffStatistics(int Unchanged, int Removed, int Added, int ChangeBlocks)
{
    public bool IsIdentical => Removed == 0 && Added == 0;

    /// <summary>Share of the two sides that is common, 0..100 (Dice coefficient over lines/tokens).</summary>
    public double SimilarityPercent
    {
        get
        {
            var total = 2 * Unchanged + Removed + Added;
            return total == 0 ? 100 : Math.Round(200.0 * Unchanged / total, 1);
        }
    }
}

/// <summary>A small, dependency-free line/token diff (classic LCS backtrace).</summary>
public static partial class TextDiffer
{
    private readonly record struct Token(string Text, string Key, bool NewLineAfter);

    public static IReadOnlyList<DiffLine> Diff(
        string? leftText, string? rightText, SchemaCompareMode mode = SchemaCompareMode.LineByLine, DiffOptions? options = null)
    {
        options ??= DiffOptions.Default;
        var left = mode == SchemaCompareMode.Content ? SplitContentTokens(leftText, options) : SplitLineTokens(leftText, options);
        var right = mode == SchemaCompareMode.Content ? SplitContentTokens(rightText, options) : SplitLineTokens(rightText, options);

        // Definitions usually differ in a few places only: strip the common head and tail first so the
        // quadratic LCS table only covers the changed middle (keeps large procedures fast and small).
        var prefix = 0;
        while (prefix < left.Count && prefix < right.Count && left[prefix].Key == right[prefix].Key) prefix++;
        var suffix = 0;
        while (suffix < left.Count - prefix && suffix < right.Count - prefix &&
               left[left.Count - 1 - suffix].Key == right[right.Count - 1 - suffix].Key) suffix++;

        var result = new List<DiffLine>(Math.Max(left.Count, right.Count));
        int leftLine = 1, rightLine = 1;

        void Equal(int a, int b) => result.Add(new DiffLine
        {
            LeftLineNumber = leftLine++, LeftText = left[a].Text,
            RightLineNumber = rightLine++, RightText = right[b].Text,
            NewLineAfter = left[a].NewLineAfter,
            Kind = DiffLineKind.Equal
        });

        void Removed(int a) => result.Add(new DiffLine
        {
            LeftLineNumber = leftLine++, LeftText = left[a].Text, NewLineAfter = left[a].NewLineAfter, Kind = DiffLineKind.Removed
        });

        void Added(int b) => result.Add(new DiffLine
        {
            RightLineNumber = rightLine++, RightText = right[b].Text, NewLineAfter = right[b].NewLineAfter, Kind = DiffLineKind.Added
        });

        for (var i = 0; i < prefix; i++) Equal(i, i);

        var n = left.Count - prefix - suffix;
        var m = right.Count - prefix - suffix;
        var lengths = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            lengths[i, j] = left[prefix + i].Key == right[prefix + j].Key
                ? lengths[i + 1, j + 1] + 1
                : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (left[prefix + x].Key == right[prefix + y].Key)
            {
                Equal(prefix + x, prefix + y);
                x++; y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                Removed(prefix + x++);
            }
            else
            {
                Added(prefix + y++);
            }
        }

        while (x < n) Removed(prefix + x++);
        while (y < m) Added(prefix + y++);

        for (var i = 0; i < suffix; i++) Equal(left.Count - suffix + i, right.Count - suffix + i);

        return result;
    }

    public static DiffStatistics Statistics(IReadOnlyList<DiffLine> diff)
    {
        int unchanged = 0, removed = 0, added = 0, blocks = 0;
        var inBlock = false;
        foreach (var line in diff)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Equal: unchanged++; inBlock = false; continue;
                case DiffLineKind.Removed: removed++; break;
                case DiffLineKind.Added: added++; break;
                default: inBlock = false; continue;
            }
            if (!inBlock) blocks++;
            inBlock = true;
        }
        return new DiffStatistics(unchanged, removed, added, blocks);
    }

    /// <summary>Indexes (into <paramref name="diff"/>) of the first line of every block of consecutive changes,
    /// used for "next / previous difference" navigation.</summary>
    public static IReadOnlyList<int> ChangeBlockStarts(IReadOnlyList<DiffLine> diff)
    {
        var starts = new List<int>();
        for (var i = 0; i < diff.Count; i++)
            if (IsChange(diff[i]) && (i == 0 || !IsChange(diff[i - 1])))
                starts.Add(i);
        return starts;
    }

    /// <summary>Keeps every change plus <paramref name="context"/> unchanged lines around it; each longer run of
    /// unchanged lines is replaced by one <see cref="DiffLineKind.Skipped"/> marker line.</summary>
    public static IReadOnlyList<DiffLine> CollapseUnchanged(IReadOnlyList<DiffLine> diff, int context = 3)
    {
        var keep = new bool[diff.Count];
        for (var i = 0; i < diff.Count; i++)
        {
            if (!IsChange(diff[i])) continue;
            for (var j = Math.Max(0, i - context); j <= Math.Min(diff.Count - 1, i + context); j++)
                keep[j] = true;
        }

        var result = new List<DiffLine>();
        var i2 = 0;
        while (i2 < diff.Count)
        {
            if (keep[i2])
            {
                result.Add(diff[i2++]);
                continue;
            }

            var start = i2;
            while (i2 < diff.Count && !keep[i2]) i2++;
            var hidden = i2 - start;
            var label = $"⋯ {hidden:N0} unchanged line(s) hidden ⋯";
            result.Add(new DiffLine { LeftText = label, RightText = label, Kind = DiffLineKind.Skipped, NewLineAfter = true });
        }
        return result;
    }

    private static bool IsChange(DiffLine line) => line.Kind is DiffLineKind.Added or DiffLineKind.Removed;

    private static string KeyOf(string text, DiffOptions options, bool collapseWhitespace)
    {
        var key = collapseWhitespace ? Whitespace().Replace(text, " ").Trim() : text;
        return options.IgnoreCase ? key.ToUpperInvariant() : key;
    }

    private static List<Token> SplitLineTokens(string? text, DiffOptions options) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n").Split('\n')
                .Select(l => new Token(l, KeyOf(l, options, options.IgnoreWhitespace), true)).ToList();

    /// <summary>Splits into non-whitespace tokens (words/punctuation) while remembering which tokens ended
    /// a source line, so formatting differences (spacing/indentation) don't count as diffs, but the
    /// original SQL layout can still be reproduced when rendering.</summary>
    private static List<Token> SplitContentTokens(string? text, DiffOptions options)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var tokens = new List<Token>();
        for (var li = 0; li < lines.Length; li++)
        {
            var words = NonWhitespace().Matches(lines[li]).Select(m => m.Value).ToList();
            for (var wi = 0; wi < words.Count; wi++)
            {
                var newLineAfter = wi == words.Count - 1 && li < lines.Length - 1;
                tokens.Add(new Token(words[wi], KeyOf(words[wi], options, collapseWhitespace: false), newLineAfter));
            }
        }

        return tokens;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\S+")]
    private static partial Regex NonWhitespace();
}
