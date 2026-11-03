using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DbExplorer.Application.Export;

/// <summary>What a <see cref="ResultFileWriter"/> needs besides the rows.</summary>
public sealed record ResultFileOptions
{
    /// <summary>Excel: name of the first sheet (more sheets get " (2)", " (3)" …).</summary>
    public string SheetName { get; init; } = "Results";

    /// <summary>SQL INSERT: qualified, quoted target table.</summary>
    public string TargetTable { get; init; } = "target_table";

    public SqlDialect Dialect { get; init; } = SqlDialect.SqlServer;

    public Func<string, string> Quote { get; init; } = id => id;

    /// <summary>SQL INSERT: a comment written at the top of the script (one line per entry).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Writes a result to a file row by row, so a result of any size is never held in memory (or built as one string).
/// Call <see cref="Begin"/> once, <see cref="WriteRow"/> per row, then <see cref="Complete"/>; a writer that was not
/// completed leaves an unfinished file.
/// </summary>
public abstract class ResultFileWriter : IDisposable
{
    /// <summary>The formats that can be written row by row (a full export of a result larger than the row limit).</summary>
    public static IReadOnlyList<ExportFormat> StreamingFormats { get; } =
        [ExportFormat.Csv, ExportFormat.Excel, ExportFormat.Json, ExportFormat.Insert];

    public static bool CanStream(ExportFormat format) => StreamingFormats.Contains(format);

    /// <summary>A writer for <paramref name="format"/> on <paramref name="output"/>, which stays open.</summary>
    public static ResultFileWriter Create(ExportFormat format, Stream output, ResultFileOptions? options = null)
    {
        options ??= new ResultFileOptions();
        return format switch
        {
            // BOM so Excel detects UTF-8 when opening CSV directly.
            ExportFormat.Csv => new CsvResultWriter(new StreamWriter(output, new UTF8Encoding(true), 1 << 16, leaveOpen: true), ownsWriter: true),
            ExportFormat.Json => new JsonResultWriter(output),
            ExportFormat.Excel => new XlsxResultWriter(output, options.SheetName),
            ExportFormat.Insert => new InsertResultWriter(
                new StreamWriter(output, new UTF8Encoding(false), 1 << 16, leaveOpen: true), ownsWriter: true, options),
            _ => throw new NotSupportedException($"{format} can't be written row by row.")
        };
    }

    protected IReadOnlyList<string> Columns { get; private set; } = [];

    public long RowCount { get; private set; }

    /// <summary>Something the user should know about the file, e.g. that Excel needed more than one sheet.</summary>
    public virtual string? Note => null;

    public void Begin(IReadOnlyList<string> columns)
    {
        Columns = columns;
        OnBegin();
    }

    public void WriteRow(IReadOnlyList<object?> row)
    {
        RowCount++;
        OnRow(row);
    }

    public abstract void Complete();

    protected abstract void OnBegin();

    protected abstract void OnRow(IReadOnlyList<object?> row);

    public virtual void Dispose() => GC.SuppressFinalize(this);

    protected object? Cell(IReadOnlyList<object?> row, int i) => i < row.Count ? row[i] : null;
}

/// <summary>RFC 4180 CSV; see <see cref="ResultExporter.ToCsv"/>.</summary>
public sealed class CsvResultWriter(TextWriter writer, bool ownsWriter, char separator = ',') : ResultFileWriter
{
    protected override void OnBegin()
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (i > 0) writer.Write(separator);
            writer.Write(ResultExporter.CsvField(Columns[i], separator));
        }
        writer.Write("\r\n");
    }

    protected override void OnRow(IReadOnlyList<object?> row)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (i > 0) writer.Write(separator);
            var value = Cell(row, i);
            var text = ResultExporter.FormatInvariant(value);
            if (value is string or char) text = ResultExporter.NeutralizeFormula(text);
            writer.Write(ResultExporter.CsvField(text, separator));
        }
        writer.Write("\r\n");
    }

    public override void Complete() => writer.Flush();

    public override void Dispose()
    {
        if (ownsWriter) writer.Dispose();
        base.Dispose();
    }
}

/// <summary>A JSON array of objects (column → value); see <see cref="ResultExporter.ToJson"/>.</summary>
public sealed class JsonResultWriter(Stream output) : ResultFileWriter
{
    private const int FlushAt = 1 << 16;
    private readonly Utf8JsonWriter _json = new(output, new JsonWriterOptions { Indented = true });
    private List<string> _names = [];

    protected override void OnBegin()
    {
        _names = ResultExporter.UniqueNames(Columns);
        _json.WriteStartArray();
    }

    protected override void OnRow(IReadOnlyList<object?> row)
    {
        _json.WriteStartObject();
        for (var i = 0; i < _names.Count; i++)
        {
            _json.WritePropertyName(_names[i]);
            ResultExporter.WriteJsonValue(_json, Cell(row, i));
        }
        _json.WriteEndObject();
        // The writer buffers everything until flushed: hand it to the file as it grows.
        if (_json.BytesPending > FlushAt) _json.Flush();
    }

    public override void Complete()
    {
        _json.WriteEndArray();
        _json.Flush();
    }

    public override void Dispose()
    {
        _json.Dispose();
        base.Dispose();
    }
}

/// <summary>One INSERT per row; see <see cref="ResultExporter.ToInsertStatements"/>.</summary>
public sealed class InsertResultWriter(TextWriter writer, bool ownsWriter, ResultFileOptions options) : ResultFileWriter
{
    private string _columnList = "";

    protected override void OnBegin()
    {
        foreach (var note in options.Notes) writer.Write("-- " + note.ReplaceLineEndings(" ") + "\n");
        _columnList = string.Join(", ", Columns.Select(options.Quote));
    }

    protected override void OnRow(IReadOnlyList<object?> row)
    {
        writer.Write("INSERT INTO ");
        writer.Write(options.TargetTable);
        writer.Write(" (");
        writer.Write(_columnList);
        writer.Write(") VALUES (");
        for (var i = 0; i < Columns.Count; i++)
        {
            if (i > 0) writer.Write(", ");
            writer.Write(ResultExporter.SqlLiteral(Cell(row, i), options.Dialect));
        }
        writer.Write(");\n");
    }

    public override void Complete() => writer.Flush();

    public override void Dispose()
    {
        if (ownsWriter) writer.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// An .xlsx workbook written as the rows arrive. A sheet holds at most <see cref="MaxRowsPerSheet"/> rows (the header
/// included), Excel's own limit, so a larger result continues on further sheets, each with the header again.
/// </summary>
public sealed class XlsxResultWriter : ResultFileWriter
{
    /// <summary>Rows in one Excel sheet, header included.</summary>
    public const int MaxRowsPerSheet = 1_048_576;

    private readonly ZipArchive _zip;
    private readonly string _sheetName;
    private readonly List<string> _sheets = [];
    private readonly int _maxRowsPerSheet;
    private StreamWriter? _sheet;
    private int _sheetRow;

    public XlsxResultWriter(Stream output, string sheetName, int maxRowsPerSheet = MaxRowsPerSheet)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        _sheetName = ResultExporter.SafeSheetName(sheetName);
        _maxRowsPerSheet = Math.Max(2, maxRowsPerSheet);
    }

    public int SheetCount => _sheets.Count;

    public override string? Note => _sheets.Count > 1
        ? $"Excel holds {_maxRowsPerSheet - 1:N0} rows per sheet, so the rows continue on {_sheets.Count} sheets."
        : null;

    protected override void OnBegin() => StartSheet();

    protected override void OnRow(IReadOnlyList<object?> row)
    {
        if (_sheetRow >= _maxRowsPerSheet)
        {
            EndSheet();
            StartSheet();
        }
        var w = _sheet!;
        _sheetRow++;
        w.Write("<row r=\"");
        w.Write(_sheetRow);
        w.Write("\">");
        for (var i = 0; i < Columns.Count; i++)
            ResultExporter.WriteXlsxCell(w, i, _sheetRow, Cell(row, i));
        w.Write("</row>");
    }

    private void StartSheet()
    {
        var number = _sheets.Count + 1;
        _sheets.Add(SheetName(number));
        var entry = _zip.CreateEntry($"xl/worksheets/sheet{number}.xml", CompressionLevel.Fastest);
        _sheet = new StreamWriter(entry.Open(), new UTF8Encoding(false), 1 << 16);
        _sheet.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><sheetData>""");
        _sheetRow = 1;
        _sheet.Write("<row r=\"1\">");
        for (var i = 0; i < Columns.Count; i++)
            ResultExporter.WriteXlsxHeaderCell(_sheet, i, Columns[i]);
        _sheet.Write("</row>");
    }

    private string SheetName(int number)
    {
        if (number == 1) return _sheetName;
        var suffix = $" ({number})";
        var name = _sheetName.Length + suffix.Length > 31 ? _sheetName[..(31 - suffix.Length)] : _sheetName;
        return name + suffix;
    }

    private void EndSheet()
    {
        if (_sheet is null) return;
        _sheet.Write("</sheetData></worksheet>");
        _sheet.Dispose();
        _sheet = null;
    }

    public override void Complete()
    {
        if (_sheets.Count == 0) StartSheet(); // no Begin: an empty workbook still opens
        EndSheet();
        ResultExporter.WriteXlsxParts(_zip, _sheets);
        _zip.Dispose();
    }

    public override void Dispose()
    {
        _sheet?.Dispose();
        _zip.Dispose();
        base.Dispose();
    }
}
