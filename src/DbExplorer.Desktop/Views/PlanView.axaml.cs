using System.ComponentModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>The Plan tab: the drawn plan, its warnings, and the selected operator's details.</summary>
public partial class PlanView : UserControl
{
    private PlanViewModel? _vm;

    public PlanView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
            _vm = DataContext as PlanViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
            _fitted = false;
        };
        // First time the plan has room: shrink a wide plan to fit (not below 80%, where text stays readable) and show the busiest operator.
        Scroller.SizeChanged += (_, e) =>
        {
            if (_fitted || _vm is null || e.NewSize.Width <= 0) return;
            _fitted = true;
            if (_vm.Layout.Width > e.NewSize.Width || _vm.Layout.Height > e.NewSize.Height)
                _vm.Zoom = Math.Max(0.8, Math.Min(1.0, Math.Floor(Math.Min(e.NewSize.Width / _vm.Layout.Width, e.NewSize.Height / _vm.Layout.Height) * 10) / 10));
            if (_vm.SelectedNode is { } node)
                Dispatcher.UIThread.Post(() => Diagram.ScrollIntoView(node), DispatcherPriority.Background);
        };
    }

    private bool _fitted;

    /// <summary>A warning picked in the list may be off screen: scroll its operator into view.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanViewModel.SelectedNode) && _vm?.SelectedNode is { } node)
            Dispatcher.UIThread.Post(() => Diagram.ScrollIntoView(node), DispatcherPriority.Background);
    }

    private void OnFit(object? sender, RoutedEventArgs e) =>
        _vm?.Fit(Scroller.Bounds.Width - 4, Scroller.Bounds.Height - 4);

    private async void OnCopyRaw(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(_vm.Plan.Raw);
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var extension = _vm.RawExtension;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save execution plan",
            SuggestedFileName = "plan." + extension,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType(extension == "sqlplan" ? "SQL Server execution plan" : "PostgreSQL JSON plan") { Patterns = ["*." + extension] }
            ]
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            // SSMS expects .sqlplan files as UTF-16, the encoding the showplan XML declares.
            var encoding = extension == "sqlplan" ? Encoding.Unicode : new UTF8Encoding(false);
            await using var writer = new StreamWriter(stream, encoding);
            await writer.WriteAsync(_vm.Plan.Raw);
        }
        catch (Exception ex)
        {
            ToolTip.SetTip(sender as Control ?? this, "Could not save: " + ex.Message);
        }
    }
}
