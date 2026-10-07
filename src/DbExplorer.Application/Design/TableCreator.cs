using DbExplorer.Application.Sessions;

namespace DbExplorer.Application.Design;

/// <summary>Writes the script for a designed table and runs it on the server.</summary>
public static class TableCreator
{
    /// <summary>The CREATE TABLE script, preceded by CREATE SCHEMA when the schema is new to the database.</summary>
    public static string Script(TableDesign design, DesignContext context)
    {
        var provider = context.ProviderKey;
        var schema = TableScriptBuilder.SchemaOf(design, provider);
        var createSchema = !context.Schemas.Contains(schema, StringComparer.OrdinalIgnoreCase) &&
                           !TableDesign.Same(schema, TableScriptBuilder.DefaultSchema(provider));
        return TableScriptBuilder.Build(design, provider, createSchema);
    }

    /// <summary>Runs <paramref name="script"/> in the design's database inside one transaction, so a failing statement
    /// (a foreign key to a column of another type, a name taken meanwhile) leaves nothing behind.</summary>
    public static async Task CreateAsync(DatabaseSession session, TableDesign design, string script, int timeoutSeconds, CancellationToken ct = default)
    {
        var database = string.IsNullOrWhiteSpace(design.Database) ? null : design.Database;
        await using var run = await session.Provider.BeginScriptSessionAsync(database, transactional: true, ct);
        await run.ExecuteAsync(script, timeoutSeconds, ct);
        await run.CommitAsync(ct);
    }
}
