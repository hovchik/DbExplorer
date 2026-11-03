using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace DbExplorer.Desktop.Services;

/// <summary>
/// Gives every button and input a name that screen readers (Narrator, NVDA, VoiceOver) can read, without a label
/// having to be written on each of the app's ~300 controls by hand.
/// <para>
/// Avalonia names a button after its content and nothing else, so an icon button reads as "plus sign",
/// "multiplication x" or nothing at all, and a text box with no label reads as "edit". When a control has no
/// <see cref="AutomationProperties.NameProperty"/> of its own, this fills one in, in this order:
/// </para>
/// <list type="number">
/// <item>Buttons: the words shown on the button (when it is an icon plus a caption), else the first phrase of its
/// tooltip ("Close (Ctrl+W)" → "Close", with Ctrl+W as the accelerator key).</item>
/// <item>Inputs (text boxes, lists, drop-downs, number boxes): the caption next to it (a text block to its left in the
/// same grid row, above it in the same column, or just before it in a panel), else the tooltip, else the watermark.</item>
/// </list>
/// The full tooltip stays the help text, which screen readers read after the name. A name set in XAML always wins,
/// and names follow the tooltip and caption when bindings change them.
/// </summary>
public static partial class AccessibleNames
{
    /// <summary>Set on controls whose name or label was filled in here, so a later update may replace it.</summary>
    private static readonly AttachedProperty<bool> AutoNamedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("AutoNamed", typeof(AccessibleNames));

    private static bool _installed;

    /// <summary>Hooks every control the app creates. Called once from <see cref="App.Initialize"/>.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Control.LoadedEvent.AddClassHandler<Control>((c, _) => Apply(c), RoutingStrategies.Direct, handledEventsToo: true);
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((c, _) => { if (c.IsLoaded) Apply(c); });
        ContentControl.ContentProperty.Changed.AddClassHandler<Button>((c, _) => { if (c.IsLoaded) Apply(c); });
        // A caption inside a button ("Connect" → "Connecting…") or next to an input can change after load.
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((t, _) =>
        {
            if (!t.IsLoaded) return;
            ILogical? node = t;
            for (var i = 0; i < 4 && node?.LogicalParent is { } parent; i++, node = parent)
                if (parent is Button b && b.GetValue(AutoNamedProperty)) { Apply(b); return; }
        });
    }

    /// <summary>The name a screen reader should use for <paramref name="control"/>, worked out as described on the class.</summary>
    public static void Apply(Control control)
    {
        if (!IsLabelled(control)) return;
        var explicitName = control.GetValue(AutomationProperties.NameProperty);
        var auto = control.GetValue(AutoNamedProperty);
        if (!auto && (!string.IsNullOrWhiteSpace(explicitName) || control.GetValue(AutomationProperties.LabeledByProperty) is not null))
            return;

        var tip = TipText(ToolTip.GetTip(control));
        string? name = null;
        Control? label = null;

        if (control is CheckBox or RadioButton) // before Button: both derive from it
        {
            var caption = ContentText(((ContentControl)control).Content);
            if (HasWords(caption) && ((ContentControl)control).Content is string) { Clear(control, auto); return; }
            name = HasWords(caption) ? caption : ShortTip(tip) ?? ColumnHeader(control) ?? NearbyCaption(control)?.Text;
        }
        else if (control is Button or SplitButton)
        {
            var content = ((ContentControl)control).Content;
            var caption = ContentText(content);
            if (content is string && HasWords(caption) && control is Button)
            {
                // Avalonia already reads plain text content; nothing to add.
                Clear(control, auto);
                SetAccelerator(control, tip);
                return;
            }
            name = NameFor(caption, tip);
        }
        else
        {
            label = NearbyCaption(control)?.Control;
            if (label is null)
                name = ColumnHeader(control) ?? ShortTip(tip) ?? Watermark(control);
        }

        if (label is not null)
        {
            control.SetValue(AutomationProperties.NameProperty, null, BindingPriority.Style);
            control.SetValue(AutomationProperties.LabeledByProperty, label, BindingPriority.Style);
            control.SetValue(AutoNamedProperty, true);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            control.SetValue(AutomationProperties.LabeledByProperty, null!, BindingPriority.Style);
            control.SetValue(AutomationProperties.NameProperty, name, BindingPriority.Style);
            control.SetValue(AutoNamedProperty, true);
        }
        else
        {
            Clear(control, auto);
        }
        SetAccelerator(control, tip);
    }

    /// <summary>The controls a screen reader announces by name when they get focus.</summary>
    public static bool IsLabelled(Control control) => control is Button or SplitButton or CheckBox or RadioButton
        or TextBox or AutoCompleteBox or ComboBox or NumericUpDown or Slider or ListBox or TreeView
        or CalendarDatePicker or DatePicker or TimePicker;

    private static void Clear(Control control, bool auto)
    {
        if (!auto) return;
        // Only the values set here (style priority): a name bound in XAML stays.
        control.SetValue(AutomationProperties.NameProperty, null, BindingPriority.Style);
        control.SetValue(AutomationProperties.LabeledByProperty, null!, BindingPriority.Style);
        control.SetValue(AutoNamedProperty, false);
    }

    private static void SetAccelerator(Control control, string? tip)
    {
        if (Shortcut(tip) is { } keys)
            control.SetValue(AutomationProperties.AcceleratorKeyProperty, keys, BindingPriority.Style);
    }

    /// <summary>
    /// A button's name from the words on it and its tooltip: the words when there are any ("Connect"), the tooltip's
    /// first phrase otherwise ("Zoom in"), and both when the button shows only a number ("Reset zoom, 75%").
    /// </summary>
    public static string? NameFor(string? caption, string? tip)
    {
        caption = Collapse(caption);
        if (HasWords(caption)) return caption;
        var shortTip = ShortTip(tip);
        if (shortTip is null) return null;
        return caption is not null && caption.Any(char.IsDigit) ? $"{shortTip}, {caption}" : shortTip;
    }

    /// <summary>
    /// The first phrase of a tooltip, which is what a screen reader should say first: it stops at a colon, a
    /// bracket, a sentence end, a middle dot or a semicolon. "Close (Ctrl+W)" → "Close",
    /// "Swap sides: the right becomes the baseline" → "Swap sides". The whole tooltip remains the help text.
    /// </summary>
    public static string? ShortTip(string? tip)
    {
        if (string.IsNullOrWhiteSpace(tip)) return null;
        var text = tip.Trim();
        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline > 0) text = text[..newline];
        var cut = PhraseEnd().Match(text);
        if (cut.Success && cut.Index > 0) text = text[..cut.Index];
        text = text.TrimEnd(' ', '.', ',', ';', ':', '-', '–', '—');
        if (text.Length > 80)
        {
            var space = text.LastIndexOf(' ', 80);
            text = (space > 20 ? text[..space] : text[..80]).TrimEnd(',', ';', ' ') + "…";
        }
        return HasWords(text) ? text : null;
    }

    /// <summary>The keyboard shortcut a tooltip mentions, such as "Ctrl+W" in "Close (Ctrl+W)" or "F8".</summary>
    public static string? Shortcut(string? tip)
    {
        if (string.IsNullOrWhiteSpace(tip)) return null;
        var m = ShortcutPattern().Match(tip);
        return m.Success ? m.Groups["keys"].Value : null;
    }

    /// <summary>True when the text has at least two letters, so it is words and not just a symbol, arrow or emoji.</summary>
    public static bool HasWords(string? text) => text is not null && text.Count(char.IsLetter) >= 2;

    private static string? Collapse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : WhiteSpace().Replace(text, " ").Trim();

    /// <summary>The text of a tooltip whether it is a string or a text block.</summary>
    private static string? TipText(object? tip) => tip switch
    {
        string s => s,
        TextBlock t => t.Text,
        ContentControl { Content: string s } => s,
        _ => null
    };

    /// <summary>The words shown inside a button: its text, or the text blocks in a panel next to an icon.</summary>
    private static string? ContentText(object? content) => content switch
    {
        null => null,
        string s => s,
        TextBlock t => t.Text,
        Control c => Collapse(string.Join(" ", c.GetLogicalDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text))),
        _ => null
    };

    /// <summary>For a check box or editor inside a data grid cell: the header of its column ("PK", "Null").</summary>
    private static string? ColumnHeader(Control control)
    {
        var row = control.FindAncestorOfType<DataGridRow>();
        var grid = row?.FindAncestorOfType<DataGrid>();
        if (row is null || grid is null) return null;
        foreach (var column in grid.Columns)
        {
            if (column.GetCellContent(row) is not { } content) continue;
            if (content == control || content.IsVisualAncestorOf(control))
                return column.Header switch
                {
                    string h when HasWords(h) => h,
                    TextBlock { Text: { } t } when HasWords(t) => t,
                    _ => null
                };
        }
        return null;
    }

    private static string? Watermark(Control control) => control switch
    {
        TextBox t => Clean(t.Watermark),
        AutoCompleteBox a => Clean(a.Watermark),
        ComboBox c => Clean(c.PlaceholderText),
        NumericUpDown n => Clean(n.Watermark),
        _ => null
    };

    private static string? Clean(string? text) => HasWords(text) ? Collapse(text)!.TrimEnd('…', '.', ' ') : null;

    /// <summary>
    /// The caption that labels an input: a text block (or label) to its left in the same grid row, above it in the
    /// same grid column, or just before it in a stack, wrap or dock panel.
    /// </summary>
    private static (Control Control, string Text)? NearbyCaption(Control control)
    {
        if (control.Parent is not Panel panel) return null;
        if (panel is Grid)
        {
            int row = Grid.GetRow(control), col = Grid.GetColumn(control);
            var left = panel.Children.Where(c => Grid.GetRow(c) == row && Grid.GetColumn(c) < col)
                .OrderByDescending(Grid.GetColumn).FirstOrDefault();
            if (Caption(left) is { } l) return l;
            var above = panel.Children.Where(c => Grid.GetColumn(c) == col && Grid.GetRow(c) == row - 1).FirstOrDefault();
            return Caption(above);
        }
        var index = panel.Children.IndexOf(control);
        return index > 0 ? Caption(panel.Children[index - 1]) : null;
    }

    private static (Control Control, string Text)? Caption(Control? candidate)
    {
        var text = candidate switch
        {
            TextBlock { IsVisible: true } t => t.Text,
            Label { IsVisible: true, Content: string s } => s,
            _ => null
        };
        return HasWords(text) ? (candidate!, Collapse(text)!.TrimEnd(':', ' ')) : null;
    }

    [GeneratedRegex(@"(:\s|\s\(|\.\s|\s·\s|;\s|\s—\s|\s–\s)")]
    private static partial Regex PhraseEnd();

    [GeneratedRegex(@"(?<keys>\b(?:(?:Ctrl|Alt|Shift|Cmd|Win)\+)+(?:F\d{1,2}|PgUp|PgDn|Enter|Space|Tab|Esc|Delete|Del|Home|End|Up|Down|Left|Right|[A-Za-z0-9]|[=+\-,./↑↓←→])(?![A-Za-z0-9])|\bF\d{1,2}\b)")]
    private static partial Regex ShortcutPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
