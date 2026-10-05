using System.Text.Json;

namespace DbExplorer.Application.Api;

/// <summary>Lenient helpers for export files, whose fields are often missing, null or of an unexpected kind.</summary>
internal static class JsonReading
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static JsonDocument? TryParse(string content)
    {
        try
        {
            return JsonDocument.Parse(content, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonDocument Parse(string content, string format)
    {
        try
        {
            return JsonDocument.Parse(content, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The {format} file is not valid JSON: {ex.Message}", ex);
        }
    }

    public static JsonElement? Prop(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    /// <summary>The property as text: strings as they are, numbers and booleans as written, anything else null.</summary>
    public static string? Str(this JsonElement e, string name) => e.Prop(name) is { } v ? AsText(v) : null;

    public static string? AsText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
        JsonValueKind.Object or JsonValueKind.Array => v.GetRawText(),
        _ => null
    };

    public static bool Bool(this JsonElement e, string name)
        => e.Prop(name) is { } v && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    public static IEnumerable<JsonElement> Array(this JsonElement e, string name)
        => e.Prop(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
}
