using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class NavigatorView : UserControl
{
    private NavigatorViewModel? _vm;

    public NavigatorView()
    {
        InitializeComponent();
        // DoubleTapped only bubbles, and the row's own handler (which opens or closes it) runs first and marks it
        // handled. So the row and its open state are noted on the second press (pointer events tunnel), and the
        // double-tap handler sees handled events too, puts the open state back and shows the object instead.
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(DoubleTappedEvent, OnTreeDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.CopyRequested -= OnCopyRequested;
            _vm = DataContext as NavigatorViewModel;
            if (_vm is not null) _vm.CopyRequested += OnCopyRequested;
        };
    }

    /// <summary>Puts the caret in the filter box (Ctrl+Shift+N style "find in tree").</summary>
    public void FocusFilter() => FilterBox.Focus();

    private async void OnCopyRequested(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    private (NavigatorNode Node, bool WasExpanded)? _doubleClicked;

    private static NavigatorNode? NodeAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as NavigatorNode;

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _doubleClicked = e.ClickCount == 2 && NodeAt(e.Source) is { } node ? (node, node.IsExpanded) : null;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || NodeAt(e.Source) is not { } node) return;
        if (!NavigatorViewModel.OpensOnDoubleClick(node)) return; // databases, schemas, folders: just open or close
        if (_doubleClicked is { } pressed && ReferenceEquals(pressed.Node, node)) node.IsExpanded = pressed.WasExpanded;
        _doubleClicked = null;
        _vm.Activate(node);
        e.Handled = true;
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter && _vm.Activate(_vm.SelectedNode))
        {
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _vm.RefreshCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            _vm.CopyNameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Escape && _vm.FilterText.Length > 0)
        {
            _vm.FilterText = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            Tree.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Right-click selects the row under the pointer first, so the menu is about that row.</summary>
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null) return;
        if ((e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is NavigatorNode node)
            _vm.SelectedNode = node;
        if (_vm.SelectedNode is null or { Kind: NavigatorNodeKind.Message or NavigatorNodeKind.ConnectionFolder })
            e.Handled = true; // nothing to offer
    }
}
