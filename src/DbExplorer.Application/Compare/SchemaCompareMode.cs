namespace DbExplorer.Application.Compare;

/// <summary>How the Schema tab of the Comparer aligns and diffs the two definitions.</summary>
public enum SchemaCompareMode
{
    /// <summary>Aligns whole lines; sensitive to line breaks (e.g. reformatted/reordered lines show as changes).</summary>
    LineByLine,

    /// <summary>Aligns individual tokens (words/symbols) regardless of whitespace or line breaks, so only
    /// actual content changes are highlighted.</summary>
    Content
}
