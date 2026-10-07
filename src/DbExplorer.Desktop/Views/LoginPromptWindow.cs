using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DbExplorer.Desktop.Views;

/// <summary>A login's name (when asked), its new password, and whether to also create a database user.</summary>
public sealed record LoginPromptAnswer(string Name, string Password, bool CreateUser);

/// <summary>
/// Asks for a new login, or a new password for an existing one. The password boxes are masked and their text goes
/// nowhere but the answer; the window closes with null when cancelled.
/// </summary>
public sealed class LoginPromptWindow : Window
{
    /// <param name="name">The login whose password changes; null to ask for a new login's name.</param>
    /// <param name="createUserLabel">Shows a ticked "also create a user" box with this text; null for none.</param>
    public LoginPromptWindow(string title, string message, string? name, string? createUserLabel)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var nameBox = new TextBox { Watermark = "Name", Text = name ?? "", IsVisible = name is null };
        var password = new TextBox { PasswordChar = '•', Watermark = "Password" };
        var confirm = new TextBox { PasswordChar = '•', Watermark = "Type the password again" };
        // A TextBlock, not a string: "_" in the database name would otherwise be read as an access key and vanish.
        var createUser = new CheckBox { Content = new TextBlock { Text = createUserLabel }, IsChecked = true, IsVisible = createUserLabel is not null };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var ok = new Button { Content = "Add to changes", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        ok.Click += (_, _) =>
        {
            var problem = (nameBox.Text ?? "").Trim() is "" ? "Type a name." :
                string.IsNullOrEmpty(password.Text) ? "Type a password." :
                password.Text != confirm.Text ? "The two passwords are not the same." : null;
            if (problem is not null)
            {
                error.Text = problem;
                error.IsVisible = true;
                return;
            }
            Close(new LoginPromptAnswer((nameBox.Text ?? "").Trim(), password.Text!, createUser.IsChecked == true));
        };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                nameBox,
                password,
                confirm,
                createUser,
                error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } }
            }
        };
        Opened += (_, _) => (name is null ? nameBox : password).Focus();
        // Nothing typed here outlives the window.
        Closed += (_, _) =>
        {
            password.Text = "";
            confirm.Text = "";
        };
    }
}
