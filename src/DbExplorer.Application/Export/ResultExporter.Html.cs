using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace DbExplorer.Application.Export;

/// <summary>Context shown in the "About this data" panel of an HTML export.</summary>
public sealed record HtmlExportInfo(string Title, DateTimeOffset GeneratedAt)
{
    /// <summary>Table or query the rows came from.</summary>
    public string? Source { get; init; }
    public string? Connection { get; init; }

    /// <summary>Rows / columns in the grid before its filters and hidden columns were applied.</summary>
    public int? FetchedRowCount { get; init; }
    public int? FetchedColumnCount { get; init; }

    /// <summary>The result had more rows than were fetched.</summary>
    public bool IsTruncated { get; init; }
    public long TotalRowCount { get; init; }
    public bool TotalRowCountIsExact { get; init; } = true;
}

public static partial class ResultExporter
{
    [GeneratedRegex(@"\{\{(FACTS|NOTES|META|HEADING)\}\}")]
    private static partial Regex Placeholder();

    // Keeps non-ASCII text readable in the file; HTML-sensitive characters (< > & ' ") are still
    // escaped, so cell text can never close the <script> block it is embedded in.
    private static readonly JavaScriptEncoder HtmlSafeJsonEncoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    /// <summary>
    /// A self-contained HTML page (no external resources) showing the rows in a table coloured by value type, with
    /// a global search box, per-column filters, click-to-sort headers, a "download CSV" of the filtered view and an
    /// "About this data" panel: where the rows came from and a per-column profile of the rows in view.
    /// </summary>
    public static string ToHtml(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows, HtmlExportInfo info)
    {
        // Per column: one kind when every non-null value shares it, else 'm' with a per-cell kind string.
        var kinds = new char[columns.Count];
        var types = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            char? kind = null;
            Type? type = null;
            foreach (var row in rows)
            {
                var value = i < row.Count ? row[i] : null;
                if (value is null or DBNull) continue;
                var k = ValueKind(value);
                if (kind is null) { kind = k; type = value.GetType(); }
                else if (kind != k) { kind = 'm'; break; }
                else if (type != value.GetType()) type = null;
            }
            kinds[i] = kind ?? 't';
            types[i] = kind switch
            {
                null => "",
                'm' => "mixed",
                _ when type is not null => TypeName(type),
                'n' => "number",
                'd' => "date/time",
                _ => "mixed"
            };
        }

        string data;
        using (var ms = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = HtmlSafeJsonEncoder }))
            {
                w.WriteStartObject();
                w.WriteStartArray("columns");
                foreach (var c in columns) w.WriteStringValue(c);
                w.WriteEndArray();
                w.WriteStartArray("kinds");
                foreach (var k in kinds) w.WriteStringValue(k.ToString());
                w.WriteEndArray();
                w.WriteStartArray("types");
                foreach (var t in types) w.WriteStringValue(t);
                w.WriteEndArray();
                w.WriteStartObject("cellKinds");
                for (var i = 0; i < columns.Count; i++)
                {
                    if (kinds[i] != 'm') continue;
                    var sb2 = new StringBuilder(rows.Count);
                    foreach (var row in rows)
                    {
                        var value = i < row.Count ? row[i] : null;
                        sb2.Append(value is null or DBNull ? '-' : ValueKind(value));
                    }
                    w.WriteString(i.ToString(CultureInfo.InvariantCulture), sb2.ToString());
                }
                w.WriteEndObject();
                w.WriteStartArray("rows");
                foreach (var row in rows)
                {
                    w.WriteStartArray();
                    for (var i = 0; i < columns.Count; i++)
                    {
                        var value = i < row.Count ? row[i] : null;
                        if (value is null or DBNull) w.WriteNullValue();
                        else w.WriteStringValue(FormatInvariant(value));
                    }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            data = Encoding.UTF8.GetString(ms.ToArray());
        }

        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var title = string.IsNullOrWhiteSpace(info.Title) ? "Results" : info.Title;
        var exported = info.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        var facts = new StringBuilder();
        void Fact(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
        }
        Fact("Source", info.Source);
        Fact("Connection", info.Connection);
        Fact("Exported", exported);
        Fact("Rows in this file", rows.Count.ToString("N0", CultureInfo.InvariantCulture));
        Fact("Columns in this file", columns.Count.ToString("N0", CultureInfo.InvariantCulture));

        var notes = new List<string>();
        if (info.FetchedRowCount is { } fetched && fetched != rows.Count)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Grid filters were applied: {rows.Count:N0} of the {fetched:N0} fetched rows were exported."));
        if (info.FetchedColumnCount is { } allColumns && allColumns > columns.Count)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{allColumns - columns.Count:N0} hidden column(s) were left out ({columns.Count:N0} of {allColumns:N0} exported)."));
        if (info.IsTruncated)
        {
            var fetchedRows = info.FetchedRowCount ?? rows.Count;
            notes.Add(info.TotalRowCountIsExact
                ? string.Create(CultureInfo.InvariantCulture, $"The query returned {info.TotalRowCount:N0} rows; only the first {fetchedRows:N0} were fetched (row limit).")
                : string.Create(CultureInfo.InvariantCulture, $"The query returned more than {fetchedRows:N0} rows; reading stopped at the row limit."));
        }
        var noteHtml = notes.Count == 0 ? "" : "<ul class=\"notes\">" + string.Concat(notes.Select(n => "<li>" + E(n) + "</li>")) + "</ul>";

        var meta = string.Create(CultureInfo.InvariantCulture, $"{rows.Count:N0} row(s) · {columns.Count:N0} column(s) · exported {exported}");
        if (!string.IsNullOrWhiteSpace(info.Connection)) meta = info.Connection + " · " + meta;

        var sb = new StringBuilder(data.Length + HtmlTemplate.Length + HtmlScript.Length + 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(E(title)).Append("</title>\n")
          // One pass, so text substituted for one placeholder is never scanned for another.
          .Append(Placeholder().Replace(HtmlTemplate, m => m.Groups[1].Value switch
          {
              "FACTS" => facts.ToString(),
              "NOTES" => noteHtml,
              "META" => E(meta),
              _ => E(title)
          }))
          .Append("<script id=\"data\" type=\"application/json\">").Append(data).Append("</script>\n")
          .Append("<script>").Append(HtmlScript).Append("</script>\n</body></html>\n");
        return sb.ToString();
    }

    /// <summary>One-letter value kind the page colours by; mirrors the result grid's cell colours.</summary>
    private static char ValueKind(object value) => value switch
    {
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => 'n',
        DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => 'd',
        bool => 'b',
        Guid => 'g',
        byte[] => 'x',
        _ => 't'
    };

    private static string TypeName(Type type) => type == typeof(byte[]) ? "binary" : Type.GetTypeCode(type) switch
    {
        TypeCode.String or TypeCode.Char => "text",
        TypeCode.Boolean => "boolean",
        TypeCode.Byte or TypeCode.SByte => "tinyint",
        TypeCode.Int16 or TypeCode.UInt16 => "smallint",
        TypeCode.Int32 or TypeCode.UInt32 => "int",
        TypeCode.Int64 or TypeCode.UInt64 => "bigint",
        TypeCode.Decimal => "decimal",
        TypeCode.Single => "real",
        TypeCode.Double => "float",
        TypeCode.DateTime => "datetime",
        _ when type == typeof(DateTimeOffset) => "datetimeoffset",
        _ when type == typeof(DateOnly) => "date",
        _ when type == typeof(TimeOnly) || type == typeof(TimeSpan) => "time",
        _ when type == typeof(Guid) => "uuid",
        _ => type.Name
    };

    private const string HtmlTemplate = """
        <style>
        :root{--bg:#fff;--fg:#1b1b1b;--muted:#6b6b6b;--line:#e3e3e3;--head:#f4f4f4;--hover:#f3f6fb;--accent:#2563eb;--input:#fff;--panel:#fafafa;--bar:#2563eb33;
          --k-t:#1f1f1f;--k-n:#1750eb;--k-d:#871094;--k-b:#b35a00;--k-g:#00796b;--k-x:#8c6c3e;--k-null:#9a9a9a}
        @media (prefers-color-scheme:dark){:root{--bg:#1e1f22;--fg:#dcdcdc;--muted:#9a9a9a;--line:#393b40;--head:#2b2d30;--hover:#26282e;--accent:#6ea0ff;--input:#161719;--panel:#232427;--bar:#6ea0ff40;
          --k-t:#dcdcdc;--k-n:#2aacb8;--k-d:#c77dbb;--k-b:#cf8e6d;--k-g:#6aab73;--k-x:#bba07a;--k-null:#7a7e85}}
        *{box-sizing:border-box}
        body{font:14px/1.45 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;margin:0;color:var(--fg);background:var(--bg)}
        header{padding:16px 20px 8px}
        h1{font-size:20px;margin:0 0 4px;overflow-wrap:anywhere}
        h2{font-size:14px;margin:16px 0 6px}
        .meta,.sub{color:var(--muted);font-size:12.5px}
        details.about{margin:4px 20px 8px;border:1px solid var(--line);border-radius:6px;background:var(--panel)}
        details.about>summary{cursor:pointer;padding:8px 12px;font-weight:600}
        .about-body{padding:0 12px 12px;overflow:auto}
        .facts{display:flex;flex-wrap:wrap;gap:8px 32px;align-items:flex-start}
        table.kv th{color:var(--muted);font-weight:normal;text-align:left;padding:2px 16px 2px 0;border:0;background:none;position:static}
        table.kv td{border:0;padding:2px 0;font-family:inherit;max-width:none;white-space:normal}
        ul.notes{margin:4px 0;padding-left:18px;color:var(--k-b)}
        .legend{display:flex;flex-wrap:wrap;gap:4px 14px;font-size:12.5px}
        .legend span::before{content:"";display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:5px;background:currentColor;vertical-align:-1px}
        table.profile{min-width:0;width:100%}
        table.profile th{position:static;background:var(--head);white-space:nowrap;cursor:default}
        table.profile td{max-width:280px}
        table.profile td.name{font-family:inherit;font-weight:600}
        table.profile td.ty{text-align:left}
        .fill{display:inline-block;width:60px;height:8px;border-radius:4px;background:var(--line);vertical-align:middle;margin-right:6px;overflow:hidden}
        .fill i{display:block;height:100%;background:var(--accent)}
        .bar{display:flex;flex-wrap:wrap;gap:8px;align-items:center;padding:8px 20px 12px}
        input{font:inherit;color:var(--fg);background:var(--input);border:1px solid var(--line);border-radius:4px;padding:4px 8px}
        input:focus{outline:2px solid var(--accent);outline-offset:-1px}
        #q{width:min(420px,100%)}
        button{font:inherit;color:var(--fg);background:var(--head);border:1px solid var(--line);border-radius:4px;padding:4px 10px;cursor:pointer}
        button:hover{border-color:var(--accent)}
        #count{color:var(--muted);margin-left:auto}
        .wrap{overflow:auto;max-height:85vh;border-top:1px solid var(--line)}
        table{border-collapse:collapse;font-size:12.5px}
        .wrap table{min-width:100%}
        th,td{border-bottom:1px solid var(--line);padding:3px 8px;text-align:left;vertical-align:top}
        thead th{background:var(--head);position:sticky;z-index:1}
        thead tr.names th{top:0;cursor:pointer;user-select:none;white-space:nowrap}
        thead tr.filters th{top:var(--names-h,40px);padding:2px 4px}
        thead tr.filters input{width:100%;min-width:70px;padding:2px 6px;font-size:12px}
        th .dir{color:var(--accent);margin-left:4px}
        th .type{display:block;font-weight:normal;font-size:11px}
        td{max-width:420px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-family:ui-monospace,Cascadia Mono,Consolas,Menlo,monospace;color:var(--k-t)}
        td.open{white-space:pre-wrap;overflow-wrap:anywhere}
        td.k-n{color:var(--k-n);text-align:right}.k-d{color:var(--k-d)}.k-b{color:var(--k-b)}.k-g{color:var(--k-g)}.k-x{color:var(--k-x)}.k-t{color:var(--k-t)}.k-n{color:var(--k-n)}
        td.rn,th.rn{color:var(--muted);text-align:right;width:1%}
        tbody tr:hover{background:var(--hover)}
        .null,td.null{color:var(--k-null);font-style:italic}
        .more{padding:10px 20px;display:flex;gap:8px}
        .hint{color:var(--muted);font-size:12px;padding:0 20px 8px}
        code{font-size:11.5px}
        </style></head><body>
        <header><h1>{{HEADING}}</h1><div class="meta">{{META}}</div></header>
        <details class="about" id="about" open>
          <summary>About this data</summary>
          <div class="about-body">
            <div class="facts">
              <div><table class="kv">{{FACTS}}</table>{{NOTES}}</div>
              <div><div class="sub">Values are coloured by type</div>
                <div class="legend"><span class="k-t">text</span><span class="k-n">number</span><span class="k-d">date / time</span><span class="k-b">boolean</span><span class="k-g">identifier</span><span class="k-x">binary</span><span class="null">NULL</span></div></div>
            </div>
            <h2>Column profile <span class="sub" id="profile-of"></span></h2>
            <table class="profile"><thead><tr><th>Column</th><th>Type</th><th>Filled</th><th>Nulls</th><th>Distinct</th><th>Min</th><th>Max</th><th>Average</th><th>Most common</th></tr></thead><tbody id="profile"></tbody></table>
          </div>
        </details>
        <div class="bar">
          <input id="q" type="search" placeholder="Search all columns…  (press / to focus)" autocomplete="off">
          <button id="clear" type="button">Clear filters</button>
          <button id="csv" type="button">Download CSV of view</button>
          <span id="count"></span>
        </div>
        <div class="hint">Column filters: <code>text</code> contains · <code>=text</code> equals · <code>!text</code> doesn't contain · <code>&gt;10</code> <code>&lt;=5</code> compare · <code>null</code> / <code>!null</code>. Click a header to sort, a cell to expand it.</div>
        <div class="wrap"><table><thead><tr class="names"></tr><tr class="filters"></tr></thead><tbody id="rows"></tbody></table></div>
        <div class="more"><button id="more" type="button">Show more</button><button id="all" type="button">Show all</button></div>

        """;

    private const string HtmlScript = """
        (function(){
        "use strict";
        var D=JSON.parse(document.getElementById("data").textContent);
        var cols=D.columns,kinds=D.kinds,types=D.types,cellKinds=D.cellKinds||{},rows=D.rows,PAGE=500;
        var num=kinds.map(function(k){return k==="n";});
        var KIND_NAMES={t:"text",n:"number",d:"date / time",b:"boolean",g:"identifier",x:"binary",m:"mixed"};
        var lower=new Array(rows.length),view=[],shown=0,sortCol=-1,sortDir=1,filters=cols.map(function(){return null;}),terms=[];
        var namesRow=document.querySelector("tr.names"),filterRow=document.querySelector("tr.filters"),tbody=document.getElementById("rows");
        var about=document.getElementById("about");
        function esc(s){return String(s).replace(/[&<>"]/g,function(c){return c==="&"?"&amp;":c==="<"?"&lt;":c===">"?"&gt;":"&quot;";});}
        function low(i){var l=lower[i];if(!l){l=lower[i]=rows[i].map(function(v){return v===null?null:v.toLowerCase();});}return l;}
        function toNum(v){var n=parseFloat(v);return isNaN(n)?null:n;}
        function kindAt(r,c){return kinds[c]==="m"?(cellKinds[c]||"").charAt(r)||"t":kinds[c];}
        function fmt(n){return n.toLocaleString();}
        function clip(s){return s.length>60?s.slice(0,59)+"…":s;}

        function parseFilter(text,c){
          var t=text.trim();if(!t)return null;
          var tl=t.toLowerCase();
          if(tl==="null")return function(v){return v===null;};
          if(tl==="!null")return function(v){return v!==null;};
          var m=/^(>=|<=|<>|>|<|=|!=|!)\s*(.*)$/.exec(t);
          if(!m){return function(v){return v!==null&&v.indexOf(tl)>=0;};}
          var op=m[1],arg=m[2],al=arg.toLowerCase();
          if(op==="!")return function(v){return v===null||v.indexOf(al)<0;};
          if(op==="=")return function(v){return v!==null&&(num[c]?toNum(v)===toNum(arg):v===al);};
          if(op==="!="||op==="<>")return function(v){return v===null||(num[c]?toNum(v)!==toNum(arg):v!==al);};
          var useNum=num[c]&&toNum(arg)!==null,an=toNum(arg);
          return function(v){
            if(v===null)return false;
            var r=useNum?(toNum(v)-an):(v<al?-1:v>al?1:0);
            return op===">"?r>0:op==="<"?r<0:op===">="?r>=0:r<=0;
          };
        }

        function compare(a,b){
          var x=rows[a][sortCol],y=rows[b][sortCol];
          if(x===y)return a-b;
          if(x===null)return 1;
          if(y===null)return -1;
          var r=num[sortCol]?toNum(x)-toNum(y):x.localeCompare(y,undefined,{numeric:true,sensitivity:"base"});
          return r===0?a-b:r*sortDir;
        }

        function apply(){
          view=[];
          for(var i=0;i<rows.length;i++){
            var l=low(i),ok=true,c,t;
            for(c=0;c<cols.length&&ok;c++)if(filters[c]&&!filters[c](l[c]))ok=false;
            for(t=0;t<terms.length&&ok;t++){
              var hit=false;
              for(c=0;c<cols.length&&!hit;c++)if(l[c]!==null&&l[c].indexOf(terms[t])>=0)hit=true;
              ok=hit;
            }
            if(ok)view.push(i);
          }
          if(sortCol>=0)view.sort(compare);
          shown=0;tbody.innerHTML="";render(PAGE);
          if(about.open)profile();
        }

        function render(n){
          var end=Math.min(view.length,shown+n),html=[];
          for(var k=shown;k<end;k++){
            var ri=view[k],r=rows[ri];
            html.push("<tr><td class=\"rn\">"+(ri+1)+"</td>");
            for(var c=0;c<cols.length;c++){
              var v=r[c];
              html.push(v===null?"<td class=\"null\">NULL</td>":"<td class=\"k-"+kindAt(ri,c)+"\">"+esc(v)+"</td>");
            }
            html.push("</tr>");
          }
          tbody.insertAdjacentHTML("beforeend",html.join(""));
          shown=end;
          document.getElementById("count").textContent="Showing "+fmt(shown)+" of "+fmt(view.length)+
            (view.length!==rows.length?" (filtered from "+fmt(rows.length)+")":"")+" row(s)";
          var rest=view.length>shown;
          document.getElementById("more").style.display=rest?"":"none";
          document.getElementById("all").style.display=rest?"":"none";
        }

        function profile(){
          var out=[],n=view.length;
          document.getElementById("profile-of").textContent="· "+fmt(n)+(n===rows.length?" row(s)":" row(s) in view (filters applied)");
          for(var c=0;c<cols.length;c++){
            var counts=new Map(),nulls=0,min=null,max=null,sum=0,cnt=0,minLen=Infinity,maxLen=0,isNum=num[c];
            for(var k=0;k<n;k++){
              var v=rows[view[k]][c];
              if(v===null){nulls++;continue;}
              counts.set(v,(counts.get(v)||0)+1);
              if(isNum){var x=toNum(v);if(x!==null){sum+=x;cnt++;if(min===null||x<min)min=x;if(max===null||x>max)max=x;}}
              else{
                if(min===null||v<min)min=v;if(max===null||v>max)max=v;
                if(v.length<minLen)minLen=v.length;if(v.length>maxLen)maxLen=v.length;sum+=v.length;cnt++;
              }
            }
            var filled=n-nulls,pct=n?Math.round(filled*100/n):0,k2=kinds[c];
            var avg="",mn="",mx="";
            if(filled){
              if(isNum){mn=fmt(min);mx=fmt(max);avg=cnt?(sum/cnt).toLocaleString(undefined,{maximumFractionDigits:4}):"";}
              else if(k2==="t"){mn=clip(min);mx=clip(max);avg=minLen===maxLen?minLen+" chars":(sum/cnt).toFixed(1)+" chars ("+minLen+"–"+maxLen+")";}
              else if(k2!=="x"&&k2!=="g"&&k2!=="b"){mn=clip(min);mx=clip(max);}
            }
            var top="";
            if(filled&&counts.size<=filled/2){
              top=Array.from(counts.entries()).sort(function(a,b){return b[1]-a[1];}).slice(0,3)
                .map(function(e){return "<span class=\"k-"+(k2==="m"?"t":k2)+"\">"+esc(clip(e[0]))+"</span> <span class=\"sub\">×"+fmt(e[1])+"</span>";}).join(", ");
            }else if(filled){top="<span class=\"sub\">"+(counts.size===filled?"all distinct":"mostly distinct")+"</span>";}
            var cls="k-"+(k2==="m"?"t":k2);
            out.push("<tr><td class=\"name\">"+esc(cols[c])+"</td><td class=\"ty "+cls+"\">"+esc(types[c]||KIND_NAMES[k2])+"</td>"+
              "<td><span class=\"fill\"><i style=\"width:"+pct+"%\"></i></span>"+pct+"%</td>"+
              "<td>"+fmt(nulls)+"</td><td>"+fmt(counts.size)+"</td>"+
              "<td class=\""+cls+"\">"+esc(mn)+"</td><td class=\""+cls+"\">"+esc(mx)+"</td><td>"+esc(avg)+"</td><td>"+top+"</td></tr>");
          }
          document.getElementById("profile").innerHTML=out.join("");
        }

        function headers(){
          var h=["<th class=\"rn\">#</th>"];
          cols.forEach(function(name,c){
            var dir=c===sortCol?(sortDir>0?"▲":"▼"):"";
            h.push("<th data-c=\""+c+"\" title=\"Sort by "+esc(name)+"\">"+esc(name)+"<span class=\"dir\">"+dir+"</span>"+
              "<span class=\"type k-"+(kinds[c]==="m"?"t":kinds[c])+"\">"+esc(types[c]||"")+"</span></th>");
          });
          namesRow.innerHTML=h.join("");
          document.documentElement.style.setProperty("--names-h",namesRow.offsetHeight+"px");
        }

        filterRow.innerHTML="<th class=\"rn\"></th>"+cols.map(function(name,c){
          return "<th><input data-c=\""+c+"\" type=\"search\" placeholder=\"filter\" aria-label=\"Filter "+esc(name)+"\"></th>";
        }).join("");

        var timer=0;
        function later(){clearTimeout(timer);timer=setTimeout(apply,rows.length>20000?250:80);}
        filterRow.addEventListener("input",function(e){
          var c=+e.target.getAttribute("data-c");filters[c]=parseFilter(e.target.value,c);later();
        });
        document.getElementById("q").addEventListener("input",function(e){
          terms=e.target.value.toLowerCase().split(/\s+/).filter(Boolean);later();
        });
        namesRow.addEventListener("click",function(e){
          var th=e.target.closest("th[data-c]");if(!th)return;
          var c=+th.getAttribute("data-c");
          if(sortCol!==c){sortCol=c;sortDir=1;}else if(sortDir>0){sortDir=-1;}else{sortCol=-1;}
          headers();apply();
        });
        tbody.addEventListener("click",function(e){
          var td=e.target.closest("td");if(td&&!td.classList.contains("rn")&&!getSelection().toString())td.classList.toggle("open");
        });
        about.addEventListener("toggle",function(){if(about.open)profile();});
        document.getElementById("more").addEventListener("click",function(){render(PAGE);});
        document.getElementById("all").addEventListener("click",function(){render(view.length);});
        document.getElementById("clear").addEventListener("click",function(){
          document.querySelectorAll(".bar input,tr.filters input").forEach(function(i){i.value="";});
          filters=cols.map(function(){return null;});terms=[];sortCol=-1;headers();apply();
        });
        document.getElementById("csv").addEventListener("click",function(){
          function f(v,c){
            if(v===null)return "";
            if(!num[c]&&/^[=+\-@\t\r]/.test(v))v="'"+v;
            return /[",\r\n]|^ | $/.test(v)?"\""+v.replace(/"/g,"\"\"")+"\"":v;
          }
          var out=[cols.map(function(n){return f(n,-1);}).join(",")];
          view.forEach(function(i){out.push(rows[i].map(f).join(","));});
          var a=document.createElement("a");
          a.href=URL.createObjectURL(new Blob(["﻿"+out.join("\r\n")+"\r\n"],{type:"text/csv;charset=utf-8"}));
          a.download=(document.title||"results").replace(/[\\/:*?"<>|]+/g,"_")+".csv";
          document.body.appendChild(a);a.click();a.remove();
          setTimeout(function(){URL.revokeObjectURL(a.href);},1000);
        });
        document.addEventListener("keydown",function(e){
          if(e.key==="/"&&!/INPUT|TEXTAREA/.test(document.activeElement.tagName)){e.preventDefault();document.getElementById("q").focus();}
        });

        headers();apply();
        })();
        """;
}
