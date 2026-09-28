using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.Editor;

/// <summary>One entry of the editor's completion list: a colored kind badge, the name and its detail.</summary>
public sealed class SqlCompletionData(CompletionItem item) : ICompletionData
{
    private static readonly IReadOnlyDictionary<CompletionKind, IBrush> KindBrushes = new Dictionary<CompletionKind, IBrush>
    {
        [CompletionKind.Column] = new SolidColorBrush(Color.Parse("#2E9D5B")),
        [CompletionKind.Table] = new SolidColorBrush(Color.Parse("#2F80ED")),
        [CompletionKind.View] = new SolidColorBrush(Color.Parse("#8E5BD9")),
        [CompletionKind.Routine] = new SolidColorBrush(Color.Parse("#C77C1F")),
        [CompletionKind.Function] = new SolidColorBrush(Color.Parse("#B8860B")),
        [CompletionKind.Keyword] = new SolidColorBrush(Color.Parse("#4A7FC1")),
        [CompletionKind.Join] = new SolidColorBrush(Color.Parse("#D0457B")),
        [CompletionKind.Snippet] = new SolidColorBrush(Color.Parse("#16A3A3")),
        [CompletionKind.Alias] = new SolidColorBrush(Color.Parse("#7A8B99")),
        [CompletionKind.Variable] = new SolidColorBrush(Color.Parse("#7A5BC4")),
        [CompletionKind.Schema] = new SolidColorBrush(Color.Parse("#7A8B99"))
    };

    public CompletionItem Item { get; } = item;

    public IImage? Image => null;

    /// <summary>What the list filters on as the user keeps typing.</summary>
    public string Text => Item.Label;

    public object Content => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("62,Auto,*"),
        Children =
        {
            new TextBlock
            {
                Text = Item.KindLabel, FontSize = 10, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = KindBrushes.GetValueOrDefault(Item.Kind, Brushes.Gray)
            },
            Place(new TextBlock { Text = Item.Label, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 1),
            Place(new TextBlock
            {
                Text = Item.Detail ?? "", Opacity = 0.6, FontSize = 11, Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380
            }, 2)
        }
    };

    public object? Description => Item.Detail is null && Item.InsertText == Item.Label ? null : $"{Item.InsertText}\n{Item.Detail}".Trim();

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        // Read the offset first: the segment is anchored and moves when text is inserted at an empty range.
        var start = completionSegment.Offset;
        textArea.Document.Replace(start, completionSegment.Length, Item.InsertText);
        textArea.Caret.Offset = Math.Min(start + (Item.CaretOffset ?? Item.InsertText.Length), textArea.Document.TextLength);
    }

    private static Control Place(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
