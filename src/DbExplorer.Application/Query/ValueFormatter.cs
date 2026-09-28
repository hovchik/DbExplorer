using System.Text.Json;
using System.Xml.Linq;

namespace DbExplorer.Application.Query;

/// <summary>Makes a cell value readable in the value viewer: JSON and XML are indented, everything else is kept.</summary>
public static class ValueFormatter
{
    public static (string Text, string Kind) Pretty(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 1 && (trimmed[0] is '{' or '['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return (JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }), "JSON");
            }
            catch (JsonException) { }
        }
        if (trimmed.Length > 1 && trimmed[0] == '<')
        {
            try
            {
                return (XElement.Parse(trimmed).ToString(), "XML");
            }
            catch (System.Xml.XmlException) { }
        }
        return (value, "Text");
    }
}

/// <summary>The status-bar summary of selected cells: count, and for numbers sum / average / min / max.</summary>
public static class SelectionStatistics
{
    public static string Summarize(IEnumerable<object?> values)
    {
        var list = values.ToList();
        var nonNull = list.Where(v => v is not null and not DBNull).ToList();
        var numbers = nonNull.Select(ToNumber).Where(n => n is not null).Select(n => n!.Value).ToList();
        var parts = new List<string> { $"Count {list.Count:N0}" };
        if (nonNull.Count < list.Count) parts.Add($"NULL {list.Count - nonNull.Count:N0}");

        if (numbers.Count > 0 && numbers.Count == nonNull.Count)
        {
            parts.Add($"Sum {Format(numbers.Sum())}");
            parts.Add($"Avg {Format(numbers.Average())}");
            parts.Add($"Min {Format(numbers.Min())}");
            parts.Add($"Max {Format(numbers.Max())}");
        }
        else
        {
            parts.Add($"Distinct {nonNull.Select(v => v!.ToString()).Distinct().Count():N0}");
        }
        return string.Join(" · ", parts);
    }

    private static decimal? ToNumber(object? value) => value switch
    {
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
        double d when !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) < 7.9e27 => (decimal)d,
        float f when !float.IsNaN(f) && !float.IsInfinity(f) => (decimal)f,
        _ => null
    };

    private static string Format(decimal value) =>
        Math.Round(value, 4).ToString("#,0.####", System.Globalization.CultureInfo.CurrentCulture);
}
