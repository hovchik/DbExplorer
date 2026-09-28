using System.Text.RegularExpressions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Compare;

/// <summary>A small, dependency-free line/token diff (classic LCS backtrace).</summary>
public static class TextDiffer
{
    private readonly record struct Token(string Text, bool NewLineAfter);

    public static IReadOnlyList<DiffLine> Diff(string? leftText, string? rightText, SchemaCompareMode mode = SchemaCompareMode.LineByLine)
    {
        var left = mode == SchemaCompareMode.Content ? SplitContentTokens(leftText) : SplitLineTokens(leftText);
        var right = mode == SchemaCompareMode.Content ? SplitContentTokens(rightText) : SplitLineTokens(rightText);

        var n = left.Count;
        var m = right.Count;
        var lengths = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            lengths[i, j] = left[i].Text == right[j].Text
                ? lengths[i + 1, j + 1] + 1
                : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        var result = new List<DiffLine>();
        int a = 0, b = 0, leftLine = 1, rightLine = 1;
        while (a < n && b < m)
        {
            if (left[a].Text == right[b].Text)
            {
                result.Add(new DiffLine
                {
                    LeftLineNumber = leftLine++, LeftText = left[a].Text,
                    RightLineNumber = rightLine++, RightText = right[b].Text,
                    NewLineAfter = left[a].NewLineAfter,
                    Kind = DiffLineKind.Equal
                });
                a++; b++;
            }
            else if (lengths[a + 1, b] >= lengths[a, b + 1])
            {
                result.Add(new DiffLine { LeftLineNumber = leftLine++, LeftText = left[a].Text, NewLineAfter = left[a].NewLineAfter, Kind = DiffLineKind.Removed });
                a++;
            }
            else
            {
                result.Add(new DiffLine { RightLineNumber = rightLine++, RightText = right[b].Text, NewLineAfter = right[b].NewLineAfter, Kind = DiffLineKind.Added });
                b++;
            }
        }

        while (a < n)
        {
            result.Add(new DiffLine { LeftLineNumber = leftLine++, LeftText = left[a].Text, NewLineAfter = left[a].NewLineAfter, Kind = DiffLineKind.Removed });
            a++;
        }

        while (b < m)
        {
            result.Add(new DiffLine { RightLineNumber = rightLine++, RightText = right[b].Text, NewLineAfter = right[b].NewLineAfter, Kind = DiffLineKind.Added });
            b++;
        }

        return result;
    }

    private static List<Token> SplitLineTokens(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n").Split('\n').Select(l => new Token(l, true)).ToList();

    /// <summary>Splits into non-whitespace tokens (words/punctuation) while remembering which tokens ended
    /// a source line, so formatting differences (spacing/indentation) don't count as diffs, but the
    /// original SQL layout can still be reproduced when rendering.</summary>
    private static List<Token> SplitContentTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var tokens = new List<Token>();
        for (var li = 0; li < lines.Length; li++)
        {
            var words = Regex.Matches(lines[li], @"\S+").Select(m => m.Value).ToList();
            for (var wi = 0; wi < words.Count; wi++)
            {
                var newLineAfter = wi == words.Count - 1 && li < lines.Length - 1;
                tokens.Add(new Token(words[wi], newLineAfter));
            }
        }

        return tokens;
    }
}
