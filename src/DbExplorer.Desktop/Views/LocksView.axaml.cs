using Avalonia.Controls;

namespace DbExplorer.Desktop.Views;

public partial class LocksView : UserControl
{
    public LocksView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.LocksViewModel vm) vm.CopyRequested += CopyAsync;
        };
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
