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

    public ConnectionDialogViewModel(ProviderRegistry registry, ConnectionProfile draft, IReadOnlyList<string>? folders = null, SshTunnelService? tunnels = null,
        bool isNew = false)
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
        _tenantId = draft.EntraTenantId ?? "";
        _clientId = draft.EntraClientId ?? "";
        _userName = draft.UserName ?? "";
        _password = draft.Password ?? "";
        _savePassword = draft.SavePassword;
        _encrypt = draft.Encrypt;
        _trustServerCertificate = draft.TrustServerCertificate;
        _readOnlyIntent = draft.ReadOnlyIntent;
        _readOnly = draft.ReadOnly;
        _environment = draft.Environment;
        _signInOptions = OptionsFor(_selectedProvider);
        _selectedSignIn = _signInOptions.FirstOrDefault(o => o.Method == SignInMethods.Of(draft)) ?? _signInOptions[0];
        // A new connection's TLS boxes follow the host (encrypt and verify unless the server is on this computer)
        // until the user ticks one of them; an existing connection keeps what it was saved with.
        _tlsFollowsHost = isNew;
        _isNew = isNew;
        if (isNew) (_encrypt, _trustServerCertificate) = ConnectionSecurity.DefaultsFor(_host);
    }

    private readonly bool _isNew;

    // ---- Connection string: paste to fill in, copy to share ----------------------------------------------------

    /// <summary>What the user pasted into the "connection string" box. Cleared once it fills the fields, so a
    /// password in it doesn't stay on screen.</summary>
    [ObservableProperty] private string _connectionStringText = "";

    /// <summary>What the last fill did, or why it failed (<see cref="ConnectionStringFailed"/>).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasConnectionStringMessage))] private string? _connectionStringMessage;
    [ObservableProperty] private bool _connectionStringFailed;
    public bool HasConnectionStringMessage => !string.IsNullOrEmpty(ConnectionStringMessage);

    /// <summary>"The clipboard has a PostgreSQL connection string for …", offered when a new connection opens.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasClipboardOffer))] private string? _clipboardOffer;
    public bool HasClipboardOffer => ClipboardOffer is not null;
    private string? _clipboardText;

    /// <summary>Text for the clipboard (a connection string to copy); the view puts it there.</summary>
    public event Action<string>? CopyRequested;

    private bool _fillingFromConnectionString;

    [RelayCommand]
    private void FillFromConnectionString() => Fill(ConnectionStringText, clearBox: true);

    /// <summary>For a new connection: offers a connection string found on the clipboard, without using it yet.</summary>
    public void OfferClipboard(string? text)
    {
        if (!_isNew || !ConnectionStrings.LooksLikeConnectionString(text)) return;
        if (!ConnectionStrings.TryParse(text, SelectedProvider.Key, out var parsed, out _) || !parsed.EngineDetected && parsed.Host is null) return;
        var target = parsed.Host is null ? parsed.Database : parsed.Database is null ? parsed.Host : $"{parsed.Host}/{parsed.Database}";
        _clipboardText = text;
        ClipboardOffer = $"The clipboard has a {ConnectionStrings.EngineName(parsed.ProviderKey)} connection string" +
                         (target is null ? "." : $" for {target}.");
    }

    [RelayCommand]
    private void UseClipboard()
    {
        if (_clipboardText is { } text) Fill(text, clearBox: false);
        DismissClipboard();
    }

    [RelayCommand]
    private void DismissClipboard()
    {
        _clipboardText = null;
        ClipboardOffer = null;
    }

    /// <summary>
    /// Fills the fields from a connection string or URL. The string describes the server, so server, port, database
    /// and user are replaced even where it leaves them out; a password typed earlier is kept only for the same user.
    /// TLS boxes change only when the string says something about TLS (sslmode, Encrypt, …).
    /// </summary>
    public bool Fill(string? text, bool clearBox)
    {
        if (!ConnectionStrings.TryParse(text, SelectedProvider.Key, out var parsed, out var error))
        {
            ConnectionStringFailed = true;
            ConnectionStringMessage = error;
            return false;
        }

        _fillingFromConnectionString = true;
        try
        {
            if (Providers.FirstOrDefault(p => p.Key == parsed.ProviderKey) is { } provider) SelectedProvider = provider;
            var sameUser = parsed.UserName is null || string.Equals(parsed.UserName, UserName, StringComparison.Ordinal);
            // A string with no server (Database=x;User ID=y) keeps the host, unless the host box holds the string itself.
            if (parsed.Host is not null) Host = parsed.Host;
            else if (ConnectionStrings.LooksLikeConnectionString(Host)) Host = "localhost";
            Port = parsed.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            Database = parsed.Database ?? "";
            var method = parsed.EntraAuth is { } entra && entra != EntraAuthMode.None
                ? SignInMethods.Of(new ConnectionProfile { EntraAuth = entra })
                : (parsed.IntegratedSecurity ?? false) && CanUseIntegratedSecurity ? SignInMethod.Windows : SignInMethod.Password;
            SelectSignIn(method);
            UserName = parsed.UserName ?? (UsesUser ? UserName : "");
            Password = parsed.Password ?? (sameUser ? Password : "");
            if (parsed.ReadOnlyIntent is { } intent) ReadOnlyIntent = intent;
            if (parsed.Encrypt is not null || parsed.TrustServerCertificate is not null)
            {
                _applyingTlsDefaults = true;
                try
                {
                    if (parsed.Encrypt is { } encrypt) Encrypt = encrypt;
                    if (parsed.TrustServerCertificate is { } trust) TrustServerCertificate = trust;
                }
                finally { _applyingTlsDefaults = false; }
                _tlsFollowsHost = false; // the string chose; a later host edit must not undo it
            }
        }
        finally { _fillingFromConnectionString = false; }

        ConnectionStringFailed = false;
        ConnectionStringMessage = "✓ " + ConnectionStrings.Describe(parsed);
        if (clearBox) ConnectionStringText = "";
        return true;
    }

    /// <summary>Copies these settings as a connection string or URL; "…+password" includes the password.</summary>
    [RelayCommand]
    private void CopyConnectionString(string? kind)
    {
        var style = kind?.StartsWith("url", StringComparison.OrdinalIgnoreCase) == true ? ConnectionStringStyle.Url : ConnectionStringStyle.KeyValue;
        var withPassword = kind?.EndsWith("+password", StringComparison.OrdinalIgnoreCase) == true;
        CopyRequested?.Invoke(ConnectionStrings.Format(ToProfile(), style, withPassword));
        Message = withPassword ? "Copied, including the password." : "Copied (without the password).";
    }

    private bool _tlsFollowsHost;
    private bool _applyingTlsDefaults;

    /// <summary>What the Encrypt and Trust certificate boxes mean together, under them.</summary>
    public string TlsSummary => ConnectionSecurity.Describe(Encrypt, TrustServerCertificate, Host);

    /// <summary>Shown when a Production connection does not check the server certificate.</summary>
    public string? TlsWarning => ConnectionSecurity.Warning(Encrypt, TrustServerCertificate, Environment);

    public bool HasTlsWarning => TlsWarning is not null;

    partial void OnHostChanged(string value)
    {
        OnPropertyChanged(nameof(ShowEntraOffer));
        // A connection string pasted straight into Host fills in every field instead.
        if (!_fillingFromConnectionString && ConnectionStrings.LooksLikeConnectionString(value))
        {
            if (Fill(value, clearBox: false)) return;
        }
        if (_tlsFollowsHost)
        {
            _applyingTlsDefaults = true;
            try { (Encrypt, TrustServerCertificate) = ConnectionSecurity.DefaultsFor(value); }
            finally { _applyingTlsDefaults = false; }
        }
        OnTlsChanged();
    }

    partial void OnEncryptChanged(bool value) => OnTlsChanged(userChange: true);
    partial void OnTrustServerCertificateChanged(bool value) => OnTlsChanged(userChange: true);
    partial void OnEnvironmentChanged(ConnectionEnvironment value) => OnTlsChanged();

    private void OnTlsChanged(bool userChange = false)
    {
        if (userChange && !_applyingTlsDefaults) _tlsFollowsHost = false;
        OnPropertyChanged(nameof(TlsSummary));
        OnPropertyChanged(nameof(TlsWarning));
        OnPropertyChanged(nameof(HasTlsWarning));
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

    // ---- Sign-in: database login, Windows, or one of the Microsoft Entra ID methods ---------------------------

    public sealed record SignInOption(SignInMethod Method, string Label)
    {
        public override string ToString() => Label;
    }

    [ObservableProperty] private IReadOnlyList<SignInOption> _signInOptions;
    [ObservableProperty] private SignInOption _selectedSignIn;

    /// <summary>Entra directory (tenant) ID or domain; empty for the account's home directory.</summary>
    [ObservableProperty] private string _tenantId;

    /// <summary>PostgreSQL with a service principal: the app's client ID (the User is its role name there).</summary>
    [ObservableProperty] private string _clientId;
    public bool UsesClientId => IsPostgres && SignIn == SignInMethod.EntraServicePrincipal;

    public SignInMethod SignIn => SelectedSignIn.Method;
    public bool IntegratedSecurity => SignIn is SignInMethod.Windows or SignInMethod.EntraIntegrated;
    public bool UsesUser => SignInMethods.UsesUser(SignIn, SelectedProvider.Key);
    public bool UsesPassword => SignInMethods.UsesPassword(SignIn);
    public bool UsesTenant => SignInMethods.UsesTenant(SignIn);
    public string UserLabel => !IsPostgres ? SignInMethods.UserLabel(SignIn)
        : SignIn == SignInMethod.EntraServicePrincipal ? "User (app's role name)"
        : SignInMethods.IsEntra(SignIn) ? "User" : SignInMethods.UserLabel(SignIn);
    public string PasswordLabel => SignInMethods.PasswordLabel(SignIn);
    public string? SignInHint => SignInMethods.Hint(SignIn, SelectedProvider.Key);
    public bool HasSignInHint => SignInHint is not null;
    public string TenantHint => SignIn == SignInMethod.EntraServicePrincipal && IsPostgres ? "required for an app" : "optional, e.g. contoso.com";
    private bool IsPostgres => string.Equals(SelectedProvider.Key, SignInMethods.PostgresKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>An Azure server signed in to with a password: Entra sign-in is one click away.</summary>
    public bool ShowEntraOffer => SignIn == SignInMethod.Password && SignInMethods.IsAzureHost(Host)
                                  && SignInOptions.Any(o => SignInMethods.IsEntra(o.Method));

    private static IReadOnlyList<SignInOption> OptionsFor(IDatabaseProviderFactory provider) =>
        SignInMethods.For(provider.Key, provider.SupportsIntegratedSecurity).Select(m => new SignInOption(m, SignInMethods.Label(m))).ToList();

    private void SelectSignIn(SignInMethod method) =>
        SelectedSignIn = SignInOptions.FirstOrDefault(o => o.Method == method) ?? SignInOptions[0];

    [RelayCommand]
    private void UseEntraSignIn() => SelectSignIn(SignInMethod.EntraInteractive);

    partial void OnSelectedSignInChanged(SignInOption value)
    {
        // A combo box clears its selection while its items are replaced; keep a real choice.
        if (value is null) { SelectedSignIn = SignInOptions[0]; return; }
        foreach (var name in new[] { nameof(SignIn), nameof(IntegratedSecurity), nameof(UsesUser), nameof(UsesPassword), nameof(UsesTenant),
                     nameof(UserLabel), nameof(PasswordLabel), nameof(SignInHint), nameof(HasSignInHint), nameof(TenantHint), nameof(ShowEntraOffer), nameof(UsesClientId) })
            OnPropertyChanged(name);
    }

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

    /// <summary>The other radio button of the pair. Only a check is applied: a radio button its group unchecks
    /// writes false too, and flipping the state on that fed the other button's check back into the group,
    /// leaving the two buttons toggling each other endlessly the moment the SSH tab opened.</summary>
    public bool SshUsePassword
    {
        get => !SshUseKey;
        set { if (value) SshUseKey = false; }
    }

    /// <summary>Shown on the General tab while a tunnel is on: Host and Port are then as the SSH server sees them.</summary>
    public string HostHint => UseSsh ? "as the SSH server sees it, often localhost" : "server or server\\instance";

    partial void OnUseSshChanged(bool value) => OnPropertyChanged(nameof(HostHint));
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;

    public string PortHint => $"default {SelectedProvider.DefaultPort}";

    /// <summary>SQLite: the connection is a file. Host holds its path; port, sign-in, TLS and SSH are hidden.</summary>
    public bool IsFileEngine => SelectedProvider.IsFileBased;
    public bool IsServerEngine => !IsFileEngine;
    public bool CanUseIntegratedSecurity => SelectedProvider.SupportsIntegratedSecurity;
    public bool CanUseReadOnlyIntent => SelectedProvider.SupportsReadOnlyIntent;

    public event Action<bool>? CloseRequested;

    partial void OnSelectedProviderChanged(IDatabaseProviderFactory value)
    {
        OnPropertyChanged(nameof(PortHint));
        OnPropertyChanged(nameof(CanUseIntegratedSecurity));
        OnPropertyChanged(nameof(CanUseReadOnlyIntent));
        OnPropertyChanged(nameof(IsFileEngine));
        OnPropertyChanged(nameof(IsServerEngine));
        // A server name means nothing as a file path, and the reverse.
        if (value.IsFileBased && !_fillingFromConnectionString && Host.Trim() == "localhost") Host = "";
        var current = SelectedSignIn?.Method ?? SignInMethod.Password;
        SignInOptions = OptionsFor(value);
        SelectSignIn(current);
        OnSelectedSignInChanged(SelectedSignIn);
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
        if (SignInProblem() is { } problem) { Message = problem; return; }
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
                Message = sshResult + "✗ Database: " + ConnectionFailure.Classify(tunnel?.Explain(ex) ?? ex, ToProfile()).Summary;
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
        if (SignInProblem() is { } problem) { Message = problem; return; }
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
            Message = "✗ " + ConnectionFailure.Classify(ex, ToProfile()).Summary;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>File-based engines: makes an empty database at <paramref name="path"/> and points the connection at it.</summary>
    public async Task CreateFileAsync(string path)
    {
        IsBusy = true;
        try
        {
            await SelectedProvider.CreateDatabaseFileAsync(path);
            Host = path;
            if (string.IsNullOrWhiteSpace(Name)) Name = Path.GetFileNameWithoutExtension(path);
            Message = $"✓ Created {Path.GetFileName(path)}. Save to keep the connection.";
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
        if (IsFileEngine)
        {
            if (string.IsNullOrWhiteSpace(Host)) { Message = "Choose the database file, or make one with New file…"; return; }
            CloseRequested?.Invoke(true);
            return;
        }
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
        if (SignInProblem() is { } problem) { Message = problem; return; }
        CloseRequested?.Invoke(true);
    }

    /// <summary>What the chosen sign-in still needs before it can work, or null.</summary>
    public string? SignInProblem() => SignIn switch
    {
        SignInMethod.EntraServicePrincipal when !IsPostgres && string.IsNullOrWhiteSpace(UserName) => "Enter the app's client ID as the user.",
        SignInMethod.EntraServicePrincipal when IsPostgres && string.IsNullOrWhiteSpace(ClientId) => "Enter the app's client ID.",
        SignInMethod.EntraServicePrincipal when IsPostgres && string.IsNullOrWhiteSpace(TenantId) =>
            "Enter the directory (tenant) ID: a service principal signs in to one directory.",
        SignInMethod.EntraPassword when string.IsNullOrWhiteSpace(UserName) => "Enter the Entra user name (name@company.com).",
        _ when IsPostgres && SignInMethods.IsEntra(SignIn) && string.IsNullOrWhiteSpace(UserName) =>
            "Enter the user: the Entra user (name@company.com), group or app name the PostgreSQL server has a role for.",
        _ => null
    };

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    public ConnectionProfile ToProfile() => IsFileEngine ? ToFileProfile() : new()
    {
        Id = _id,
        Name = string.IsNullOrWhiteSpace(Name) ? $"{Host.Trim()}/{Database.Trim()}" : Name.Trim(),
        Folder = ConnectionFolders.Normalize(Folder),
        ProviderKey = SelectedProvider.Key,
        Host = Host.Trim(),
        Port = int.TryParse(Port, out var port) ? port : null,
        Database = Database.Trim(),
        IntegratedSecurity = SignIn == SignInMethod.Windows && CanUseIntegratedSecurity,
        EntraAuth = SignInMethods.EntraModeOf(SignIn),
        EntraTenantId = UsesTenant && !string.IsNullOrWhiteSpace(TenantId) ? TenantId.Trim() : null,
        EntraClientId = UsesClientId && !string.IsNullOrWhiteSpace(ClientId) ? ClientId.Trim() : null,
        UserName = !UsesUser || string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(),
        Password = !UsesPassword || string.IsNullOrEmpty(Password) ? null : Password,
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

    /// <summary>A file connection: the path in Host (quotes from a pasted path dropped), named after the file by default.</summary>
    private ConnectionProfile ToFileProfile()
    {
        var path = Host.Trim().Trim('"', '\'');
        return new ConnectionProfile
        {
            Id = _id,
            Name = string.IsNullOrWhiteSpace(Name) ? (Path.GetFileNameWithoutExtension(path) is { Length: > 0 } n ? n : path) : Name.Trim(),
            Folder = ConnectionFolders.Normalize(Folder),
            ProviderKey = SelectedProvider.Key,
            Host = path,
            ReadOnly = ReadOnly,
            Environment = Environment,
            TrustServerCertificate = false,
            ReadOnlyIntent = false
        };
    }
}
