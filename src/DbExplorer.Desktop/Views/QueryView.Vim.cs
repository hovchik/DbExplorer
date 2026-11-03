using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Application.Vim;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.Views;

/// <summary>Vim keys in the query editor (switched in the F1 sheet, the Tools menu or the command palette). Normal and
/// visual mode keys go to <see cref="VimEngine"/>; insert mode is the editor as usual. Ctrl and Alt shortcuts, F-keys and
/// the completion list keep working in every mode.</summary>
public partial class QueryView
{
    private VimEngine? _vim;
    private bool _keymapHooked;

    private void InstallVim()
    {
        Editor.TextArea.AddHandler(TextInputEvent, OnVimTextInput, RoutingStrategies.Tunnel);
        ApplyVimMode();
    }

    private void HookKeymap()
    {
        var attached = this.IsAttachedToVisualTree();
        if (attached == _keymapHooked) return;
        _keymapHooked = attached;
        if (attached)
        {
            Keymap.Changed += OnKeymapChanged;
            ApplyVimMode();
        }
        else Keymap.Changed -= OnKeymapChanged;
    }

    private void OnKeymapChanged() => Dispatcher.UIThread.Post(() =>
    {
        ApplyVimMode();
        UpdateEditorHint();
    });

    /// <summary>Turns the Vim keys on or off to match the keymap.</summary>
    private void ApplyVimMode()
    {
        var on = Keymap.Current.VimMode;
        if (on && _vim is null)
        {
            _vim = new VimEngine(new EditorVimHost(Editor));
            _vim.StateChanged += ShowVimState;
            _vim.ExCommand += OnVimExCommand;
        }
        else if (!on && _vim is not null)
        {
            _vim.StateChanged -= ShowVimState;
            _vim.ExCommand -= OnVimExCommand;
            _vim = null;
        }
        if (VimItem is not null) VimItem.IsChecked = on;
        ShowVimState();
    }

    /// <summary>Another tab's document was bound: start it in normal mode.</summary>
    private void ResetVim() => _vim?.Reset();

    private void ShowVimState()
    {
        var vim = _vim;
        Editor.TextArea.OverstrikeMode = vim is not null && vim.Mode != VimMode.Insert;
        VimInfo.IsVisible = vim is not null;
        if (vim is null) return;
        var mode = vim.Mode switch
        {
            VimMode.Insert => "-- INSERT --",
            VimMode.Visual => "-- VISUAL --",
            VimMode.VisualLine => "-- VISUAL LINE --",
            VimMode.CommandLine => vim.CommandLine,
            _ => "NORMAL"
        };
        var extra = vim.Mode == VimMode.CommandLine ? "" : vim.PendingKeys;
        VimInfo.Text = string.Join("   ", new[] { mode, extra, vim.Message }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>Keys that the editor would otherwise act on in normal and visual mode (Esc, Enter, Backspace, arrows), and
    /// Esc / Ctrl+[ in insert mode. Letters and symbols arrive as text input instead, so keyboard layouts just work.</summary>
    private bool VimKeyDown(KeyEventArgs e)
    {
        if (_vim is not { } vim) return false;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var escape = (e.Key == Key.Escape && !ctrl && !alt) || (ctrl && !alt && e.Key == Key.Oem4);

        if (vim.Mode == VimMode.Insert)
        {
            if (!escape) return false;
            SignaturePopup.IsOpen = false;
            _completion?.Close();
            return vim.Key("<Esc>");
        }

        if (escape)
        {
            SignaturePopup.IsOpen = false;
            return vim.Key("<Esc>");
        }
        if (ctrl && !alt && !shift && e.Key == Key.R) return vim.Key("<C-r>");
        if (ctrl || alt) return false;
        var key = e.Key switch
        {
            Key.Enter => "<CR>",
            Key.Back => "<BS>",
            Key.Delete => "<Del>",
            Key.Tab => "<Tab>",
            Key.Left => "<Left>",
            Key.Right => "<Right>",
            Key.Up => "<Up>",
            Key.Down => "<Down>",
            Key.Home => "<Home>",
            Key.End => "<End>",
            _ => null
        };
        return key is not null && vim.Key(key);
    }

    private void OnVimTextInput(object? sender, TextInputEventArgs e)
    {
        if (_vim is not { } vim || vim.Mode == VimMode.Insert || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        foreach (var ch in e.Text) vim.Key(ch.ToString());
    }

    private async void OnVimExCommand(VimExCommand command)
    {
        if (_vim?.Mode == VimMode.Insert || DataContext is not ViewModels.QueryViewModel doc) return;
        if (this.FindAncestorOfType<QueryWorkspaceView>() is { } workspace) await workspace.RunVimCommandAsync(doc, command);
    }

    /// <summary>The key hints under the editor, with the keys in use now.</summary>
    private void UpdateEditorHint()
    {
        static string? K(string id) => Keymap.Current.Primary(id)?.ToString();
        var parts = new List<string>();
        void Add(string? keys, string what) { if (keys is not null) parts.Add($"{keys} {what}"); }
        Add(K(ShortcutCommands.Complete), "suggest");
        Add(K(ShortcutCommands.ShowFixes), "fix");
        Add(K(ShortcutCommands.RunScript), "run");
        Add(K(ShortcutCommands.RunStatement), "statement");
        Add(K(ShortcutCommands.Explain), "plan");
        Add(K(ShortcutCommands.GoToDefinition) is { } definition ? definition + "/Ctrl+Click" : "Ctrl+Click", "definition");
        Add(K(ShortcutCommands.Rename), "rename");
        Add(K(ShortcutCommands.FindUsages), "usages");
        Add(K(ShortcutCommands.Format), "format");
        Add(K(ShortcutCommands.ToggleComment), "comment");
        Add(K(ShortcutCommands.Duplicate), "duplicate");
        if (K(ShortcutCommands.MoveLinesUp) is { } up && K(ShortcutCommands.MoveLinesDown) is { } down)
            Add(up == "Alt+↑" && down == "Alt+↓" ? "Alt+↑↓" : $"{up}/{down}", "move line");
        Add(K(ShortcutCommands.WordWrap), "wrap");
        Add("Ctrl+wheel", "zoom");
        EditorHint.Text = string.Join(" · ", parts);
    }

    private void OnToggleVim(object? sender, RoutedEventArgs e) => Keymap.Current.SetVimMode(!Keymap.Current.VimMode);

    /// <summary>The query editor as seen by <see cref="VimEngine"/>.</summary>
    private sealed class EditorVimHost(TextEditor editor) : IVimHost
    {
        public string Text => editor.Document.Text;

        public int Caret
        {
            get => editor.CaretOffset;
            set
            {
                editor.CaretOffset = Math.Clamp(value, 0, editor.Document.TextLength);
                editor.TextArea.Caret.BringCaretToView();
            }
        }

        public void Select(int start, int end, int caret)
        {
            var area = editor.TextArea;
            var length = editor.Document.TextLength;
            start = Math.Clamp(start, 0, length);
            end = Math.Clamp(end, start, length);
            area.Caret.Offset = Math.Clamp(caret, 0, length);
            area.Selection = caret <= start ? Selection.Create(area, end, start) : Selection.Create(area, start, end);
            area.Caret.BringCaretToView();
        }

        public void ClearSelection()
        {
            if (!editor.TextArea.Selection.IsEmpty) editor.TextArea.ClearSelection();
        }

        public void Replace(int offset, int length, string text) => editor.Document.Replace(offset, length, text);

        public void BeginChange() => editor.Document.BeginUpdate();

        public void EndChange() => editor.Document.EndUpdate();

        public void Undo() => editor.Undo();

        public void Redo() => editor.Redo();

        public (int Start, int End)? MouseSelection
        {
            get
            {
                var selection = editor.TextArea.Selection;
                if (selection.IsEmpty || selection.SurroundingSegment is not { } segment) return null;
                return (segment.Offset, segment.EndOffset);
            }
        }
    }
}
