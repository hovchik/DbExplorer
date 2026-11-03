using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Interactivity;

namespace DbExplorer.Desktop.Views;

/// <summary>The Preferences window (Ctrl+,). Settings apply as they change, so Close (or Esc) is the only button needed.</summary>
public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>A click in the section list scrolls that section to the top of the page.</summary>
    private void OnSectionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not ListBoxItem { Tag: string name } || this.FindControl<Control>(name) is not { } section) return;
        var top = section.TranslatePoint(new Point(0, 0), (Visual)Scroller.Content!);
        if (top is { } point) Scroller.Offset = new Vector(0, Math.Max(0, point.Y - 14));
    }
}
