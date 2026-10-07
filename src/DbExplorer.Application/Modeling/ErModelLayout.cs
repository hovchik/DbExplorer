using DbExplorer.Application.Design;
using DbExplorer.Application.Diagram;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Modeling;

/// <summary>Places the model's tables and turns the model into an <see cref="ErDiagram"/>, so the canvas draws it the
/// way the Diagram tab draws the catalog: same boxes, same curves, every column shown.</summary>
public static class ErModelLayout
{
    /// <summary>Room left right of and below the tables, to drag a table or draw a new one into.</summary>
    public const double Slack = 320;

    public static double HeightOf(TableDesign design)
    {
        var rows = design.Columns.Count(c => c.Name.Trim().Length > 0);
        return ErDiagramBuilder.HeaderHeight + (rows == 0 ? 0 : rows * ErDiagramBuilder.RowHeight + ErDiagramBuilder.Padding * 2);
    }

    /// <summary>Referenced tables to the left of the tables that reference them, as in the Diagram tab: each table's
    /// column is the length of its longest chain of parents in the model (cycles and self references are cut).</summary>
    public static ErModel Arrange(ErModel model)
    {
        if (model.Tables.Count == 0) return model;
        var parents = model.Relationships()
            .Where(r => r.Child.Id != r.Parent.Id)
            .ToLookup(r => r.Child.Id, r => r.Parent.Id);
        var level = new Dictionary<string, int>();
        foreach (var t in model.Tables) Depth(t.Id, []);

        var positions = new Dictionary<string, (double X, double Y)>();
        foreach (var column in model.Tables.GroupBy(t => level[t.Id]).OrderBy(g => g.Key))
        {
            var y = ErDiagramBuilder.Margin;
            foreach (var t in column.OrderBy(t => t.FullName(model.ProviderKey), StringComparer.OrdinalIgnoreCase))
            {
                positions[t.Id] = (ErDiagramBuilder.Margin + column.Key * (ErDiagramBuilder.TableWidth + ErDiagramBuilder.ColumnGap), y);
                y += HeightOf(t.Design) + ErDiagramBuilder.RowGap;
            }
        }
        return model with { Tables = model.Tables.Select(t => t with { X = positions[t.Id].X, Y = positions[t.Id].Y }).ToList() };

        int Depth(string id, HashSet<string> path)
        {
            if (level.TryGetValue(id, out var known)) return known;
            if (!path.Add(id)) return 0;
            var up = parents[id].Distinct().ToList();
            var d = up.Count == 0 ? 0 : up.Max(p => Depth(p, path)) + 1;
            path.Remove(id);
            level[id] = d;
            return d;
        }
    }

    /// <summary>A free spot for a new table: right of the others on the top row.</summary>
    public static (double X, double Y) FreeSpot(ErModel model) =>
        model.Tables.Count == 0
            ? (ErDiagramBuilder.Margin, ErDiagramBuilder.Margin)
            : (model.Tables.Max(t => t.X + ErDiagramBuilder.TableWidth) + ErDiagramBuilder.ColumnGap / 2, ErDiagramBuilder.Margin);

    /// <summary>The model as a diagram, and which model table each drawn box is.</summary>
    public static (ErDiagram Diagram, IReadOnlyDictionary<ErTable, string> Ids) ToDiagram(ErModel model)
    {
        var provider = model.ProviderKey;
        var boxes = new Dictionary<string, ErTable>();
        var ids = new Dictionary<ErTable, string>(ReferenceEqualityComparer.Instance);
        foreach (var t in model.Tables)
        {
            var fkColumns = t.Design.ForeignKeys.Select(f => f.Column.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var box = new ErTable
            {
                Object = new DbObject { Database = model.Database, Schema = t.Schema(provider), Name = t.Name.Length > 0 ? t.Name : "(unnamed)", Type = DbObjectType.Table },
                Columns = t.Design.Columns.Where(c => c.Name.Trim().Length > 0)
                    .Select(c => new ErColumn(c.Name.Trim(), c.FullType, c.IsPrimaryKey, fkColumns.Contains(c.Name.Trim()), !c.WritesNotNull))
                    .ToList(),
                IsFocus = t.Baseline is null,
                X = t.X,
                Y = t.Y,
                Width = ErDiagramBuilder.TableWidth,
                Height = HeightOf(t.Design)
            };
            boxes[t.Id] = box;
            ids[box] = t.Id;
        }

        var edges = model.Relationships().Select(r => new ErEdge(boxes[r.Child.Id], boxes[r.Parent.Id], new DbForeignKey
        {
            Database = model.Database,
            Name = r.Key.Name,
            Schema = r.Child.Schema(provider),
            Table = r.Child.Name,
            Columns = r.Key.Column.Trim(),
            ReferencedSchema = r.Parent.Schema(provider),
            ReferencedTable = r.Parent.Name,
            ReferencedColumns = r.Key.ReferencedColumn.Trim()
        })).ToList();

        var width = boxes.Count == 0 ? 0 : boxes.Values.Max(b => b.X + b.Width);
        var height = boxes.Count == 0 ? 0 : boxes.Values.Max(b => b.Y + b.Height);
        return (new ErDiagram(boxes.Values.ToList(), edges, width + Slack, height + Slack, 0), ids);
    }
}
