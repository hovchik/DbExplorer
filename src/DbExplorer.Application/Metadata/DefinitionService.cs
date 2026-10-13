using System.Text;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Metadata;

public sealed class DefinitionService
{
    public async Task<string?> GetDefinitionAsync(DatabaseSession session, DbObject obj, CancellationToken ct = default)
    {
        if (obj.Type is DbObjectType.Table or DbObjectType.ForeignTable)
            return ScriptTable(session.Provider, obj, session.Snapshot.ColumnsOf(obj.Database, obj.Schema, obj.Name));

        var texts = await GetDefinitionsAsync(session, obj, ct);
        return texts.Count > 0 ? string.Join("\n\n", texts) : null;
    }

    /// <summary>The CREATE text of a view, routine, trigger, sequence or synonym, one per overload; empty when the
    /// server does not show it (encrypted, or no permission).</summary>
    public async Task<IReadOnlyList<string>> GetDefinitionsAsync(DatabaseSession session, DbObject obj, CancellationToken ct = default)
    {
        // Served from the snapshot when possible: no server round-trip.
        var cached = session.Snapshot.Modules
            .Where(m => string.Equals(m.Database, obj.Database, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(m.Schema, obj.Schema, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(m.Name, obj.Name, StringComparison.OrdinalIgnoreCase)
                     && m.Type == obj.Type && m.Definition is not null)
            .Select(m => m.Definition!)
            .ToList();

        if (cached.Count > 0) return cached;

        var text = await session.Provider.GetDefinitionAsync(obj, ct);
        return string.IsNullOrWhiteSpace(text) ? [] : [text];
    }

    public static string ScriptTable(IDatabaseProvider provider, DbObject table, IEnumerable<DbColumn> columns)
    {
        var cols = columns.OrderBy(c => c.Ordinal).ToList();
        var q = provider.QuoteIdentifier;
        var sb = new StringBuilder();

        sb.Append("CREATE TABLE ").Append(q(table.Schema)).Append('.').Append(q(table.Name)).AppendLine(" (");

        // Names and types padded so the columns line up.
        var nameWidth = cols.Select(c => q(c.Name).Length).DefaultIfEmpty(0).Max();
        var typeWidth = cols.Select(c => c.DataType.Length).DefaultIfEmpty(0).Max();
        var lines = cols.Select(c =>
            $"    {q(c.Name).PadRight(nameWidth)} {c.DataType.PadRight(typeWidth)}{(c.IsNullable ? " NULL" : " NOT NULL")}{(c.IsComputed ? " /* computed */" : "")}")
            .ToList();

        var keys = cols.Where(c => c.IsPrimaryKey).Select(c => q(c.Name)).ToList();
        if (keys.Count > 0) lines.Add($"    PRIMARY KEY ({string.Join(", ", keys)})");

        sb.AppendLine(string.Join("," + Environment.NewLine, lines));
        sb.AppendLine(");");
        sb.AppendLine();
        sb.Append("-- Generated from catalog metadata; see the Indexes tab for indexes.");
        return sb.ToString();
    }
}
