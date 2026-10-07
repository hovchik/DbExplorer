namespace DbExplorer.Application.Query.Plans;

/// <summary>Where a plan operator is drawn.</summary>
public readonly record struct PlanBox(PlanNode Node, double X, double Y)
{
    public bool Contains(double x, double y) =>
        x >= X && x <= X + PlanLayout.NodeWidth && y >= Y && y <= Y + PlanLayout.NodeHeight;
}

/// <summary>
/// Lays a plan out as a tree that reads like SQL Server Management Studio's: the statement's last operator on the left,
/// its inputs to the right, data flowing right to left. Leaves get one row each; a parent sits level with the middle
/// of its children.
/// </summary>
public sealed class PlanLayout
{
    public const double NodeWidth = 210;
    public const double NodeHeight = 78;
    public const double ColumnGap = 64;
    public const double RowGap = 18;
    public const double Margin = 16;

    private PlanLayout(IReadOnlyList<PlanBox> boxes, double width, double height)
    {
        Boxes = boxes;
        Width = width;
        Height = height;
    }

    /// <summary>One box per node, indexed by <see cref="PlanNode.Id"/>.</summary>
    public IReadOnlyList<PlanBox> Boxes { get; }

    public double Width { get; }
    public double Height { get; }

    public PlanBox this[PlanNode node] => Boxes[node.Id];

    public PlanNode? HitTest(double x, double y) => Boxes.LastOrDefault(b => b.Contains(x, y)).Node;

    public static PlanLayout Arrange(ExecutionPlan plan)
    {
        var y = new double[plan.Nodes.Count];
        var depth = new int[plan.Nodes.Count];
        var nextRow = 0;

        double Place(PlanNode node, int level)
        {
            depth[node.Id] = level;
            if (node.Children.Count == 0)
                return y[node.Id] = nextRow++ * (NodeHeight + RowGap);
            var first = 0.0;
            var last = 0.0;
            for (var i = 0; i < node.Children.Count; i++)
            {
                var childY = Place(node.Children[i], level + 1);
                if (i == 0) first = childY;
                last = childY;
            }
            return y[node.Id] = (first + last) / 2;
        }
        Place(plan.Root, 0);

        var boxes = plan.Nodes
            .Select(n => new PlanBox(n, Margin + depth[n.Id] * (NodeWidth + ColumnGap), Margin + y[n.Id]))
            .ToList();
        var width = boxes.Max(b => b.X) + NodeWidth + Margin;
        var height = boxes.Max(b => b.Y) + NodeHeight + Margin;
        return new PlanLayout(boxes, width, height);
    }
}

/// <summary>A plan as grid rows, one per operator, indented by depth: the row view of a plan.</summary>
public static class PlanTable
{
    public static readonly IReadOnlyList<string> Columns =
        ["Operator", "Object", "Est. rows", "Actual rows", "Executions", "Cost share %", "Subtree cost", "Time ms", "Detail", "Warnings"];

    public static IReadOnlyList<IReadOnlyList<object?>> Rows(ExecutionPlan plan) =>
        plan.Nodes.Select(n => (IReadOnlyList<object?>)
        [
            new string(' ', Depth(n) * 3) + (n.Parent is null ? "" : "└ ") + n.Operator,
            n.Object,
            Math.Round(n.EstimatedRows * Math.Max(1, n.EstimatedExecutions), 2),
            n.ActualRows,
            n.ActualExecutions,
            Math.Round(n.Share * 100, 1),
            n.SubtreeCost,
            n.ActualTimeMs is { } t ? Math.Round(t, 3) : null,
            n.Detail,
            n.Warnings.Count == 0 ? null : string.Join(" | ", n.Warnings.Select(w => w.Message))
        ]).ToList();

    private static int Depth(PlanNode node)
    {
        var depth = 0;
        for (var p = node.Parent; p is not null; p = p.Parent) depth++;
        return depth;
    }
}
