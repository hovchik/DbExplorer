using System.Text;
using DbExplorer.Application.Design;
using DbExplorer.Application.Export;
using DbExplorer.Application.Import;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public sealed class TableImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-import-" + Guid.NewGuid().ToString("N"));

    public TableImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string WriteFile(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private static readonly ImportValueOptions Defaults = new();

    // ----- Delimited text -----

    [Fact]
    public void Reads_quoted_fields_with_delimiters_quotes_and_line_breaks()
    {
        var rows = DelimitedText.ParseAll("id,name,note\r\n1,\"Smith, John\",\"said \"\"hi\"\"\nthen left\"\r\n2,Ann,\n", ',');

        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "Smith, John", "said \"hi\"\nthen left"], rows[1]);
        Assert.Equal(["2", "Ann", ""], rows[2]);
    }

    [Fact]
    public void Skips_blank_lines_and_keeps_a_last_line_without_a_break()
    {
        var rows = DelimitedText.ParseAll("a;b\n\n1;2\n\r\n3;4", ';');

        Assert.Equal([["a", "b"], ["1", "2"], ["3", "4"]], rows);
    }

    [Fact]
    public void A_quote_inside_an_unquoted_field_is_text()
    {
        var rows = DelimitedText.ParseAll("5'10\",x\n", ',');

        Assert.Equal(["5'10\"", "x"], rows[0]);
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3\n4,5,6\n", ',')]
    [InlineData("a;b;c\n1,5;2,5;3\n4;5;6\n", ';')]
    [InlineData("a\tb\n1\t2\n", '\t')]
    [InlineData("a|b|c\n1|2|3\n", '|')]
    [InlineData("\"x;y\",b\n\"1;2\",3\n", ',')]
    public void Sniffs_the_delimiter(string sample, char expected) => Assert.Equal(expected, DelimitedText.Sniff(sample));

    [Fact]
    public void Detects_utf8_bom_utf16_and_latin1()
    {
        Assert.Equal("utf-8", DelimitedText.DetectEncoding(Encoding.UTF8.GetPreamble().Concat("ab"u8.ToArray()).ToArray()).WebName);
        Assert.Equal("utf-16", DelimitedText.DetectEncoding([0xFF, 0xFE, 0x61, 0]).WebName);
        Assert.Equal("utf-8", DelimitedText.DetectEncoding("Grüße"u8.ToArray()).WebName);
        Assert.Equal("iso-8859-1", DelimitedText.DetectEncoding([0x47, 0x72, 0xFC, 0xDF, 0x65]).WebName);
    }

    [Fact]
    public void Opens_a_latin1_semicolon_file_with_its_header()
    {
        var path = WriteFile("people.csv", "Name;Stadt;Betrag\nJürgen;Köln;1,50\nAnn;Wien;2,25\n", Encoding.Latin1);

        var (file, format) = ImportFile.Open(path);
        var preview = file.Preview(format);

        Assert.Equal(ImportFileKind.Delimited, file.Kind);
        Assert.Equal(';', format.Delimiter);
        Assert.True(format.HasHeader);
        Assert.Equal(["Name", "Stadt", "Betrag"], preview.Headers);
        Assert.Equal("Köln", preview.Rows[0][1]);
        Assert.True(preview.ReachedEnd);
    }

    [Fact]
    public void A_first_row_of_numbers_is_data_not_a_header()
    {
        Assert.False(ImportFile.GuessHeader([["1", "Ann", "2024-01-02"], ["2", "Bob", "2024-02-03"]]));
        Assert.True(ImportFile.GuessHeader([["id", "name"], ["1", "Ann"]]));
        Assert.True(ImportFile.GuessHeader([["name", "city"], ["Ann", "Oslo"]]));
    }

    [Fact]
    public void Without_a_header_columns_are_numbered_and_every_row_is_data()
    {
        var path = WriteFile("plain.csv", "1,Ann\n2,Bob\n");
        var (file, format) = ImportFile.Open(path);

        Assert.False(format.HasHeader);
        Assert.Equal(["Column1", "Column2"], file.Preview(format).Headers);
        Assert.Equal(2, file.ReadDataRows(format).Count());
    }

    [Fact]
    public void Skip_rows_leaves_out_lines_above_the_header()
    {
        var path = WriteFile("report.csv", "Sales report\nid,total\n1,5\n");
        var (file, format) = ImportFile.Open(path);
        format = format with { SkipRows = 1, HasHeader = true, Delimiter = ',' };

        Assert.Equal(["id", "total"], file.Preview(format).Headers);
        Assert.Single(file.ReadDataRows(format));
    }

    [Fact]
    public void Binary_files_and_old_xls_are_refused()
    {
        var path = Path.Combine(_dir, "data.bin");
        File.WriteAllBytes(path, [1, 0, 2, 0, 3]);
        Assert.Throws<FormatException>(() => ImportFile.Open(path));
    }

    [Fact]
    public void Repeated_and_empty_titles_get_unique_names()
    {
        Assert.Equal(["id", "Column2", "name", "Name_2", "Column5"], ImportValues.ColumnNames(["id", " ", "name", "Name"], 5));
    }

    // ----- Excel -----

    [Fact]
    public void Reads_an_xlsx_workbook_written_by_the_exporter()
    {
        var path = Path.Combine(_dir, "book.xlsx");
        using (var stream = File.Create(path))
            ResultExporter.WriteXlsx(stream, ["id", "name", "price"], [[1, "Ann", 2.5m], [2, "B & \"C\"", null]], "People");

        var (file, format) = ImportFile.Open(path);
        var preview = file.Preview(format);

        Assert.Equal(ImportFileKind.Excel, file.Kind);
        Assert.Equal(["People"], file.Sheets);
        Assert.True(format.HasHeader);
        Assert.Equal(["id", "name", "price"], preview.Headers);
        Assert.Equal(["1", "Ann", "2.5"], preview.Rows[0]);
        Assert.Equal(["2", "B & \"C\""], preview.Rows[1]);
    }

    [Fact]
    public void Reads_shared_strings_inline_strings_booleans_dates_and_gaps()
    {
        var path = Path.Combine(_dir, "hand.xlsx");
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(path), System.IO.Compression.ZipArchiveMode.Create))
        {
            void Entry(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(xml);
            }
            Entry("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Data" sheetId="1" r:id="rId1"/><sheet name="Other" sheetId="2" r:id="rId2"/></sheets></workbook>""");
            Entry("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Target="worksheets/sheet2.xml"/></Relationships>""");
            Entry("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>name</t></si><si><r><t>Jo</t></r><r><t>hn</t></r><rPh><t>ジョン</t></rPh></si></sst>""");
            Entry("xl/styles.xml", """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts><numFmt numFmtId="164" formatCode="dd/mm/yyyy hh:mm"/><numFmt numFmtId="165" formatCode="0.00&quot;d&quot;"/></numFmts><cellXfs><xf numFmtId="0"/><xf numFmtId="14"/><xf numFmtId="164"/><xf numFmtId="165"/></cellXfs></styleSheet>""");
            Entry("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="C1" t="inlineStr"><is><t>when</t></is></c></row>
                <row r="2"/>
                <row r="3"><c r="A3" t="s"><v>1</v></c><c r="B3" t="b"><v>1</v></c><c r="C3" s="1"><v>45658</v></c><c r="D3" s="2"><v>45658.5</v></c><c r="E3" s="3"><v>1.5</v></c><c r="F3"><f>1+1</f><v>2</v></c></row>
                </sheetData></worksheet>
                """);
            Entry("xl/worksheets/sheet2.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row><c><v>7</v></c></row></sheetData></worksheet>""");
        }

        using var reader = XlsxReader.Open(path);
        var rows = reader.ReadRows().ToList();

        Assert.Equal(["Data", "Other"], reader.SheetNames);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["name", "", "when"], rows[0]);
        Assert.Equal(["John", "true", "2025-01-01", "2025-01-01 12:00:00", "1.5", "2"], rows[1]);
        Assert.Equal(["7"], reader.ReadRows("Other").Single());
    }

    [Theory]
    [InlineData(14, null, true)]
    [InlineData(0, null, false)]
    [InlineData(164, "yyyy-mm-dd", true)]
    [InlineData(165, "#,##0.00", false)]
    [InlineData(166, "[Red]0.00", false)]
    [InlineData(167, "0\"days\"", false)]
    public void Knows_date_formats(int id, string? code, bool expected) => Assert.Equal(expected, XlsxReader.IsDateFormat(id, code));

    // ----- Values -----

    [Theory]
    [InlineData("42", TypeFamily.Integer, 42L)]
    [InlineData(" -7 ", TypeFamily.Integer, -7L)]
    [InlineData("1,234,567", TypeFamily.Integer, 1234567L)]
    [InlineData("12.0", TypeFamily.Integer, 12L)]
    [InlineData("yes", TypeFamily.Boolean, true)]
    [InlineData("0", TypeFamily.Boolean, false)]
    public void Parses_values_by_type(string text, TypeFamily family, object expected) =>
        Assert.Equal(expected, ImportValues.Parse(text, family, Defaults).Value);

    [Fact]
    public void Parses_decimals_with_either_separator()
    {
        Assert.Equal(1234.5m, ImportValues.Parse("1,234.5", TypeFamily.Decimal, Defaults).Value);
        var comma = Defaults with { DecimalSeparator = ',' };
        Assert.Equal(1234.5m, ImportValues.Parse("1.234,5", TypeFamily.Decimal, comma).Value);
        Assert.Equal(1234.5m, ImportValues.Parse("1 234,5", TypeFamily.Decimal, comma).Value);
        Assert.Equal(0.25, ImportValues.Parse("0,25", TypeFamily.Float, comma).Value);
    }

    [Fact]
    public void Parses_dates_iso_and_by_format()
    {
        Assert.Equal(new DateOnly(2024, 3, 4), ImportValues.Parse("2024-03-04", TypeFamily.Date, Defaults).Value);
        Assert.Equal(new DateTime(2024, 3, 4, 10, 30, 0), ImportValues.Parse("2024-03-04T10:30", TypeFamily.DateTime, Defaults).Value);
        Assert.Equal(new DateTimeOffset(2024, 3, 4, 10, 30, 0, TimeSpan.FromHours(2)),
            ImportValues.Parse("2024-03-04 10:30:00+02:00", TypeFamily.DateTime, Defaults).Value);
        var german = Defaults with { DateFormat = "dd.MM.yyyy" };
        Assert.Equal(new DateOnly(2024, 3, 4), ImportValues.Parse("04.03.2024", TypeFamily.Date, german).Value);
        Assert.False(ImportValues.Parse("2024-03-04 10:00", TypeFamily.Date, Defaults).IsValid);
    }

    [Fact]
    public void Empty_and_null_text_become_null()
    {
        Assert.Null(ImportValues.Parse("", TypeFamily.Integer, Defaults with { EmptyIsNull = false }).Value);
        Assert.True(ImportValues.Parse("", TypeFamily.Integer, Defaults with { EmptyIsNull = false }).IsValid);
        Assert.Equal("", ImportValues.Parse("", TypeFamily.Text, Defaults with { EmptyIsNull = false }).Value);
        Assert.Null(ImportValues.Parse("", TypeFamily.Text, Defaults).Value);
        Assert.Null(ImportValues.Parse("\\N", TypeFamily.Text, Defaults with { NullText = "\\N" }).Value);
        Assert.Equal("  padded ", ImportValues.Parse("  padded ", TypeFamily.Text, Defaults).Value);
        Assert.Equal("padded", ImportValues.Parse("  padded ", TypeFamily.Text, Defaults with { TrimText = true }).Value);
    }

    [Fact]
    public void Bad_values_say_what_was_expected()
    {
        Assert.Equal("'abc' is not a whole number", ImportValues.Parse("abc", TypeFamily.Integer, Defaults).Error);
        Assert.Equal("'maybe' is not true or false", ImportValues.Parse("maybe", TypeFamily.Boolean, Defaults).Error);
        Assert.Equal("'abcdef' is 6 characters long; the column takes 5", ImportValues.Parse("abcdef", TypeFamily.Text, Defaults, 5).Error);
    }

    [Theory]
    [InlineData("varchar(50)", 50)]
    [InlineData("nvarchar(100)", 100)]
    [InlineData("character varying(20)", 20)]
    [InlineData("nvarchar(max)", null)]
    [InlineData("text", null)]
    [InlineData("int", null)]
    public void Reads_text_length_limits(string type, int? expected) => Assert.Equal(expected, ImportValues.MaxLength(type));

    [Fact]
    public void Guesses_the_decimal_separator_from_values()
    {
        Assert.Equal(',', ImportValues.GuessDecimalSeparator(["1,5", "2", "3,25"]));
        Assert.Equal('.', ImportValues.GuessDecimalSeparator(["1.5", "2,5"]));
        Assert.Equal('.', ImportValues.GuessDecimalSeparator(["1,234", "abc"]));
    }

    // ----- Type guessing -----

    [Theory]
    [InlineData(new[] { "1", "2", "", "300" }, "Postgres", "integer")]
    [InlineData(new[] { "1", "9999999999" }, "Postgres", "bigint")]
    [InlineData(new[] { "1.5", "20.25" }, "Postgres", "numeric(18,2)")]
    [InlineData(new[] { "true", "false" }, "SqlServer", "bit")]
    [InlineData(new[] { "1", "0", "1" }, "Postgres", "integer")]
    [InlineData(new[] { "2024-01-02", "2024-02-03" }, "Postgres", "date")]
    [InlineData(new[] { "2024-01-02 10:00", "2024-02-03" }, "SqlServer", "datetime2")]
    [InlineData(new[] { "007", "012" }, "Postgres", "varchar(50)")]
    [InlineData(new[] { "Ann", "Bob" }, "SqlServer", "nvarchar(50)")]
    [InlineData(new[] { "7f1c0e5a-1111-4c3b-9a55-0123456789ab" }, "Postgres", "uuid")]
    [InlineData(new[] { "", "" }, "MySql", "varchar(50)")]
    [InlineData(new[] { "20240102" }, "Postgres", "integer")]
    public void Guesses_column_types(string[] values, string provider, string expected) =>
        Assert.Equal(expected, ImportValues.GuessColumn("c", values, provider, Defaults).FullType);

    [Fact]
    public void Long_text_gets_room_to_spare_or_an_unlimited_type()
    {
        Assert.Equal("varchar(255)", ImportValues.GuessColumn("c", [new string('x', 120)], "Postgres", Defaults).FullType);
        Assert.Equal("text", ImportValues.GuessColumn("c", [new string('x', 5000)], "Postgres", Defaults).FullType);
        Assert.Equal("nvarchar(max)", ImportValues.GuessColumn("c", [new string('x', 5000)], "SqlServer", Defaults).FullType);
    }

    [Fact]
    public void Decimal_comma_files_guess_decimal_columns()
    {
        var comma = Defaults with { DecimalSeparator = ',' };
        Assert.Equal("decimal(18,2)", ImportValues.GuessColumn("c", ["1,50", "22,25"], "MySql", comma).FullType);
    }

    // ----- Mapping and checking -----

    private static readonly IReadOnlyList<DbColumn> People =
    [
        new() { Name = "id", DataType = "int", BaseType = "int", Ordinal = 1, IsIdentity = true },
        new() { Name = "first_name", DataType = "varchar(5)", BaseType = "varchar", Ordinal = 2 },
        new() { Name = "born", DataType = "date", BaseType = "date", Ordinal = 3 },
        new() { Name = "full", DataType = "text", BaseType = "text", Ordinal = 4, IsComputed = true }
    ];

    [Fact]
    public void Maps_file_columns_by_loose_name_and_never_to_computed_columns()
    {
        var map = TableImport.MapByName(["First Name", "ID", "extra", "full"], People, byPosition: false);

        Assert.Equal([(0, "first_name"), (1, "id")], map.Select(m => (m.Source, m.Column)));
        Assert.True(map[1].IsIdentity);
        Assert.Equal(5, map[0].MaxLength);
    }

    [Fact]
    public void Maps_by_position_without_a_header()
    {
        var map = TableImport.MapByName(["Column1", "Column2", "Column3", "Column4"], People, byPosition: true);

        Assert.Equal(["id", "first_name", "born"], map.Select(m => m.Column));
    }

    [Fact]
    public void Check_counts_rows_and_names_each_bad_value_by_its_file_row()
    {
        var map = TableImport.MapByName(["id", "first_name", "born"], People, byPosition: false);
        string[][] rows = [["1", "Ann", "2000-01-02"], ["x", "Bob", ""], ["3", "Christopher", "2001-02-03"], ["4", "Dee", "not a date"]];

        var check = TableImport.Check(rows, map, Defaults, firstRowNumber: 2);

        Assert.Equal(4, check.Rows);
        Assert.Equal(3, check.InvalidRows);
        Assert.Equal("Row 3, id: 'x' is not a whole number", check.Errors[0].Text);
        Assert.Equal(4, check.Errors[1].Row);
        Assert.Equal("born", check.Errors[2].Column);
    }

    // ----- SQL -----

    private static ImportPlan Plan(params ImportColumn[] columns) => new()
    {
        Table = new DbObject { Schema = "dbo", Name = "People", Type = DbObjectType.Table },
        Columns = columns
    };

    [Fact]
    public void Insert_statements_keep_types_per_engine()
    {
        var plan = Plan(new ImportColumn(0, "id", "int"), new ImportColumn(1, "name", "nvarchar(50)"), new ImportColumn(2, "born", "date"));
        object?[][] rows = [[1L, "O'Neil", new DateOnly(2000, 1, 2)], [2L, null, null]];

        Assert.Equal("INSERT INTO [dbo].[People] ([id], [name], [born])\nVALUES\n  (1, N'O''Neil', CAST(N'2000-01-02' AS date)),\n  (2, NULL, NULL);",
            TableImport.InsertStatement(plan, rows, SqlDialect.SqlServer, n => $"[{n}]"));
        Assert.Equal("INSERT INTO \"dbo\".\"People\" (\"id\", \"name\", \"born\")\nVALUES\n  (1, 'O''Neil', '2000-01-02'),\n  (2, NULL, NULL);",
            TableImport.InsertStatement(plan, rows, SqlDialect.Postgres, n => $"\"{n}\""));
    }

    [Fact]
    public void Identity_columns_are_written_with_identity_insert_or_overriding()
    {
        var plan = Plan(new ImportColumn(0, "id", "int", IsIdentity: true), new ImportColumn(1, "name", "text"));
        string[][] rows = [["5", "a"], ["6", "b"]];

        var sqlServer = TableImport.Script(plan, rows, 2, SqlDialect.SqlServer, n => $"[{n}]");
        Assert.StartsWith("SET IDENTITY_INSERT [dbo].[People] ON;", sqlServer);
        Assert.EndsWith("SET IDENTITY_INSERT [dbo].[People] OFF;\n", sqlServer);
        Assert.Contains("OVERRIDING SYSTEM VALUE", TableImport.InsertStatement(plan, [[5L, "a"]], SqlDialect.Postgres, n => n));
    }

    [Fact]
    public void Script_creates_or_empties_the_table_first_and_notes_rows_left_out()
    {
        var plan = Plan(new ImportColumn(0, "id", "int")) with { DeleteExisting = true, BatchSize = 2 };
        string[][] rows = [["1"], ["two"], ["3"], ["4"]];

        var script = TableImport.Script(plan, rows, 2, SqlDialect.Postgres, n => n);

        Assert.StartsWith("DELETE FROM dbo.People;", script);
        Assert.Contains("-- left out: Row 3, id: 'two' is not a whole number", script);
        Assert.Contains("(1),\n  (3);", script);
        Assert.Contains("VALUES\n  (4);", script);

        var created = plan with { CreateScript = "CREATE TABLE dbo.People (id int);" };
        Assert.StartsWith("CREATE TABLE dbo.People (id int);", TableImport.Script(created, rows, 2, SqlDialect.Postgres, n => n));
        Assert.DoesNotContain("DELETE", TableImport.Script(created, rows, 2, SqlDialect.Postgres, n => n));
    }

    [Fact]
    public void Script_stops_at_its_row_limit()
    {
        var plan = Plan(new ImportColumn(0, "id", "int"));
        var rows = Enumerable.Range(1, 50).Select(i => new[] { i.ToString() });

        var script = TableImport.Script(plan, rows, 1, SqlDialect.Postgres, n => n, maxRows: 10);

        Assert.Contains("-- stopped after 10 rows", script);
        Assert.DoesNotContain("(11)", script);
    }

    [Fact]
    public void Copy_text_uses_postgres_input_forms()
    {
        Assert.Null(TableImport.CopyText(null));
        Assert.Equal("t", TableImport.CopyText(true));
        Assert.Equal("\\x0AFF", TableImport.CopyText(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("2024-01-02", TableImport.CopyText(new DateTime(2024, 1, 2)));
        Assert.Equal("2024-01-02 03:04:05.5", TableImport.CopyText(new DateTime(2024, 1, 2, 3, 4, 5, 500)));
        Assert.Equal("1.5", TableImport.CopyText(1.5m));
        Assert.Equal("2 days 01:00:00.0000000", TableImport.CopyText(new TimeSpan(2, 1, 0, 0)));
    }

    [Fact]
    public void File_row_of_a_refused_copy_line_skips_rows_left_out()
    {
        Assert.Equal(4, TableImport.FileRow(2, 3, []));
        Assert.Equal(6, TableImport.FileRow(2, 3, [3, 5]));
        Assert.Equal(4, TableImport.FileRow(2, 3, [10]));
    }
}
