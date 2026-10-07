namespace DbExplorer.Application.Query;

/// <summary>Recognizes binary cell values that are pictures, from their first bytes.</summary>
public static class ImageSniffer
{
    /// <summary>"PNG", "JPEG", "GIF", "BMP", "WEBP" or "ICO"; null when the bytes are not a known image format.</summary>
    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return "PNG";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "JPEG";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "GIF";
        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8)) return "BMP";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "WEBP";
        if (bytes.Length >= 6 && bytes.StartsWith((ReadOnlySpan<byte>)[0, 0, 1, 0]) && bytes[4] + bytes[5] > 0) return "ICO";
        return null;
    }

    /// <summary>The usual file extension for <paramref name="format"/>.</summary>
    public static string Extension(string format) => format switch { "JPEG" => "jpg", _ => format.ToLowerInvariant() };
}
