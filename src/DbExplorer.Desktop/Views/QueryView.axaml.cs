using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class QueryView : UserControl
{
    public QueryView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Editor.TextChanged += OnEditorTextChanged;
        Editor.LostFocus += (_, _) => SuggestionsPopup.IsOpen = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not QueryViewModel vm) return;

        var word = GetCurrentWord(out var wordStart);
        if (word.Length < 2)
        {
            SuggestionsPopup.IsOpen = false;
            return;
        }

        var textBeforeCaret = (Editor.Text ?? "")[..wordStart];
        var matches = vm.GetSuggestions(word, textBeforeCaret);
        vm.Suggestions.Clear();
        foreach (var m in matches) vm.Suggestions.Add(m);

        if (matches.Count > 0) SuggestionsList.SelectedIndex = 0;
        SuggestionsPopup.IsOpen = matches.Count > 0;
    }

    private string GetCurrentWord(out int wordStart)
    {
        var text = Editor.Text ?? "";
        var caret = Editor.CaretIndex;
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        wordStart = start;
        return text[start..caret];
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (SuggestionsPopup.IsOpen)
        {
            if (e.Key is Key.Down or Key.Up)
            {
                var list = SuggestionsList;
                var next = list.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                if (next >= 0 && next < list.ItemCount) list.SelectedIndex = next;
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Enter or Key.Tab)
            {
                var suggestion = SuggestionsList.SelectedItem as string
                    ?? (SuggestionsList.ItemCount > 0 ? SuggestionsList.Items[0] as string : null);
                if (suggestion is not null)
                {
                    ApplySuggestion(suggestion);
                    e.Handled = true;
                    return;
                }
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

    private void ApplySuggestion(string suggestion)
    {
        var word = GetCurrentWord(out var wordStart);
        var text = Editor.Text ?? "";
        var caret = Editor.CaretIndex;
        var newText = text[..wordStart] + suggestion + text[caret..];
        Editor.Text = newText;
        Editor.CaretIndex = wordStart + suggestion.Length;
        SuggestionsPopup.IsOpen = false;
    }
}
