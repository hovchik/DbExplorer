using System.Text.Json;
using DbExplorer.Application.Connections;

namespace DbExplorer.Application.Assistant;

/// <summary>
/// The user's Anthropic API key for the AI assistant. It is encrypted for the current Windows user (DPAPI) in
/// <c>assistant.json</c>; where no OS secret store is available it is kept for this session only. The assistant is
/// hidden until a key is set.
/// </summary>
public sealed class AssistantSettings
{
    public const string DefaultModel = "claude-opus-5-5";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly ISecretProtector _protector;
    private string? _apiKey;

    public AssistantSettings(AppPaths paths, ISecretProtector protector)
    {
        _file = Path.Combine(paths.Root, "assistant.json");
        _protector = protector;
        var stored = Load();
        Model = string.IsNullOrWhiteSpace(stored.Model) ? DefaultModel : stored.Model;
        if (stored.ProtectedApiKey is { Length: > 0 } secret && protector.IsSupported)
            _apiKey = protector.Unprotect(secret);
    }

    /// <summary>Model the assistant asks. Not shown in the UI; editable in assistant.json.</summary>
    public string Model { get; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>False when the key cannot be stored encrypted on this OS, so it lasts until the app closes.</summary>
    public bool CanPersistKey => _protector.IsSupported;

    public string? ApiKey => _apiKey;

    /// <summary>Raised when the key was set or removed, so the Query tabs show or hide the assistant.</summary>
    public event Action? Changed;

    /// <summary>Sets the key, or removes it when <paramref name="apiKey"/> is empty.</summary>
    public void SetApiKey(string? apiKey)
    {
        apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (apiKey == _apiKey) return;
        _apiKey = apiKey;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Shows the start and end of the key only ("sk-ant-…a1b2"), for the settings dialog.</summary>
    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "" : key.Length <= 12 ? new string('•', key.Length) : key[..7] + "…" + key[^4..];

    private Stored Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file), Json) ?? new Stored() : new Stored();
        }
        catch
        {
            return new Stored();
        }
    }

    private void Save()
    {
        try
        {
            var stored = new Stored
            {
                Model = Model == DefaultModel ? null : Model,
                ProtectedApiKey = _apiKey is not null && _protector.IsSupported ? _protector.Protect(_apiKey) : null
            };
            File.WriteAllText(_file, JsonSerializer.Serialize(stored, Json));
        }
        catch
        {
            // Best-effort: the key still works for this session.
        }
    }

    private sealed class Stored
    {
        public string? Model { get; set; }
        public string? ProtectedApiKey { get; set; }
    }
}
