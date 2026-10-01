using System.Collections.Concurrent;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.Query;

/// <summary>
/// The most frequent values of columns, for the editor to suggest after <c>Status = </c>. Each column is sampled once
/// per connection (the first rows of the table, like column profiling, with the data-search lock and statement
/// timeouts) the first time the editor asks for it; until then <see cref="TryGet"/> returns null and starts the read.
/// </summary>
public sealed class ColumnValueCache(DatabaseSession session, int sampleRows = 10_000, int top = 30)
{
    private static readonly DataSearchOptions Options = new(1000, 10, 1000);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<ValueFrequency>>> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A column's values finished loading (raised on a worker thread).</summary>
    public event Action? ValuesLoaded;

    public DatabaseSession Session { get; } = session;

    /// <summary>The cached values, or null while they load (or when the column is not worth sampling).</summary>
    public IReadOnlyList<ValueFrequency>? TryGet(DbObject table, DbColumn column)
    {
        if (!IsSuggestible(column, Session.Provider.ProviderKey) || !table.IsTableLike || Session.Provider.GetProfileLevel(column) == ColumnProfileLevel.NullsOnly) return null;
        var key = $"{table.Database}\u0001{table.Schema}\u0001{table.Name}\u0001{column.Name}";
        var task = _values.GetOrAdd(key, _ => LoadAsync(table, column));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private async Task<IReadOnlyList<ValueFrequency>> LoadAsync(DbObject table, DbColumn column)
    {
        try
        {
            await Task.Yield();
            var values = await Session.Provider.GetTopValuesAsync(table, column, sampleRows, top, Options);
            return values.Where(v => v.Value is not null).ToList();
        }
        catch
        {
            return []; // a locked or unreadable table simply offers no values
        }
        finally
        {
            ValuesLoaded?.Invoke();
        }
    }

    /// <summary>Short text, numbers, booleans, dates and ids: values someone would type into a condition.</summary>
    public static bool IsSuggestible(DbColumn column, string providerKey)
    {
        var type = column.BaseType.ToLowerInvariant();
        var dataType = column.DataType.ToLowerInvariant();
        // SQL Server's text/ntext are legacy LOBs; PostgreSQL's text is the everyday string type.
        if (type is "text" && providerKey == "SqlServer") return false;
        if (type is "ntext" or "xml" or "json" or "jsonb" or "bytea" or "image" or "varbinary" or "binary" or "geometry" or "geography") return false;
        if (dataType.Contains("(max)", StringComparison.Ordinal)) return false;
        return true;
    }
}
