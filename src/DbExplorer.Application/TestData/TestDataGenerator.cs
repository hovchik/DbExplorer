using System.Globalization;
using System.Text;
using DbExplorer.Application.Design;

namespace DbExplorer.Application.TestData;

/// <summary>
/// Makes rows from a <see cref="TestDataPlan"/>: realistic values for each column's kind, parent keys for foreign
/// keys, and no repeats where a primary key or unique index forbids them. The same plan, count and seed give the same
/// rows, so a preview and the insert that follows match.
/// </summary>
public static class TestDataGenerator
{
    public const int MaxRows = 100_000;

    public static TestDataRows Generate(TestDataPlan plan, int rowCount, int seed, int nullPercent = 10, DateTime? today = null)
    {
        rowCount = Math.Clamp(rowCount, 0, MaxRows);
        nullPercent = Math.Clamp(nullPercent, 0, 100);
        var anchor = (today ?? DateTime.Today).Date;
        var rng = new Random(seed);
        var token = Token(seed);
        var columns = plan.Columns.Where(c => c.IsInserted).ToList();
        var notes = new List<string>();

        // A parent with no rows: its columns can only be NULL, or no row can be made at all.
        var emptyParents = plan.References.Select((r, i) => (r, i)).Where(x => x.r.Rows.Count == 0).Select(x => x.i).ToHashSet();
        foreach (var index in emptyParents)
        {
            var reference = plan.References[index];
            var cols = columns.Where(c => c.Kind == TestValueKind.Reference && c.ReferenceIndex == index).ToList();
            if (cols.Count == 0) continue;
            if (cols.Any(c => !c.Column.IsNullable))
            {
                notes.Add(reference.IsSelfReference
                    ? $"{string.Join(", ", reference.Columns)} must point at an existing row of this table, and it has none. Make the column nullable or add a first row by hand."
                    : $"{reference.ParentName} has no rows, so {string.Join(", ", reference.Columns)} has nothing to point at. Fill {reference.ParentName} first.");
                return new TestDataRows { Columns = columns, Rows = [], Shortfall = rowCount, Notes = notes };
            }
            notes.Add($"{reference.ParentName} has no rows, so {string.Join(", ", reference.Columns)} is left NULL.");
        }

        // One-to-one keys: each parent row used once, in a shuffled order.
        var pools = new Dictionary<int, Queue<object?[]>>();
        for (var i = 0; i < plan.References.Count; i++)
        {
            if (emptyParents.Contains(i)) continue;
            var unique = columns.Any(c => c.Kind == TestValueKind.Reference && c.ReferenceIndex == i && c.Unique);
            if (unique) pools[i] = new Queue<object?[]>(Shuffle(plan.References[i].Rows.ToList(), rng));
        }

        var position = columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);
        var uniqueKeys = new List<(int[] Slots, HashSet<string> Seen)>();
        foreach (var key in plan.UniqueKeys)
        {
            // A key with a column the server fills (identity) can't collide on the generated part alone.
            var slots = key.Columns.Select(n => position.TryGetValue(n, out var p) ? p : -1).ToArray();
            if (slots.Any(s => s < 0)) continue;
            uniqueKeys.Add((slots, new HashSet<string>(key.Existing, StringComparer.Ordinal)));
        }
        foreach (var column in columns.Where(c => c.Unique && plan.UniqueKeys.All(k => k.Columns.Count != 1 || !Same(k.Columns[0], c.Name))))
            uniqueKeys.Add(([position[column.Name]], new HashSet<string>(StringComparer.Ordinal)));

        var oneOfOrder = columns.Where(c => c.Kind == TestValueKind.OneOf && c.Unique)
            .ToDictionary(c => c.Name, c => new Queue<string>(Shuffle(c.Choices.ToList(), rng)), StringComparer.OrdinalIgnoreCase);

        var rows = new List<object?[]>(rowCount);
        var attempts = 0;
        var maxAttempts = rowCount * 20 + 1000;
        var exhausted = false;
        while (rows.Count < rowCount && attempts++ < maxAttempts && !exhausted)
        {
            var n = rows.Count + 1;
            var parents = new Dictionary<int, object?[]?>();
            for (var i = 0; i < plan.References.Count; i++)
            {
                if (emptyParents.Contains(i)) { parents[i] = null; continue; }
                if (pools.TryGetValue(i, out var pool))
                {
                    if (pool.Count == 0) { exhausted = true; break; }
                    parents[i] = pool.Dequeue();
                }
                else
                {
                    var choices = plan.References[i].Rows;
                    parents[i] = choices[rng.Next(choices.Count)];
                }
            }
            if (exhausted) break;

            var person = Person.Next(rng);
            var row = new object?[columns.Count];
            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                if (column.Kind == TestValueKind.OneOf && column.Unique)
                {
                    var queue = oneOfOrder[column.Name];
                    if (queue.Count == 0) { exhausted = true; break; }
                    row[c] = queue.Dequeue();
                    continue;
                }
                if (column.Column.IsNullable && !column.Unique && nullPercent > 0 && CanBeRandomNull(column) && rng.Next(100) < nullPercent)
                {
                    row[c] = null;
                    continue;
                }
                row[c] = column.Kind == TestValueKind.Reference
                    ? (column.ReferenceIndex is { } r && parents.TryGetValue(r, out var tuple) && tuple is not null ? tuple[column.ReferenceSlot] : null)
                    : Value(column, rng, n, token, anchor, person);
            }
            if (exhausted) break;

            foreach (var order in plan.Orders) Order(row, position, order);

            var keys = uniqueKeys.Select(k => KeyOf(row, k.Slots)).ToList();
            var clash = false;
            for (var k = 0; k < uniqueKeys.Count && !clash; k++)
                clash = keys[k] is { } key && uniqueKeys[k].Seen.Contains(key);
            if (clash) continue;
            for (var k = 0; k < uniqueKeys.Count; k++)
                if (keys[k] is { } key) uniqueKeys[k].Seen.Add(key);
            rows.Add(row);
        }

        var shortfall = rowCount - rows.Count;
        if (shortfall > 0)
            notes.Add(rows.Count == 0
                ? "No row could be made without repeating a unique value."
                : $"Only {rows.Count:N0} distinct row(s) fit the unique keys and the parent rows available; {shortfall:N0} fewer than asked.");
        return new TestDataRows { Columns = columns, Rows = rows, Shortfall = shortfall, Notes = notes };
    }

    private static bool CanBeRandomNull(TestColumnPlan column) =>
        column.Kind is not (TestValueKind.Fixed or TestValueKind.Sequence or TestValueKind.Null);

    /// <summary>A NULL in any column means the row doesn't take part in the key (NULLs never clash).</summary>
    private static string? KeyOf(object?[] row, int[] slots)
    {
        var sb = new StringBuilder();
        foreach (var slot in slots)
        {
            if (row[slot] is null) return null;
            sb.Append(KeyText(row[slot])).Append('\u001f');
        }
        return sb.ToString();
    }

    /// <summary>The text a value is compared by for uniqueness: case-insensitive, since SQL Server and MySQL collations
    /// usually are, and numbers without trailing zeros.</summary>
    public static string KeyText(object? value) => value switch
    {
        null => "",
        decimal d => d.ToString("G29", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexString(b),
        bool b => b ? "1" : "0",
        _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").ToLowerInvariant()
    };

    private static void Order(object?[] row, Dictionary<string, int> position, TestColumnOrder order)
    {
        if (!position.TryGetValue(order.Greater, out var g) || !position.TryGetValue(order.Lesser, out var l)) return;
        if (row[g] is not IComparable greater || row[l] is not { } lesser || greater.GetType() != lesser.GetType()) return;
        var cmp = greater.CompareTo(lesser);
        if (cmp < 0) (row[g], row[l]) = (row[l], row[g]);
        if (cmp == 0 && order.Strict)
            row[g] = row[g] switch
            {
                DateTime dt => dt.AddDays(1),
                DateOnly d => d.AddDays(1),
                TimeOnly t => t.AddMinutes(1),
                long v => v + 1,
                decimal v => v + 1,
                double v => v + 1,
                var v => v
            };
    }

    private static object? Value(TestColumnPlan c, Random rng, int n, string token, DateTime today, Person person)
    {
        switch (c.Kind)
        {
            case TestValueKind.Null:
            case TestValueKind.ServerDefault:
            case TestValueKind.Reference:
                return null;
            case TestValueKind.Fixed:
                return c.FixedValue;
            case TestValueKind.Sequence:
                return Number(c, c.SequenceStart + n - 1);
            case TestValueKind.OneOf:
                return c.Choices.Count == 0 ? null : Fit(c, c.Choices[rng.Next(c.Choices.Count)]);
            case TestValueKind.Integer:
                return Number(c, RandomRange(c, 1, 1000, rng, 0));
            case TestValueKind.Quantity:
                return Number(c, RandomRange(c, 1, 100, rng, 0));
            case TestValueKind.Age:
                return Number(c, RandomRange(c, 18, 90, rng, 0));
            case TestValueKind.Year:
                return Number(c, RandomRange(c, 1990, today.Year, rng, 0));
            case TestValueKind.Price:
                return Number(c, RandomRange(c, 1, 1000, rng, Math.Min(c.Scale ?? 2, 2)));
            case TestValueKind.Decimal:
                return c.Note switch
                {
                    "latitude" => Number(c, RandomRange(c with { Min = -90, Max = 90 }, -90, 90, rng, Math.Min(c.Scale ?? 6, 6))),
                    "longitude" => Number(c, RandomRange(c with { Min = -180, Max = 180 }, -180, 180, rng, Math.Min(c.Scale ?? 6, 6))),
                    _ => Number(c, RandomRange(c, 0, 100, rng, Math.Min(c.Scale ?? 2, 4)))
                };
            case TestValueKind.Boolean:
                return c.Family == TypeFamily.Integer ? Number(c, rng.Next(2)) : rng.Next(2) == 1;
            case TestValueKind.Date:
                return Moment(c, today.AddDays(-rng.Next(730)), dateOnly: true, unique: c.Unique ? n : 0);
            case TestValueKind.BirthDate:
                return Moment(c, new DateTime(1950, 1, 1).AddDays(rng.Next(20_000)), dateOnly: true, unique: c.Unique ? n : 0);
            case TestValueKind.DateTime:
                return Moment(c, today.AddDays(-rng.Next(730)).AddSeconds(rng.Next(86_400)), dateOnly: false, unique: c.Unique ? n : 0);
            case TestValueKind.Time:
                var time = new TimeOnly(8, 0).Add(TimeSpan.FromMinutes(rng.Next(600)));
                return c.Family == TypeFamily.Text ? Fit(c, time.ToString("HH:mm", CultureInfo.InvariantCulture)) : time;
            case TestValueKind.Uuid:
                var bytes = new byte[16];
                rng.NextBytes(bytes);
                bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // version 4
                bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
                var guid = new Guid(bytes);
                return c.Family == TypeFamily.Uuid ? guid : Fit(c, guid.ToString("D"));
            case TestValueKind.Binary:
                var data = new byte[16];
                rng.NextBytes(data);
                return data;
            case TestValueKind.Json:
                return Fit(c, $"{{\"id\": {n}, \"tag\": \"{Pick(Words.Tags, rng)}\", \"score\": {rng.Next(100)}}}");
        }

        var text = c.Kind switch
        {
            TestValueKind.Word => Pick(Words.Tags, rng),
            TestValueKind.ProductName => $"{Pick(Words.Adjectives, rng)} {Pick(Words.Materials, rng)} {Pick(Words.Products, rng)}",
            TestValueKind.Sentence => Sentence(rng),
            TestValueKind.Paragraph => string.Join(" ", Enumerable.Range(0, 3 + rng.Next(3)).Select(_ => Sentence(rng))),
            TestValueKind.Code => Code(rng, Math.Min(c.MaxLength ?? 10, 10)),
            TestValueKind.FirstName => person.First,
            TestValueKind.LastName => person.Last,
            TestValueKind.FullName => person.First + " " + person.Last,
            TestValueKind.Username => (person.First[0] + person.Last).ToLowerInvariant() + rng.Next(10, 99).ToString(CultureInfo.InvariantCulture),
            TestValueKind.Email => Email(person, c.Unique ? $"{n}.{token}" : "", rng),
            TestValueKind.Phone => Phone(rng, c.MaxLength),
            TestValueKind.Company => $"{Pick(Words.LastNames, rng)} {Pick(Words.CompanySuffixes, rng)}",
            TestValueKind.Street => $"{rng.Next(1, 999)} {Pick(Words.Streets, rng)} {Pick(Words.StreetTypes, rng)}",
            TestValueKind.City => Pick(Words.Cities, rng),
            TestValueKind.Region => Pick(Words.Regions, rng),
            TestValueKind.Country => c.MaxLength is <= 3 ? Pick(Words.CountryCodes, rng)[..Math.Min(c.MaxLength.Value, 2)] : Pick(Words.Countries, rng),
            TestValueKind.PostalCode => rng.Next(10000, 99999).ToString(CultureInfo.InvariantCulture),
            TestValueKind.Url => $"https://www.{Pick(Words.LastNames, rng).ToLowerInvariant()}{Pick(Words.Domains, rng)}",
            TestValueKind.Color => Pick(Words.Colors, rng),
            _ => ""
        };
        // Email carries its own suffix; any other unique text gets one so values never repeat.
        return c.Unique && c.Kind != TestValueKind.Email ? FitUnique(c, text, $"-{token}{n}") : Fit(c, text);
    }

    /// <summary>A whole number in the column's own type: decimal for decimal columns, double for floats, long otherwise.</summary>
    private static object Number(TestColumnPlan c, decimal value) => c.Family switch
    {
        TypeFamily.Decimal => value,
        TypeFamily.Float => (double)value,
        TypeFamily.Text => Fit(c, value.ToString(CultureInfo.InvariantCulture)),
        _ => (long)value
    };

    private static object Number(TestColumnPlan c, long value) => Number(c, (decimal)value);

    /// <summary>A value between the kind's usual range and the column's limits (type and CHECK), with
    /// <paramref name="scale"/> decimals.</summary>
    internal static decimal RandomRange(TestColumnPlan c, decimal defaultLow, decimal defaultHigh, Random rng, int scale)
    {
        if (c.Family is TypeFamily.Integer) scale = 0;
        var step = 1m;
        for (var i = 0; i < scale; i++) step /= 10;
        decimal? min = c.Min is { } m ? (c.MinExclusive ? m + step : m) : null;
        decimal? max = c.Max is { } x ? (c.MaxExclusive ? x - step : x) : null;
        if (scale == 0)
        {
            if (min is { } mn) min = Math.Ceiling(mn);
            if (max is { } mx) max = Math.Floor(mx);
        }

        var low = Math.Max(defaultLow, min ?? decimal.MinValue);
        var high = Math.Min(defaultHigh, max ?? decimal.MaxValue);
        if (low > high)
        {
            var width = defaultHigh - defaultLow;
            if (min is { } lo && lo > defaultHigh) { low = lo; high = Math.Min(max ?? decimal.MaxValue, lo + width); }
            else if (max is { } hi) { high = hi; low = Math.Max(min ?? decimal.MinValue, hi - width); }
            if (low > high) high = low;
        }

        var steps = (long)Math.Min((high - low) / step, long.MaxValue - 1);
        var pick = steps <= 0 ? 0 : (long)(rng.NextDouble() * (steps + 1));
        return Math.Min(low + pick * step, high);
    }

    private static object Moment(TestColumnPlan c, DateTime value, bool dateOnly, int unique)
    {
        if (unique > 0) value = dateOnly ? value.AddDays(-unique * 731L % 36500) : value.AddMinutes(-unique);
        value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (c.Family == TypeFamily.Date || (dateOnly && c.Family != TypeFamily.DateTime && c.Family != TypeFamily.Text))
            return DateOnly.FromDateTime(value);
        if (c.Family == TypeFamily.Text)
            return Fit(c, value.ToString(dateOnly ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        return dateOnly ? value.Date : value;
    }

    private static string Fit(TestColumnPlan c, string text) =>
        c.MaxLength is { } max && text.Length > max ? text[..max].TrimEnd() : text;

    private static string FitUnique(TestColumnPlan c, string text, string suffix)
    {
        if (c.MaxLength is not { } max) return text + suffix;
        if (suffix.Length >= max) return suffix.TrimStart('-')[^Math.Min(max, suffix.Length - 1)..];
        return (text.Length + suffix.Length > max ? text[..(max - suffix.Length)] : text) + suffix;
    }

    private static string Email(Person person, string unique, Random rng)
    {
        var local = $"{person.First}.{person.Last}".ToLowerInvariant().Replace("'", "").Replace(" ", "");
        return unique.Length > 0
            ? $"{local}.{unique}@{Pick(Words.EmailDomains, rng)}"
            : $"{local}{(rng.Next(3) == 0 ? rng.Next(1, 99).ToString(CultureInfo.InvariantCulture) : "")}@{Pick(Words.EmailDomains, rng)}";
    }

    private static string Phone(Random rng, int? maxLength)
    {
        var area = rng.Next(200, 999);
        var line = rng.Next(1000, 9999);
        var full = $"+1 555-{area:000}-{line:0000}";
        if (maxLength is null || full.Length <= maxLength) return full;
        var digits = $"555{area:000}{line:0000}";
        return digits.Length <= maxLength ? digits : digits[..maxLength.Value];
    }

    private static string Code(Random rng, int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[Math.Max(1, length)];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(chars);
    }

    private static string Sentence(Random rng)
    {
        var count = 6 + rng.Next(7);
        var words = Enumerable.Range(0, count).Select(_ => Pick(Words.Lorem, rng)).ToArray();
        words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
        return string.Join(" ", words) + ".";
    }

    /// <summary>A short base-36 tag of the seed, put into unique text so a second run doesn't repeat the first's values.</summary>
    private static string Token(int seed)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var value = (uint)seed % (36u * 36 * 36 * 36);
        var chars = new char[4];
        for (var i = 3; i >= 0; i--) { chars[i] = digits[(int)(value % 36)]; value /= 36; }
        return new string(chars);
    }

    private static T Pick<T>(IReadOnlyList<T> items, Random rng) => items[rng.Next(items.Count)];

    private static List<T> Shuffle<T>(List<T> items, Random rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>One made-up person per row, so first name, last name and email in the same row agree.</summary>
    private sealed record Person(string First, string Last)
    {
        public static Person Next(Random rng) => new(Pick(Words.FirstNames, rng), Pick(Words.LastNames, rng));
    }

    private static class Words
    {
        public static readonly string[] FirstNames =
        [
            "Anna", "James", "Maria", "David", "Sofia", "Liam", "Emma", "Noah", "Olivia", "Lucas", "Mia", "Ethan", "Ava",
            "Mateo", "Chloe", "Leo", "Grace", "Daniel", "Zoe", "Samuel", "Nora", "Aram", "Lilit", "Hiro", "Yuki", "Priya",
            "Arjun", "Fatima", "Omar", "Elena", "Ivan", "Chen", "Mei", "Kwame", "Amara", "Lars", "Ingrid", "Diego", "Lucia", "Tomás"
        ];

        public static readonly string[] LastNames =
        [
            "Smith", "Johnson", "Garcia", "Brown", "Miller", "Davis", "Martinez", "Wilson", "Anderson", "Taylor", "Thomas",
            "Moore", "Martin", "Lee", "Walker", "Hall", "Young", "King", "Wright", "Lopez", "Hill", "Green", "Adams", "Baker",
            "Nelson", "Carter", "Petrosyan", "Sato", "Kumar", "Haddad", "Novak", "Rossi", "Muller", "Silva", "Nguyen", "Kim",
            "Okafor", "Larsen", "Ivanova", "Costa"
        ];

        public static readonly string[] CompanySuffixes = ["Ltd", "Group", "Labs", "Systems", "Trading", "Partners", "Inc", "Studio", "Logistics"];
        public static readonly string[] Streets = ["Oak", "Maple", "Cedar", "Pine", "Elm", "Lake", "Hill", "River", "Park", "Sunset", "Mill", "Church", "Station"];
        public static readonly string[] StreetTypes = ["Street", "Avenue", "Road", "Lane", "Drive", "Court", "Way"];

        public static readonly string[] Cities =
        [
            "Springfield", "Riverton", "Lakeside", "Fairview", "Georgetown", "Madison", "Clinton", "Franklin", "Ashland",
            "Yerevan", "Lisbon", "Oslo", "Austin", "Denver", "Lyon", "Porto", "Kyoto", "Melbourne", "Toronto", "Dublin"
        ];

        public static readonly string[] Regions = ["California", "Texas", "Ontario", "Bavaria", "Lombardy", "Kyoto", "Victoria", "Ararat", "Catalonia", "Oregon"];

        public static readonly string[] Countries =
        [
            "United States", "Canada", "Germany", "France", "Italy", "Spain", "Portugal", "Japan", "Australia", "Armenia",
            "Ireland", "Norway", "Brazil", "India", "Netherlands"
        ];

        public static readonly string[] CountryCodes = ["US", "CA", "DE", "FR", "IT", "ES", "PT", "JP", "AU", "AM", "IE", "NO", "BR", "IN", "NL"];
        public static readonly string[] Domains = [".com", ".io", ".net", ".org", ".dev"];
        public static readonly string[] EmailDomains = ["example.com", "example.org", "example.net", "mail.example.com"];
        public static readonly string[] Colors = ["red", "green", "blue", "black", "white", "orange", "purple", "teal", "gold", "silver", "navy", "olive"];

        public static readonly string[] Adjectives =
            ["Smart", "Classic", "Compact", "Deluxe", "Eco", "Ultra", "Rustic", "Modern", "Handmade", "Sleek", "Ergonomic", "Vintage"];

        public static readonly string[] Materials = ["Oak", "Steel", "Cotton", "Leather", "Glass", "Bamboo", "Granite", "Wool", "Ceramic", "Copper"];

        public static readonly string[] Products =
            ["Lamp", "Chair", "Table", "Backpack", "Mug", "Watch", "Keyboard", "Bottle", "Jacket", "Speaker", "Notebook", "Desk", "Shelf", "Bowl"];

        public static readonly string[] Tags =
            ["alpha", "beta", "gamma", "basic", "premium", "standard", "retail", "wholesale", "online", "local", "internal", "external", "new", "legacy"];

        public static readonly string[] Lorem =
        [
            "the", "order", "customer", "quickly", "shipped", "team", "report", "monthly", "review", "updated", "account",
            "payment", "received", "product", "quality", "service", "delivery", "request", "approved", "after", "before",
            "with", "support", "invoice", "new", "item", "status", "changed", "note", "follow", "up", "planned", "for", "next", "week"
        ];
    }
}
