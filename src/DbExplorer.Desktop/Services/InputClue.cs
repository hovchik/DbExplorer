using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DbExplorer.Desktop.Services;

/// <summary>
/// Turns clicks and shortcuts into short descriptions for <see cref="HangClues"/>, e.g. "click on Button 'Run' in
/// QueryView" or "Ctrl+Enter in QueryView". Watches every window and popup through class handlers. Never records what
/// is typed: a key without Ctrl or Alt is only "typing".
/// </summary>
public static class InputClue
{
    private const int MaxLabel = 40;

    public static void Watch(HangClues clues)
    {
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>(
            (_, e) => Record(clues, () => "click on " + Describe(e.Source) + Where(e.Source)), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>(
            (_, e) => Record(clues, () => Key(e.Key, e.KeyModifiers) + Where(e.Source)), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void Record(HangClues clues, Func<string> describe)
    {
        try { clues.Input(describe()); }
        catch { }
    }

    /// <summary>"Ctrl+Shift+F", "F5", "Enter", or "typing" for a plain or Shift-ed character key.</summary>
    public static string Key(Key key, KeyModifiers modifiers)
    {
        var command = (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        var character = key is >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z or >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9
            or >= Avalonia.Input.Key.NumPad0 and <= Avalonia.Input.Key.NumPad9 or >= Avalonia.Input.Key.Oem1 and <= Avalonia.Input.Key.Oem102
            or Avalonia.Input.Key.Space or Avalonia.Input.Key.Multiply or Avalonia.Input.Key.Add or Avalonia.Input.Key.Subtract
            or Avalonia.Input.Key.Divide or Avalonia.Input.Key.Decimal;
        if (character && !command) return "typing";
        return new KeyGesture(key, modifiers).ToString();
    }

    /// <summary>The control that was clicked: the nearest button, menu item or tab, with its label when it has one.</summary>
    public static string Describe(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            var label = v switch
            {
                MenuItem m => Label(m.Header),
                HeaderedContentControl h => Label(h.Header),
                ContentControl c and (Button or ToggleButton or ListBoxItem or TabItem) => Label(c.Content),
                _ => null
            };
            if (v is Button or MenuItem or TabItem or ToggleButton or ListBoxItem or TreeViewItem or DataGridCell or TextBox
                or ScrollBar or Thumb or GridSplitter)
                return Name(v) + (label is null ? "" : $" '{label}'");
        }
        return source is Visual s ? Name(s) : "window";
    }

    /// <summary>" in QueryView": the nearest view or window around the control.</summary>
    public static string Where(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            var type = v.GetType().Name;
            if (v is UserControl && type.EndsWith("View", StringComparison.Ordinal) || v is Window or PopupRoot)
                return " in " + type;
        }
        return "";
    }

    private static string Name(Visual v) =>
        v is Control { Name: { Length: > 0 } name } ? $"{v.GetType().Name} {name}" : v.GetType().Name;

    private static string? Label(object? content) => content is string { Length: > 0 } s
        ? (s.Length > MaxLabel ? s[..MaxLabel] + "…" : s).ReplaceLineEndings(" ")
        : null;
}
