using System.Globalization;
using System.Text;

namespace DbExplorer.Application.Query;

/// <summary>The encodings a script file is read in and written back in, by a stable name kept with the tab.</summary>
public static class SqlFileEncodings
{
    public const string Utf8 = "utf-8";
    public const string Utf8Bom = "utf-8-bom";
    public const string Utf16Le = "utf-16le";
    public const string Utf16Be = "utf-16be";
    public const string Utf32Le = "utf-32le";
    /// <summary>Text with no BOM that is not valid UTF-8: what SSMS and Notepad wrote as "ANSI" on Western Windows.</summary>
    public const string Windows1252 = "windows-1252";

    static SqlFileEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>"UTF-8 with BOM", for the tab's tooltip and the status line.</summary>
    public static string Label(string? name) => name switch
    {
        Utf8Bom => "UTF-8 with BOM",
        Utf16Le => "UTF-16 LE",
        Utf16Be => "UTF-16 BE",
        Utf32Le => "UTF-32 LE",
        Windows1252 => "Windows-1252",
        _ => "UTF-8"
    };

    /// <summary>The encoding without its BOM; the BOM is written separately (see <see cref="Preamble"/>).</summary>
    internal static Encoding For(string? name, bool strict)
    {
        var encoderFallback = strict ? EncoderFallback.ExceptionFallback : EncoderFallback.ReplacementFallback;
        var decoderFallback = strict ? DecoderFallback.ExceptionFallback : DecoderFallback.ReplacementFallback;
        return name switch
        {
            Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: strict),
            Utf16Be => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: strict),
            Utf32Le => new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: strict),
            Windows1252 => Encoding.GetEncoding(1252, encoderFallback, decoderFallback),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: strict)
        };
    }

    internal static byte[] Preamble(string? name) => name switch
    {
        Utf8Bom => [0xEF, 0xBB, 0xBF],
        Utf16Le => [0xFF, 0xFE],
        Utf16Be => [0xFE, 0xFF],
        Utf32Le => [0xFF, 0xFE, 0x00, 0x00],
        _ => []
    };

    /// <summary>A name read back from a saved tab; unknown or missing names are UTF-8, what new files get.</summary>
    public static string Normalize(string? name) =>
        name is Utf8Bom or Utf16Le or Utf16Be or Utf32Le or Windows1252 ? name : Utf8;
}

/// <summary>When a file was last written, to notice that another program changed it.</summary>
public readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
{
    /// <summary>The file's stamp now; null when it is gone or cannot be read.</summary>
    public static FileStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>A script file's text, the encoding it was stored in and its stamp when read.</summary>
public sealed record SqlFileContent(string Text, string Encoding, FileStamp Stamp);

/// <summary>What <see cref="SqlFile.WriteAsync"/> did: the encoding written (UTF-8 with BOM when the text did not fit
/// the file's own one) and the file's new stamp.</summary>
public sealed record SqlFileWrite(string Encoding, FileStamp? Stamp, bool EncodingChanged);

/// <summary>A file that cannot be opened as a script (too large, not text).</summary>
public sealed class SqlFileException(string message) : Exception(message);

/// <summary>
/// Reads and writes .sql files the way other editors left them: the encoding (BOM or not, UTF-16 from SSMS, ANSI)
/// is detected on open and kept on save, so a file round-trips byte for byte when nothing was edited. Line endings
/// are left to the editor, which keeps the ones the file had.
/// </summary>
public static class SqlFile
{
    /// <summary>Larger files would make the editor unusable; they are refused with a message instead.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    public static async Task<SqlFileContent> ReadAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"{Path.GetFileName(path)} was not found.", path);
        if (info.Length > MaxBytes)
            throw new SqlFileException(string.Create(CultureInfo.CurrentCulture,
                $"{info.Name} is {info.Length / (1024.0 * 1024):N0} MB; files over {MaxBytes / (1024 * 1024)} MB are too large to open in the editor."));
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var (text, encoding) = Decode(bytes, info.Name);
        // The stamp after reading: a write that raced the read shows up as a change on the next check.
        return new SqlFileContent(text, encoding, FileStamp.Of(path) ?? new FileStamp(info.LastWriteTimeUtc, bytes.Length));
    }

    /// <summary>The text of <paramref name="bytes"/> and the encoding it was in.</summary>
    /// <exception cref="SqlFileException">The bytes are not text (a NUL character outside UTF-16/32).</exception>
    public static (string Text, string Encoding) Decode(ReadOnlySpan<byte> bytes, string name = "The file")
    {
        string encoding;
        int skip;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00])) (encoding, skip) = (SqlFileEncodings.Utf32Le, 4);
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) (encoding, skip) = (SqlFileEncodings.Utf8Bom, 3);
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) (encoding, skip) = (SqlFileEncodings.Utf16Le, 2);
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) (encoding, skip) = (SqlFileEncodings.Utf16Be, 2);
        else
        {
            skip = 0;
            if (bytes.Contains((byte)0)) throw NotText(name);
            try
            {
                return (SqlFileEncodings.For(SqlFileEncodings.Utf8, strict: true).GetString(bytes), SqlFileEncodings.Utf8);
            }
            catch (DecoderFallbackException)
            {
                encoding = SqlFileEncodings.Windows1252;
            }
        }

        var text = SqlFileEncodings.For(encoding, strict: false).GetString(bytes[skip..]);
        if (text.Contains('\0')) throw NotText(name);
        return (text, encoding);
    }

    private static SqlFileException NotText(string name) =>
        new($"{name} does not look like a text file, so it was not opened.");

    /// <summary>The encoding of the file at <paramref name="path"/>; UTF-8 when it cannot be read.</summary>
    public static string TryDetectEncoding(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return SqlFileEncodings.Utf8;
            return Decode(File.ReadAllBytes(path)).Encoding;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqlFileException or ArgumentException or NotSupportedException)
        {
            return SqlFileEncodings.Utf8;
        }
    }

    /// <summary>The bytes of <paramref name="text"/> in <paramref name="encoding"/>, or in UTF-8 with BOM when some
    /// character has no place in it (an emoji in a Windows-1252 file), so nothing is lost.</summary>
    public static (byte[] Bytes, string Encoding) Encode(string text, string? encoding)
    {
        encoding = SqlFileEncodings.Normalize(encoding);
        byte[] body;
        try
        {
            body = SqlFileEncodings.For(encoding, strict: true).GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            encoding = SqlFileEncodings.Utf8Bom;
            body = SqlFileEncodings.For(encoding, strict: false).GetBytes(text);
        }
        var preamble = SqlFileEncodings.Preamble(encoding);
        if (preamble.Length == 0) return (body, encoding);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return (bytes, encoding);
    }

    /// <summary>Writes the file in place (keeping its links and permissions), in its own encoding when the text fits.</summary>
    public static async Task<SqlFileWrite> WriteAsync(string path, string text, string? encoding, CancellationToken ct = default)
    {
        var requested = SqlFileEncodings.Normalize(encoding);
        var (bytes, written) = Encode(text, requested);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return new SqlFileWrite(written, FileStamp.Of(path), written != requested);
    }
}
