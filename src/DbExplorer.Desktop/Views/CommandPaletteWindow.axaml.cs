using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class CommandPaletteWindow : Window
{
    private bool _closing;

    public CommandPaletteWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => QueryBox.Focus();
        Deactivated += (_, _) => SafeClose();
        ResultsList.DoubleTapped += (_, _) => (DataContext as CommandPaletteViewModel)?.Accept(PaletteModifier.None);
        ResultsList.SelectionChanged += (_, _) =>
        {
            if (ResultsList.SelectedItem is { } item) ResultsList.ScrollIntoView(item);
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CommandPaletteViewModel vm) vm.CloseRequested += SafeClose;
        };
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel vm) return;
        switch (e.Key)
        {
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                vm.Move(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp:
                vm.Move(e.Key == Key.PageDown ? 10 : -10);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.Accept(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? PaletteModifier.Shift
                    : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? PaletteModifier.Control
                    : PaletteModifier.None);
                e.Handled = true;
                break;
        }
    }
}
