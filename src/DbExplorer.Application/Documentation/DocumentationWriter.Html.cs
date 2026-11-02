using System.Globalization;
using System.Net;
using System.Text;
using DbExplorer.Application.Diagram;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Documentation;

public static partial class DocumentationWriter
{
    /// <summary>
    /// One self-contained HTML page (no external resources): a sidebar with every object and a filter box, the
    /// diagram as inline SVG (tables link to their section), and a section per object. Follows the system's light or
    /// dark theme and prints without the sidebar.
    /// </summary>
    public static string ToHtml(DatabaseDocument doc)
    {
        var sb = new StringBuilder();
        string Name(DbObject o) => doc.MultipleSchemas ? o.FullName : o.Name;

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<meta name=\"generator\" content=\"DbExplorer\">\n")
          .Append("<title>").Append(H(doc.Title)).Append(" · database documentation</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");

        // ----- Sidebar -----
        sb.Append("<nav id=\"side\" aria-label=\"Objects\">\n<div class=\"side-head\"><div class=\"brand\">").Append(H(doc.Title)).Append("</div>")
          .Append("<input id=\"filter\" type=\"search\" placeholder=\"Filter objects…\" aria-label=\"Filter objects\" autocomplete=\"off\"></div>\n<div class=\"side-list\">\n");
        if (doc.Diagram is not null) sb.Append("<a class=\"top\" href=\"#diagram\">Diagram</a>\n");
        void Side<T>(string title, string anchor, IReadOnlyList<T> items, Func<T, DbObject> obj, Func<T, string> itemAnchor)
        {
            if (items.Count == 0) return;
            sb.Append("<div class=\"grp\"><a class=\"top\" href=\"#").Append(anchor).Append("\">").Append(title)
              .Append(" <span class=\"n\">").Append(items.Count).Append("</span></a>\n");
            foreach (var item in items)
            {
                var o = obj(item);
                sb.Append("<a class=\"obj\" href=\"#").Append(itemAnchor(item)).Append("\" data-name=\"").Append(H(o.FullName.ToLowerInvariant())).Append("\">")
                  .Append("<i class=\"dot ").Append(TypeClass(o.Type)).Append("\"></i>").Append(H(Name(o))).Append("</a>\n");
            }
            sb.Append("</div>\n");
        }
        Side("Tables", "tables", doc.Tables, t => t.Object, t => t.Anchor);
        Side("Views", "views", doc.Views, t => t.Object, t => t.Anchor);
        Side("Routines", "routines", doc.Routines, r => r.Object, r => r.Anchor);
        Side("Triggers", "triggers", doc.Triggers, r => r.Object, r => r.Anchor);
        Side("Sequences", "sequences", doc.Sequences, r => r.Object, r => r.Anchor);
        Side("Synonyms", "synonyms", doc.Synonyms, r => r.Object, r => r.Anchor);
        sb.Append("<p id=\"nomatch\" hidden>No object matches.</p>\n</div>\n</nav>\n");

        // ----- Header -----
        sb.Append("<main>\n<header>\n<h1>").Append(H(doc.Title)).Append("</h1>\n<p class=\"sub\">").Append(H(Subtitle(doc))).Append("</p>\n");
        if (doc.Comment is { } dbComment) sb.Append("<p class=\"comment\">").Append(Text(dbComment)).Append("</p>\n");
        sb.Append("<div class=\"stats\">");
        void Stat(int count, string label, string anchor)
        {
            if (count > 0) sb.Append("<a class=\"stat\" href=\"#").Append(anchor).Append("\"><b>").Append(count.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</b>").Append(label).Append("</a>");
        }
        Stat(doc.Tables.Count, doc.Tables.Count == 1 ? "table" : "tables", "tables");
        Stat(doc.Tables.Sum(t => t.Columns.Count), "columns", "tables");
        Stat(doc.Tables.Sum(t => t.ForeignKeys.Count), "foreign keys", "tables");
        Stat(doc.Views.Count, doc.Views.Count == 1 ? "view" : "views", "views");
        Stat(doc.Routines.Count, doc.Routines.Count == 1 ? "routine" : "routines", "routines");
        Stat(doc.Triggers.Count, doc.Triggers.Count == 1 ? "trigger" : "triggers", "triggers");
        Stat(doc.Sequences.Count, doc.Sequences.Count == 1 ? "sequence" : "sequences", "sequences");
        Stat(doc.Synonyms.Count, doc.Synonyms.Count == 1 ? "synonym" : "synonyms", "synonyms");
        sb.Append("</div>\n");
        foreach (var note in doc.Notes) sb.Append("<p class=\"note\">").Append(H(note)).Append("</p>\n");
        if (doc.Tables.Count + doc.Views.Count + doc.Routines.Count + doc.Triggers.Count + doc.Sequences.Count + doc.Synonyms.Count == 0)
            sb.Append("<p class=\"empty\">This database has no objects to document.</p>\n");
        sb.Append("</header>\n");

        if (doc.SchemaComments.Count > 0)
        {
            sb.Append("<section><h2>Schemas</h2>\n<table class=\"grid\"><thead><tr><th>Schema</th><th>Description</th></tr></thead><tbody>\n");
            foreach (var (schema, text) in doc.SchemaComments.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append("<tr><td class=\"name\">").Append(H(schema)).Append("</td><td>").Append(Text(text)).Append("</td></tr>\n");
            sb.Append("</tbody></table></section>\n");
        }

        // ----- Diagram -----
        if (doc.Diagram is { } diagram)
        {
            sb.Append("<section id=\"diagram\"><h2>Diagram</h2>\n<p class=\"hint\">Primary and foreign key columns only. Click a table to jump to it.</p>\n")
              .Append("<div class=\"diagram\">").Append(ErDiagramSvg.Render(diagram, t => "#" + DocumentationAnchors.For(t.Object))).Append("</div>\n</section>\n");
        }

        // ----- Tables and views -----
        void Tables(string title, string anchor, IReadOnlyList<DocTable> tables)
        {
            if (tables.Count == 0) return;
            sb.Append("<section id=\"").Append(anchor).Append("\"><h2>").Append(title).Append("</h2>\n");
            foreach (var t in tables)
            {
                sb.Append("<article class=\"obj-card\" id=\"").Append(t.Anchor).Append("\" data-name=\"").Append(H(t.Object.FullName.ToLowerInvariant())).Append("\">\n");
                sb.Append("<h3><i class=\"dot ").Append(TypeClass(t.Object.Type)).Append("\"></i>").Append(H(Name(t.Object)))
                  .Append("<a class=\"self\" href=\"#").Append(t.Anchor).Append("\" aria-label=\"Link to this section\">#</a></h3>\n");
                sb.Append("<p class=\"facts\"><span class=\"badge ").Append(TypeClass(t.Object.Type)).Append("\">").Append(TypeName(t.Object.Type)).Append("</span>");
                if (doc.ShowRowCounts && t.Object.RowCount is long rows && rows >= 0)
                    sb.Append("<span>about ").Append(rows.ToString("N0", CultureInfo.InvariantCulture)).Append(" rows</span>");
                sb.Append("<span>").Append(t.Columns.Count).Append(t.Columns.Count == 1 ? " column" : " columns").Append("</span>");
                if (t.Object.ModifiedAt is DateTime modified) sb.Append("<span>changed ").Append(modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</span>");
                sb.Append("</p>\n");
                if (t.Comment is { } comment) sb.Append("<p class=\"comment\">").Append(Text(comment)).Append("</p>\n");

                if (t.Columns.Count > 0)
                {
                    var showDefault = t.Columns.Any(c => c.Default is not null);
                    sb.Append("<div class=\"scroll\"><table class=\"grid cols\"><thead><tr><th class=\"num\">#</th><th>Column</th><th>Type</th><th>Null</th>")
                      .Append(showDefault ? "<th>Default</th>" : "").Append("<th>Key</th><th>Description</th></tr></thead><tbody>\n");
                    foreach (var col in t.Columns)
                    {
                        var c = col.Column;
                        sb.Append("<tr><td class=\"num\">").Append(c.Ordinal).Append("</td><td class=\"name").Append(c.IsPrimaryKey ? " pk" : "").Append("\">")
                          .Append(H(c.Name)).Append("</td><td><code>").Append(H(c.DataType)).Append("</code></td><td>")
                          .Append(c.IsNullable ? "<span class=\"muted\">null</span>" : "not null").Append("</td>");
                        if (showDefault) sb.Append("<td>").Append(col.Default is { } d ? "<code>" + H(d) + "</code>" : "").Append("</td>");
                        sb.Append("<td class=\"keys\">").Append(KeyHtml(col)).Append("</td><td>").Append(col.Comment is { } cc ? Text(cc) : "").Append("</td></tr>\n");
                    }
                    sb.Append("</tbody></table></div>\n");
                }

                if (t.Indexes.Count > 0)
                {
                    sb.Append("<h4>Indexes</h4>\n<div class=\"scroll\"><table class=\"grid\"><thead><tr><th>Name</th><th>Columns</th><th>Unique</th><th>Type</th></tr></thead><tbody>\n");
                    foreach (var i in t.Indexes)
                    {
                        sb.Append("<tr><td class=\"name\">").Append(H(i.Name)).Append(i.IsPrimaryKey ? " <span class=\"tag pk\">primary key</span>" : "")
                          .Append(i.IsDisabled ? " <span class=\"tag\">disabled</span>" : "").Append("</td><td>").Append(H(i.Columns ?? ""));
                        if (!string.IsNullOrEmpty(i.IncludedColumns)) sb.Append(" <span class=\"muted\">include ").Append(H(i.IncludedColumns)).Append("</span>");
                        if (!string.IsNullOrEmpty(i.Filter)) sb.Append(" <span class=\"muted\">where</span> <code>").Append(H(i.Filter)).Append("</code>");
                        sb.Append("</td><td>").Append(i.IsUnique ? "yes" : "").Append("</td><td>").Append(H(i.Type)).Append("</td></tr>\n");
                    }
                    sb.Append("</tbody></table></div>\n");
                }

                if (t.ForeignKeys.Count > 0)
                {
                    sb.Append("<h4>Foreign keys</h4>\n<ul class=\"rel\">\n");
                    foreach (var fk in t.ForeignKeys)
                        sb.Append("<li><code>").Append(H(fk.Name)).Append("</code> (").Append(H(fk.Columns ?? "")).Append(") → ")
                          .Append(TableLink(fk.ReferencedSchema, fk.ReferencedTable)).Append(" (").Append(H(fk.ReferencedColumns ?? "")).Append(')')
                          .Append(FlagsHtml(fk)).Append("</li>\n");
                    sb.Append("</ul>\n");
                }

                if (t.ReferencedBy.Count > 0)
                {
                    sb.Append("<h4>Referenced by</h4>\n<ul class=\"rel\">\n");
                    foreach (var fk in t.ReferencedBy)
                        sb.Append("<li>").Append(TableLink(fk.Schema, fk.Table)).Append(" (").Append(H(fk.Columns ?? "")).Append(") via <code>")
                          .Append(H(fk.Name)).Append("</code>").Append(FlagsHtml(fk)).Append("</li>\n");
                    sb.Append("</ul>\n");
                }

                if (t.Checks.Count > 0)
                {
                    sb.Append("<h4>Checks</h4>\n<ul class=\"rel\">\n");
                    foreach (var check in t.Checks)
                        sb.Append("<li><code>").Append(H(check.Name)).Append("</code>: <code>").Append(H(check.Expression)).Append("</code></li>\n");
                    sb.Append("</ul>\n");
                }

                if (t.Definition is { } definition) Source(definition, open: false);
                sb.Append("</article>\n");
            }
            sb.Append("</section>\n");
        }
        Tables("Tables", "tables", doc.Tables);
        Tables("Views", "views", doc.Views);

        // ----- Routines, triggers, sequences, synonyms -----
        void Objects(string title, string anchor, IReadOnlyList<DocObject> items)
        {
            if (items.Count == 0) return;
            sb.Append("<section id=\"").Append(anchor).Append("\"><h2>").Append(title).Append("</h2>\n");
            foreach (var r in items)
            {
                sb.Append("<article class=\"obj-card\" id=\"").Append(r.Anchor).Append("\" data-name=\"").Append(H(r.Object.FullName.ToLowerInvariant())).Append("\">\n");
                sb.Append("<h3><i class=\"dot ").Append(TypeClass(r.Object.Type)).Append("\"></i>").Append(H(Name(r.Object)))
                  .Append("<a class=\"self\" href=\"#").Append(r.Anchor).Append("\" aria-label=\"Link to this section\">#</a></h3>\n");
                sb.Append("<p class=\"facts\"><span class=\"badge ").Append(TypeClass(r.Object.Type)).Append("\">").Append(TypeName(r.Object.Type)).Append("</span>");
                if (r.Object.ModifiedAt is DateTime modified) sb.Append("<span>changed ").Append(modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</span>");
                sb.Append("</p>\n");
                if (r.Comment is { } comment) sb.Append("<p class=\"comment\">").Append(Text(comment)).Append("</p>\n");
                if (r.Definition is { } definition) Source(definition, open: true);
                sb.Append("</article>\n");
            }
            sb.Append("</section>\n");
        }
        Objects("Routines", "routines", doc.Routines);
        Objects("Triggers", "triggers", doc.Triggers);
        Objects("Sequences", "sequences", doc.Sequences);
        Objects("Synonyms", "synonyms", doc.Synonyms);

        sb.Append("<footer>Generated by DbExplorer on ").Append(doc.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(".</footer>\n");
        sb.Append("</main>\n<script>\n").Append(Script).Append("</script>\n</body>\n</html>\n");
        return sb.ToString();

        void Source(string definition, bool open)
        {
            var lines = definition.Replace("\r\n", "\n").TrimEnd().Split('\n').Length;
            sb.Append("<details class=\"src\"").Append(open && lines <= 40 ? " open" : "").Append("><summary>Source (")
              .Append(lines).Append(lines == 1 ? " line" : " lines").Append(")</summary><pre><code>")
              .Append(H(definition.Replace("\r\n", "\n").TrimEnd())).Append("</code></pre></details>\n");
        }

        string TableLink(string schema, string table)
        {
            var label = doc.MultipleSchemas ? $"{schema}.{table}" : table;
            return $"<a href=\"#{DocumentationAnchors.For("table", schema, table)}\">{H(label)}</a>";
        }

        string KeyHtml(DocColumn col)
        {
            var parts = new List<string>();
            if (col.Column.IsPrimaryKey) parts.Add("<span class=\"tag pk\">PK</span>");
            if (col.References is { } r)
                parts.Add("<span class=\"tag fk\">FK</span> " + TableLink(r.Schema, r.Table) + (r.Column is null ? "" : $"<span class=\"muted\">.{H(r.Column)}</span>"));
            if (col.Column.IsIdentity) parts.Add("<span class=\"tag\">identity</span>");
            if (col.Column.IsComputed) parts.Add("<span class=\"tag\">computed</span>");
            return string.Join(" ", parts);
        }
    }

    private static string FlagsHtml(DbForeignKey fk) =>
        (fk.IsVirtual ? " <span class=\"tag\">inferred, not in the database</span>" : "") + (fk.IsDisabled ? " <span class=\"tag\">disabled</span>" : "");

    private static string TypeClass(DbObjectType type) => type switch
    {
        DbObjectType.Table => "t-table",
        DbObjectType.View => "t-view",
        DbObjectType.MaterializedView => "t-mview",
        DbObjectType.ForeignTable => "t-foreign",
        DbObjectType.Procedure => "t-proc",
        DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction => "t-func",
        DbObjectType.Trigger => "t-trigger",
        DbObjectType.Sequence => "t-seq",
        DbObjectType.Synonym => "t-syn",
        _ => "t-other"
    };

    private static string H(string text) => WebUtility.HtmlEncode(text);

    /// <summary>A comment as HTML: escaped, line breaks kept.</summary>
    private static string Text(string text) => H(text.Replace("\r\n", "\n")).Replace("\n", "<br>");

    // Object colours match the app's Objects tab (light and dark).
    private const string Css = """
        :root{color-scheme:light dark;--bg:#ffffff;--side:#f6f7f9;--text:#1f2430;--muted:#6b7385;--line:#e3e6eb;--card:#ffffff;--code:#f3f4f6;
          --accent:#1f6feb;--note:#fff8e1;--note-line:#e0c068;
          --c-table:#c25100;--c-view:#2e7d32;--c-mview:#00838f;--c-foreign:#8d6e63;--c-proc:#1565c0;--c-func:#7b1fa2;--c-trigger:#c62828;--c-seq:#8a6800;--c-syn:#ad1457;--c-other:#757575;
          --er-body:#ffffff;--er-head:#fbe9d7;--er-border:#c5ccd6;--er-text:#1f2430;--er-muted:#6b7385;--er-line:#8a94a6;--er-pk:#b7791f;--er-fk:#1565c0}
        @media (prefers-color-scheme:dark){:root{--bg:#16181d;--side:#1c1f26;--text:#e6e8ec;--muted:#9aa2b1;--line:#2c313b;--card:#1b1e24;--code:#252a33;
          --accent:#6ea8fe;--note:#2b2618;--note-line:#7a6528;
          --c-table:#f5a55a;--c-view:#7bc67e;--c-mview:#4dd0e1;--c-foreign:#c4a99c;--c-proc:#6eb0f7;--c-func:#c792ea;--c-trigger:#f27878;--c-seq:#e2c35c;--c-syn:#f48fb1;--c-other:#a0a0a0;
          --er-body:#1f232b;--er-head:#3a2c1f;--er-border:#3a404c;--er-text:#e6e8ec;--er-muted:#9aa2b1;--er-line:#6b7385;--er-pk:#e2c35c;--er-fk:#6eb0f7}}
        *{box-sizing:border-box}
        html{scroll-padding-top:16px}
        body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,-apple-system,sans-serif;display:flex;min-height:100vh}
        a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}
        code,pre{font-family:"Cascadia Mono",Consolas,"SF Mono",Menlo,monospace;font-size:12.5px}
        code{background:var(--code);padding:1px 4px;border-radius:4px}
        #side{width:270px;flex:none;background:var(--side);border-right:1px solid var(--line);position:sticky;top:0;height:100vh;display:flex;flex-direction:column}
        .side-head{padding:14px 14px 10px;border-bottom:1px solid var(--line)}
        .brand{font-weight:600;margin-bottom:8px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        #filter{width:100%;padding:6px 8px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--text);font:inherit}
        .side-list{overflow:auto;padding:8px 6px 24px;flex:1}
        .side-list a{display:block;padding:2px 8px;border-radius:5px;color:var(--text);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .side-list a:hover{background:var(--line);text-decoration:none}
        .side-list a.top{font-weight:600;margin-top:8px}
        .side-list .n{color:var(--muted);font-weight:400;font-size:12px}
        .side-list a.obj{padding-left:14px;font-size:13px}
        #nomatch{color:var(--muted);padding:0 8px}
        .dot{display:inline-block;width:8px;height:8px;border-radius:2px;margin-right:7px;vertical-align:middle;background:var(--c-other)}
        main{flex:1;min-width:0;padding:28px 40px 60px;max-width:1200px}
        header h1{margin:0 0 4px;font-size:26px}
        .sub{color:var(--muted);margin:0 0 12px}
        .stats{display:flex;flex-wrap:wrap;gap:8px;margin:14px 0}
        .stat{border:1px solid var(--line);border-radius:8px;padding:6px 12px;color:var(--text);background:var(--card)}
        .stat b{font-size:17px;margin-right:6px}
        .note{background:var(--note);border-left:3px solid var(--note-line);padding:8px 12px;border-radius:4px}
        .empty{color:var(--muted)}
        section{margin-top:34px}
        h2{font-size:20px;border-bottom:1px solid var(--line);padding-bottom:6px}
        .obj-card{border:1px solid var(--line);border-radius:10px;padding:14px 18px;margin:14px 0;background:var(--card)}
        h3{margin:0;font-size:17px;display:flex;align-items:center}
        h3 .self{margin-left:8px;color:var(--muted);opacity:0;font-weight:400}
        h3:hover .self{opacity:1}
        h4{margin:16px 0 6px;font-size:13px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}
        .facts{margin:6px 0 8px;color:var(--muted);font-size:13px;display:flex;flex-wrap:wrap;gap:12px;align-items:center}
        .badge{font-size:12px;font-weight:600;padding:1px 8px;border-radius:10px;border:1px solid currentColor}
        .comment{margin:6px 0 10px}
        .hint{color:var(--muted);margin-top:-4px}
        .scroll{overflow-x:auto}
        table.grid{border-collapse:collapse;width:100%;font-size:13px}
        .grid th{text-align:left;font-weight:600;color:var(--muted);border-bottom:1px solid var(--line);padding:5px 10px 5px 0;white-space:nowrap}
        .grid td{border-bottom:1px solid var(--line);padding:5px 10px 5px 0;vertical-align:top}
        .grid tr:last-child td{border-bottom:none}
        .grid .num{text-align:right;color:var(--muted);width:2.5em}
        .grid td.name{font-weight:500;white-space:nowrap}
        .grid td.pk{font-weight:700}
        .keys{white-space:nowrap}
        .tag{font-size:11px;padding:0 5px;border-radius:4px;background:var(--code);color:var(--muted);font-weight:600}
        .tag.pk{color:var(--er-pk)}.tag.fk{color:var(--er-fk)}
        .muted{color:var(--muted)}
        ul.rel{margin:0;padding-left:20px}
        details.src{margin-top:12px}
        details.src summary{cursor:pointer;color:var(--muted);font-size:13px}
        pre{background:var(--code);padding:10px 12px;border-radius:6px;overflow:auto;max-height:520px;margin:6px 0 0}
        pre code{background:none;padding:0}
        .diagram{overflow:auto;border:1px solid var(--line);border-radius:10px;background:var(--side);padding:8px;max-height:80vh}
        .diagram svg{display:block}
        .diagram a:hover rect{stroke:var(--accent)}
        footer{margin-top:40px;color:var(--muted);font-size:12px}
        .t-table{--c:var(--c-table)}.t-view{--c:var(--c-view)}.t-mview{--c:var(--c-mview)}.t-foreign{--c:var(--c-foreign)}.t-proc{--c:var(--c-proc)}
        .t-func{--c:var(--c-func)}.t-trigger{--c:var(--c-trigger)}.t-seq{--c:var(--c-seq)}.t-syn{--c:var(--c-syn)}.t-other{--c:var(--c-other)}
        .dot[class*=t-]{background:var(--c)}.badge[class*=t-]{color:var(--c)}
        .hidden{display:none!important}
        @media (max-width:820px){body{display:block}#side{position:static;width:auto;height:auto;max-height:40vh}main{padding:20px 16px}}
        @media print{#side{display:none}main{padding:0;max-width:none}.obj-card{break-inside:avoid}details.src pre{max-height:none}.diagram{max-height:none;overflow:visible}}

        """;

    // The filter hides sidebar entries and sections whose schema.name doesn't contain the text.
    private const string Script = """
        (function(){
          var input=document.getElementById('filter');if(!input)return;
          var links=[].slice.call(document.querySelectorAll('.side-list a.obj'));
          var cards=[].slice.call(document.querySelectorAll('.obj-card'));
          var groups=[].slice.call(document.querySelectorAll('.side-list .grp'));
          var none=document.getElementById('nomatch');
          input.addEventListener('input',function(){
            var q=input.value.trim().toLowerCase(),any=false;
            links.forEach(function(a){var hit=!q||a.getAttribute('data-name').indexOf(q)>=0;a.classList.toggle('hidden',!hit);if(hit)any=true;});
            cards.forEach(function(c){c.classList.toggle('hidden',!!q&&c.getAttribute('data-name').indexOf(q)<0);});
            groups.forEach(function(g){g.classList.toggle('hidden',!!q&&!g.querySelector('a.obj:not(.hidden)'));});
            none.hidden=any||!q;
          });
          document.addEventListener('keydown',function(e){if(e.key==='/'&&document.activeElement!==input){e.preventDefault();input.focus();}});
        })();

        """;
}
