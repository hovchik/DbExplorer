using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class DataSearchView : UserControl
{
    public DataSearchView()
    {
        InitializeComponent();
        TermBox.AddHandler(KeyDownEvent, OnTermKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Bubble);
        ResultsGrid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        ResultsGrid.SelectionChanged += (_, _) =>
            (DataContext as DataSearchViewModel)?.SetSelectedMatches(ResultsGrid.SelectedItems.OfType<DataMatchRow>().ToList());
        ResultsGrid.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is null) return;
            OpenRecord();
        };
        // Picking a recent term runs it; close the list so the results are in view.
        RecentList.SelectionChanged += (_, _) =>
        {
            if (RecentList.SelectedItem is not null) RecentButton.Flyout?.Hide();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is DataSearchViewModel vm)
            vm.SetClipboardText = async text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            };
    }

    private void OnTermKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not DataSearchViewModel vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (vm.StartCommand.CanExecute(null)) vm.StartCommand.Execute(null);
                break;
            case Key.Down when vm.Matches.Count > 0:
                // Down from the search box goes to the results, so the keyboard can go type → pick → Enter.
                e.Handled = true;
                vm.SelectedMatch ??= vm.Matches[0];
                ResultsGrid.Focus();
                break;
        }
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        OpenRecord();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not DataSearchViewModel vm || !vm.CancelCommand.CanExecute(null)) return;
        e.Handled = true;
        vm.CancelCommand.Execute(null);
    }

    private void OpenRecord()
    {
        if (DataContext is DataSearchViewModel vm && vm.OpenRecordCommand.CanExecute(null)) vm.OpenRecordCommand.Execute(null);
    }
}
