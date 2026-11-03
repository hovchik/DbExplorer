using DbExplorer.Application.Copy;

namespace DbExplorer.Application.Design;

/// <summary>A ready-made column the Table designer offers in its "Common columns" menu.</summary>
public sealed record CommonColumn(string Label, string Hint, ColumnDesign Column);

/// <summary>
/// The columns most tables end up with (timestamps, a name, an active flag…), typed for the engine and named the way
/// the database already names its columns, so adding one is a click instead of four edits.
/// </summary>
public static class CommonColumns
{
    /// <summary>The common columns for <paramref name="design"/>, leaving out the ones it already has.</summary>
    public static IReadOnlyList<CommonColumn> For(string providerKey, DesignContext context, TableDesign design)
    {
        var style = StyleFor(providerKey, context, design);
        string N(string snake) => style == NamingStyle.Pascal ? Pascal(snake) : snake;

        ColumnDesign Typed(string snake, TypeFamily family, bool nullable = true, string? @default = null, string? size = null)
        {
            var (type, preferredSize) = ColumnTypes.Preferred(providerKey, family);
            return new ColumnDesign { Name = N(snake), Type = type, Size = size ?? preferredSize, IsNullable = nullable, Default = @default };
        }

        var now = (ColumnDesign c) => c with { Default = ColumnTypes.NowExpression(providerKey, c.BaseType) };
        var list = new List<CommonColumn>
        {
            new("Created at", "When the row was added; filled in by the server", now(Typed("created_at", TypeFamily.DateTime, nullable: false))),
            new("Updated at", "When the row last changed; set it from your code or a trigger", Typed("updated_at", TypeFamily.DateTime)),
            new("Name", "Short required text", Typed("name", TypeFamily.Text, nullable: false, size: "200")),
            new("Description", "Long optional text", Typed("description", TypeFamily.LargeText)),
            new("Email", "An email address", Typed("email", TypeFamily.Text, size: "320")),
            new("Active flag", "Yes/no, on by default", Typed("is_active", TypeFamily.Boolean, nullable: false, @default: True(providerKey))),
            new("Status", "A short code such as new, paid, shipped", Typed("status", TypeFamily.Text, nullable: false, size: "30")),
            new("Sort order", "A number to order rows by", Typed("sort_order", TypeFamily.Integer, nullable: false, @default: "0")),
            new("Amount", "Money or another exact decimal", Typed("amount", TypeFamily.Decimal)),
            new("Public id", "A random unique id to show outside the database", Typed("public_id", TypeFamily.Uuid, nullable: false, @default: NewUuid(providerKey))),
        };
        var taken = design.Columns.Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list.Where(c => !taken.Contains(c.Column.Name)).ToList();
    }

    /// <summary>The naming the catalog uses, else the naming of the design's own columns, else the engine's habit.</summary>
    public static NamingStyle StyleFor(string providerKey, DesignContext context, TableDesign design)
    {
        if (context.ColumnStyle != NamingStyle.Unknown) return context.ColumnStyle;
        // A one-word lowercase name ("id", "total") fits both styles, so only names that show their style count.
        var names = design.Columns.Select(c => c.Name.Trim()).ToList();
        var pascal = names.Any(n => DesignContext.StyleOf(n) == NamingStyle.Pascal);
        var snake = names.Any(n => n.Contains('_') && DesignContext.StyleOf(n) == NamingStyle.Snake);
        if (pascal != snake) return pascal ? NamingStyle.Pascal : NamingStyle.Snake;
        return providerKey == SqlDialect.SqlServerKey ? NamingStyle.Pascal : NamingStyle.Snake;
    }

    private static string Pascal(string snake) =>
        string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static string True(string providerKey) => providerKey switch
    {
        SqlDialect.SqlServerKey or SqlDialect.SqliteKey => "1",
        _ => "true"
    };

    private static string? NewUuid(string providerKey) => providerKey switch
    {
        SqlDialect.SqlServerKey => "NEWID()",
        SqlDialect.MySqlKey => "(UUID())",
        SqlDialect.SqliteKey => null,
        _ => "gen_random_uuid()"
    };
}
