namespace DbExplorer.Application.Query;

/// <summary>
/// Pure logic behind several carets in the query editor: finding the next / every occurrence of a word or selection,
/// and turning an edit at one caret into the same edit at every other caret.
/// </summary>
public static class MultiCaret
{
    /// <summary>The word around <paramref name="offset"/> (letters, digits, _, @, #, $), or null between words.</summary>
    public static (int Start, int Length)? WordAt(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = offset;
        while (end < text.Length && IsWordChar(text[end])) end++;
        return end > start ? (start, end - start) : null;
    }

    /// <summary>
    /// Every place <paramref name="needle"/> occurs, case-insensitively (SQL names are). With <paramref name="wholeWord"/>
    /// a match must not be part of a longer name.
    /// </summary>
    public static IReadOnlyList<int> FindAll(string text, string needle, bool wholeWord)
    {
        var found = new List<int>();
        if (needle.Length == 0) return found;
        for (var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = at + needle.Length <= text.Length ? text.IndexOf(needle, at + needle.Length, StringComparison.OrdinalIgnoreCase) : -1)
        {
            if (wholeWord && ((at > 0 && IsWordChar(text[at - 1])) || (at + needle.Length < text.Length && IsWordChar(text[at + needle.Length]))))
                continue;
            found.Add(at);
        }
        return found;
    }

    /// <summary>
    /// The first occurrence after <paramref name="after"/> that is not already taken, wrapping to the top of the text;
    /// null when every occurrence is taken.
    /// </summary>
    public static int? FindNext(string text, string needle, bool wholeWord, int after, IReadOnlyCollection<int> taken)
    {
        var all = FindAll(text, needle, wholeWord);
        foreach (var at in all) if (at >= after && !taken.Contains(at)) return at;
        foreach (var at in all) if (at < after && !taken.Contains(at)) return at;
        return null;
    }

    /// <summary>
    /// Which caret an edit belongs to: the index of the first range the edit touches (removes from, or inserts in or
    /// right next to) and the edit's offset relative to that range's start. Null when the edit is away from every caret,
    /// which ends multi-caret editing.
    /// </summary>
    public static (int Index, int Relative)? Locate(IReadOnlyList<(int Start, int End)> ranges, int offset, int removalLength)
    {
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            if (offset <= end && offset + removalLength >= start) return (i, offset - start);
        }
        return null;
    }

    /// <summary>
    /// Where the same edit lands at another caret: <paramref name="relative"/> from its start, clamped to the text.
    /// The removal is shortened where it would run past either end of the text (Backspace at the very start does nothing).
    /// </summary>
    public static (int Offset, int RemovalLength) Mirror(int otherStart, int relative, int removalLength, int textLength)
    {
        var offset = otherStart + relative;
        if (offset < 0) { removalLength += offset; offset = 0; }
        offset = Math.Min(offset, textLength);
        return (offset, Math.Clamp(removalLength, 0, textLength - offset));
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';
}
