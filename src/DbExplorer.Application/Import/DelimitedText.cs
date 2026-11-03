using System.Text;

namespace DbExplorer.Application.Import;

/// <summary>
/// Reads delimited text (CSV, TSV, semicolon or pipe separated) as RFC 4180 does: fields in quotes may hold the
/// delimiter, line breaks and doubled quotes; a quote inside an unquoted field is kept as text. Rows are streamed, so a
/// file of any size is read once without holding it in memory. Blank lines are skipped.
/// </summary>
public static class DelimitedText
{
    /// <summary>The delimiters the importer recognises, in the order a tie is broken.</summary>
    public static readonly char[] Candidates = [',', ';', '\t', '|'];

    public static IEnumerable<string[]> ReadRows(TextReader reader, char delimiter, char quote = '"')
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quotedField = false; // the field started with a quote, so its closing quote ends the text
        var rowHasContent = false;

        int c;
        while ((c = reader.Read()) >= 0)
        {
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == quote)
                {
                    if (reader.Peek() == quote)
                    {
                        reader.Read();
                        field.Append(quote);
                    }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }

            if (ch == quote && field.Length == 0 && !quotedField)
            {
                inQuotes = quotedField = true;
                rowHasContent = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                quotedField = false;
                rowHasContent = true;
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                if (rowHasContent || field.Length > 0)
                {
                    fields.Add(field.ToString());
                    yield return fields.ToArray();
                }
                fields.Clear();
                field.Clear();
                quotedField = false;
                rowHasContent = false;
            }
            else
            {
                field.Append(ch);
                rowHasContent = true;
            }
        }

        if (rowHasContent || field.Length > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }

    public static IReadOnlyList<string[]> ParseAll(string text, char delimiter, char quote = '"') =>
        ReadRows(new StringReader(text), delimiter, quote).ToList();

    /// <summary>
    /// The delimiter of a sample (the first few kilobytes of a file): the candidate that splits the most lines into the
    /// same number (more than one) of fields. Comma when nothing splits the lines.
    /// </summary>
    public static char Sniff(string sample, char quote = '"')
    {
        // The last line of a sample may be cut off, so it is left out when there are others.
        var best = ',';
        var bestScore = 0.0;
        foreach (var candidate in Candidates)
        {
            var rows = ParseAll(sample, candidate, quote);
            if (rows.Count > 1) rows = rows.Take(rows.Count - 1).ToList();
            if (rows.Count == 0) continue;
            var counts = rows.Select(r => r.Length).ToList();
            var common = counts.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (common.Key < 2) continue;
            // Consistent lines count most; more fields break a tie (1,5;2,5 splits on both, but into more on ';').
            var score = (double)common.Count() / counts.Count * 1000 + common.Key;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>The text encoding of a file's first bytes: a byte order mark, else UTF-8 when the bytes are valid UTF-8,
    /// else Windows' Western European code page as Latin-1 (what Excel writes for "CSV" on many systems).</summary>
    public static Encoding DetectEncoding(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return new UTF8Encoding(false);
        if (head.StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode;
        if (head.StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode;
        return IsValidUtf8(head) ? new UTF8Encoding(false) : Encoding.Latin1;
    }

    /// <summary>Valid UTF-8, allowing a sequence cut off at the end of the sample.</summary>
    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            var extra = b < 0x80 ? 0 : (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2 : (b & 0xF8) == 0xF0 ? 3 : -1;
            if (extra < 0) return false;
            for (var k = 1; k <= extra; k++)
            {
                if (i + k >= bytes.Length) return true;
                if ((bytes[i + k] & 0xC0) != 0x80) return false;
            }
            i += extra + 1;
        }
        return true;
    }
}
