using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbExplorer.Desktop.Views;

public partial class ConfirmWindow : Window
{
    private readonly string? _requiredText;

    public ConfirmWindow()
    {
        InitializeComponent();
    }

    /// <param name="requiredText">When set, the confirm button stays disabled until this exact text is typed.</param>
    /// <param name="banner">Optional red warning strip above the message (e.g. "PRODUCTION · Shop").</param>
    /// <param name="details">Optional text shown under the message as code, e.g. the script about to run.</param>
    public ConfirmWindow(string message, string confirmText = "Run", string? requiredText = null, string? banner = null, string? details = null) : this()
    {
        MessageText.Text = message;
        if (!string.IsNullOrEmpty(details))
        {
            DetailsBox.IsVisible = true;
            DetailsText.Text = details;
            Width = 680;
        }
        ConfirmButton.Content = confirmText;
        _requiredText = requiredText;

        if (banner is not null)
        {
            Banner.IsVisible = true;
            BannerText.Text = banner;
        }

        if (requiredText is not null)
        {
            TypePanel.IsVisible = true;
            TypePrompt.Text = $"Type {requiredText} to confirm:";
            ConfirmButton.IsEnabled = false;
            TypeBox.TextChanged += (_, _) => ConfirmButton.IsEnabled = TypeBox.Text?.Trim() == requiredText;
            Opened += (_, _) => TypeBox.Focus();
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_requiredText is not null && TypeBox.Text?.Trim() != _requiredText) return;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
