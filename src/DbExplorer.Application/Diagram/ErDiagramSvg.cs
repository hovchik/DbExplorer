using System.Globalization;
using System.Net;
using System.Text;

namespace DbExplorer.Application.Diagram;

/// <summary>
/// An <see cref="ErDiagram"/> as standalone SVG markup, laid out exactly as the Diagram tab lays it out. Colours come
/// from CSS custom properties with light defaults (--er-*), so a page can restyle it for dark mode.
/// </summary>
public static class ErDiagramSvg
{
    public static string Render(ErDiagram diagram, Func<ErTable, string?>? link = null)
    {
        static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        static string E(string s) => WebUtility.HtmlEncode(s);

        var width = Math.Max(diagram.Width, ErDiagramBuilder.TableWidth + 2 * ErDiagramBuilder.Margin);
        var height = Math.Max(diagram.Height, 60);
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"er\" role=\"img\" aria-label=\"Entity-relationship diagram\" ")
          .Append("viewBox=\"0 0 ").Append(N(width)).Append(' ').Append(N(height)).Append("\" width=\"").Append(N(width))
          .Append("\" height=\"").Append(N(height)).Append("\" font-family=\"Segoe UI, system-ui, sans-serif\" font-size=\"12\">\n");
        sb.Append("<defs><marker id=\"er-one\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"8\" markerHeight=\"8\" orient=\"auto-start-reverse\">")
          .Append("<path d=\"M9 0V10\" stroke=\"var(--er-line,#8a94a6)\" stroke-width=\"1.5\" fill=\"none\"/></marker></defs>\n");

        // Edges first, so tables sit on top of them.
        foreach (var e in diagram.Edges)
        {
            var (child, parent) = (e.Child, e.Parent);
            var y1 = child.Y + ErDiagramBuilder.HeaderHeight / 2;
            var y2 = parent.Y + ErDiagramBuilder.HeaderHeight / 2;
            double x1, x2, c1, c2;
            if (ReferenceEquals(child, parent) || child.Object == parent.Object)
            {
                // Self reference: a loop off the right edge.
                x1 = child.X + child.Width;
                var loop = $"M{N(x1)} {N(y1)}c30 0 30 24 0 24";
                sb.Append("<path d=\"").Append(loop).Append("\" fill=\"none\" stroke=\"var(--er-line,#8a94a6)\" stroke-width=\"1.2\"")
                  .Append(e.ForeignKey.IsVirtual ? " stroke-dasharray=\"4 3\"" : "").Append("><title>").Append(E(e.ForeignKey.Name)).Append("</title></path>\n");
                continue;
            }
            if (parent.X + parent.Width <= child.X)
            {
                (x1, x2) = (child.X, parent.X + parent.Width);
                (c1, c2) = (x1 - 40, x2 + 40);
            }
            else if (child.X + child.Width <= parent.X)
            {
                (x1, x2) = (child.X + child.Width, parent.X);
                (c1, c2) = (x1 + 40, x2 - 40);
            }
            else
            {
                // Same column: leave from the left of both and bend out.
                (x1, x2) = (child.X, parent.X);
                (c1, c2) = (x1 - 50, x2 - 50);
            }
            sb.Append("<path d=\"M").Append(N(x1)).Append(' ').Append(N(y1)).Append(" C").Append(N(c1)).Append(' ').Append(N(y1))
              .Append(' ').Append(N(c2)).Append(' ').Append(N(y2)).Append(' ').Append(N(x2)).Append(' ').Append(N(y2))
              .Append("\" fill=\"none\" stroke=\"var(--er-line,#8a94a6)\" stroke-width=\"1.2\" marker-end=\"url(#er-one)\"")
              .Append(e.ForeignKey.IsVirtual ? " stroke-dasharray=\"4 3\"" : "")
              .Append("><title>").Append(E($"{e.ForeignKey.Name}: {child.Title} ({e.ForeignKey.Columns}) → {parent.Title} ({e.ForeignKey.ReferencedColumns})"))
              .Append("</title></path>\n");
        }

        foreach (var t in diagram.Tables)
        {
            var href = link?.Invoke(t);
            if (href is not null) sb.Append("<a href=\"").Append(E(href)).Append("\">");
            sb.Append("<g class=\"er-table\">");
            sb.Append("<rect x=\"").Append(N(t.X)).Append("\" y=\"").Append(N(t.Y)).Append("\" width=\"").Append(N(t.Width))
              .Append("\" height=\"").Append(N(t.Height)).Append("\" rx=\"6\" fill=\"var(--er-body,#ffffff)\" stroke=\"var(--er-border,#c5ccd6)\"/>");
            sb.Append("<path d=\"M").Append(N(t.X)).Append(' ').Append(N(t.Y + ErDiagramBuilder.HeaderHeight)).Append("V").Append(N(t.Y + 6))
              .Append("a6 6 0 0 1 6 -6H").Append(N(t.X + t.Width - 6)).Append("a6 6 0 0 1 6 6V").Append(N(t.Y + ErDiagramBuilder.HeaderHeight))
              .Append("Z\" fill=\"var(--er-head,#f6e3cf)\" stroke=\"var(--er-border,#c5ccd6)\"/>");
            sb.Append("<text x=\"").Append(N(t.X + 8)).Append("\" y=\"").Append(N(t.Y + 18)).Append("\" font-weight=\"600\" fill=\"var(--er-text,#1f2430)\">")
              .Append(E(Clip(t.Title, 32))).Append("</text>");
            var y = t.Y + ErDiagramBuilder.HeaderHeight + ErDiagramBuilder.Padding;
            foreach (var c in t.Columns)
            {
                var marker = c.IsPrimaryKey ? "PK" : c.IsForeignKey ? "FK" : "";
                if (marker.Length > 0)
                    sb.Append("<text x=\"").Append(N(t.X + 8)).Append("\" y=\"").Append(N(y + 13)).Append("\" font-size=\"10\" font-weight=\"600\" fill=\"")
                      .Append(c.IsPrimaryKey ? "var(--er-pk,#b7791f)" : "var(--er-fk,#2b6cb0)").Append("\">").Append(marker).Append("</text>");
                sb.Append("<text x=\"").Append(N(t.X + 30)).Append("\" y=\"").Append(N(y + 13)).Append("\" fill=\"var(--er-text,#1f2430)\"")
                  .Append(c.IsNullable ? "" : " font-weight=\"500\"").Append('>').Append(E(Clip(c.Name, 18))).Append("</text>");
                sb.Append("<text x=\"").Append(N(t.X + t.Width - 8)).Append("\" y=\"").Append(N(y + 13))
                  .Append("\" text-anchor=\"end\" font-size=\"11\" fill=\"var(--er-muted,#6b7385)\">").Append(E(Clip(c.DataType, 14))).Append("</text>");
                y += ErDiagramBuilder.RowHeight;
            }
            if (t.HiddenColumnCount > 0)
                sb.Append("<title>").Append(E($"{t.Title}: {t.HiddenColumnCount} more column(s) without keys")).Append("</title>");
            sb.Append("</g>");
            if (href is not null) sb.Append("</a>");
            sb.Append('\n');
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
