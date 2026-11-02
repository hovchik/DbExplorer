namespace DbExplorer.Application.Vim;

public enum VimMode
{
    Normal,
    Insert,
    Visual,
    VisualLine,

    /// <summary>Typing a ":" command or a "/" or "?" search on the status line.</summary>
    CommandLine
}

/// <summary>The editor the Vim keys act on. Offsets are character offsets into <see cref="Text"/>.</summary>
public interface IVimHost
{
    /// <summary>The whole text; read once per key.</summary>
    string Text { get; }

    int Caret { get; set; }

    /// <summary>Selects [start, end) with the caret drawn at <paramref name="caret"/> (visual mode).</summary>
    void Select(int start, int end, int caret);

    void ClearSelection();

    void Replace(int offset, int length, string text);

    /// <summary>Groups the edits until <see cref="EndChange"/> into one undo step.</summary>
    void BeginChange();

    void EndChange();

    void Undo();

    void Redo();

    /// <summary>The selection when Vim mode was not driving it (a mouse drag), as [start, end), or null.</summary>
    (int Start, int End)? MouseSelection { get; }
}

/// <summary>A ":" command the editor carries out itself.</summary>
public enum VimExCommand
{
    Save,
    Close,
    SaveAndClose
}
