using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;

namespace DbExplorer.Application.Export;

public enum SqlDialect
{
    SqlServer,
    Postgres
}

public enum ExportFormat
{
    Csv,
    Excel,
    Json,
    Markdown,
    Insert,
    Html
}

/// <summary>Serializes a tabular result (columns + rows of raw provider values) to common formats.</summary>
public static partial class ResultExporter
{
    private const int ExcelMaxCellLength = 32_767;

    public static string FileExtension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "csv",
        ExportFormat.Excel => "xlsx",
        ExportFormat.Json => "json",
        ExportFormat.Markdown => "md",
        ExportFormat.Html => "html",
        _ => "sql"
    };

    public static SqlDialect DialectFor(string providerKey) =>
        string.Equals(providerKey, "Postgres", StringComparison.OrdinalIgnoreCase) ? SqlDialect.Postgres : SqlDialect.SqlServer;

    /// <summary>
    /// RFC 4180 CSV. Text that a spreadsheet would evaluate as a formula (leading = + - @ tab CR)
    /// is prefixed with an apostrophe, so opening an export of untrusted data can't run formulas.
    /// </summary>
    public static string ToCsv(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows, char separator = ',')
    {
        var sb = new StringBuilder();
        sb.AppendJoin(separator, columns.Select(c => CsvField(c, separator))).Append("\r\n");
        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(separator);
                var value = i < row.Count ? row[i] : null;
                var text = FormatInvariant(value);
                if (value is string or char) text = NeutralizeFormula(text);
                sb.Append(CsvField(text, separator));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Tab-separated text for the clipboard: pastes into Excel / Sheets as cells.</summary>
    public static string ToTsv(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows, bool includeHeader = true)
    {
        static string Clean(string s) => s.Replace('\t', ' ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

        var sb = new StringBuilder();
        if (includeHeader) sb.AppendJoin('\t', columns.Select(Clean)).Append(Environment.NewLine);
        foreach (var row in rows)
        {
            sb.AppendJoin('\t', Enumerable.Range(0, columns.Count)
                .Select(i => Clean(FormatInvariant(i < row.Count ? row[i] : null))));
            sb.Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            var names = UniqueNames(columns);
            w.WriteStartArray();
            foreach (var row in rows)
            {
                w.WriteStartObject();
                for (var i = 0; i < names.Count; i++)
                {
                    w.WritePropertyName(names[i]);
                    WriteJsonValue(w, i < row.Count ? row[i] : null);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>One row as a JSON object (column → value), for viewing or copying a single record.</summary>
    public static string ToJsonObject(IReadOnlyList<string> columns, IReadOnlyList<object?> row)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            var names = UniqueNames(columns);
            w.WriteStartObject();
            for (var i = 0; i < names.Count; i++)
            {
                w.WritePropertyName(names[i]);
                WriteJsonValue(w, i < row.Count ? row[i] : null);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string ToMarkdown(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        static string Cell(string s) => s.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>");

        var sb = new StringBuilder();
        sb.Append("| ").AppendJoin(" | ", columns.Select(Cell)).Append(" |\n");
        sb.Append('|').AppendJoin("|", columns.Select(_ => " --- ")).Append("|\n");
        foreach (var row in rows)
        {
            sb.Append("| ")
              .AppendJoin(" | ", Enumerable.Range(0, columns.Count).Select(i =>
                  i < row.Count && row[i] is null ? "*NULL*" : Cell(FormatInvariant(i < row.Count ? row[i] : null))))
              .Append(" |\n");
        }
        return sb.ToString();
    }

    /// <summary>One INSERT per row, with literals rendered for the given dialect.</summary>
    public static string ToInsertStatements(
        IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows,
        string targetTable, SqlDialect dialect, Func<string, string> quote)
    {
        var columnList = string.Join(", ", columns.Select(quote));
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append("INSERT INTO ").Append(targetTable).Append(" (").Append(columnList).Append(") VALUES (");
            sb.AppendJoin(", ", Enumerable.Range(0, columns.Count).Select(i => SqlLiteral(i < row.Count ? row[i] : null, dialect)));
            sb.Append(");\n");
        }
        return sb.ToString();
    }

    public static string SqlLiteral(object? value, SqlDialect dialect)
    {
        string Str(string s) => (dialect == SqlDialect.SqlServer ? "N'" : "'") + s.Replace("'", "''") + "'";

        return value switch
        {
            null or DBNull => "NULL",
            bool b => dialect == SqlDialect.SqlServer ? (b ? "1" : "0") : (b ? "TRUE" : "FALSE"),
            byte or sbyte or short or ushort or int or uint or long or ulong or decimal
                => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            double d => double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : Str(d.ToString(CultureInfo.InvariantCulture)),
            float f => float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : Str(f.ToString(CultureInfo.InvariantCulture)),
            byte[] bytes => dialect == SqlDialect.SqlServer
                ? "0x" + Convert.ToHexString(bytes)
                : "'\\x" + Convert.ToHexString(bytes) + "'::bytea",
            _ => Str(FormatInvariant(value))
        };
    }

    /// <summary>Writes a single-sheet .xlsx (Office Open XML) with a header row; numbers stay numeric.</summary>
    public static void WriteXlsx(
        Stream output, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows, string sheetName = "Results")
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        WriteEntry(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
        WriteEntry(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(zip, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
            <sheets><sheet name="{Xml(SafeSheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        // Style 1 = bold header.
        WriteEntry(zip, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
            <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
            <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
            <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
            <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
            <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs>
            <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
            </styleSheet>
            """);

        var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><sheetData>""");

        var rowNumber = 1;
        w.Write("<row r=\"1\">");
        for (var i = 0; i < columns.Count; i++)
            WriteStringCell(w, CellRef(i, rowNumber), columns[i], style: 1);
        w.Write("</row>");

        foreach (var row in rows)
        {
            rowNumber++;
            w.Write($"<row r=\"{rowNumber}\">");
            for (var i = 0; i < columns.Count; i++)
            {
                var value = i < row.Count ? row[i] : null;
                if (value is null or DBNull) continue;
                var r = CellRef(i, rowNumber);
                if (IsNumeric(value) && value is not bool)
                    w.Write($"<c r=\"{r}\"><v>{FormatInvariant(value)}</v></c>");
                else if (value is bool b)
                    w.Write($"<c r=\"{r}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                else
                    WriteStringCell(w, r, FormatInvariant(value), style: 0);
            }
            w.Write("</row>");
        }

        w.Write("</sheetData></worksheet>");
    }

    public static string FormatInvariant(object? value) => value switch
    {
        null or DBNull => "",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static bool IsNumeric(object value) => value is byte or sbyte or short or ushort or int or uint or long
        or ulong or decimal or double or float && (value is not double d || double.IsFinite(d)) && (value is not float f || float.IsFinite(f));

    private static string NeutralizeFormula(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    private static string CsvField(string text, char separator) =>
        text.IndexOfAny([separator, '"', '\r', '\n']) >= 0 || (text.Length > 0 && (text[0] == ' ' || text[^1] == ' '))
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;

    private static void WriteJsonValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null or DBNull: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case byte or sbyte or short or ushort or int or uint or long: w.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case ulong ul: w.WriteNumberValue(ul); break;
            case decimal m: w.WriteNumberValue(m); break;
            case double d when double.IsFinite(d): w.WriteNumberValue(d); break;
            case float f when float.IsFinite(f): w.WriteNumberValue(f); break;
            default: w.WriteStringValue(FormatInvariant(value)); break;
        }
    }

    private static List<string> UniqueNames(IReadOnlyList<string> columns)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var c in columns)
        {
            var name = string.IsNullOrEmpty(c) ? "column" : c;
            var candidate = name;
            for (var n = 2; !seen.Add(candidate); n++) candidate = $"{name}_{n}";
            result.Add(candidate);
        }
        return result;
    }

    private static void WriteStringCell(TextWriter w, string cellRef, string text, int style)
    {
        if (text.Length > ExcelMaxCellLength) text = text[..ExcelMaxCellLength];
        var s = style == 0 ? "" : $" s=\"{style}\"";
        w.Write($"<c r=\"{cellRef}\" t=\"inlineStr\"{s}><is><t xml:space=\"preserve\">{Xml(text)}</t></is></c>");
    }

    private static string CellRef(int columnIndex, int row)
    {
        var letters = "";
        for (var n = columnIndex + 1; n > 0; n = (n - 1) / 26)
            letters = (char)('A' + (n - 1) % 26) + letters;
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>XML-escapes and drops characters XML 1.0 cannot carry (control chars other than tab/CR/LF).</summary>
    private static string Xml(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch is '\t' or '\n' or '\r' || ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF) sb.Append(ch);
        return SecurityElement.Escape(sb.ToString());
    }

    private static string SafeSheetName(string name)
    {
        var cleaned = new string(name.Where(c => c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Results";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content.Trim());
    }
}
