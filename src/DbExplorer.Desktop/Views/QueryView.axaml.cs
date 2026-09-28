using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class QueryView : UserControl
{
    private bool _applying;
    private int _replaceStart;

    public QueryView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Editor.TextChanged += (_, _) => { if (!_applying) UpdateSuggestions(explicitRequest: false); };
        Editor.LostFocus += (_, _) => SuggestionsPopup.IsOpen = false;
        SuggestionsList.DoubleTapped += (_, _) => ApplySelected();
    }

    private void UpdateSuggestions(bool explicitRequest)
    {
        if (DataContext is not QueryViewModel vm) return;

        var text = Editor.Text ?? "";
        var caret = Editor.CaretIndex;
        var result = vm.GetCompletions(text, caret, explicitRequest);

        // Typing: wait for two characters, unless the word follows a qualifier ("o." / "dbo.").
        var afterDot = result.ReplaceStart > 0 && text[result.ReplaceStart - 1] == '.';
        if (!explicitRequest && !afterDot && caret - result.ReplaceStart < 2)
        {
            SuggestionsPopup.IsOpen = false;
            return;
        }

        _replaceStart = result.ReplaceStart;
        vm.Suggestions.Clear();
        foreach (var item in result.Items) vm.Suggestions.Add(item);

        if (result.Items.Count > 0) SuggestionsList.SelectedIndex = 0;
        SuggestionsPopup.IsOpen = result.Items.Count > 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Editor.IsFocused)
        {
            UpdateSuggestions(explicitRequest: true);
            e.Handled = true;
            return;
        }

        if (SuggestionsPopup.IsOpen)
        {
            if (e.Key is Key.Down or Key.Up)
            {
                var list = SuggestionsList;
                var next = list.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                if (next >= 0 && next < list.ItemCount) list.SelectedIndex = next;
                list.ScrollIntoView(list.SelectedIndex);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Enter or Key.Tab && ApplySelected())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                SuggestionsPopup.IsOpen = false;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.F5 && DataContext is QueryViewModel vm && vm.ExecuteCommand.CanExecute(null))
        {
            vm.ExecuteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private bool ApplySelected()
    {
        var item = SuggestionsList.SelectedItem as CompletionItem
                   ?? (SuggestionsList.ItemCount > 0 ? SuggestionsList.Items[0] as CompletionItem : null);
        if (item is null) return false;

        var text = Editor.Text ?? "";
        var caret = Math.Clamp(Editor.CaretIndex, 0, text.Length);
        var start = Math.Clamp(_replaceStart, 0, caret);

        _applying = true;
        try
        {
            Editor.Text = text[..start] + item.InsertText + text[caret..];
            Editor.CaretIndex = start + item.InsertText.Length;
        }
        finally
        {
            _applying = false;
        }
        SuggestionsPopup.IsOpen = false;
        Editor.Focus();
        return true;
    }
}
