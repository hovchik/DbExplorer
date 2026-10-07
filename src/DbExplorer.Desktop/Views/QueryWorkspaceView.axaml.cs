using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class QueryWorkspaceView : UserControl
{
    private static readonly FilePickerFileType SqlFiles = new("SQL script") { Patterns = ["*.sql"] };
    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = ["*"] };

    public QueryWorkspaceView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DocTabs.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        DocTabs.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        DocTabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        DocTabs.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Direct);
    }

    private QueryWorkspaceViewModel? Vm => DataContext as QueryWorkspaceViewModel;

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.PageUp when shift: e.Handled = true; await vm.MoveTabLeftCommand.ExecuteAsync(null); break;
            case Key.PageDown when shift: e.Handled = true; await vm.MoveTabRightCommand.ExecuteAsync(null); break;
            case Key.PageUp: e.Handled = true; vm.SelectNeighbour(-1); break;
            case Key.PageDown: e.Handled = true; vm.SelectNeighbour(1); break;
            case Key.N: e.Handled = true; vm.NewTab(); break;
            case Key.W: e.Handled = true; await vm.CloseTabCommand.ExecuteAsync(null); break;
            case Key.O: e.Handled = true; await OpenAsync(); break;
            case Key.S: e.Handled = true; await SaveAsync(saveAs: shift); break;
        }
    }

    // ---- Tab strip: drag a tab to move it, middle-click to close it ----

    private const double DragThreshold = 6;
    private TabItem? _pressedTab;
    private Point _pressedAt;
    private bool _dragging;
    private int _dropSlot = -1;

    /// <summary>The document tab under the pointer, not a tab of a result pane nested inside the editor.</summary>
    private TabItem? HeaderUnder(PointerEventArgs e)
    {
        if (e.Source is not Visual source) return null;
        var tab = source as TabItem ?? source.FindAncestorOfType<TabItem>();
        if (tab is null || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(tab), DocTabs)) return null;
        // Only the header strip: a press inside the tab's content (editor, results) is not a tab press.
        var p = e.GetPosition(tab);
        return p.Y >= 0 && p.Y <= tab.Bounds.Height ? tab : null;
    }

    private async void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (HeaderUnder(e) is not { } tab || Vm is not { } vm) return;
        var props = e.GetCurrentPoint(tab).Properties;
        if (props.IsMiddleButtonPressed)
        {
            e.Handled = true;
            await vm.CloseTabCommand.ExecuteAsync(tab.DataContext as QueryViewModel);
            return;
        }
        if (!props.IsLeftButtonPressed || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        _pressedTab = tab;
        _pressedAt = e.GetPosition(DocTabs);
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTab is null) return;
        var at = e.GetPosition(DocTabs);
        if (!_dragging)
        {
            if (Math.Abs(at.X - _pressedAt.X) < DragThreshold && Math.Abs(at.Y - _pressedAt.Y) < DragThreshold) return;
            _dragging = true;
            _pressedTab.Classes.Add("dragging");
            e.Pointer.Capture(DocTabs);
        }
        e.Handled = true;
        ShowDropMarker(at);
    }

    private async void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var tab = _pressedTab;
        var slot = _dropSlot;
        var wasDragging = _dragging;
        EndDrag();
        if (!wasDragging || tab?.DataContext is not QueryViewModel doc || Vm is not { } vm) return;
        e.Handled = true;
        e.Pointer.Capture(null);
        var from = vm.Documents.IndexOf(doc);
        await vm.MoveTabAsync(doc, TabOrder.DropTarget(from, slot, vm.Documents.Count));
    }

    private void EndDrag()
    {
        _pressedTab?.Classes.Remove("dragging");
        _pressedTab = null;
        _dragging = false;
        _dropSlot = -1;
        DropMarker.IsVisible = false;
    }

    /// <summary>
    /// Finds the gap between tabs nearest to <paramref name="at"/> (the strip can wrap onto several rows)
    /// and draws the marker there.
    /// </summary>
    private void ShowDropMarker(Point at)
    {
        var headers = new List<Rect>();
        for (var i = 0; i < DocTabs.ItemCount; i++)
        {
            if (DocTabs.ContainerFromIndex(i) is not TabItem t || t.TranslatePoint(default, DocTabs) is not { } origin)
            {
                DropMarker.IsVisible = false;
                _dropSlot = -1;
                return;
            }
            headers.Add(new Rect(origin, t.Bounds.Size));
        }
        if (headers.Count == 0) return;

        // The row the pointer is on, or the nearest one above or below the strip.
        var rowTop = headers.MinBy(r => at.Y < r.Top ? r.Top - at.Y : at.Y > r.Bottom ? at.Y - r.Bottom : 0).Top;
        var row = Enumerable.Range(0, headers.Count).Where(i => Math.Abs(headers[i].Top - rowTop) < 1).ToList();
        var before = row.FirstOrDefault(i => at.X < headers[i].Center.X, -1);
        _dropSlot = before >= 0 ? before : row[^1] + 1;

        var edge = before >= 0 ? headers[before].Left : headers[row[^1]].Right;
        var rect = headers[before >= 0 ? before : row[^1]];
        var offset = DocTabs.TranslatePoint(default, this) ?? default;
        Canvas.SetLeft(DropMarker, offset.X + edge - 1);
        Canvas.SetTop(DropMarker, offset.Y + rect.Top + 4);
        DropMarker.Height = Math.Max(0, rect.Height - 8);
        DropMarker.IsVisible = true;
    }

    private async void OnOpen(object? sender, RoutedEventArgs e) => await OpenAsync();
    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveAsync(saveAs: false);
    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    private async Task OpenAsync()
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL script", AllowMultiple = true, FileTypeFilter = [SqlFiles, AllFiles]
        });
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;
            try { await vm.OpenFileAsync(path, file.Name); }
            catch (Exception ex) { if (vm.SelectedDocument is { } doc) doc.Status = $"Could not open {file.Name}: {ex.Message}"; }
        }
    }

    private async Task SaveAsync(bool saveAs)
    {
        if (Vm is not { SelectedDocument: { } doc } vm) return;
        var path = doc.FilePath;
        var name = doc.Title;
        if (saveAs || path is null)
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save SQL script",
                SuggestedFileName = (doc.FilePath is null ? doc.Title : Path.GetFileName(doc.FilePath)).Replace(' ', '_').TrimEnd('.') +
                                    (doc.FilePath is null ? ".sql" : ""),
                DefaultExtension = "sql", ShowOverwritePrompt = true, FileTypeChoices = [SqlFiles, AllFiles]
            });
            if (file?.TryGetLocalPath() is not { } chosen) return;
            path = chosen;
            name = file.Name;
        }

        try
        {
            await vm.SaveAsync(doc, path, name);
            doc.Status = $"Saved to {path}";
        }
        catch (Exception ex)
        {
            doc.Status = "Could not save: " + ex.Message;
        }
    }

    private void OnHistoryOpened(object? sender, EventArgs e) => Vm?.LoadHistoryCommand.Execute(null);

    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { } vm && HistoryList.SelectedItem is HistoryItem item) vm.OpenHistoryCommand.Execute(item);
    }
}
