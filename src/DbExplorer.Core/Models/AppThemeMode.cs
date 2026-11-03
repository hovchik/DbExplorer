namespace DbExplorer.Core.Models;

/// <summary>User-selectable UI theme; <see cref="System"/> follows the OS setting, including Windows' contrast themes.</summary>
public enum AppThemeMode
{
    System,
    Light,
    Dark,
    /// <summary>Black, white and bright colours with a yellow accent, for low vision.</summary>
    HighContrast
}
