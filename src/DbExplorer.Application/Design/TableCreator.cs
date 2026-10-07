using DbExplorer.Application.Sessions;

namespace DbExplorer.Application.Design;

/// <summary>Writes the script for a designed table (CREATE, or ALTER for an existing one) and runs it on the server.</summary>
public static class TableCreator
{
    /// <summary>The CREATE TABLE script, preceded by CREATE SCHEMA when the schema is new to the database.</summary>
    /// <param name="original">The table as it is on the server: the script is then the ALTER statements that turn it into
    /// <paramref name="design"/>.</param>
    public static string Script(TableDesign design, DesignContext context, TableDesign? original = null)
    {
        var provider = context.ProviderKey;
        var schema = TableScriptBuilder.SchemaOf(design, provider);
        var createSchema = !context.Schemas.Contains(schema, StringComparer.OrdinalIgnoreCase) &&
                           !TableDesign.Same(schema, TableScriptBuilder.DefaultSchema(provider, design.Database));
        return original is null
            ? TableScriptBuilder.Build(design, provider, createSchema)
            : TableAlterScriptBuilder.Build(original, design, provider, createSchema);
    }

    /// <summary>Runs <paramref name="script"/> in the design's database inside one transaction, so a failing statement
    /// (a foreign key to a column of another type, a name taken meanwhile, a value that does not fit a new type) leaves
    /// nothing behind. SQL Server and PostgreSQL roll back DDL with the transaction; MySQL commits each DDL statement as it
    /// runs, so there a failure leaves the statements before it applied (the confirmation says so).</summary>
    public static async Task CreateAsync(DatabaseSession session, TableDesign design, string script, int timeoutSeconds, CancellationToken ct = default)
    {
        var database = string.IsNullOrWhiteSpace(design.Database) ? null : design.Database;
        await using var run = await session.Provider.BeginScriptSessionAsync(database, transactional: true, ct);
        await run.ExecuteAsync(script, timeoutSeconds, ct);
        await run.CommitAsync(ct);
    }
}
