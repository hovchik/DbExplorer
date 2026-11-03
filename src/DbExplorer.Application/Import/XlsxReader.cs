using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DbExplorer.Application.Import;

/// <summary>
/// Reads the cells of an Excel workbook (.xlsx, Office Open XML) as text, one sheet at a time, without Excel or a
/// library: shared and inline strings as written, numbers in invariant form, booleans as true/false, and numbers with a
/// date format as ISO dates (2025-03-04, or 2025-03-04 10:30:00 when there is a time). Formulas give their last
/// calculated value. Rows are streamed; empty rows are skipped and gaps between cells are filled with empty text.
/// </summary>
public sealed partial class XlsxReader : IDisposable
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly ZipArchive _zip;
    private readonly List<(string Name, string Path)> _sheets = [];
    private readonly List<string> _sharedStrings = [];
    private readonly HashSet<int> _dateStyles = [];
    private bool _date1904;

    public XlsxReader(Stream stream)
    {
        try
        {
            _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw new FormatException("This is not an .xlsx workbook. Older .xls files need to be saved as .xlsx or CSV first.", ex);
        }
        ReadWorkbook();
        ReadSharedStrings();
        ReadStyles();
    }

    public static XlsxReader Open(string path) => new(File.OpenRead(path));

    /// <summary>The worksheets in workbook order.</summary>
    public IReadOnlyList<string> SheetNames => _sheets.Select(s => s.Name).ToList();

    public IEnumerable<string[]> ReadRows(string? sheet = null)
    {
        var index = sheet is null ? 0 : _sheets.FindIndex(s => string.Equals(s.Name, sheet, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index >= _sheets.Count) throw new FormatException(sheet is null ? "The workbook has no sheets." : $"The workbook has no sheet named {sheet}.");
        var entry = _zip.GetEntry(_sheets[index].Path) ?? throw new FormatException($"Sheet {_sheets[index].Name} is missing from the workbook.");
        return ReadSheet(entry);
    }

    private IEnumerable<string[]> ReadSheet(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Prohibit });
        var cells = new List<string>();
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "row" || xml.NamespaceURI != MainNs) continue;
            cells.Clear();
            if (xml.IsEmptyElement) continue;
            var depth = xml.Depth;
            while (xml.Read() && !(xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth))
            {
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "c") continue;
                var reference = xml.GetAttribute("r");
                var type = xml.GetAttribute("t");
                var style = int.TryParse(xml.GetAttribute("s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
                var text = ReadCell(xml, type, style);
                var column = reference is null ? cells.Count : ColumnIndex(reference);
                while (cells.Count < column) cells.Add("");
                if (column < cells.Count) cells[column] = text;
                else cells.Add(text);
            }
            while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
            if (cells.Count > 0) yield return cells.ToArray();
        }
    }

    /// <summary>The text of one &lt;c&gt; element; leaves the reader on its end.</summary>
    private string ReadCell(XmlReader xml, string? type, int style)
    {
        if (xml.IsEmptyElement) return "";
        var depth = xml.Depth;
        string? value = null;
        var inline = new StringBuilder();
        xml.Read();
        while (!xml.EOF && !(xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth))
        {
            // ReadElementContentAsString moves past the element by itself.
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "v") value = xml.ReadElementContentAsString();
            else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t" && type == "inlineStr") inline.Append(xml.ReadElementContentAsString());
            else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "rPh") xml.Skip();
            else xml.Read();
        }

        switch (type)
        {
            case "inlineStr":
                return inline.ToString();
            case "s":
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < _sharedStrings.Count
                    ? _sharedStrings[i]
                    : "";
            case "b":
                return value == "1" ? "true" : value == "0" ? "false" : value ?? "";
            case "str" or "e" or "d":
                return value ?? "";
        }
        if (value is null) return "";
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return value;
        if (_dateStyles.Contains(style)) return FormatDate(number);
        // Whole numbers without ".0" or an exponent; others as Excel stores them (shortest round-trip form).
        return number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);
    }

    private string FormatDate(double serial)
    {
        var epoch = _date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
        DateTime value;
        try
        {
            value = epoch.AddTicks((long)Math.Round(serial * TimeSpan.TicksPerDay / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond);
        }
        catch (ArgumentOutOfRangeException)
        {
            return serial.ToString("R", CultureInfo.InvariantCulture);
        }
        if (serial < 1 && serial >= 0) return value.ToString("HH:mm:ss", CultureInfo.InvariantCulture); // a time of day only
        return value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString(value.Millisecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>0 for A, 25 for Z, 26 for AA, from a reference such as "AB12".</summary>
    public static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') index = index * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') index = index * 26 + (ch - 'a' + 1);
            else break;
        }
        return Math.Max(0, index - 1);
    }

    private void ReadWorkbook()
    {
        var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
        {
            using var xml = XmlReader.Create(rels.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (xml.Read())
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Relationship" &&
                    xml.GetAttribute("Id") is { } id && xml.GetAttribute("Target") is { } target)
                    relationships[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }

        var workbook = _zip.GetEntry("xl/workbook.xml") ?? throw new FormatException("This is not an Excel workbook (xl/workbook.xml is missing).");
        using (var xml = XmlReader.Create(workbook.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element) continue;
                if (xml.LocalName == "workbookPr" && xml.GetAttribute("date1904") is "1" or "true") _date1904 = true;
                if (xml.LocalName != "sheet") continue;
                var name = xml.GetAttribute("name") ?? $"Sheet{_sheets.Count + 1}";
                var rid = xml.GetAttribute("id", RelNs);
                var path = rid is not null && relationships.TryGetValue(rid, out var p) ? p : $"xl/worksheets/sheet{_sheets.Count + 1}.xml";
                _sheets.Add((name, path));
            }
        }
    }

    private void ReadSharedStrings()
    {
        if (_zip.GetEntry("xl/sharedStrings.xml") is not { } entry) return;
        using var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var text = new StringBuilder();
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "si") continue;
            text.Clear();
            if (xml.IsEmptyElement)
            {
                _sharedStrings.Add("");
                continue;
            }
            var depth = xml.Depth;
            xml.Read();
            while (!xml.EOF && !(xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth))
            {
                // Phonetic guides (rPh) repeat the text as a reading aid; they are not part of the value.
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "rPh") xml.Skip();
                else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t") text.Append(xml.ReadElementContentAsString());
                else xml.Read();
            }
            _sharedStrings.Add(text.ToString());
        }
    }

    /// <summary>The cell styles (indexes into cellXfs) whose number format shows a date or time.</summary>
    private void ReadStyles()
    {
        if (_zip.GetEntry("xl/styles.xml") is not { } entry) return;
        var customFormats = new Dictionary<int, string>();
        using var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var inCellXfs = false;
        var xfIndex = 0;
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "cellXfs") inCellXfs = false;
            if (xml.NodeType != XmlNodeType.Element) continue;
            switch (xml.LocalName)
            {
                case "numFmt" when int.TryParse(xml.GetAttribute("numFmtId"), out var id):
                    customFormats[id] = xml.GetAttribute("formatCode") ?? "";
                    break;
                case "cellXfs":
                    inCellXfs = !xml.IsEmptyElement;
                    break;
                case "xf" when inCellXfs:
                    var formatId = int.TryParse(xml.GetAttribute("numFmtId"), out var f) ? f : 0;
                    if (IsDateFormat(formatId, customFormats.GetValueOrDefault(formatId))) _dateStyles.Add(xfIndex);
                    xfIndex++;
                    break;
            }
        }
    }

    /// <summary>Excel's built-in date and time formats (14–22, 45–47) and custom formats with day, month, year or hour
    /// codes outside quotes and brackets ([Red], [$-409]); "0.00" and "#,##0" are numbers.</summary>
    public static bool IsDateFormat(int id, string? code)
    {
        if (id is >= 14 and <= 22 or >= 45 and <= 47) return true;
        if (string.IsNullOrEmpty(code)) return false;
        var bare = QuotedOrBracketed().Replace(code, "");
        return DateCodes().IsMatch(bare);
    }

    [GeneratedRegex(@"""[^""]*""|\[[^\]]*\]|\\.")]
    private static partial Regex QuotedOrBracketed();

    [GeneratedRegex("[dmyhs]", RegexOptions.IgnoreCase)]
    private static partial Regex DateCodes();

    public void Dispose() => _zip.Dispose();
}
