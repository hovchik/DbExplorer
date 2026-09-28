using System.Text.RegularExpressions;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

public enum CompletionKind
{
    Column,
    Alias,
    Keyword,
    Table,
    View,
    Routine,
    Schema,
    Other
}

public sealed record CompletionItem(string Label, string InsertText, CompletionKind Kind, string? Detail = null)
{
    public string KindLabel => Kind.ToString().ToLowerInvariant();
}

/// <summary>Items to show plus the text range [ReplaceStart, caret) that picking one replaces.</summary>
public sealed record CompletionResult(int ReplaceStart, IReadOnlyList<CompletionItem> Items)
{
    public static readonly CompletionResult Empty = new(0, []);
}

/// <summary>
/// Context-aware completion over a metadata snapshot. Understands table aliases
/// (<c>FROM dbo.Orders o</c> then <c>o.</c> lists Orders' columns), schema qualifiers
/// (<c>sales.</c> lists objects in that schema) and ranks columns of the tables referenced
/// in the current statement above everything else.
/// </summary>
public sealed class SqlCompletionEngine
{
    public static readonly IReadOnlyList<string> Keywords =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE", "IS", "NULL",
        "ORDER BY", "GROUP BY", "HAVING", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN",
        "CROSS JOIN", "CROSS APPLY", "OUTER APPLY", "ON", "AS", "DISTINCT", "TOP", "LIMIT", "OFFSET",
        "UNION", "UNION ALL", "EXCEPT", "INTERSECT", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM",
        "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE INDEX", "CREATE PROCEDURE", "CREATE FUNCTION",
        "BEGIN", "END", "IF", "ELSE", "DECLARE", "CAST", "CONVERT", "COALESCE", "COUNT", "SUM", "AVG", "MIN",
        "MAX", "CASE", "WHEN", "THEN", "OVER", "PARTITION BY", "WITH", "NOLOCK", "ASC", "DESC"
    ];

    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "GROUP", "ORDER", "HAVING",
        "UNION", "EXCEPT", "INTERSECT", "WITH", "SET", "VALUES", "SELECT", "LIMIT", "OFFSET", "AS", "APPLY",
        "NATURAL", "USING", "WINDOW", "FOR", "OPTION", "PIVOT", "UNPIVOT", "TABLESAMPLE", "WHEN", "THEN"
    };

    private const string Ident = @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w$#@]*)";

    /// <summary>A (db.)(schema.)table reference after FROM/JOIN/UPDATE/INTO or a comma, with an optional alias.</summary>
    private static readonly Regex TableReference = new(
        $@"(?:\b(?:FROM|JOIN|UPDATE|INTO|APPLY)\b|,)\s*(?<name>{Ident}(?:\s*\.\s*{Ident}){{0,2}})(?:\s+(?:AS\s+)?(?<alias>{Ident}))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex SimpleIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly Func<string, string> _quote;
    private readonly IReadOnlyList<DbObject> _objects;
    private readonly ILookup<string, DbObject> _objectsByName;
    private readonly ILookup<string, DbObject> _objectsBySchema;
    private readonly IReadOnlyList<string> _schemas;
    private readonly IReadOnlyList<string> _allColumnNames;
    private readonly MetadataSnapshot _snapshot;

    public SqlCompletionEngine(MetadataSnapshot snapshot, Func<string, string> quote)
    {
        _snapshot = snapshot;
        _quote = quote;
        _objects = snapshot.Objects
            .Where(o => o.Type is not DbObjectType.Trigger)
            .DistinctBy(o => (o.Schema.ToUpperInvariant(), o.Name.ToUpperInvariant()))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _objectsByName = _objects.ToLookup(o => o.Name, StringComparer.OrdinalIgnoreCase);
        _objectsBySchema = _objects.ToLookup(o => o.Schema, StringComparer.OrdinalIgnoreCase);
        _schemas = _objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        _allColumnNames = snapshot.Columns.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <param name="explicitRequest">True for Ctrl+Space: an empty prefix still lists suggestions.</param>
    public CompletionResult Complete(string text, int caret, bool explicitRequest = false, int max = 25)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var prefix = text[start..caret];

        var qualifier = ReadQualifier(text, start);
        var statement = CurrentStatement(text, caret);
        var references = ParseReferences(statement);

        IEnumerable<CompletionItem> items;
        if (qualifier is not null)
        {
            items = QualifiedItems(qualifier, references, prefix);
        }
        else
        {
            if (prefix.Length == 0 && !explicitRequest) return CompletionResult.Empty;
            items = UnqualifiedItems(references, prefix);
        }

        var list = items
            .DistinctBy(i => (i.Kind == CompletionKind.Keyword, i.InsertText.ToUpperInvariant()))
            .Take(max)
            .ToList();
        return new CompletionResult(start, list);
    }

    /// <summary>The (db.)schema.table references and their aliases found in <paramref name="sql"/> that resolve to real objects.</summary>
    public IReadOnlyList<TableReferenceInfo> ParseReferences(string sql)
    {
        var result = new List<TableReferenceInfo>();
        foreach (Match m in TableReference.Matches(sql))
        {
            var parts = SplitQualified(m.Groups["name"].Value);
            var table = parts[^1];
            var schema = parts.Count >= 2 ? parts[^2] : null;
            var obj = Resolve(schema, table);
            if (obj is null) continue;

            var alias = m.Groups["alias"].Success ? Unquote(m.Groups["alias"].Value) : null;
            if (alias is not null && NotAnAlias.Contains(alias)) alias = null;
            result.Add(new TableReferenceInfo(obj, alias));
        }
        return result;
    }

    private IEnumerable<CompletionItem> QualifiedItems(
        IReadOnlyList<string> qualifier, IReadOnlyList<TableReferenceInfo> references, string prefix)
    {
        var last = qualifier[^1];

        if (qualifier.Count == 1)
        {
            var aliased = references.FirstOrDefault(r => string.Equals(r.Alias, last, StringComparison.OrdinalIgnoreCase));
            if (aliased is not null) return ColumnItems(aliased.Object, prefix);
        }

        var schema = qualifier.Count >= 2 ? qualifier[^2] : null;
        var table = Resolve(schema, last);
        var items = new List<CompletionItem>();
        if (table is not null) items.AddRange(ColumnItems(table, prefix));

        if (qualifier.Count == 1 || qualifier.Count == 2)
        {
            items.AddRange(_objectsBySchema[last]
                .Where(o => Matches(o.Name, prefix))
                .Select(o => ObjectItem(o, qualified: false)));
        }
        return items;
    }

    private IEnumerable<CompletionItem> UnqualifiedItems(IReadOnlyList<TableReferenceInfo> references, string prefix)
    {
        var contextualColumns = references
            .SelectMany(r => _snapshot.ColumnsOf(r.Object.Database, r.Object.Schema, r.Object.Name))
            .Where(c => Matches(c.Name, prefix))
            .Select(c => new CompletionItem(c.Name, QuoteIfNeeded(c.Name), CompletionKind.Column, $"{c.Table} · {c.DataType}"));

        var aliases = references
            .Where(r => r.Alias is not null && Matches(r.Alias, prefix))
            .Select(r => new CompletionItem(r.Alias!, r.Alias!, CompletionKind.Alias, r.Object.FullName));

        var keywords = Keywords
            .Where(k => Matches(k, prefix))
            .Select(k => new CompletionItem(k, k, CompletionKind.Keyword));

        var objects = _objects
            .Where(o => Matches(o.Name, prefix) || Matches(o.FullName, prefix))
            .OrderBy(o => o.IsTableLike ? 0 : 1)
            .Select(o => ObjectItem(o, qualified: true));

        var schemas = _schemas
            .Where(s => Matches(s, prefix))
            .Select(s => new CompletionItem(s, QuoteIfNeeded(s), CompletionKind.Schema));

        var otherColumns = prefix.Length == 0
            ? []
            : _allColumnNames.Where(c => Matches(c, prefix))
                .Select(c => new CompletionItem(c, QuoteIfNeeded(c), CompletionKind.Column));

        return contextualColumns.Concat(aliases).Concat(keywords).Concat(objects).Concat(schemas).Concat(otherColumns);
    }

    private IEnumerable<CompletionItem> ColumnItems(DbObject obj, string prefix) =>
        _snapshot.ColumnsOf(obj.Database, obj.Schema, obj.Name)
            .OrderBy(c => c.Ordinal)
            .Where(c => Matches(c.Name, prefix))
            .Select(c => new CompletionItem(c.Name, QuoteIfNeeded(c.Name), CompletionKind.Column,
                c.DataType + (c.IsPrimaryKey ? " · PK" : "")));

    private CompletionItem ObjectItem(DbObject o, bool qualified)
    {
        var kind = o.Type switch
        {
            DbObjectType.Table or DbObjectType.ForeignTable => CompletionKind.Table,
            DbObjectType.View or DbObjectType.MaterializedView => CompletionKind.View,
            _ when o.IsRoutine => CompletionKind.Routine,
            _ => CompletionKind.Other
        };
        var insert = qualified ? $"{QuoteIfNeeded(o.Schema)}.{QuoteIfNeeded(o.Name)}" : QuoteIfNeeded(o.Name);
        return new CompletionItem(o.Name, insert, kind, o.Schema);
    }

    private DbObject? Resolve(string? schema, string name)
    {
        var candidates = _objectsByName[name];
        return schema is null
            ? candidates.OrderBy(o => o.IsTableLike ? 0 : 1).FirstOrDefault()
            : candidates.FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
    }

    private string QuoteIfNeeded(string identifier) =>
        SimpleIdentifier.IsMatch(identifier) ? identifier : _quote(identifier);

    private static bool Matches(string candidate, string prefix) =>
        prefix.Length == 0 || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    /// <summary>Reads "a.b." immediately before the word start, e.g. <c>dbo.Orders.</c> → [dbo, Orders].</summary>
    private static IReadOnlyList<string>? ReadQualifier(string text, int wordStart)
    {
        var parts = new List<string>();
        var i = wordStart;
        while (i > 0 && text[i - 1] == '.')
        {
            var end = i - 1;
            var j = end;
            if (j > 0 && text[j - 1] is ']' or '"')
            {
                var close = text[j - 1];
                var open = close == ']' ? '[' : '"';
                var k = j - 2;
                while (k >= 0 && text[k] != open) k--;
                if (k < 0) break;
                parts.Insert(0, text[(k + 1)..(j - 1)]);
                i = k;
            }
            else
            {
                while (j > 0 && IsWordChar(text[j - 1])) j--;
                if (j == end) break;
                parts.Insert(0, text[j..end]);
                i = j;
            }
        }
        return parts.Count == 0 ? null : parts;
    }

    /// <summary>The statement around the caret: split on ';' and on GO lines so aliases don't leak across statements.</summary>
    private static string CurrentStatement(string text, int caret)
    {
        var start = text.LastIndexOf(';', Math.Max(0, caret - 1));
        var goBefore = LastGo(text, caret);
        start = Math.Max(start, goBefore) + 1;
        var end = text.IndexOf(';', caret);
        if (end < 0) end = text.Length;
        var goAfter = NextGo(text, caret);
        if (goAfter >= 0 && goAfter < end) end = goAfter;
        return start >= end ? "" : text[start..end];
    }

    private static readonly Regex GoLine = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int LastGo(string text, int caret)
    {
        var last = -1;
        foreach (Match m in GoLine.Matches(text))
        {
            if (m.Index + m.Length > caret) break;
            last = m.Index + m.Length - 1;
        }
        return last;
    }

    private static int NextGo(string text, int caret)
    {
        foreach (Match m in GoLine.Matches(text))
            if (m.Index >= caret) return m.Index;
        return -1;
    }

    private static List<string> SplitQualified(string name) =>
        Regex.Matches(name, Ident).Select(m => Unquote(m.Value)).ToList();

    private static string Unquote(string identifier) =>
        identifier.Length >= 2 && (identifier[0] == '[' || identifier[0] == '"') ? identifier[1..^1] : identifier;
}

public sealed record TableReferenceInfo(DbObject Object, string? Alias);
