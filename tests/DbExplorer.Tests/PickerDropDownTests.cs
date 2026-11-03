using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Desktop.Controls;

namespace DbExplorer.Tests;

/// <summary>
/// The pickers that open their list on focus (Table designer types and tables, Security roles, Comparer objects, Diagram
/// table, Import columns). Picking an item with the mouse used to throw "Index was out of range" from
/// Visual.OnDetachedFromVisualTreeCore: closing the list handed focus back to the box, and the handler reopened it
/// while Avalonia was still removing the old one.
/// </summary>
[Collection(nameof(HeadlessUiCollection))]
public class PickerDropDownTests
{
    [AvaloniaFact]
    public void Clicking_an_item_in_a_picker_that_opens_on_focus_does_not_throw()
    {
        Exception? unhandled = null;
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { unhandled = e.Exception; e.Handled = true; };
        Dispatcher.UIThread.UnhandledException += handler;
        try
        {
            var box = new AutoCompleteBox
            {
                Width = 200,
                ItemsSource = new[] { "integer", "int8", "text", "varchar" },
                MinimumPrefixLength = 0,
                FilterMode = AutoCompleteFilterMode.StartsWith
            };
            box.GotFocus += (sender, _) => PickerDropDown.OpenOnFocus(sender);
            var window = new Window { Width = 600, Height = 400, Content = new StackPanel { Children = { box } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            box.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(box.IsDropDownOpen);

            var popup = box.GetVisualDescendants().OfType<Popup>().First();
            var item = ((Visual)popup.Host!).GetVisualDescendants().OfType<ListBoxItem>().ElementAt(1);
            var point = item.TranslatePoint(new Point(5, 5), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(unhandled);
            Assert.Equal("int8", box.Text);
            // The pick closes the list; the focus that comes back from it does not reopen it.
            Assert.False(box.IsDropDownOpen);
            window.Close();
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= handler;
        }
    }

    [AvaloniaFact]
    public void Focusing_a_picker_from_elsewhere_opens_its_list()
    {
        var other = new TextBox();
        var box = new AutoCompleteBox { ItemsSource = new[] { "a", "b" }, MinimumPrefixLength = 0 };
        box.GotFocus += (sender, _) => PickerDropDown.OpenOnFocus(sender);
        var window = new Window { Width = 600, Height = 400, Content = new StackPanel { Children = { other, box } } };
        window.Show();
        other.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.False(box.IsDropDownOpen);

        box.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsDropDownOpen);
        window.Close();
    }
}
