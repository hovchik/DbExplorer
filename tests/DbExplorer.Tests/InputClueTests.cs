using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Tests;

/// <summary>The last click or shortcut before a UI hang, as the watchdog line in errors.log names it.</summary>
[Collection(nameof(HeadlessUiCollection))]
public class InputClueTests
{
    private sealed class SampleView : UserControl;

    [AvaloniaFact]
    public void Clicks_and_shortcuts_are_recorded_with_their_control_and_view_but_typing_is_not()
    {
        var now = new DateTimeOffset(2026, 11, 12, 10, 6, 40, TimeSpan.Zero);
        var clues = new HangClues(() => now);
        InputClue.Watch(clues);

        var button = new Button { Content = "Run", Width = 100, Height = 30 };
        var box = new TextBox { Width = 200 };
        var window = new Window
        {
            Width = 400, Height = 300,
            Content = new SampleView { Content = new StackPanel { Children = { button, box } } }
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var point = button.TranslatePoint(new Point(10, 10), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal("Last input before it: click on Button 'Run' in SampleView, just then.", clues.Describe(now));

        box.Focus();
        window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
        Assert.Contains("F5 in SampleView", clues.Describe(now));

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.None);
        Assert.Contains("typing in SampleView", clues.Describe(now));
        window.Close();
    }
}
