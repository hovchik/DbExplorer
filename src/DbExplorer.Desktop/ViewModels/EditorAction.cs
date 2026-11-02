namespace DbExplorer.Desktop.ViewModels;

/// <summary>Editor actions the command palette can ask the Query editor for. They live in the view (they need the
/// caret, selection and editor), so the view model only raises <see cref="QueryViewModel.EditorActionRequested"/>.</summary>
public enum EditorAction
{
    RunScript,
    RunStatement,
    Explain,
    ExplainAnalyze,
    Format,
    ToggleComment,
    Find,
    Replace,
    GoToLine,
    UpperCase,
    LowerCase,
    Duplicate,
    NextWarning,
    ZoomIn,
    ZoomOut,
    ResetZoom,
    ToggleWordWrap,
    MaximizeResults,
    MaximizeEditor,
    ResetLayout,
    Focus
}

/// <summary>File actions of the Query workspace that need the window's file pickers.</summary>
public enum QueryFileAction
{
    Open,
    Save,
    SaveAs
}
