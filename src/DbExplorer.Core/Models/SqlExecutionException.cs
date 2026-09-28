namespace DbExplorer.Core.Models;

/// <summary>
/// A script failed on the server. <see cref="Line"/>/<see cref="Column"/> point into the script that was sent
/// (1-based), when the engine reports where the error is, so the editor can mark it.
/// </summary>
public sealed class SqlExecutionException(string message, int? line, int? column, Exception inner) : Exception(message, inner)
{
    public int? Line { get; } = line;
    public int? Column { get; } = column;

    /// <summary>1-based line and column of a 0-based offset into <paramref name="text"/>.</summary>
    public static (int Line, int Column) LocationOf(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] != '\n') continue;
            line++;
            lineStart = i + 1;
        }
        return (line, offset - lineStart + 1);
    }
}
