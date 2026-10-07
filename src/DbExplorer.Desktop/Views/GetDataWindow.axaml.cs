using Avalonia.Controls;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class GetDataWindow : Window
{
    private bool _discardConfirmed;

    public GetDataWindow()
    {
        InitializeComponent();
    }

    /// <summary>Closing by the user with uncommitted edits asks first; closing with the application does not.</summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _discardConfirmed || e.CloseReason != WindowCloseReason.WindowClosing ||
            DataContext is not GetDataViewModel { HasUncommittedEdits: true }) return;

        e.Cancel = true;
        var confirm = new ConfirmWindow(
            "The rows have changes that are not committed yet (edited, new or deleted rows). Close and discard them?", "Discard and close");
        if (!await confirm.ShowDialog<bool>(this)) return;
        _discardConfirmed = true;
        Close();
    }
}
