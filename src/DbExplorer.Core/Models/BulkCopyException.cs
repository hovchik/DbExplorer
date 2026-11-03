namespace DbExplorer.Core.Models;

/// <summary>A bulk load the server refused, with the 1-based position of the row it stopped at when the engine says.</summary>
public sealed class BulkCopyException(string message, long? row, Exception? inner = null) : Exception(message, inner)
{
    public long? Row { get; } = row;
}
