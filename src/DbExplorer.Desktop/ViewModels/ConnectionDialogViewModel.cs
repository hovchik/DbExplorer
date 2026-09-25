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

    public ConnectionDialogViewModel(ProviderRegistry registry, ConnectionProfile draft)
    {
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
    }

    public IReadOnlyList<IDatabaseProviderFactory> Providers { get; }
    public ObservableCollection<string> Databases { get; } = [];

    [ObservableProperty] private IDatabaseProviderFactory _selectedProvider;
    [ObservableProperty] private string _name;
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
    [ObservableProperty] private string? _selectedDatabase;
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

    [RelayCommand]
    private async Task TestAsync()
    {
        IsBusy = true;
        Message = "Connecting…";
        try
        {
            await using var provider = SelectedProvider.Create(ToProfile());
            var version = await provider.GetServerVersionAsync();
            Message = "✓ Connected: " + version;
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
    private async Task LoadDatabasesAsync()
    {
        IsBusy = true;
        Message = "Loading databases…";
        try
        {
            var list = await SelectedProvider.ListDatabasesAsync(ToProfile());
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
        if (!string.IsNullOrWhiteSpace(Port) && !int.TryParse(Port, out _)) { Message = "Port must be a number."; return; }
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    public ConnectionProfile ToProfile() => new()
    {
        Id = _id,
        Name = string.IsNullOrWhiteSpace(Name) ? $"{Host.Trim()}/{Database.Trim()}" : Name.Trim(),
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
        ReadOnlyIntent = ReadOnlyIntent && CanUseReadOnlyIntent
    };
}
