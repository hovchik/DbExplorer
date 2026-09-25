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
            return ScriptTable(session.Provider, obj, session.Snapshot.ColumnsOf(obj.Schema, obj.Name));

        // Served from the snapshot when possible: no server round-trip.
        var cached = session.Snapshot.Modules
            .Where(m => m.Schema == obj.Schema && m.Name == obj.Name && m.Type == obj.Type && m.Definition is not null)
            .Select(m => m.Definition!)
            .ToList();

        if (cached.Count > 0) return string.Join("\n\n", cached);

        return await session.Provider.GetDefinitionAsync(obj, ct);
    }

    public static string ScriptTable(IDatabaseProvider provider, DbObject table, IEnumerable<DbColumn> columns)
    {
        var cols = columns.OrderBy(c => c.Ordinal).ToList();
        var q = provider.QuoteIdentifier;
        var sb = new StringBuilder();

        sb.Append("CREATE TABLE ").Append(q(table.Schema)).Append('.').Append(q(table.Name)).AppendLine(" (");

        var lines = cols.Select(c =>
            $"    {q(c.Name)} {c.DataType}{(c.IsNullable ? " NULL" : " NOT NULL")}{(c.IsComputed ? " /* computed */" : "")}")
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
