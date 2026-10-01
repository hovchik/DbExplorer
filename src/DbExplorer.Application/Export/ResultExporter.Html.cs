using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace DbExplorer.Application.Export;

public static partial class ResultExporter
{
    // Keeps non-ASCII text readable in the file; HTML-sensitive characters (< > & ' ") are still
    // escaped, so cell text can never close the <script> block it is embedded in.
    private static readonly JavaScriptEncoder HtmlSafeJsonEncoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    /// <summary>
    /// A self-contained HTML page (no external resources) showing the rows in a table with a global search box,
    /// per-column filters, click-to-sort headers and a "download CSV" of the filtered view.
    /// </summary>
    public static string ToHtml(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows, string title, DateTimeOffset generatedAt)
    {
        var numeric = new bool[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var any = false;
            var all = true;
            foreach (var row in rows)
            {
                var value = i < row.Count ? row[i] : null;
                if (value is null or DBNull) continue;
                any = true;
                if (!IsNumeric(value)) { all = false; break; }
            }
            numeric[i] = any && all;
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
                w.WriteStartArray("numeric");
                foreach (var n in numeric) w.WriteBooleanValue(n);
                w.WriteEndArray();
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

        var heading = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? "Results" : title);
        var meta = string.Create(CultureInfo.InvariantCulture,
            $"{rows.Count:N0} row(s) · {columns.Count:N0} column(s) · exported {generatedAt:yyyy-MM-dd HH:mm:ss zzz}");

        var sb = new StringBuilder(data.Length + HtmlTemplate.Length + 512);
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(heading).Append("</title>\n")
          .Append(HtmlTemplate.Replace("{{HEADING}}", heading).Replace("{{META}}", WebUtility.HtmlEncode(meta)))
          .Append("<script id=\"data\" type=\"application/json\">").Append(data).Append("</script>\n")
          .Append("<script>").Append(HtmlScript).Append("</script>\n</body></html>\n");
        return sb.ToString();
    }

    private const string HtmlTemplate = """
        <style>
        :root{--bg:#fff;--fg:#1b1b1b;--muted:#6b6b6b;--line:#e3e3e3;--head:#f4f4f4;--hover:#f7f9fc;--accent:#2563eb;--input:#fff}
        @media (prefers-color-scheme:dark){:root{--bg:#1e1e1e;--fg:#e6e6e6;--muted:#9a9a9a;--line:#383838;--head:#2a2a2a;--hover:#262a31;--accent:#6ea0ff;--input:#151515}}
        *{box-sizing:border-box}
        body{font:14px/1.45 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;margin:0;color:var(--fg);background:var(--bg)}
        header{padding:16px 20px 8px}
        h1{font-size:20px;margin:0 0 4px}
        .meta{color:var(--muted);font-size:12.5px}
        .bar{display:flex;flex-wrap:wrap;gap:8px;align-items:center;padding:8px 20px 12px}
        input{font:inherit;color:var(--fg);background:var(--input);border:1px solid var(--line);border-radius:4px;padding:4px 8px}
        input:focus{outline:2px solid var(--accent);outline-offset:-1px}
        #q{width:min(420px,100%)}
        button{font:inherit;color:var(--fg);background:var(--head);border:1px solid var(--line);border-radius:4px;padding:4px 10px;cursor:pointer}
        button:hover{border-color:var(--accent)}
        #count{color:var(--muted);margin-left:auto}
        .wrap{overflow:auto;max-height:calc(100vh - 120px);border-top:1px solid var(--line)}
        table{border-collapse:collapse;font-size:12.5px;min-width:100%}
        th,td{border-bottom:1px solid var(--line);padding:3px 8px;text-align:left;vertical-align:top}
        thead th{background:var(--head);position:sticky;z-index:1}
        thead tr.names th{top:0;cursor:pointer;user-select:none;white-space:nowrap}
        thead tr.filters th{top:var(--names-h,27px);padding:2px 4px}
        thead tr.filters input{width:100%;min-width:70px;padding:2px 6px;font-size:12px}
        th .dir{color:var(--accent);margin-left:4px}
        td{max-width:420px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-family:ui-monospace,Cascadia Mono,Consolas,Menlo,monospace}
        td.open{white-space:pre-wrap;overflow-wrap:anywhere}
        td.num{text-align:right}
        td.rn,th.rn{color:var(--muted);text-align:right;width:1%}
        tbody tr:hover{background:var(--hover)}
        .null{color:var(--muted);font-style:italic}
        .more{padding:10px 20px;display:flex;gap:8px}
        .hint{color:var(--muted);font-size:12px;padding:0 20px 8px}
        code{font-size:11.5px}
        </style></head><body>
        <header><h1>{{HEADING}}</h1><div class="meta">{{META}}</div></header>
        <div class="bar">
          <input id="q" type="search" placeholder="Search all columns…  (press / to focus)" autocomplete="off">
          <button id="clear" type="button">Clear filters</button>
          <button id="csv" type="button">Download CSV of view</button>
          <span id="count"></span>
        </div>
        <div class="hint">Column filters: <code>text</code> contains · <code>=text</code> equals · <code>!text</code> doesn't contain · <code>&gt;10</code> <code>&lt;=5</code> compare · <code>null</code> / <code>!null</code>. Click a header to sort, a cell to expand it.</div>
        <div class="wrap"><table><thead><tr class="names"></tr><tr class="filters"></tr></thead><tbody></tbody></table></div>
        <div class="more"><button id="more" type="button">Show more</button><button id="all" type="button">Show all</button></div>

        """;

    private const string HtmlScript = """
        (function(){
        "use strict";
        var D=JSON.parse(document.getElementById("data").textContent);
        var cols=D.columns,num=D.numeric,rows=D.rows,PAGE=500;
        var lower=new Array(rows.length),view=[],shown=0,sortCol=-1,sortDir=1,filters=cols.map(function(){return null;}),terms=[];
        var namesRow=document.querySelector("tr.names"),filterRow=document.querySelector("tr.filters"),tbody=document.querySelector("tbody");
        function esc(s){return s.replace(/[&<>"]/g,function(c){return c==="&"?"&amp;":c==="<"?"&lt;":c===">"?"&gt;":"&quot;";});}
        function low(i){var l=lower[i];if(!l){l=lower[i]=rows[i].map(function(v){return v===null?null:v.toLowerCase();});}return l;}
        function toNum(v){var n=parseFloat(v);return isNaN(n)?null:n;}

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
        }

        function render(n){
          var end=Math.min(view.length,shown+n),html=[];
          for(var k=shown;k<end;k++){
            var r=rows[view[k]];
            html.push("<tr><td class=\"rn\">"+(view[k]+1)+"</td>");
            for(var c=0;c<cols.length;c++){
              var v=r[c];
              html.push(v===null?"<td class=\"null\">NULL</td>":"<td"+(num[c]?" class=\"num\"":"")+">"+esc(v)+"</td>");
            }
            html.push("</tr>");
          }
          tbody.insertAdjacentHTML("beforeend",html.join(""));
          shown=end;
          document.getElementById("count").textContent="Showing "+shown.toLocaleString()+" of "+view.length.toLocaleString()+
            (view.length!==rows.length?" (filtered from "+rows.length.toLocaleString()+")":"")+" row(s)";
          var rest=view.length>shown;
          document.getElementById("more").style.display=rest?"":"none";
          document.getElementById("all").style.display=rest?"":"none";
        }

        function headers(){
          var h=["<th class=\"rn\">#</th>"];
          cols.forEach(function(name,c){
            var dir=c===sortCol?(sortDir>0?"▲":"▼"):"";
            h.push("<th data-c=\""+c+"\" title=\"Sort by "+esc(name)+"\">"+esc(name)+"<span class=\"dir\">"+dir+"</span></th>");
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
        document.getElementById("more").addEventListener("click",function(){render(PAGE);});
        document.getElementById("all").addEventListener("click",function(){render(view.length);});
        document.getElementById("clear").addEventListener("click",function(){
          document.querySelectorAll("input").forEach(function(i){i.value="";});
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
