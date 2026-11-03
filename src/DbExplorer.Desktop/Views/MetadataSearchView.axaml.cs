using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Desktop.Editor;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class MetadataSearchView : UserControl
{
    private readonly EditorDecorations _decorations = new();
    private MetadataSearchViewModel? _vm;

    public MetadataSearchView()
    {
        InitializeComponent();
        PreviewEditor.TextArea.TextView.BackgroundRenderers.Add(_decorations);
        // Tunnel so Enter, Esc and ↓ are seen before the TextBox handles them.
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        ResultsList.AddHandler(KeyDownEvent, OnResultsKeyDown, RoutingStrategies.Tunnel);
        ResultsList.AddHandler(DoubleTappedEvent, OnResultDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        ResultsList.SelectionChanged += (_, _) =>
        {
            if (ResultsList.SelectedItem is { } item) ResultsList.ScrollIntoView(item);
        };

        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
        DataContextChanged += (_, _) => BindViewModel();
        AttachedToVisualTree += (_, _) =>
        {
            Keymap.Changed -= UpdateShortcutHint;
            Keymap.Changed += UpdateShortcutHint;
            UpdateShortcutHint();
        };
        DetachedFromVisualTree += (_, _) => Keymap.Changed -= UpdateShortcutHint;
        ApplyHighlighting();
        UpdateShortcutHint();
    }

    private void UpdateShortcutHint() =>
        ShortcutHint.Text = Keymap.Current.Primary(ShortcutCommands.SearchNames) is { } key
            ? $"{key} brings you here from anywhere in the app."
            : "";

    private void ApplyHighlighting() =>
        PreviewEditor.SyntaxHighlighting = SqlHighlighting.Get(dark: AppThemes.IsDark(ActualThemeVariant));

    private void BindViewModel()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.PreviewLineRequested -= OnPreviewLineRequested;
            _vm.FocusRequested -= FocusSearchBox;
        }
        _vm = DataContext as MetadataSearchViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.PreviewLineRequested += OnPreviewLineRequested;
        _vm.FocusRequested += FocusSearchBox;
        ShowPreviewText();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MetadataSearchViewModel.PreviewText) or nameof(MetadataSearchViewModel.PreviewMatches))
            ShowPreviewText();
    }

    // The editor's text is not bindable, so it follows the view model by hand.
    private void ShowPreviewText()
    {
        if (_vm is null) return;
        if (PreviewEditor.Text != _vm.PreviewText) PreviewEditor.Text = _vm.PreviewText;
        _decorations.Occurrences = _vm.PreviewMatches;
        MarkLine();
    }

    private void OnPreviewLineRequested()
    {
        MarkLine();
        var line = _vm?.PreviewLine ?? 0;
        if (line <= 0 || line > PreviewEditor.Document.LineCount) return;
        // After layout, so a freshly loaded text has its lines measured.
        Dispatcher.UIThread.Post(() =>
        {
            if (line > PreviewEditor.Document.LineCount) return;
            PreviewEditor.TextArea.Caret.Line = line;
            PreviewEditor.TextArea.Caret.Column = 1;
            PreviewEditor.ScrollTo(line, 1);
            CenterLine(line);
            // A hit far right on the line: scroll sideways so it shows.
            var docLine = PreviewEditor.Document.GetLineByNumber(line);
            if (_vm?.PreviewMatches.FirstOrDefault(m => m.Start >= docLine.Offset && m.Start < docLine.EndOffset) is { Length: > 0 } hit)
            {
                var column = hit.Start - docLine.Offset + 1;
                PreviewEditor.ScrollTo(line, column + hit.Length);
                PreviewEditor.TextArea.Caret.Column = column;
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Puts the line a third of the way down the preview so there is context above it.</summary>
    private void CenterLine(int line)
    {
        var textView = PreviewEditor.TextArea.TextView;
        var lineHeight = textView.DefaultLineHeight;
        if (lineHeight <= 0 || textView.Bounds.Height <= 0) return;
        var top = Math.Max(0, (line - 1) * lineHeight - textView.Bounds.Height / 3);
        PreviewEditor.ScrollToVerticalOffset(top);
    }

    private void MarkLine()
    {
        var line = _vm?.PreviewLine ?? 0;
        var document = PreviewEditor.Document;
        _decorations.Executed = line > 0 && line <= document.LineCount
            ? (document.GetLineByNumber(line).Offset, Math.Max(1, document.GetLineByNumber(line).Length))
            : null;
        PreviewEditor.TextArea.TextView.InvalidateLayer(_decorations.Layer);
    }

    private void FocusSearchBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (_vm.SearchCommand.CanExecute(null)) _vm.SearchCommand.Execute(null);
                break;
            case Key.Escape when _vm.Query.Length > 0:
                e.Handled = true;
                _vm.ClearCommand.Execute(null);
                break;
            case Key.Down when _vm.Rows.Count > 0:
                e.Handled = true;
                _vm.SelectedRow ??= _vm.Rows[0];
                FocusSelectedResult();
                break;
        }
    }

    private void FocusSelectedResult()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ResultsList.SelectedItem is { } item && ResultsList.ContainerFromItem(item) is { } container) container.Focus();
            else ResultsList.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm?.SelectedRow is not { } row) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _vm.Activate(row);
                break;
            case Key.Left when row.IsObject && row.IsExpanded && row.HasChildren:
                e.Handled = true;
                _vm.SetExpanded(row, false);
                FocusSelectedResult();
                break;
            case Key.Left when row.IsHit:
                // To the object the hit belongs to.
                e.Handled = true;
                _vm.SelectedRow = _vm.Rows.FirstOrDefault(r => r.IsObject && ReferenceEquals(r.Group, row.Group));
                FocusSelectedResult();
                break;
            case Key.Right when row.IsObject && !row.IsExpanded:
                e.Handled = true;
                _vm.SetExpanded(row, true);
                FocusSelectedResult();
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                e.Handled = true;
                if (_vm.CopyNameCommand.CanExecute(null)) _vm.CopyNameCommand.Execute(null);
                break;
            case Key.Up when ResultsList.SelectedIndex == 0:
                e.Handled = true;
                QueryBox.Focus();
                break;
        }
    }

    private void OnChevronClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MetadataSearchRow row } && _vm is not null) _vm.SetExpanded(row, !row.IsExpanded);
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The chevron button expands and collapses; double-clicking it is not "open".
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (_vm is null) return;
        var row = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as MetadataSearchRow
                  ?? _vm.SelectedRow;
        _vm.Activate(row);
    }
}
