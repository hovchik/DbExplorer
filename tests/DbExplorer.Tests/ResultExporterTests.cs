using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using DbExplorer.Application.Export;

namespace DbExplorer.Tests;

public class ResultExporterTests
{
    private static readonly string[] Columns = ["Id", "Name", "Price", "Created", "Active"];

    private static readonly IReadOnlyList<object?>[] Rows =
    [
        [1, "Widget, large", 9.5m, new DateTime(2024, 1, 2, 3, 4, 5), true],
        [2, "Say \"hi\"\nthere", null, new DateTime(2024, 5, 6), false],
        [3, "=HYPERLINK(\"http://evil\")", -1.25m, null, null]
    ];

    [Fact]
    public void Csv_quotes_special_characters_and_neutralizes_formulas()
    {
        var csv = ResultExporter.ToCsv(Columns, Rows);
        var lines = csv.Split("\r\n");

        Assert.Equal("Id,Name,Price,Created,Active", lines[0]);
        Assert.Equal("1,\"Widget, large\",9.5,2024-01-02 03:04:05,true", lines[1]);
        Assert.StartsWith("2,\"Say \"\"hi\"\"\nthere\",,2024-05-06,false", csv.Split("\r\n", 3)[2]);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", csv);
        // Negative numbers are data, not formulas: left alone.
        Assert.Contains(",-1.25,", csv);
    }

    [Fact]
    public void Tsv_flattens_tabs_and_newlines()
    {
        var tsv = ResultExporter.ToTsv(["a", "b"], [["x\ty", "line1\nline2"]], includeHeader: false);
        Assert.Equal("x y\tline1 line2" + Environment.NewLine, tsv);
    }

    [Fact]
    public void Json_keeps_types_and_deduplicates_column_names()
    {
        var json = ResultExporter.ToJson(["Id", "Id", "Name"], [[1, 2, null]]);
        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement[0];

        Assert.Equal(1, row.GetProperty("Id").GetInt32());
        Assert.Equal(2, row.GetProperty("Id_2").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("Name").ValueKind);
    }

    [Fact]
    public void Markdown_escapes_pipes_and_marks_nulls()
    {
        var md = ResultExporter.ToMarkdown(["a", "b"], [["x|y", null]]);
        Assert.Contains("| x\\|y | *NULL* |", md);
    }

    [Fact]
    public void Html_embeds_rows_as_json_that_cannot_break_out_of_the_script_block()
    {
        var html = ResultExporter.ToHtml(["Id", "Note"], [[1, "</script><b>x</b> & café"], [2.5m, null]], "<dbo>.T", DateTimeOffset.UnixEpoch);

        Assert.Contains("<title>&lt;dbo&gt;.T</title>", html);
        const string open = "<script id=\"data\" type=\"application/json\">";
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var json = html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("<", json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Note", root.GetProperty("columns")[1].GetString());
        Assert.True(root.GetProperty("numeric")[0].GetBoolean());
        Assert.False(root.GetProperty("numeric")[1].GetBoolean());
        Assert.Equal("</script><b>x</b> & café", root.GetProperty("rows")[0][1].GetString());
        Assert.Equal("2.5", root.GetProperty("rows")[1][0].GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rows")[1][1].ValueKind);
        Assert.Contains("café", json);
    }

    [Theory]
    [InlineData(SqlDialect.SqlServer, "INSERT INTO [dbo].[T] ([Id], [Name], [Price], [Created], [Active]) VALUES (1, N'Widget, large', 9.5, N'2024-01-02 03:04:05', 1);")]
    [InlineData(SqlDialect.Postgres, "INSERT INTO [dbo].[T] ([Id], [Name], [Price], [Created], [Active]) VALUES (1, 'Widget, large', 9.5, '2024-01-02 03:04:05', TRUE);")]
    public void Insert_statements_render_dialect_literals(SqlDialect dialect, string expectedFirst)
    {
        var sql = ResultExporter.ToInsertStatements(Columns, Rows, "[dbo].[T]", dialect, id => $"[{id}]");
        Assert.Equal(expectedFirst, sql.Split('\n')[0]);
        Assert.Contains("NULL, NULL);", sql);
    }

    [Fact]
    public void Sql_literals_escape_quotes_and_binary()
    {
        Assert.Equal("N'O''Brien'", ResultExporter.SqlLiteral("O'Brien", SqlDialect.SqlServer));
        Assert.Equal("0x0AFF", ResultExporter.SqlLiteral(new byte[] { 0x0A, 0xFF }, SqlDialect.SqlServer));
        Assert.Equal("'\\x0AFF'::bytea", ResultExporter.SqlLiteral(new byte[] { 0x0A, 0xFF }, SqlDialect.Postgres));
    }

    [Fact]
    public void Xlsx_is_a_valid_package_with_typed_cells()
    {
        using var ms = new MemoryStream();
        ResultExporter.WriteXlsx(ms, Columns, Rows, "My: Sheet[1]");
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Equal("My Sheet1", workbook.Descendants(ns + "sheet").Single().Attribute("name")!.Value);

        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var rows = sheet.Descendants(ns + "row").ToList();
        Assert.Equal(4, rows.Count);

        var a2 = rows[1].Elements(ns + "c").First();
        Assert.Equal("A2", a2.Attribute("r")!.Value);
        Assert.Null(a2.Attribute("t"));
        Assert.Equal("1", a2.Element(ns + "v")!.Value);

        var b3 = rows[2].Elements(ns + "c").First(c => c.Attribute("r")!.Value == "B3");
        Assert.Equal("inlineStr", b3.Attribute("t")!.Value);
        Assert.Equal("Say \"hi\"\nthere", b3.Descendants(ns + "t").Single().Value);

        // NULL cells are omitted rather than written empty.
        Assert.DoesNotContain(rows[2].Elements(ns + "c"), c => c.Attribute("r")!.Value == "C3");
    }

    [Fact]
    public void Xlsx_strips_characters_xml_cannot_hold()
    {
        using var ms = new MemoryStream();
        ResultExporter.WriteXlsx(ms, ["v"], [["a\u0001b"]]);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Assert.Contains(sheet.Descendants(), e => e.Value == "ab");
    }
}
