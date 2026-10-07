using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

public partial class ErModelView : UserControl
{
    public ErModelView()
    {
        InitializeComponent();
        Canvas.TableMoved += (table, x, y, final) => Vm?.MoveTable(table, x, y, final);
        Canvas.LinkRequested += (child, column, parent, parentColumn) => Vm?.Link(child, column, parent, parentColumn);
        Canvas.EmptyDoubleClicked += (x, y) => Vm?.AddTableAt(x, y);
        Canvas.DeleteRequested += _ => Vm?.RemoveTableCommand.Execute(null);
    }

    private ErModelViewModel? Vm => DataContext as ErModelViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Undo/redo of the model, unless a text box handled the keys for its own text.
        if (e.Handled || Vm is not { } vm || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.Z && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) vm.UndoCommand.Execute(null);
        else if (e.Key == Key.Y || e.Key == Key.Z) vm.RedoCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    private void OnFit(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Diagram.Tables.Count: > 0 } vm) return;
        var view = Viewport.Bounds.Size;
        var width = vm.Diagram.Tables.Max(t => t.X + t.Width) + 20;
        var height = vm.Diagram.Tables.Max(t => t.Y + t.Height) + 20;
        var zoom = Math.Min((view.Width - 4) / width, (view.Height - 4) / height);
        vm.Zoom = Math.Clamp(Math.Floor(zoom * 20) / 20, 0.3, 1.5);
    }
}
