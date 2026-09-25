using System.Globalization;

namespace DbExplorer.Core.Search;

/// <summary>
/// A data-search term. Text columns are always searched with <see cref="Text"/>;
/// numeric and uniqueidentifier/uuid columns only when the term parses as such.
/// </summary>
public sealed record SearchTerm(string Text, SearchMatchMode Mode, decimal? Number, Guid? Uuid)
{
    public bool HasText => Text.Length > 0;

    public static SearchTerm Create(string text, SearchMatchMode mode, bool includeNumeric, bool includeGuid)
    {
        var trimmed = text.Trim();

        decimal? number = includeNumeric
            && decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;

        Guid? uuid = includeGuid && Guid.TryParse(trimmed, out var g) ? g : null;

        return new SearchTerm(text, mode, number, uuid);
    }
}
