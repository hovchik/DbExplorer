namespace DbExplorer.Application.Query;

/// <summary>
/// What a screen reader says when the current cell of a result grid changes: where it is, the column and the value
/// ("Row 3 of 120, city: Oslo"). The grid control itself exposes no cells to automation, so the result view announces
/// this through a polite live region.
/// </summary>
public static class CellSpeech
{
    public const int MaxValueLength = 200;

    public static string Describe(int row, int rowCount, string column, object? value, bool edited = false)
    {
        var text = ResultViewQuery.DisplayText(value) switch
        {
            null => "NULL",
            "" => "empty text",
            { } s => Shorten(s)
        };
        return $"Row {row:N0} of {rowCount:N0}, {column}: {text}{(edited ? ", edited" : "")}";
    }

    private static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (line.Length == 0) return "blank";
        return line.Length > MaxValueLength ? line[..MaxValueLength] + ", cut" : line;
    }
}
