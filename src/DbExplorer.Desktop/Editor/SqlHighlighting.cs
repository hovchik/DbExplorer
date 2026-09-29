using System.Security;
using System.Text;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.Editor;

/// <summary>SQL syntax highlighting for the query editor, with a palette for light and one for dark themes.</summary>
public static class SqlHighlighting
{
    private static IHighlightingDefinition? _light;
    private static IHighlightingDefinition? _dark;

    public static IHighlightingDefinition Get(bool dark) =>
        dark ? _dark ??= Build(dark: true) : _light ??= Build(dark: false);

    private static IHighlightingDefinition Build(bool dark)
    {
        var (keyword, function, text, comment, number, variable, identifier, op) = dark
            ? ("#569CD6", "#DCDCAA", "#CE9178", "#6A9955", "#B5CEA8", "#9CDCFE", "#4EC9B0", "#D4D4D4")
            : ("#0000FF", "#795E26", "#A31515", "#008000", "#098658", "#001080", "#267F99", "#505050");

        var functions = SqlCompletionEngine.KnownFunctionNames;
        var keywords = SqlCompletionEngine.KeywordSet.Where(k => !functions.Contains(k)).Order();

        static string Words(IEnumerable<string> words) =>
            string.Concat(words.Select(w => $"<Word>{SecurityElement.Escape(w)}</Word>"));

        var xml = $"""
            <SyntaxDefinition name="SQL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{comment}" fontStyle="italic" />
              <Color name="String" foreground="{text}" />
              <Color name="Keyword" foreground="{keyword}" fontWeight="bold" />
              <Color name="Function" foreground="{function}" />
              <Color name="Number" foreground="{number}" />
              <Color name="Variable" foreground="{variable}" />
              <Color name="Identifier" foreground="{identifier}" />
              <Color name="Operator" foreground="{op}" />
              <RuleSet ignoreCase="true">
                <Span color="Comment" begin="--" />
                <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                <Span color="String" multiline="true" begin="[NnEe]?'" end="'" />
                <Span color="String" multiline="true" begin="\$(?&lt;tag&gt;[A-Za-z_]*)\$" end="\$[A-Za-z_]*\$" />
                <Span color="Identifier" begin="\[" end="\]" />
                <Span color="Identifier" begin="&quot;" end="&quot;" />
                <Keywords color="Keyword">{Words(keywords)}</Keywords>
                <Keywords color="Function">{Words(functions)}</Keywords>
                <Rule color="Variable">@@?[A-Za-z_][\w@$#]*</Rule>
                <Rule color="Number">\b0[xX][0-9a-fA-F]+|\b\d+(\.\d+)?([eE][+-]?\d+)?</Rule>
                <Rule color="Operator">[-+*/%=&lt;&gt;!|&amp;^~:]+</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;

        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), HighlightingManager.Instance);
    }
}
