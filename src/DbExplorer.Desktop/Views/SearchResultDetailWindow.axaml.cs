using Avalonia.Controls;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class SearchResultDetailWindow : Window
{
    private SearchResultDetailViewModel? _vm;

    public SearchResultDetailWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.ScrollToLine -= OnScrollToLine;
        _vm = DataContext as SearchResultDetailViewModel;
        if (_vm is not null) _vm.ScrollToLine += OnScrollToLine;
    }

    private void OnScrollToLine(int line)
    {
        Dispatcher_UIThread_Post(() =>
        {
            var container = Linescontrol.ContainerFromIndex(Math.Max(0, line - 1));
            container?.BringIntoView();
        });
    }

    private static void Dispatcher_UIThread_Post(Action action) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
}
