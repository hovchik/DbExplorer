using Avalonia.Controls;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ConnectionDialog : Window
{
    private ConnectionDialogViewModel? _vm;

    public ConnectionDialog()
    {
        InitializeComponent();
        // Typing in the list filters it; picking a database fills the box and closes the list.
        DatabaseList.Picked += name =>
        {
            if (_vm is not null) _vm.Database = name;
            BrowseDatabases.Flyout?.Hide();
            DatabaseBox.Focus();
        };
        DatabaseList.Dismissed += () => BrowseDatabases.Flyout?.Hide();
        if (BrowseDatabases.Flyout is { } flyout) flyout.Opened += (_, _) => DatabaseList.Reset(_vm?.Database);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.CloseRequested -= OnCloseRequested;
        _vm = DataContext as ConnectionDialogViewModel;
        if (_vm is not null) _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(bool ok) => Close(ok);
}
