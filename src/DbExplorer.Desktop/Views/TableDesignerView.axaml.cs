using Avalonia.Controls;
using Avalonia.Input;
using DbExplorer.Desktop.Controls;

namespace DbExplorer.Desktop.Views;

public partial class TableDesignerView : UserControl
{
    public TableDesignerView()
    {
        InitializeComponent();
    }

    /// <summary>Type, schema and table pickers open their list on focus, so the choices show before anything is typed.</summary>
    private void OnPickerFocus(object? sender, GotFocusEventArgs e)
    {
        PickerDropDown.OpenOnFocus(sender);
    }
}
