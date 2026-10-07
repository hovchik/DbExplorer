using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using DbExplorer.Desktop.Editor;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ObjectsView : UserControl
{
    private ObjectsViewModel? _vm;

    public ObjectsView()
    {
        InitializeComponent();
        ObjectsGrid.SelectionChanged += (_, _) =>
        {
            if (ObjectsGrid.SelectedItem is { } item) ObjectsGrid.ScrollIntoView(item, null);
        };

        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
        DataContextChanged += (_, _) => BindDefinition();
        ApplyHighlighting();
    }

    private void ApplyHighlighting() =>
        DefinitionEditor.SyntaxHighlighting = SqlHighlighting.Get(dark: ActualThemeVariant == ThemeVariant.Dark);

    // The editor's text is not bindable, so it follows the view model's Definition by hand.
    private void BindDefinition()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as ObjectsViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelPropertyChanged;
        ShowDefinition();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ObjectsViewModel.Definition)) ShowDefinition();
    }

    private void ShowDefinition()
    {
        DefinitionEditor.Text = _vm?.Definition ?? "";
        DefinitionEditor.ScrollToHome();
    }

    private async void OnCopyDefinitionClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && DefinitionEditor.Text is { Length: > 0 } text)
            await clipboard.SetTextAsync(text);
    }

    private void OnObjectsDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is not ObjectsViewModel vm) return;

        if (vm.CanExecuteSelected && vm.ExecuteSelectedCommand.CanExecute(null))
            vm.ExecuteSelectedCommand.Execute(null);
        else if (vm.CanGetData && vm.GetDataCommand.CanExecute(null))
            vm.GetDataCommand.Execute(null);
    }
}
