using System.Text.Json;
using DbExplorer.Core.Models;

namespace DbExplorer.Application;

/// <summary>Experimental Query tab features, each of which can be turned off (all are on by default).</summary>
public enum ExperimentalFeature
{
    /// <summary>Re-running a query shows which rows are new, gone or changed since the previous run.</summary>
    ResultDiff,

    /// <summary>The statement at the caret shows the planner's estimate (rows, cost, full scans) before it is run.</summary>
    CostLens,

    /// <summary><c>-- expect: …</c> comments are checked against the results after each run.</summary>
    Expectations
}

/// <summary>Small app-wide preferences persisted as JSON: the UI theme, the SQL editor's zoom, word wrap and live SQL
/// warnings, and which experimental features are on.</summary>
public sealed class AppSettingsService
{
    public const double DefaultEditorFontSize = 13;
    public const double MinEditorFontSize = 8;
    public const double MaxEditorFontSize = 36;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;

    public AppSettingsService(AppPaths paths)
    {
        _file = Path.Combine(paths.Root, "settings.json");
        var stored = Load();
        Theme = stored.Theme;
        EditorFontSize = ClampFontSize(stored.EditorFontSize ?? DefaultEditorFontSize);
        EditorWordWrap = stored.EditorWordWrap;
        SqlInspections = !stored.SqlInspectionsOff;
        _disabled = (stored.DisabledExperiments ?? [])
            .Select(name => Enum.TryParse<ExperimentalFeature>(name, out var f) ? f : (ExperimentalFeature?)null)
            .OfType<ExperimentalFeature>().ToHashSet();
    }

    private readonly HashSet<ExperimentalFeature> _disabled;

    public AppThemeMode Theme { get; private set; }

    /// <summary>Font size of the SQL editors (zoomed with Ctrl+wheel, Ctrl+= and Ctrl+-), the same in every query tab.</summary>
    public double EditorFontSize { get; private set; }

    /// <summary>Whether the SQL editors wrap long lines (Alt+Z).</summary>
    public bool EditorWordWrap { get; private set; }

    /// <summary>Whether the SQL editors underline unknown tables and columns, ambiguous names and GROUP BY mistakes while
    /// typing (checked against the cached catalog, never on the server).</summary>
    public bool SqlInspections { get; private set; }

    public event Action<AppThemeMode>? ThemeChanged;

    /// <summary>Raised when an experimental feature was turned on or off.</summary>
    public event Action<ExperimentalFeature>? ExperimentChanged;

    public bool IsEnabled(ExperimentalFeature feature) => !_disabled.Contains(feature);

    public void SetEnabled(ExperimentalFeature feature, bool enabled)
    {
        if (IsEnabled(feature) == enabled) return;
        if (enabled) _disabled.Remove(feature);
        else _disabled.Add(feature);
        ExperimentChanged?.Invoke(feature);
        Save();
    }

    /// <summary>Raised when the editor font size, word wrap or live SQL warnings changed, so every open editor follows.</summary>
    public event Action? EditorChanged;

    public void SetTheme(AppThemeMode theme)
    {
        if (Theme == theme) return;
        Theme = theme;
        ThemeChanged?.Invoke(theme);
        Save();
    }

    public void SetEditorFontSize(double size)
    {
        size = ClampFontSize(size);
        if (EditorFontSize.Equals(size)) return;
        EditorFontSize = size;
        EditorChanged?.Invoke();
        Save();
    }

    public void SetEditorWordWrap(bool wrap)
    {
        if (EditorWordWrap == wrap) return;
        EditorWordWrap = wrap;
        EditorChanged?.Invoke();
        Save();
    }

    public void SetSqlInspections(bool on)
    {
        if (SqlInspections == on) return;
        SqlInspections = on;
        EditorChanged?.Invoke();
        Save();
    }

    public static double ClampFontSize(double size) =>
        double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinEditorFontSize, MaxEditorFontSize) : DefaultEditorFontSize;

    private StoredSettings Load()
    {
        try
        {
            if (!File.Exists(_file)) return new StoredSettings();
            return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_file), Json) ?? new StoredSettings();
        }
        catch
        {
            return new StoredSettings();
        }
    }

    private void Save()
    {
        try
        {
            var stored = new StoredSettings
            {
                Theme = Theme, EditorFontSize = EditorFontSize, EditorWordWrap = EditorWordWrap, SqlInspectionsOff = !SqlInspections,
                DisabledExperiments = _disabled.Count == 0 ? null : _disabled.Order().Select(f => f.ToString()).ToList()
            };
            AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(stored, Json));
        }
        catch
        {
            // Best-effort; a failed save just means the preference resets to default next launch.
        }
    }

    private sealed class StoredSettings
    {
        public AppThemeMode Theme { get; set; }
        public double? EditorFontSize { get; set; }
        public bool EditorWordWrap { get; set; }

        /// <summary>Stored as "off" so the warnings start on.</summary>
        public bool SqlInspectionsOff { get; set; }

        /// <summary>Experimental features turned off; stored as the off ones so new experiments start on.</summary>
        public List<string>? DisabledExperiments { get; set; }
    }
}
