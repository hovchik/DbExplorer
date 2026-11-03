using System.Text;

namespace DbExplorer.Application.Import;

public enum ImportFileKind { Delimited, Excel }

/// <summary>How a file is read: its kind, delimiter and quote (text files), encoding, sheet (workbooks) and whether
/// the first row holds the column titles.</summary>
public sealed record ImportFileFormat
{
    public ImportFileKind Kind { get; init; }
    public char Delimiter { get; init; } = ',';
    public char Quote { get; init; } = '"';

    /// <summary>The text encoding's web name (utf-8, utf-16, iso-8859-1); null to detect it from the file.</summary>
    public string? Encoding { get; init; }

    public bool HasHeader { get; init; } = true;

    /// <summary>The worksheet of a workbook; null for the first one.</summary>
    public string? Sheet { get; init; }

    /// <summary>Rows at the top to leave out before the header (titles, notes).</summary>
    public int SkipRows { get; init; }
}

/// <summary>The first rows of a file, for the mapping and the preview.</summary>
public sealed record ImportPreview(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows, int ColumnCount, bool ReachedEnd);

/// <summary>A CSV / TSV / Excel file to import: detects how to read it and streams its rows as often as needed (the
/// check and the import read it again, so a file of any size is never held in memory).</summary>
public sealed class ImportFile
{
    private const int SampleBytes = 64 * 1024;

    public string Path { get; }
    public ImportFileKind Kind { get; }
    public IReadOnlyList<string> Sheets { get; } = [];

    private ImportFile(string path, ImportFileKind kind, IReadOnlyList<string> sheets)
    {
        Path = path;
        Kind = kind;
        Sheets = sheets;
    }

    public string Name => System.IO.Path.GetFileName(Path);

    public static bool IsWorkbook(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm";

    /// <summary>Opens the file and works out how to read it: workbooks by their extension (or zip signature), text by
    /// sniffing the delimiter and encoding from its first 64 KB, and whether the first row is a header.</summary>
    /// <exception cref="FormatException">The file cannot be read as a table.</exception>
    public static (ImportFile File, ImportFileFormat Format) Open(string path)
    {
        var head = ReadHead(path);
        var zip = head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4;
        if (zip || IsWorkbook(path))
        {
            if (System.IO.Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase) && !zip)
                throw new FormatException("Old .xls workbooks cannot be read. Save the sheet as .xlsx or CSV in Excel first.");
            using var reader = XlsxReader.Open(path);
            var sheets = reader.SheetNames;
            var file = new ImportFile(path, ImportFileKind.Excel, sheets);
            var format = new ImportFileFormat { Kind = ImportFileKind.Excel, Sheet = sheets.FirstOrDefault() };
            return (file, format with { HasHeader = GuessHeader(file.ReadRows(format).Take(20).ToList()) });
        }
        if (head.AsSpan().IndexOf((byte)0) >= 0 && !(head.Length >= 2 && (head[0] == 0xFF || head[0] == 0xFE)))
            throw new FormatException("This file is not text (it holds binary data). Import reads CSV, TSV and .xlsx files.");

        var encoding = DelimitedText.DetectEncoding(head);
        var sample = encoding.GetString(head);
        var text = new ImportFile(path, ImportFileKind.Delimited, []);
        var delimiter = System.IO.Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : DelimitedText.Sniff(sample);
        var textFormat = new ImportFileFormat { Kind = ImportFileKind.Delimited, Delimiter = delimiter, Encoding = encoding.WebName };
        return (text, textFormat with { HasHeader = GuessHeader(text.ReadRows(textFormat).Take(20).ToList()) });
    }

    private static byte[] ReadHead(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[SampleBytes];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
        return buffer[..read];
    }

    /// <summary>Every row of the file as text, the header included, after <see cref="ImportFileFormat.SkipRows"/>.</summary>
    public IEnumerable<string[]> ReadRows(ImportFileFormat format)
    {
        var rows = Kind == ImportFileKind.Excel ? ReadWorkbook(format.Sheet) : ReadText(format);
        return format.SkipRows > 0 ? rows.Skip(format.SkipRows) : rows;
    }

    private IEnumerable<string[]> ReadWorkbook(string? sheet)
    {
        using var reader = XlsxReader.Open(Path);
        foreach (var row in reader.ReadRows(sheet)) yield return row;
    }

    private IEnumerable<string[]> ReadText(ImportFileFormat format)
    {
        var encoding = format.Encoding is { } name ? Encoding.GetEncoding(name) : DelimitedText.DetectEncoding(ReadHead(Path));
        using var reader = new StreamReader(Path, encoding, detectEncodingFromByteOrderMarks: true);
        foreach (var row in DelimitedText.ReadRows(reader, format.Delimiter, format.Quote)) yield return row;
    }

    /// <summary>The data rows only (the header row left out when there is one).</summary>
    public IEnumerable<string[]> ReadDataRows(ImportFileFormat format) => format.HasHeader ? ReadRows(format).Skip(1) : ReadRows(format);

    public ImportPreview Preview(ImportFileFormat format, int maxRows = 200)
    {
        var rows = new List<string[]>();
        string[]? header = null;
        var reachedEnd = true;
        foreach (var row in ReadRows(format))
        {
            if (format.HasHeader && header is null)
            {
                header = row;
                continue;
            }
            if (rows.Count == maxRows)
            {
                reachedEnd = false;
                break;
            }
            rows.Add(row);
        }
        var count = Math.Max(header?.Length ?? 0, rows.Count == 0 ? 0 : rows.Max(r => r.Length));
        var headers = format.HasHeader
            ? ImportValues.ColumnNames(header ?? [], count)
            : Enumerable.Range(1, count).Select(i => $"Column{i}").ToList();
        return new ImportPreview(headers, rows, count, reachedEnd);
    }

    /// <summary>
    /// Whether the first row holds titles: it does unless one of its fields reads as a number or a date in a column
    /// where the rows below hold numbers or dates too (a file of only text is taken to have a header).
    /// </summary>
    public static bool GuessHeader(IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0) return true;
        var first = rows[0];
        var options = new ImportValueOptions();
        bool Typed(string field) =>
            ImportValues.Parse(field, Design.TypeFamily.Decimal, options) is { IsValid: true, Value: not null } ||
            (field.Any(char.IsDigit) && ImportValues.Parse(field, Design.TypeFamily.DateTime, options) is { IsValid: true, Value: not null });
        for (var i = 0; i < first.Length; i++)
            if (Typed(first[i]) && (rows.Count == 1 || rows.Skip(1).Any(r => i < r.Length && Typed(r[i]))))
                return false;
        return true;
    }
}
