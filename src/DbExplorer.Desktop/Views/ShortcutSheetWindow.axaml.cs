using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ShortcutSheetWindow : Window
{
    public ShortcutSheetWindow()
    {
        InitializeComponent();
        Opened += (_, _) => FilterBox.Focus();
        Closed += (_, _) => (DataContext as ShortcutSheetViewModel)?.Detach();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnKeyClick(object? sender, RoutedEventArgs e) =>
        (DataContext as ShortcutSheetViewModel)?.StartEditCommand.Execute((sender as Control)?.DataContext as ShortcutInfo);

    private void OnResetClick(object? sender, RoutedEventArgs e) =>
        (DataContext as ShortcutSheetViewModel)?.ResetCommand.Execute((sender as Control)?.DataContext as ShortcutInfo);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = DataContext as ShortcutSheetViewModel;
        if (vm is { IsEditing: true })
        {
            // While a row is being changed every key press is the new shortcut, except Esc on its own.
            e.Handled = true;
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { vm.CancelEditCommand.Execute(null); return; }
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && vm.CanAssign) { vm.AssignCommand.Execute(null); return; }
            if (Keymap.Chord(e) is { } chord) vm.Record(chord);
            return;
        }
        if (e.Key != Key.Escape) return;
        if (!string.IsNullOrEmpty(FilterBox.Text)) FilterBox.Text = "";
        else Close();
        e.Handled = true;
    }
}
