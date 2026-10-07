using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Desktop.Views;

namespace DbExplorer.Desktop.Services;

/// <summary>Shows an SSH server's new or changed host key over whichever window is in front (the connection dialog when testing).</summary>
public sealed class SshHostKeyPrompt : ISshHostKeyPrompt
{
    public Task<bool> TrustAsync(SshHostKey key, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return false;
            var owner = desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.Windows.LastOrDefault(w => w.IsVisible) ?? desktop.MainWindow;
            if (owner is null) return false;

            var details = $"Server:       {key.Server}\nKey type:     {key.KeyType}\nFingerprint:  {key.Fingerprint}";
            var window = key.Changed
                ? new ConfirmWindow(
                    $"The host key of {key.Server} is not the one you accepted before. The server may have been reinstalled, " +
                    "or someone may be intercepting the connection. Trust the new key only if the server's administrator confirms its fingerprint.",
                    "Trust new key and connect", banner: "SSH host key changed",
                    details: details + $"\nAccepted before: {key.KnownFingerprint}")
                : new ConfirmWindow(
                    $"First connection to the SSH server {key.Server}. Check that its fingerprint matches the server's " +
                    "(ssh-keygen -lf on the server's host key) before trusting it. It is remembered for later connections.",
                    "Trust and connect", details: details);
            return await window.ShowDialog<bool>(owner);
        });
}
