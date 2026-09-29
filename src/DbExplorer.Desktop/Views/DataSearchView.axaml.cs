using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class DataSearchView : UserControl
{
    public DataSearchView()
    {
        InitializeComponent();
        TermBox.AddHandler(KeyDownEvent, OnTermKeyDown, RoutingStrategies.Tunnel);
        ResultsGrid.SelectionChanged += (_, _) =>
            (DataContext as DataSearchViewModel)?.SetSelectedMatches(ResultsGrid.SelectedItems.OfType<DataMatch>().ToList());
        ResultsGrid.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is null) return;
            if (DataContext is DataSearchViewModel vm && vm.OpenRecordCommand.CanExecute(null)) vm.OpenRecordCommand.Execute(null);
        };
    }

    private void OnTermKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not DataSearchViewModel vm) return;
        e.Handled = true;
        if (vm.StartCommand.CanExecute(null)) vm.StartCommand.Execute(null);
    }
}
