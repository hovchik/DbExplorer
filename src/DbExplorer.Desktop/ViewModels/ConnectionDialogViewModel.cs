using DbExplorer.Application.Connections;
using DbExplorer.Application.Connections.Ssh;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Providers;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Desktop.ViewModels;

public partial class ConnectionDialogViewModel : ViewModelBase
{
    private readonly Guid _id;
    private readonly SshTunnelService? _tunnels;

    public ConnectionDialogViewModel(ProviderRegistry registry, ConnectionProfile draft, IReadOnlyList<string>? folders = null, SshTunnelService? tunnels = null)
    {
        _tunnels = tunnels;
        _useSsh = draft.Ssh.Enabled;
        _sshHost = draft.Ssh.Host;
        _sshPort = draft.Ssh.Port == SshTunnelSettings.DefaultPort ? "" : draft.Ssh.Port.ToString();
        _sshUserName = draft.Ssh.UserName;
        _sshUseKey = draft.Ssh.AuthMethod == SshAuthMethod.PrivateKey;
        _sshPassword = draft.Ssh.Password ?? "";
        _sshKeyPath = draft.Ssh.PrivateKeyPath;
        _sshPassphrase = draft.Ssh.Passphrase ?? "";
        Folders = folders ?? [];
        _folder = draft.Folder;
        Providers = registry.All;
        _id = draft.Id;
        _selectedProvider = Providers.FirstOrDefault(p => p.Key == draft.ProviderKey) ?? Providers[0];
        _name = draft.Name;
        _host = draft.Host;
        _port = draft.Port?.ToString() ?? "";
        _database = draft.Database;
        _integratedSecurity = draft.IntegratedSecurity;
        _userName = draft.UserName ?? "";
        _password = draft.Password ?? "";
        _savePassword = draft.SavePassword;
        _encrypt = draft.Encrypt;
        _trustServerCertificate = draft.TrustServerCertificate;
        _readOnlyIntent = draft.ReadOnlyIntent;
        _readOnly = draft.ReadOnly;
        _environment = draft.Environment;
    }

    public IReadOnlyList<ConnectionEnvironment> Environments { get; } = Enum.GetValues<ConnectionEnvironment>();

    [ObservableProperty] private ConnectionEnvironment _environment;

    public IReadOnlyList<IDatabaseProviderFactory> Providers { get; }
    public ObservableCollection<string> Databases { get; } = [];

    [ObservableProperty] private IDatabaseProviderFactory _selectedProvider;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _folder;

    /// <summary>Folders other connections use, offered while typing one.</summary>
    public IReadOnlyList<string> Folders { get; }
    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _database;
    [ObservableProperty] private bool _integratedSecurity;
    [ObservableProperty] private string _userName;
    [ObservableProperty] private string _password;
    [ObservableProperty] private bool _savePassword;
    [ObservableProperty] private bool _encrypt;
    [ObservableProperty] private bool _trustServerCertificate;
    [ObservableProperty] private bool _readOnlyIntent;
    [ObservableProperty] private bool _readOnly;
    [ObservableProperty] private string? _selectedDatabase;

    [ObservableProperty] private bool _useSsh;
    [ObservableProperty] private string _sshHost;
    [ObservableProperty] private string _sshPort;
    [ObservableProperty] private string _sshUserName;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SshUsePassword))] private bool _sshUseKey;
    [ObservableProperty] private string _sshPassword;
    [ObservableProperty] private string _sshKeyPath;
    [ObservableProperty] private string _sshPassphrase;

    public bool SshUsePassword
    {
        get => !SshUseKey;
        set => SshUseKey = !value;
    }

    /// <summary>Shown on the General tab while a tunnel is on: Host and Port are then as the SSH server sees them.</summary>
    public string HostHint => UseSsh ? "as the SSH server sees it, often localhost" : "server or server\\instance";

    partial void OnUseSshChanged(bool value) => OnPropertyChanged(nameof(HostHint));
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;

    public string PortHint => $"default {SelectedProvider.DefaultPort}";
    public bool CanUseIntegratedSecurity => SelectedProvider.SupportsIntegratedSecurity;
    public bool CanUseReadOnlyIntent => SelectedProvider.SupportsReadOnlyIntent;

    public event Action<bool>? CloseRequested;

    partial void OnSelectedProviderChanged(IDatabaseProviderFactory value)
    {
        OnPropertyChanged(nameof(PortHint));
        OnPropertyChanged(nameof(CanUseIntegratedSecurity));
        OnPropertyChanged(nameof(CanUseReadOnlyIntent));
        Databases.Clear();
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value)) Database = value;
    }

    /// <summary>With a tunnel, the SSH step and the database step are checked and reported separately.</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        IsBusy = true;
        var profile = ToProfile();
        SshTunnel? tunnel = null;
        var sshResult = "";
        try
        {
            if (profile.Ssh.Enabled && _tunnels is not null)
            {
                Message = $"Opening SSH tunnel to {profile.Ssh}…";
                try
                {
                    tunnel = await _tunnels.OpenAsync(profile, SelectedProvider.DefaultPort);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Message = "✗ SSH: " + ex.Message;
                    return;
                }
                sshResult = $"✓ SSH: connected to {tunnel.Server}. ";
                profile = tunnel.Local(profile);
            }

            Message = sshResult + "Connecting to the database…";
            try
            {
                await using var provider = SelectedProvider.Create(profile);
                var version = await provider.GetServerVersionAsync();
                Message = sshResult + "✓ Database: " + version;
            }
            catch (Exception ex)
            {
                Message = sshResult + "✗ Database: " + (tunnel?.Explain(ex) ?? ex).Message;
            }
        }
        finally
        {
            if (tunnel is not null) await tunnel.DisposeAsync();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadDatabasesAsync()
    {
        IsBusy = true;
        Message = "Loading databases…";
        try
        {
            var profile = ToProfile();
            IReadOnlyList<string> list;
            if (profile.Ssh.Enabled && _tunnels is not null)
            {
                await using var tunnel = await _tunnels.OpenAsync(profile, SelectedProvider.DefaultPort);
                list = await new TunnelledProviderFactory(SelectedProvider, tunnel).ListDatabasesAsync(profile);
            }
            else
            {
                list = await SelectedProvider.ListDatabasesAsync(profile);
            }
            Databases.Clear();
            foreach (var db in list) Databases.Add(db);
            Message = $"{list.Count} databases found.";
        }
        catch (Exception ex)
        {
            Message = "✗ " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Host)) { Message = "Host is required."; return; }
        if (!string.IsNullOrWhiteSpace(Port) && !(int.TryParse(Port, out var port) && port is > 0 and <= 65535))
        { Message = "Port must be a number from 1 to 65535 (or empty for the default)."; return; }
        if (UseSsh)
        {
            if (string.IsNullOrWhiteSpace(SshHost)) { Message = "SSH: enter the SSH server."; return; }
            if (!string.IsNullOrWhiteSpace(SshPort) && !(int.TryParse(SshPort, out var sshPort) && sshPort is > 0 and <= 65535))
            { Message = "SSH: the port must be a number from 1 to 65535."; return; }
            if (string.IsNullOrWhiteSpace(SshUserName)) { Message = "SSH: enter the user."; return; }
            if (SshUseKey && string.IsNullOrWhiteSpace(SshKeyPath)) { Message = "SSH: choose the private key file."; return; }
        }
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    public ConnectionProfile ToProfile() => new()
    {
        Id = _id,
        Name = string.IsNullOrWhiteSpace(Name) ? $"{Host.Trim()}/{Database.Trim()}" : Name.Trim(),
        Folder = ConnectionFolders.Normalize(Folder),
        ProviderKey = SelectedProvider.Key,
        Host = Host.Trim(),
        Port = int.TryParse(Port, out var port) ? port : null,
        Database = Database.Trim(),
        IntegratedSecurity = IntegratedSecurity && CanUseIntegratedSecurity,
        UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(),
        Password = string.IsNullOrEmpty(Password) ? null : Password,
        SavePassword = SavePassword,
        Encrypt = Encrypt,
        TrustServerCertificate = TrustServerCertificate,
        ReadOnlyIntent = ReadOnlyIntent && CanUseReadOnlyIntent,
        ReadOnly = ReadOnly,
        Environment = Environment,
        Ssh = new SshTunnelSettings
        {
            Enabled = UseSsh,
            Host = SshHost.Trim(),
            Port = int.TryParse(SshPort, out var sshPort) ? sshPort : SshTunnelSettings.DefaultPort,
            UserName = SshUserName.Trim(),
            AuthMethod = SshUseKey ? SshAuthMethod.PrivateKey : SshAuthMethod.Password,
            Password = string.IsNullOrEmpty(SshPassword) ? null : SshPassword,
            PrivateKeyPath = SshKeyPath.Trim(),
            Passphrase = string.IsNullOrEmpty(SshPassphrase) ? null : SshPassphrase
        }
    };
}
