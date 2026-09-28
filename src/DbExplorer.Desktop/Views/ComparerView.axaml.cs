using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbExplorer.Desktop.Views;

public partial class ComparerView : UserControl
{
    public ComparerView()
    {
        InitializeComponent();
    }

    /// <summary>Opens the drop-down as soon as the box is focused, so the full object list can be
    /// browsed with a click alone (no typing required) instead of only filtering as-you-type.</summary>
    private void OnObjectPickerGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = true;
    }
}
