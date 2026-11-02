using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DbExplorer.Desktop.Views;

public partial class ShortcutSheetWindow : Window
{
    public ShortcutSheetWindow()
    {
        InitializeComponent();
        Opened += (_, _) => FilterBox.Focus();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (!string.IsNullOrEmpty(FilterBox.Text)) FilterBox.Text = "";
            else Close();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
