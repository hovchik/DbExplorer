using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(DbExplorer.Tests.HeadlessTestApp))]

namespace DbExplorer.Tests;

public sealed class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DbExplorer.Desktop.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class ResultGridViewTests
{
    [AvaloniaTheory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    public void Clicking_the_blank_area_right_of_the_last_column_does_not_throw(MouseButton button)
    {
        var view = new ResultGridView
        {
            ResultSet = new ResultSetView("t", ["id", "name"], [new ResultRow([1, "a"]), new ResultRow([2, "b"])])
        };
        var window = new Window { Width = 1400, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Two narrow columns in a wide window: each row's last cell is the grid's column-less filler cell.
        var cells = window.GetVisualDescendants().OfType<DataGridCell>().ToList();
        var filler = cells.Last();
        Assert.True(filler.Bounds.Width > 500);
        var point = filler.TranslatePoint(new Point(100, filler.Bounds.Height / 2), window)!.Value;

        window.MouseDown(point, button);
        window.MouseUp(point, button);
        Dispatcher.UIThread.RunJobs();

        var first = window.GetVisualDescendants().OfType<DataGridCell>().First(c => c.IsEffectivelyVisible);
        var cell = first.TranslatePoint(new Point(5, 5), window)!.Value;
        window.MouseDown(cell, button);
        window.MouseUp(cell, button);
        Dispatcher.UIThread.RunJobs();
        window.Close();
    }
}
