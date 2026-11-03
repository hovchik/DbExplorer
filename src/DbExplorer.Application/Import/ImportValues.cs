using System.Globalization;
using System.Text.RegularExpressions;
using DbExplorer.Application.Design;

namespace DbExplorer.Application.Import;

/// <summary>How text from a file is read as values.</summary>
public sealed record ImportValueOptions
{
    /// <summary>An empty field (or one of only spaces) is NULL rather than empty text.</summary>
    public bool EmptyIsNull { get; init; } = true;

    /// <summary>Text that also means NULL, such as NULL or \N; empty for none. Compared ignoring case.</summary>
    public string NullText { get; init; } = "";

    /// <summary>The decimal separator of numbers in the file: '.' or ','. With ',' the thousands separator is '.' or a space.</summary>
    public char DecimalSeparator { get; init; } = '.';

    /// <summary>A .NET date format such as dd.MM.yyyy or MM/dd/yyyy HH:mm; empty to read ISO-8601 dates, and otherwise
    /// dates in the format of the computer's region.</summary>
    public string DateFormat { get; init; } = "";

    /// <summary>Spaces around a field are removed before it is read (always for numbers, dates and the like; this decides
    /// it for text).</summary>
    public bool TrimText { get; init; }
}

/// <summary>A field read as the column's type: the value, or why it is not one.</summary>
public readonly record struct ImportValue(object? Value, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Turns text from a CSV or Excel file into values of a column's type (by <see cref="TypeFamily"/>), and guesses the
/// type of a column from its values for a new table. Text that is not valid for the type gives an error naming the
/// value, so a file can be checked before anything is sent to the server.
/// </summary>
public static partial class ImportValues
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ImportValue Parse(string? text, TypeFamily family, ImportValueOptions options, int? maxLength = null)
    {
        if (text is null) return new ImportValue(null, null);
        var trimmed = text.Trim();
        if (trimmed.Length == 0 && (options.EmptyIsNull || family is not (TypeFamily.Text or TypeFamily.LargeText or TypeFamily.Other or TypeFamily.Json)))
            return new ImportValue(null, null);
        if (options.NullText.Length > 0 && string.Equals(trimmed, options.NullText, StringComparison.OrdinalIgnoreCase))
            return new ImportValue(null, null);

        object? value = family switch
        {
            TypeFamily.Integer => ParseInteger(trimmed, options),
            TypeFamily.Decimal => ParseDecimal(trimmed, options),
            TypeFamily.Float => ParseDecimal(trimmed, options) is decimal d ? (double)d : ParseDouble(trimmed, options),
            TypeFamily.Boolean => ParseBoolean(trimmed),
            TypeFamily.Date => ParseDateTime(trimmed, options) is { } date && date.TimeOfDay == TimeSpan.Zero ? DateOnly.FromDateTime(date.DateTime) : null,
            TypeFamily.DateTime => ParseDateTime(trimmed, options) is { } dt ? HasOffset(trimmed) ? dt : (object)dt.DateTime : null,
            TypeFamily.Time => ParseTime(trimmed),
            TypeFamily.Uuid => Guid.TryParse(trimmed, out var g) ? g : null,
            TypeFamily.Binary => ParseHex(trimmed),
            _ => options.TrimText ? trimmed : text
        };
        if (value is null) return new ImportValue(null, $"'{Shorten(text)}' is not {Describe(family)}");
        if (maxLength is { } max && value is string s && s.Length > max)
            return new ImportValue(null, $"'{Shorten(text)}' is {s.Length:N0} characters long; the column takes {max:N0}");
        return new ImportValue(value, null);
    }

    private static object? ParseInteger(string text, ImportValueOptions options)
    {
        var clean = StripGroups(text, options);
        if (long.TryParse(clean, NumberStyles.AllowLeadingSign, Inv, out var l)) return l;
        // 12.0 from a spreadsheet is still a whole number.
        if (decimal.TryParse(Normalize(clean, options), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out var d) &&
            d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue)
            return (long)d;
        return null;
    }

    private static object? ParseDecimal(string text, ImportValueOptions options) =>
        decimal.TryParse(Normalize(StripGroups(text, options), options),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, Inv, out var d)
            ? d
            : null;

    private static object? ParseDouble(string text, ImportValueOptions options) =>
        double.TryParse(Normalize(StripGroups(text, options), options), NumberStyles.Float, Inv, out var d) ? d : null;

    /// <summary>Thousands separators out: 1,234,567 (with '.') or 1.234.567 / 1 234 567 (with ','), only when grouped by three.</summary>
    private static string StripGroups(string text, ImportValueOptions options)
    {
        var group = options.DecimalSeparator == ',' ? '.' : ',';
        var spaced = text.Replace(' ', ' ').Replace(' ', ' ');
        if (spaced.Contains(' ') && GroupedBySpace().IsMatch(spaced)) return spaced.Replace(" ", "");
        if (spaced.Contains(group) && Grouped(group).IsMatch(spaced)) return spaced.Replace(group.ToString(), "");
        return spaced;
    }

    private static Regex Grouped(char group) => group == ',' ? GroupedByComma() : GroupedByDot();

    [GeneratedRegex(@"^[+-]?\d{1,3}(,\d{3})+(\.\d*)?$")]
    private static partial Regex GroupedByComma();

    [GeneratedRegex(@"^[+-]?\d{1,3}(\.\d{3})+(,\d*)?$")]
    private static partial Regex GroupedByDot();

    [GeneratedRegex(@"^[+-]?\d{1,3}( \d{3})+([.,]\d*)?$")]
    private static partial Regex GroupedBySpace();

    private static string Normalize(string text, ImportValueOptions options) =>
        options.DecimalSeparator == ',' ? text.Replace(',', '.') : text;

    private static object? ParseBoolean(string text) => text.ToLowerInvariant() switch
    {
        "1" or "true" or "t" or "yes" or "y" or "on" or "-1" => true,
        "0" or "false" or "f" or "no" or "n" or "off" => false,
        _ => null
    };

    private static bool HasOffset(string text) => OffsetSuffix().IsMatch(text);

    /// <summary>A time followed by an offset: 10:30Z, 10:30:00+02, 10:30:00.5 -05:00 (a date's own "-02" is not one).</summary>
    [GeneratedRegex(@"\d:\d{2}(:\d{2}(\.\d+)?)?\s*(Z|[+-]\d{2}(:?\d{2})?)$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetSuffix();

    [GeneratedRegex(@"^\d{4}-\d{1,2}-\d{1,2}([T ]\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?)?\s*(Z|[+-]\d{2}(:?\d{2})?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex IsoDate();

    /// <summary>A date and time with its offset (zero when the text has none): the format given, else ISO-8601, else
    /// the computer's region.</summary>
    private static DateTimeOffset? ParseDateTime(string text, ImportValueOptions options)
    {
        var styles = DateTimeStyles.AllowWhiteSpaces | (HasOffset(text) ? DateTimeStyles.None : DateTimeStyles.AssumeUniversal);
        if (options.DateFormat.Length > 0)
            return DateTimeOffset.TryParseExact(text, options.DateFormat, Inv, styles, out var exact) ? exact : null;
        if (IsoDate().IsMatch(text))
            return DateTimeOffset.TryParse(text.Replace('T', ' ').Replace('t', ' '), Inv, styles, out var iso) ? iso : null;
        return DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, styles, out var local) ? local : null;
    }

    private static object? ParseTime(string text) =>
        TimeSpan.TryParse(text, Inv, out var ts) ? ts
        : DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var dt) && dt.Date == DateTime.MinValue.Date ? dt.TimeOfDay
        : null;

    private static byte[]? ParseHex(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("\\x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Describe(TypeFamily family) => family switch
    {
        TypeFamily.Integer => "a whole number",
        TypeFamily.Decimal or TypeFamily.Float => "a number",
        TypeFamily.Boolean => "true or false",
        TypeFamily.Date => "a date",
        TypeFamily.DateTime => "a date and time",
        TypeFamily.Time => "a time",
        TypeFamily.Uuid => "a GUID",
        TypeFamily.Binary => "hex bytes (0x…)",
        _ => "valid"
    };

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length > 40 ? single[..40] + "…" : single;
    }

    /// <summary>The length limit of a text type such as varchar(50) or nvarchar(100); null for max, text and other types.</summary>
    public static int? MaxLength(string dataType)
    {
        var match = LengthOf().Match(dataType);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) && n > 0 ? n : null;
    }

    [GeneratedRegex(@"^\s*(?:n?varchar|n?char|character varying|character|varchar2|bpchar)\s*\(\s*(\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex LengthOf();

    /// <summary>'.' unless the sample's numbers use a decimal comma: 1,5 or 1.234,56, and none look like 1.5.</summary>
    public static char GuessDecimalSeparator(IEnumerable<string?> values)
    {
        int comma = 0, dot = 0;
        foreach (var v in values)
        {
            if (v is null) continue;
            var t = v.Trim();
            if (CommaDecimal().IsMatch(t) && !GroupedByComma().IsMatch(t)) comma++;
            else if (DotDecimal().IsMatch(t) && !GroupedByDot().IsMatch(t)) dot++;
        }
        return comma > 0 && dot == 0 ? ',' : '.';
    }

    [GeneratedRegex(@"^[+-]?(\d{1,3}(\.\d{3})+|\d+),\d+$")]
    private static partial Regex CommaDecimal();

    [GeneratedRegex(@"^[+-]?(\d{1,3}(,\d{3})+|\d+)\.\d+$")]
    private static partial Regex DotDecimal();

    /// <summary>
    /// The type for a new table's column from its values (empty ones aside): true/false → boolean, whole numbers → int
    /// or bigint, other numbers → decimal sized to fit, dates, dates with times, GUIDs, and otherwise text sized to the
    /// longest value (rounded up, so later rows have room) or unlimited text past 4,000 characters.
    /// </summary>
    public static ColumnDesign GuessColumn(string name, IEnumerable<string?> values, string providerKey, ImportValueOptions options)
    {
        bool allBool = true, allInt = true, allDecimal = true, allDate = true, allDateTime = true, allGuid = true;
        var any = false;
        long min = 0, max = 0;
        int intDigits = 1, scale = 0, maxLength = 0;
        foreach (var raw in values)
        {
            var parsedNull = Parse(raw, TypeFamily.Integer, options) is { IsValid: true, Value: null };
            if (raw is null || parsedNull) continue;
            any = true;
            var text = raw.Trim();
            maxLength = Math.Max(maxLength, raw.Length);
            if (allBool) allBool = text.ToLowerInvariant() is "true" or "false" or "yes" or "no" or "t" or "f" or "y" or "n";
            if (allInt)
            {
                if (Parse(text, TypeFamily.Integer, options).Value is long l && !LeadingZero(text))
                {
                    min = Math.Min(min, l);
                    max = Math.Max(max, l);
                }
                else allInt = false;
            }
            if (allDecimal)
            {
                if (Parse(text, TypeFamily.Decimal, options).Value is decimal d && !LeadingZero(text) && !text.Contains('e', StringComparison.OrdinalIgnoreCase))
                {
                    var abs = Math.Abs(d);
                    var digits = abs >= 1 ? decimal.Truncate(abs).ToString(Inv).Length : 1;
                    intDigits = Math.Max(intDigits, digits);
                    scale = Math.Max(scale, d.Scale);
                }
                else allDecimal = false;
            }
            if (allDate || allDateTime)
            {
                var dt = Parse(text, TypeFamily.DateTime, options);
                if (!dt.IsValid || !LooksLikeDate(text)) allDate = allDateTime = false;
                else if (dt.Value is DateTime t && t.TimeOfDay != TimeSpan.Zero || dt.Value is DateTimeOffset) allDate = false;
            }
            if (allGuid) allGuid = Guid.TryParse(text, out _);
        }

        TypeFamily family;
        string? size = null;
        if (!any) family = TypeFamily.Text;
        else if (allBool) family = TypeFamily.Boolean;
        else if (allInt)
        {
            family = TypeFamily.Integer;
            if (min < int.MinValue || max > int.MaxValue) return new ColumnDesign { Name = name, Type = "bigint" };
        }
        else if (allDecimal)
        {
            family = TypeFamily.Decimal;
            var s = Math.Min(scale, 10);
            size = $"{Math.Min(38, Math.Max(intDigits + s + 2, 18))},{s}";
        }
        else if (allDate) family = TypeFamily.Date;
        else if (allDateTime) family = TypeFamily.DateTime;
        else if (allGuid) family = TypeFamily.Uuid;
        else family = maxLength > 4000 ? TypeFamily.LargeText : TypeFamily.Text;

        var (type, preferredSize) = ColumnTypes.Preferred(providerKey, family);
        if (family == TypeFamily.Text) size = RoundLength(maxLength).ToString(Inv);
        return new ColumnDesign { Name = name, Type = type, Size = size ?? preferredSize };
    }

    /// <summary>Codes like 007 or 0123 lose their zeros as numbers, so they stay text.</summary>
    private static bool LeadingZero(string text)
    {
        var t = text.TrimStart('+', '-');
        return t.Length > 1 && t[0] == '0' && char.IsDigit(t[1]);
    }

    /// <summary>A date has separators: 20250304 is a number, not a date.</summary>
    private static bool LooksLikeDate(string text) => text.Any(c => c is '-' or '/' or '.') && text.Any(char.IsDigit) && text.Length >= 6;

    /// <summary>A text length with room to spare: 50, 100, 255, 500, 1000, 2000, 4000.</summary>
    public static int RoundLength(int length) =>
        new[] { 50, 100, 255, 500, 1000, 2000, 4000 }.FirstOrDefault(n => n >= length * 1.2, 4000);

    /// <summary>A file's column titles as table column names: trimmed, Column1… for empty ones, numbered when repeated.</summary>
    public static IReadOnlyList<string> ColumnNames(IReadOnlyList<string> headers, int count)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = i < headers.Count ? headers[i].Trim().ReplaceLineEndings(" ") : "";
            if (name.Length == 0) name = $"Column{i + 1}";
            if (name.Length > 63) name = name[..63].TrimEnd();
            var unique = name;
            for (var n = 2; !seen.Add(unique); n++) unique = $"{name}_{n}";
            names.Add(unique);
        }
        return names;
    }

    /// <summary>A name compared loosely: case, spaces, underscores and dashes ignored ("First name" matches first_name).</summary>
    public static string LooseName(string name) =>
        new(name.Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '-' or '.')).Select(char.ToLowerInvariant).ToArray());
}
