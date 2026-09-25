using Avalonia.Controls;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ConnectionDialog : Window
{
    private ConnectionDialogViewModel? _vm;

    public ConnectionDialog()
    {
        InitializeComponent();
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
