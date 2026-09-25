using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class DataSearchView : UserControl
{
    public DataSearchView()
    {
        InitializeComponent();
        TermBox.AddHandler(KeyDownEvent, OnTermKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnTermKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not DataSearchViewModel vm) return;
        e.Handled = true;
        if (vm.StartCommand.CanExecute(null)) vm.StartCommand.Execute(null);
    }
}
