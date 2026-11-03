namespace DbExplorer.Application.QueryBuilder;

/// <summary>A table box to place: its alias and height (every box has the same width).</summary>
public sealed record LayoutBox(string Alias, double Height);

/// <summary>
/// Tidies the builder's canvas: the first table on the left, each table joined to it in the next column, the tables
/// joined to those in the column after, and so on (breadth first over the joins). Tables joined to nothing get columns
/// of their own at the end. Within a column the boxes stack top to bottom in placing order.
/// </summary>
public static class QueryLayout
{
    public const double Margin = 24;
    public const double ColumnGap = 90;
    public const double RowGap = 28;

    public static IReadOnlyDictionary<string, (double X, double Y)> Arrange(IReadOnlyList<LayoutBox> boxes, IEnumerable<(string Left, string Right)> joins, double width)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var neighbours = boxes.ToDictionary(b => b.Alias, _ => new List<string>(), comparer);
        foreach (var (left, right) in joins)
        {
            if (!neighbours.ContainsKey(left) || !neighbours.ContainsKey(right) || comparer.Equals(left, right)) continue;
            neighbours[left].Add(right);
            neighbours[right].Add(left);
        }

        var level = new Dictionary<string, int>(comparer);
        var next = 0;
        foreach (var root in boxes)
        {
            if (level.ContainsKey(root.Alias)) continue;
            // A new group of joined tables starts in the column after everything placed so far.
            var start = level.Count == 0 ? 0 : next;
            level[root.Alias] = start;
            var queue = new Queue<string>([root.Alias]);
            while (queue.Count > 0)
            {
                var alias = queue.Dequeue();
                foreach (var n in boxes.Where(b => neighbours[alias].Contains(b.Alias, comparer)).Select(b => b.Alias))
                {
                    if (level.ContainsKey(n)) continue;
                    level[n] = level[alias] + 1;
                    queue.Enqueue(n);
                }
            }
            next = level.Values.Max() + 1;
        }

        var result = new Dictionary<string, (double X, double Y)>(comparer);
        var heights = new Dictionary<int, double>();
        foreach (var box in boxes)
        {
            var column = level[box.Alias];
            var y = heights.GetValueOrDefault(column, Margin);
            result[box.Alias] = (Margin + column * (width + ColumnGap), y);
            heights[column] = y + box.Height + RowGap;
        }
        return result;
    }
}
