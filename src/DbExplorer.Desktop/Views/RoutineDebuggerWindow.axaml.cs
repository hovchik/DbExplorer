using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using DbExplorer.Core.Debugging;
using DbExplorer.Desktop.Editor;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class RoutineDebuggerWindow : Window
{
    private readonly DebuggerMargin _margin = new();
    private readonly DebuggerLineHighlight _highlight = new();
    private RoutineDebuggerViewModel? _vm;

    public RoutineDebuggerWindow()
    {
        InitializeComponent();
        SourceEditor.TextArea.LeftMargins.Insert(0, _margin);
        SourceEditor.TextArea.TextView.BackgroundRenderers.Add(_highlight);
        _margin.LineClicked += line => _ = _vm?.ToggleBreakpointAsync(line);
        _margin.HasBreakpoint = line => _vm?.HasBreakpoint(line) == true;

        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
        ApplyHighlighting();
        DataContextChanged += (_, _) => Bind();
        KeyDown += OnKeyDown;
        VariablesGrid.DoubleTapped += (_, _) => _ = EditVariableAsync();
        Closed += async (_, _) =>
        {
            if (_vm is not null) await _vm.DisposeAsync();
        };
    }

    private void ApplyHighlighting() =>
        SourceEditor.SyntaxHighlighting = SqlHighlighting.Get(dark: ActualThemeVariant == ThemeVariant.Dark);

    private void Bind()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.BreakpointsChanged -= OnBreakpointsChanged;
        }
        _vm = DataContext as RoutineDebuggerViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.BreakpointsChanged += OnBreakpointsChanged;
        ShowSource();
    }

    private void OnBreakpointsChanged(object? sender, EventArgs e) => _margin.InvalidateVisual();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RoutineDebuggerViewModel.SourceText):
                ShowSource();
                break;
            case nameof(RoutineDebuggerViewModel.CurrentLine):
            case nameof(RoutineDebuggerViewModel.IsCurrentFrame):
                ShowCurrentLine();
                break;
            case nameof(RoutineDebuggerViewModel.ResultSets) when _vm?.ResultSets.Count > 0:
                OutputTabs.SelectedIndex = 1;
                break;
        }
    }

    // The editor's text is not bindable, so it follows the view model by hand.
    private void ShowSource()
    {
        if (_vm is null) return;
        if (SourceEditor.Text != _vm.SourceText) SourceEditor.Text = _vm.SourceText;
        ShowCurrentLine();
    }

    private void ShowCurrentLine()
    {
        if (_vm is null) return;
        _margin.CurrentLine = _highlight.Line = _vm.CurrentLine;
        _margin.IsCaller = _highlight.IsCaller = !_vm.IsCurrentFrame;
        if (_vm.CurrentLine > 0 && _vm.CurrentLine <= SourceEditor.Document.LineCount)
            SourceEditor.ScrollTo(_vm.CurrentLine, 1);
        SourceEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        _margin.InvalidateVisual();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F9 || _vm is null) return;
        _ = _vm.ToggleBreakpointAsync(SourceEditor.TextArea.Caret.Line);
        e.Handled = true;
    }

    private async Task EditVariableAsync()
    {
        if (_vm is null || VariablesGrid.SelectedItem is not DebugVariable variable) return;
        if (!_vm.IsRunning || _vm.SelectedFrame?.Frame.Level != 0) return;
        var value = await new TextPromptWindow(
            "Change variable", $"New value for {variable.Name} ({variable.DataType}). It is cast to the variable's type.",
            variable.Name, variable.Value ?? "", "value").ShowDialog<string?>(this);
        if (value is not null) await _vm.SetVariableAsync(variable, value);
    }
}
