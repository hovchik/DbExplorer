using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class MetadataSearchView : UserControl
{
    public MetadataSearchView()
    {
        InitializeComponent();
        // Tunnel so Enter is seen before the TextBox handles it.
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MetadataSearchViewModel vm) return;
        e.Handled = true;
        if (vm.SearchCommand.CanExecute(null)) vm.SearchCommand.Execute(null);
    }
}
