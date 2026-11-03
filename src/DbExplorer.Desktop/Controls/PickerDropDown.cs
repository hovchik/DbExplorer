using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DbExplorer.Desktop.Controls;

/// <summary>
/// Opens an <see cref="AutoCompleteBox"/>'s list when the box gets focus, so its choices can be browsed with a click
/// and no typing. Use this instead of setting <c>IsDropDownOpen = true</c> in a <c>GotFocus</c> handler.
/// <para>
/// Picking an item closes the list, and closing it hands focus back to the box. A plain handler then reopened the list
/// while Avalonia was still taking the old one out of the window, and Avalonia 11.3 threw "Index was out of range"
/// from <c>Visual.OnDetachedFromVisualTreeCore</c>. Here the focus that comes back from the closing list is ignored, and
/// the list opens after the focus change has finished instead of in the middle of it.
/// </para>
/// </summary>
public static class PickerDropDown
{
    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<AutoCompleteBox, bool>("Hooked", typeof(PickerDropDown));

    private static readonly AttachedProperty<bool> ClosingProperty =
        AvaloniaProperty.RegisterAttached<AutoCompleteBox, bool>("Closing", typeof(PickerDropDown));

    /// <summary>The <c>GotFocus</c> handler: opens the list of <paramref name="sender"/> when it is an <see cref="AutoCompleteBox"/>.</summary>
    public static void OpenOnFocus(object? sender)
    {
        if (sender is not AutoCompleteBox box) return;
        Hook(box);
        if (box.IsDropDownOpen || box.GetValue(ClosingProperty)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (box.IsDropDownOpen || box.GetValue(ClosingProperty) || !box.IsKeyboardFocusWithin || !box.IsEffectivelyEnabled) return;
            box.IsDropDownOpen = true;
        }, DispatcherPriority.Input);
    }

    private static void Hook(AutoCompleteBox box)
    {
        if (box.GetValue(HookedProperty)) return;
        box.SetValue(HookedProperty, true);
        box.DropDownClosing += (_, e) =>
        {
            if (e.Cancel) return;
            box.SetValue(ClosingProperty, true);
            // Cleared after the close and the focus it hands back (and an open posted by that focus) have run.
            Dispatcher.UIThread.Post(() => box.SetValue(ClosingProperty, false), DispatcherPriority.Background);
        };
    }
}
