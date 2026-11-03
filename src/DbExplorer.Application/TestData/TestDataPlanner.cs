using System.Globalization;
using System.Text.RegularExpressions;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.TestData;

/// <summary>
/// Decides how to fill each column of a table from its type, its name, its keys and its CHECK constraints, without
/// touching the server. <see cref="TestDataService"/> then reads what only the data can tell (parent rows, the largest
/// key, enum labels) and <see cref="TestDataGenerator"/> makes the rows.
/// </summary>
public static class TestDataPlanner
{
    private static readonly string[] StatusChoices = ["active", "pending", "inactive", "archived"];
    private static readonly string[] CurrencyChoices = ["USD", "EUR", "GBP", "JPY", "CHF"];
    private static readonly string[] PriorityChoices = ["low", "medium", "high"];

    public static TestDataPlan Plan(
        string providerKey, DbObject table, IEnumerable<DbColumn> columns, IEnumerable<DbForeignKey> foreignKeys,
        IEnumerable<DbIndex> indexes, DbTableConstraints constraints)
    {
        var dialect = SqlDialect.For(providerKey);
        var cols = columns.OrderBy(c => c.Ordinal).ToList();
        var warnings = new List<string>();

        var uniqueSingles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueKeys = new List<TestUniqueKey>();
        var keyColumns = cols.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        if (keyColumns.Count == 1) uniqueSingles.Add(keyColumns[0]);
        else if (keyColumns.Count > 1) uniqueKeys.Add(new TestUniqueKey("primary key", keyColumns));
        foreach (var index in indexes.Where(i => i.IsUnique && !i.IsPrimaryKey && !i.IsDisabled))
        {
            var names = IndexColumns(index.Columns, cols);
            if (names is null) continue; // an expression index: nothing to check it against
            if (names.Count == 1) uniqueSingles.Add(names[0]);
            else if (names.Count > 1 && !uniqueKeys.Any(k => k.Columns.SequenceEqual(names, StringComparer.OrdinalIgnoreCase)))
                uniqueKeys.Add(new TestUniqueKey(index.Name, names));
        }

        var references = new List<TestReference>();
        var referenceOf = new Dictionary<string, (int Index, int Slot)>(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in foreignKeys.Where(f => !f.IsVirtual && !f.IsDisabled))
        {
            var child = SplitList(fk.Columns);
            var parent = SplitList(fk.ReferencedColumns);
            if (child.Count == 0 || child.Count != parent.Count) continue;
            if (child.Any(referenceOf.ContainsKey)) continue; // a column in two keys: the first one wins
            var self = Same(fk.ReferencedSchema, table.Schema) && Same(fk.ReferencedTable, table.Name);
            references.Add(new TestReference { Key = fk, Columns = child, ParentColumns = parent, IsSelfReference = self });
            for (var i = 0; i < child.Count; i++) referenceOf[child[i]] = (references.Count - 1, i);
        }

        var checks = ReadChecks(constraints.Checks, cols, warnings);

        var plans = new List<TestColumnPlan>();
        foreach (var column in cols)
        {
            var family = ColumnTypes.Family(column.DataType.Length > 0 ? column.DataType : column.BaseType);
            if (family == TypeFamily.Other && column.BaseType.Length > 0) family = ColumnTypes.Family(column.BaseType);
            var (length, precision, scale) = Sizes(column.DataType, family);
            var plan = new TestColumnPlan
            {
                Column = column,
                Family = family,
                MaxLength = length,
                Precision = precision,
                Scale = scale,
                Unique = uniqueSingles.Contains(column.Name),
                HasDefault = constraints.Defaults.ContainsKey(column.Name),
                Choices = EnumChoices(column),
            };
            plan = WithTypeRange(plan, column);
            if (checks.TryGetValue(column.Name, out var check)) plan = check(plan);
            if (referenceOf.TryGetValue(column.Name, out var reference))
                plan = plan with { ReferenceIndex = reference.Index, ReferenceSlot = reference.Slot };

            plans.Add(Decide(plan, table, dialect));
        }

        return new TestDataPlan
        {
            ProviderKey = providerKey,
            Table = table,
            Columns = plans,
            References = references,
            UniqueKeys = uniqueKeys,
            Orders = ReadOrders(constraints.Checks, cols),
            Warnings = warnings
        };
    }

    /// <summary>The detected kind, the note explaining it, and the kinds the user may switch to.</summary>
    private static TestColumnPlan Decide(TestColumnPlan plan, DbObject table, SqlDialect dialect)
    {
        var column = plan.Column;
        var options = new List<TestValueKind>();
        TestValueKind kind;
        string note;

        if (column.IsComputed || dialect.IsReadOnlyColumn(column))
        {
            return plan with { Kind = TestValueKind.ServerDefault, Options = [TestValueKind.ServerDefault], Note = "computed by the server" };
        }
        if (column.IsIdentity)
        {
            return plan with { Kind = TestValueKind.ServerDefault, Options = [TestValueKind.ServerDefault], Note = "identity: the server numbers it" };
        }

        if (plan.ReferenceIndex is not null)
        {
            kind = TestValueKind.Reference;
            note = plan.Unique ? "foreign key, each parent used once" : "foreign key";
            options.Add(TestValueKind.Reference);
        }
        else if (plan.Choices.Count > 0)
        {
            kind = TestValueKind.OneOf;
            note = "one of " + string.Join(", ", plan.Choices.Take(4)) + (plan.Choices.Count > 4 ? ", …" : "");
            options.Add(TestValueKind.OneOf);
        }
        else
        {
            (kind, note) = Detect(plan, table);
            if (kind == TestValueKind.OneOf) plan = plan with { Choices = ChoicesFor(Normalize(column.Name)) };
            if (kind == TestValueKind.OneOf) note = "one of " + string.Join(", ", plan.Choices);
        }

        if (plan.Unique && plan.HasDefault && kind != TestValueKind.Reference)
        {
            // serial / nextval(), NEWID(), a default from a sequence: values from here would leave the sequence behind,
            // so the next ordinary insert would collide with them.
            options.Add(TestValueKind.ServerDefault);
            kind = TestValueKind.ServerDefault;
            note = "unique with a default (a sequence or generated value): the server fills it";
        }
        else if (plan.Unique && kind is TestValueKind.Integer or TestValueKind.Quantity or TestValueKind.Age or TestValueKind.Year)
        {
            kind = TestValueKind.Sequence;
            note = "unique: numbers after the largest in the table";
        }
        else if (plan.Unique && kind is TestValueKind.OneOf or TestValueKind.Boolean)
        {
            note += " (unique: at most one row per value)";
        }
        else if (plan.Unique && kind != TestValueKind.Reference)
        {
            note = note.Length > 0 ? note + ", unique" : "unique";
        }

        if (kind == TestValueKind.ServerDefault && !column.IsNullable && !plan.HasDefault)
            note = "no generator for this type: pick one or type a fixed value";

        options.Add(kind);
        options.AddRange(KindsFor(plan.Family));
        if (plan.Choices.Count > 0) options.Add(TestValueKind.OneOf);
        if (column.IsNullable) options.Add(TestValueKind.Null);
        options.Add(TestValueKind.Fixed);
        if (column.IsNullable || plan.HasDefault) options.Add(TestValueKind.ServerDefault);

        return plan with { Kind = kind, Note = note, Options = options.Distinct().ToList() };
    }

    /// <summary>The kind a column's name and type suggest.</summary>
    private static (TestValueKind Kind, string Note) Detect(TestColumnPlan plan, DbObject table)
    {
        var name = Normalize(plan.Column.Name);
        var tableName = Normalize(table.Name);
        bool Has(params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.Ordinal));
        bool Is(params string[] names) => names.Any(n => name == n);
        bool Ends(params string[] parts) => parts.Any(p => name.EndsWith(p, StringComparison.Ordinal));

        switch (plan.Family)
        {
            case TypeFamily.Text:
            case TypeFamily.LargeText:
            case TypeFamily.Other when plan.Column.DataType.Contains("char", StringComparison.OrdinalIgnoreCase):
                if (Has("email", "mail")) return (TestValueKind.Email, "email from the name");
                if (Is("firstname", "fname", "givenname", "forename")) return (TestValueKind.FirstName, "first name from the name");
                if (Is("lastname", "lname", "surname", "familyname")) return (TestValueKind.LastName, "last name from the name");
                if (Is("username", "login", "userlogin", "nickname", "handle", "screenname")) return (TestValueKind.Username, "user name from the name");
                if (Has("phone", "mobile", "fax") || Is("tel", "telephone")) return (TestValueKind.Phone, "phone number from the name");
                if (Has("company", "organization", "organisation", "employer", "vendor", "supplier")) return (TestValueKind.Company, "company from the name");
                if (Has("street", "address")) return (TestValueKind.Street, "street address from the name");
                if (Has("city", "town")) return (TestValueKind.City, "city from the name");
                if (Has("country")) return (TestValueKind.Country, "country from the name");
                if (Is("state", "region", "province", "county")) return (TestValueKind.Region, "region from the name");
                if (Has("zip", "postal", "postcode")) return (TestValueKind.PostalCode, "postal code from the name");
                if (Has("url", "website", "homepage", "link", "uri")) return (TestValueKind.Url, "URL from the name");
                if (Has("color", "colour")) return (TestValueKind.Color, "colour from the name");
                if (Has("status", "state") && !plan.Unique) return (TestValueKind.OneOf, "");
                if (Has("currency") && !plan.Unique) return (TestValueKind.OneOf, "");
                if (Has("priority", "severity") && !plan.Unique) return (TestValueKind.OneOf, "");
                if (Has("sku", "code", "token", "hash", "password", "secret", "key", "serial", "barcode", "isbn", "reference"))
                    return (TestValueKind.Code, "code from the name");
                if (Is("name", "fullname", "displayname", "contactname", "customername", "employeename", "authorname"))
                {
                    if (!Is("name") || Personish(tableName)) return (TestValueKind.FullName, "person's name");
                    return Companyish(tableName)
                        ? (TestValueKind.Company, "company name from the table")
                        : (TestValueKind.ProductName, "a name");
                }
                if (Is("title", "productname", "itemname", "subject", "label", "caption", "heading"))
                    return (TestValueKind.ProductName, "a title");
                if (Has("description", "comment", "note", "bio", "body", "content", "message", "summary", "remark", "details", "text"))
                    return (plan.Family == TypeFamily.LargeText ? TestValueKind.Paragraph : TestValueKind.Sentence, "text from the name");
                if (Has("type", "kind", "category", "group", "tag", "gender", "role")) return (TestValueKind.Word, "a word");
                if (Has("name")) return (TestValueKind.ProductName, "a name");
                return plan.Family == TypeFamily.LargeText || plan.MaxLength is null or > 120
                    ? (TestValueKind.Sentence, "text")
                    : (TestValueKind.Word, "text");

            case TypeFamily.Integer:
                if (Is("age")) return (TestValueKind.Age, "age 18–90");
                if (Ends("year") || Is("yr")) return (TestValueKind.Year, "a recent year");
                if (Has("qty", "quantity", "count", "stock", "units", "items")) return (TestValueKind.Quantity, "quantity");
                if (Has("price", "cost", "amount", "total", "salary", "balance", "fee")) return (TestValueKind.Price, "amount");
                return (TestValueKind.Integer, "");

            case TypeFamily.Decimal:
            case TypeFamily.Float:
                if (Has("lat") && !Has("late")) return (TestValueKind.Decimal, "latitude");
                if (Has("lng", "lon")) return (TestValueKind.Decimal, "longitude");
                if (Has("qty", "quantity")) return (TestValueKind.Quantity, "quantity");
                return Has("percent", "pct", "rate", "ratio", "discount", "tax")
                    ? (TestValueKind.Decimal, "")
                    : (TestValueKind.Price, Has("price", "cost", "amount", "total", "salary", "balance", "fee") ? "amount" : "");

            case TypeFamily.Boolean:
                return (TestValueKind.Boolean, "");
            case TypeFamily.Date:
                return Has("birth", "dob", "born") ? (TestValueKind.BirthDate, "birth date") : (TestValueKind.Date, "within the last two years");
            case TypeFamily.DateTime:
                return Has("birth", "dob", "born") ? (TestValueKind.BirthDate, "birth date") : (TestValueKind.DateTime, "within the last two years");
            case TypeFamily.Time:
                return (TestValueKind.Time, "");
            case TypeFamily.Uuid:
                return (TestValueKind.Uuid, "");
            case TypeFamily.Json:
                return plan.Column.BaseType.Equals("xml", StringComparison.OrdinalIgnoreCase)
                    ? (TestValueKind.Fixed, "XML: type a fixed value")
                    : (TestValueKind.Json, "small JSON object");
            case TypeFamily.Binary:
                return (TestValueKind.Binary, "16 random bytes");
            default:
                return (TestValueKind.ServerDefault, plan.Column.IsNullable ? "type not generated: left NULL" : "");
        }
    }

    private static bool Personish(string table) =>
        new[] { "customer", "user", "employee", "person", "people", "author", "contact", "member", "student", "client",
            "patient", "staff", "teacher", "account", "owner", "driver", "player", "subscriber" }
            .Any(table.Contains);

    private static bool Companyish(string table) =>
        new[] { "company", "companies", "vendor", "supplier", "organization", "organisation", "partner", "brand", "manufacturer" }
            .Any(table.Contains);

    private static IReadOnlyList<string> ChoicesFor(string name) =>
        name.Contains("currency") ? CurrencyChoices
        : name.Contains("priority") || name.Contains("severity") ? PriorityChoices
        : StatusChoices;

    /// <summary>The kinds that make sense for a type, offered in the column's drop-down.</summary>
    private static IEnumerable<TestValueKind> KindsFor(TypeFamily family) => family switch
    {
        TypeFamily.Text or TypeFamily.LargeText =>
        [
            TestValueKind.Word, TestValueKind.ProductName, TestValueKind.Sentence, TestValueKind.Paragraph, TestValueKind.Code,
            TestValueKind.FirstName, TestValueKind.LastName, TestValueKind.FullName, TestValueKind.Username, TestValueKind.Email,
            TestValueKind.Phone, TestValueKind.Company, TestValueKind.Street, TestValueKind.City, TestValueKind.Region,
            TestValueKind.Country, TestValueKind.PostalCode, TestValueKind.Url, TestValueKind.Color, TestValueKind.Uuid,
            TestValueKind.Json
        ],
        TypeFamily.Integer => [TestValueKind.Integer, TestValueKind.Sequence, TestValueKind.Quantity, TestValueKind.Price, TestValueKind.Age, TestValueKind.Year],
        TypeFamily.Decimal or TypeFamily.Float => [TestValueKind.Price, TestValueKind.Decimal, TestValueKind.Quantity, TestValueKind.Integer],
        TypeFamily.Boolean => [TestValueKind.Boolean],
        TypeFamily.Date or TypeFamily.DateTime => [TestValueKind.DateTime, TestValueKind.Date, TestValueKind.BirthDate],
        TypeFamily.Time => [TestValueKind.Time],
        TypeFamily.Uuid => [TestValueKind.Uuid],
        TypeFamily.Json => [TestValueKind.Json],
        TypeFamily.Binary => [TestValueKind.Binary],
        _ => []
    };

    /// <summary>Length for text, precision and scale for decimals, read from the display type ("nvarchar(50)",
    /// "numeric(10,2)", "character varying(80)").</summary>
    private static (int? Length, int? Precision, int? Scale) Sizes(string dataType, TypeFamily family)
    {
        var match = Regex.Match(dataType, @"\(\s*(\d+)\s*(?:,\s*(\d+)\s*)?\)", RegexOptions.CultureInvariant);
        if (!match.Success) return (null, null, null);
        var first = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int? second = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
        return family switch
        {
            TypeFamily.Text => (first, null, null),
            TypeFamily.Decimal => (null, first, second ?? 0),
            _ => (null, null, null)
        };
    }

    /// <summary>The numeric range the column's type can hold.</summary>
    private static TestColumnPlan WithTypeRange(TestColumnPlan plan, DbColumn column)
    {
        var type = column.BaseType.ToLowerInvariant();
        var unsigned = column.DataType.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
        return plan.Family switch
        {
            TypeFamily.Integer => type switch
            {
                "tinyint" => plan with { Min = 0, Max = unsigned || column.DataType.Length == 0 ? 255 : 127 },
                "smallint" or "int2" => plan with { Min = unsigned ? 0 : -32768, Max = unsigned ? 65535 : 32767 },
                "mediumint" => plan with { Min = unsigned ? 0 : -8388608, Max = unsigned ? 16777215 : 8388607 },
                "int" or "integer" or "int4" or "serial" => plan with { Min = unsigned ? 0 : int.MinValue, Max = unsigned ? uint.MaxValue : int.MaxValue },
                _ => plan with { Min = unsigned ? 0 : long.MinValue, Max = long.MaxValue }
            },
            TypeFamily.Decimal when plan.Precision is { } p && p - (plan.Scale ?? 0) < 28 =>
                plan with { Max = Pow10(p - (plan.Scale ?? 0)) - Step(plan.Scale ?? 0), Min = -(Pow10(p - (plan.Scale ?? 0)) - Step(plan.Scale ?? 0)) },
            TypeFamily.Decimal when type is "smallmoney" => plan with { Min = -214748m, Max = 214748m },
            _ => plan
        };
    }

    private static decimal Pow10(int n)
    {
        var value = 1m;
        for (var i = 0; i < n; i++) value *= 10;
        return value;
    }

    private static decimal Step(int scale)
    {
        var value = 1m;
        for (var i = 0; i < scale; i++) value /= 10;
        return value;
    }

    /// <summary>MySQL enum('a','b') and set(...) labels from the column type.</summary>
    private static IReadOnlyList<string> EnumChoices(DbColumn column)
    {
        var type = column.DataType.TrimStart();
        if (!type.StartsWith("enum(", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("set(", StringComparison.OrdinalIgnoreCase))
            return [];
        return StringLiterals(type);
    }

    private static readonly Regex StringLiteral = new(@"N?'((?:[^']|'')*)'", RegexOptions.CultureInvariant);

    private static List<string> StringLiterals(string text) =>
        StringLiteral.Matches(text).Select(m => m.Groups[1].Value.Replace("''", "'")).Distinct().ToList();

    /// <summary>
    /// The one-column CHECKs the generator can satisfy: a list of allowed strings (IN (…), = ANY (ARRAY[…]), a = 'x' OR
    /// a = 'y') and numeric bounds (&gt;, &gt;=, &lt;, &lt;=, BETWEEN). Others are listed as warnings, since a row that
    /// breaks one makes the whole insert fail and roll back.
    /// </summary>
    private static Dictionary<string, Func<TestColumnPlan, TestColumnPlan>> ReadChecks(
        IReadOnlyList<DbCheckConstraint> checks, IReadOnlyList<DbColumn> columns, List<string> warnings)
    {
        var result = new Dictionary<string, Func<TestColumnPlan, TestColumnPlan>>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in checks)
        {
            var expression = Simplify(check.Expression);
            var used = columns.Where(c => Mentions(expression, c.Name)).ToList();
            if (used.Count == 2 && OrderPattern(expression, used[0].Name, used[1].Name) is not null) continue;
            if (used.Count != 1)
            {
                if (used.Count > 1) warnings.Add($"Check {check.Name} ({check.Expression}) spans several columns and is not checked; rows that break it make the insert fail.");
                continue;
            }

            var name = used[0].Name;
            Func<TestColumnPlan, TestColumnPlan>? apply = null;
            var literals = StringLiterals(expression);
            if (literals.Count > 0 && IsValueList(expression, name))
            {
                apply = p => p with { Choices = literals };
            }
            else if (Bounds(expression, name) is { } bounds)
            {
                apply = p => p with
                {
                    Min = bounds.Min is { } min && (p.Min is null || min >= p.Min) ? min : p.Min,
                    MinExclusive = bounds.Min is not null ? bounds.MinExclusive : p.MinExclusive,
                    Max = bounds.Max is { } max && (p.Max is null || max <= p.Max) ? max : p.Max,
                    MaxExclusive = bounds.Max is not null ? bounds.MaxExclusive : p.MaxExclusive
                };
            }
            else if (IsNonEmptyCheck(expression, name))
            {
                continue; // generated text is never empty
            }

            if (apply is null)
            {
                warnings.Add($"Check {check.Name} on {name} ({check.Expression}) is not understood; rows that break it make the insert fail.");
                continue;
            }
            result[name] = result.TryGetValue(name, out var earlier) ? p => apply(earlier(p)) : apply;
        }
        return result;
    }

    private static IReadOnlyList<TestColumnOrder> ReadOrders(IReadOnlyList<DbCheckConstraint> checks, IReadOnlyList<DbColumn> columns)
    {
        var orders = new List<TestColumnOrder>();
        foreach (var check in checks)
        {
            var expression = Simplify(check.Expression);
            var used = columns.Where(c => Mentions(expression, c.Name)).ToList();
            if (used.Count == 2 && OrderPattern(expression, used[0].Name, used[1].Name) is { } order) orders.Add(order);
        }
        return orders;
    }

    /// <summary>"a &gt;= b", "b &lt; a" (optionally "a IS NULL OR …"): a is the greater one.</summary>
    private static TestColumnOrder? OrderPattern(string expression, string first, string second)
    {
        foreach (var (a, b) in new[] { (first, second), (second, first) })
        {
            var match = Regex.Match(expression,
                $@"^(?:.*\bOR\s+)?\(*\s*{Regex.Escape(a)}\s*(>=|>|<=|<)\s*{Regex.Escape(b)}\s*\)*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var op = match.Groups[1].Value;
            return op.StartsWith('>') ? new TestColumnOrder(a, b, op == ">") : new TestColumnOrder(b, a, op == "<");
        }
        return null;
    }

    private static (decimal? Min, bool MinExclusive, decimal? Max, bool MaxExclusive)? Bounds(string expression, string column)
    {
        if (Regex.IsMatch(expression, @"\bOR\b|\bNOT\b", RegexOptions.IgnoreCase)) return null;
        decimal? min = null, max = null;
        bool minEx = false, maxEx = false, any = false;
        var col = Regex.Escape(column);
        const string number = @"(-?\d+(?:\.\d+)?)";

        var between = Regex.Match(expression, $@"\b{col}\s+BETWEEN\s+\(*{number}\)*\s+AND\s+\(*{number}\)*", RegexOptions.IgnoreCase);
        if (between.Success)
        {
            min = Parse(between.Groups[1].Value);
            max = Parse(between.Groups[2].Value);
            any = true;
        }
        foreach (Match m in Regex.Matches(expression, $@"\b{col}\s*(>=|>|<=|<)\s*\(*{number}", RegexOptions.IgnoreCase))
        {
            any = true;
            Apply(m.Groups[1].Value, Parse(m.Groups[2].Value));
        }
        foreach (Match m in Regex.Matches(expression, $@"{number}\)*\s*(>=|>|<=|<)\s*{col}\b", RegexOptions.IgnoreCase))
        {
            any = true;
            Apply(Flip(m.Groups[2].Value), Parse(m.Groups[1].Value));
        }
        if (!any) return null;
        // Anything left besides the comparisons (functions, other operators) means the check says more than bounds.
        var rest = Regex.Replace(expression, $@"\b{col}\b|BETWEEN|\bAND\b|>=|<=|>|<|-?\d+(?:\.\d+)?|[()\s]", "", RegexOptions.IgnoreCase);
        return rest.Length == 0 ? (min, minEx, max, maxEx) : null;

        void Apply(string op, decimal value)
        {
            switch (op)
            {
                case ">": min = value; minEx = true; break;
                case ">=": min = value; minEx = false; break;
                case "<": max = value; maxEx = true; break;
                case "<=": max = value; maxEx = false; break;
            }
        }
    }

    /// <summary>Nothing but the column, string literals, IN / = ANY (ARRAY[…]) / = … OR = …: a list of allowed values.</summary>
    private static bool IsValueList(string expression, string column)
    {
        var rest = StringLiteral.Replace(expression, "");
        rest = Regex.Replace(rest, $@"\b{Regex.Escape(column)}\b|\bIN\b|\bANY\b|\bARRAY\b|\bOR\b|=|[(),\s]", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return rest.Length == 0;
    }

    private static string Flip(string op) => op switch { ">" => "<", ">=" => "<=", "<" => ">", _ => ">=" };

    private static decimal Parse(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool IsNonEmptyCheck(string expression, string column) =>
        Regex.IsMatch(expression,
            $@"^\(*\s*(?:(?:char_)?length|len|trim|btrim)\s*\(+\s*(?:(?:trim|btrim|ltrim|rtrim)\s*\(\s*)?{Regex.Escape(column)}\s*\)*\s*(>\s*0|>=\s*1|<>\s*''|!=\s*'')\s*\)*$|^\(*\s*(?:(?:trim|btrim)\s*\(\s*)?{Regex.Escape(column)}\s*\)*\s*(<>|!=)\s*''\s*\)*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Drops the noise engines add when they store a CHECK: casts (::numeric, ::text[]), quoting of names,
    /// ARRAY brackets, and redundant parentheses around numbers.</summary>
    private static string Simplify(string expression)
    {
        var text = Regex.Replace(expression, @"::[\w ]+(\[\])?(\(\d+(,\d+)?\))?", "", RegexOptions.CultureInvariant);
        text = text.Replace("[", "").Replace("]", "").Replace("`", "").Replace("\"", "");
        text = Regex.Replace(text, @"\((-?\d+(?:\.\d+)?)\)", "$1", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\((-?\d+(?:\.\d+)?)\)", "$1", RegexOptions.CultureInvariant);
        return text.Trim();
    }

    private static bool Mentions(string expression, string column)
    {
        // Names inside string literals don't count: CHECK (status IN ('status')).
        var code = StringLiteral.Replace(expression, "''");
        return Regex.IsMatch(code, $@"(?<![\w$]){Regex.Escape(column)}(?![\w$])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>An index's plain column names, or null when it indexes an expression.</summary>
    private static List<string>? IndexColumns(string? list, IReadOnlyList<DbColumn> columns)
    {
        var names = new List<string>();
        foreach (var raw in SplitList(list))
        {
            var name = Regex.Replace(raw, @"\s+(ASC|DESC)$", "", RegexOptions.IgnoreCase).Trim().Trim('"', '[', ']', '`');
            var column = columns.FirstOrDefault(c => Same(c.Name, name));
            if (column is null) return null;
            names.Add(column.Name);
        }
        return names;
    }

    private static List<string> SplitList(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim('"', '[', ']', '`')).ToList();

    internal static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
