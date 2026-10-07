using Avalonia.Controls;
using Avalonia.Input;

namespace DbExplorer.Desktop.Views;

public partial class SecurityView : UserControl
{
    public SecurityView()
    {
        InitializeComponent();
    }

    /// <summary>Role, member, permission and object pickers open their list on focus, so the choices show before anything is typed.</summary>
    private void OnPickerFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is AutoCompleteBox box) box.IsDropDownOpen = true;
    }
}
