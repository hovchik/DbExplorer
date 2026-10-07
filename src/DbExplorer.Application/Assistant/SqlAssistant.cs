using System.Text.RegularExpressions;
using DbExplorer.Application.Metadata;

namespace DbExplorer.Application.Assistant;

public enum AssistantTask
{
    /// <summary>Write SQL from a sentence.</summary>
    Write,

    /// <summary>Explain what a query does.</summary>
    Explain,

    /// <summary>Fix a query that failed with an error.</summary>
    Fix
}

/// <summary>What the assistant answered: its prose, and the SQL it proposes (if any) ready to insert.</summary>
public sealed record AssistantAnswer(AssistantTask Task, string Text, string? Sql);

/// <summary>Where the question is asked: the dialect and the catalog of the tab's database.</summary>
public sealed record AssistantContext(string ProviderKey, MetadataSnapshot Snapshot, string? Database);

/// <summary>
/// The schema-aware SQL assistant behind the Query tab's AI menu. It sends the question, the query text and the
/// catalog's names and types (see <see cref="SchemaContextBuilder"/>), never row data, and never runs anything:
/// proposed SQL is returned for the user to insert.
/// </summary>
public sealed partial class SqlAssistant(IAssistantModel model)
{
    public Task<AssistantAnswer> WriteAsync(AssistantContext context, string request, string? currentSql, CancellationToken ct)
    {
        var question = "Write SQL for this request:\n" + request.Trim();
        if (!string.IsNullOrWhiteSpace(currentSql))
            question += "\n\nThe editor currently contains this query, for context (you may build on it):\n```sql\n" + currentSql.Trim() + "\n```";
        return AskAsync(AssistantTask.Write, context, request + "\n" + currentSql, question, ct);
    }

    public Task<AssistantAnswer> ExplainAsync(AssistantContext context, string sql, CancellationToken ct) =>
        AskAsync(AssistantTask.Explain, context, sql,
            "Explain what this query does, step by step, in plain language. Mention anything that looks wrong or slow. " +
            "Do not rewrite it unless there is a bug.\n```sql\n" + sql.Trim() + "\n```", ct);

    public Task<AssistantAnswer> FixAsync(AssistantContext context, string sql, string error, CancellationToken ct) =>
        AskAsync(AssistantTask.Fix, context, sql + "\n" + error,
            "This query failed. Say in one or two sentences what caused the error, then give the corrected query.\n" +
            "```sql\n" + sql.Trim() + "\n```\nError:\n" + error.Trim(), ct);

    private async Task<AssistantAnswer> AskAsync(AssistantTask task, AssistantContext context, string focus, string question, CancellationToken ct)
    {
        var schema = SchemaContextBuilder.Build(context.Snapshot, context.Database, focus);
        var text = await model.AskAsync(SystemPrompt(context, schema), question, ct);
        return new AssistantAnswer(task, text.Trim(), ExtractSql(text));
    }

    public static string DialectName(string providerKey) => providerKey switch
    {
        "SqlServer" => "Microsoft SQL Server (T-SQL)",
        "Postgres" => "PostgreSQL",
        _ => providerKey
    };

    /// <summary>Instructions plus the schema. Kept identical between questions on the same database so it is cached.</summary>
    public static string SystemPrompt(AssistantContext context, string schema) =>
        $"""
        You are the SQL assistant inside Db Explorer, a desktop database IDE. The user is connected to a
        {DialectName(context.ProviderKey)} database{(context.Database is { Length: > 0 } db ? $" named \"{db}\"" : "")}.

        Use only the tables, views, columns and routines listed in the schema below, with their exact names, and quote
        identifiers the way {DialectName(context.ProviderKey)} needs. If the request cannot be answered from this schema,
        say what is missing instead of inventing names. Prefer queries that only read data; when a change is asked for,
        write it but say plainly that it modifies data.

        Put any SQL you propose in a single ```sql fenced block. Your SQL is inserted into the user's editor and never run
        automatically. Keep the prose short.

        Schema (names and types only):
        {schema}
        """;

    /// <summary>The first ```sql block of <paramref name="text"/> (or the first fenced block of any language).</summary>
    public static string? ExtractSql(string text)
    {
        var match = SqlFenceRegex().Match(text);
        if (!match.Success) match = AnyFenceRegex().Match(text);
        return match.Success && match.Groups["sql"].Value.Trim() is { Length: > 0 } sql ? sql : null;
    }

    [GeneratedRegex(@"```[ \t]*(?:sql|tsql|t-sql|postgresql|pgsql|plpgsql)[ \t]*\r?\n(?<sql>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SqlFenceRegex();

    [GeneratedRegex(@"```[^\n]*\r?\n(?<sql>.*?)```", RegexOptions.Singleline)]
    private static partial Regex AnyFenceRegex();
}
