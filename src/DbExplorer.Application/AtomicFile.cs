using System.Text.Json;

namespace DbExplorer.Application;

/// <summary>
/// Replaces a file in one step: the new content goes to a temporary file next to it, which is then moved over the
/// old one, so a crash or a concurrent reader never sees a half-written file. Each write uses its own temporary
/// name, so two writers never share (and corrupt) one temporary file; the last move wins.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var tmp = TempPath(path);
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static async Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var tmp = TempPath(path);
        try
        {
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, value, options, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static string TempPath(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
