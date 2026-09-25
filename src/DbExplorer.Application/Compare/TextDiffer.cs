using DbExplorer.Core.Models;

namespace DbExplorer.Application.Compare;

/// <summary>A small, dependency-free line-based diff (classic LCS backtrace).</summary>
public static class TextDiffer
{
    public static IReadOnlyList<DiffLine> Diff(string? leftText, string? rightText)
    {
        var left = SplitLines(leftText);
        var right = SplitLines(rightText);

        var n = left.Count;
        var m = right.Count;
        var lengths = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            lengths[i, j] = left[i] == right[j]
                ? lengths[i + 1, j + 1] + 1
                : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        var result = new List<DiffLine>();
        int a = 0, b = 0, leftLine = 1, rightLine = 1;
        while (a < n && b < m)
        {
            if (left[a] == right[b])
            {
                result.Add(new DiffLine
                {
                    LeftLineNumber = leftLine++, LeftText = left[a],
                    RightLineNumber = rightLine++, RightText = right[b],
                    Kind = DiffLineKind.Equal
                });
                a++; b++;
            }
            else if (lengths[a + 1, b] >= lengths[a, b + 1])
            {
                result.Add(new DiffLine { LeftLineNumber = leftLine++, LeftText = left[a], Kind = DiffLineKind.Removed });
                a++;
            }
            else
            {
                result.Add(new DiffLine { RightLineNumber = rightLine++, RightText = right[b], Kind = DiffLineKind.Added });
                b++;
            }
        }

        while (a < n)
        {
            result.Add(new DiffLine { LeftLineNumber = leftLine++, LeftText = left[a], Kind = DiffLineKind.Removed });
            a++;
        }

        while (b < m)
        {
            result.Add(new DiffLine { RightLineNumber = rightLine++, RightText = right[b], Kind = DiffLineKind.Added });
            b++;
        }

        return result;
    }

    private static List<string> SplitLines(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n").Split('\n').ToList();
}
