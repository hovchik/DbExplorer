using Avalonia.Controls;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ObjectsView : UserControl
{
    public ObjectsView()
    {
        InitializeComponent();
        ObjectsGrid.SelectionChanged += (_, _) =>
        {
            if (ObjectsGrid.SelectedItem is { } item) ObjectsGrid.ScrollIntoView(item, null);
        };
    }

    private void OnObjectsDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not ObjectsViewModel vm) return;

        if (vm.CanExecuteSelected && vm.ExecuteSelectedCommand.CanExecute(null))
            vm.ExecuteSelectedCommand.Execute(null);
        else if (vm.CanGetData && vm.GetDataCommand.CanExecute(null))
            vm.GetDataCommand.Execute(null);
    }
}
