using System.Globalization;
using System.Text.RegularExpressions;

namespace DbExplorer.Application.Api;

/// <summary>
/// Replaces <c>{{name}}</c> placeholders. Scopes are given from lowest to highest priority, as in Postman:
/// collection variables, then the selected environment, then values set for one run. A value may itself contain
/// placeholders (resolved up to <see cref="MaxDepth"/> levels). Unknown names stay as written and are listed in
/// <see cref="Unresolved"/>. Postman's dynamic variables (<c>{{$guid}}</c>, <c>{{$timestamp}}</c>…) are supported.
/// </summary>
public sealed partial class ApiVariableResolver
{
    public const int MaxDepth = 10;

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;
    private readonly Random _random;

    public ApiVariableResolver(params IEnumerable<ApiVariable>?[] scopes)
        : this(scopes, () => DateTimeOffset.UtcNow, Random.Shared)
    {
    }

    public ApiVariableResolver(IEnumerable<IEnumerable<ApiVariable>?> scopes, Func<DateTimeOffset> now, Random random)
    {
        _now = now;
        _random = random;
        foreach (var scope in scopes)
        {
            if (scope is null) continue;
            foreach (var v in scope)
                if (v.Enabled && !string.IsNullOrEmpty(v.Key)) _values[v.Key] = v.Value;
        }
    }

    /// <summary>Collection variables overridden by the environment's, then by <paramref name="overrides"/>.</summary>
    public static ApiVariableResolver For(ApiCollection? collection, ApiEnvironment? environment, IEnumerable<ApiVariable>? overrides = null)
        => new(collection?.Variables, environment?.Variables, overrides);

    /// <summary>Names that were referenced but have no value, across every call so far.</summary>
    public IReadOnlyCollection<string> Unresolved => _unresolved;

    public IReadOnlyDictionary<string, string> Values => _values;

    public string Resolve(string? text) => Resolve(text, 0);

    private string Resolve(string? text, int depth)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal)) return text ?? "";

        return PlaceholderRegex().Replace(text, m =>
        {
            var name = m.Groups["name"].Value.Trim();
            if (name.StartsWith('$') && Dynamic(name) is { } dynamic) return dynamic;
            if (!_values.TryGetValue(name, out var value))
            {
                _unresolved.Add(name);
                return m.Value;
            }
            if (depth + 1 >= MaxDepth)
            {
                // A variable referring to itself (directly or in a loop): stop instead of recursing forever.
                _unresolved.Add(name);
                return value;
            }
            return Resolve(value, depth + 1);
        });
    }

    private string? Dynamic(string name) => name switch
    {
        "$guid" or "$randomUUID" => Guid.NewGuid().ToString(),
        "$timestamp" => _now().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        "$isoTimestamp" => _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        "$randomInt" => _random.Next(0, 1001).ToString(CultureInfo.InvariantCulture),
        "$randomBoolean" => _random.Next(2) == 0 ? "false" : "true",
        _ => null
    };

    [GeneratedRegex(@"\{\{(?<name>[^{}]+)\}\}")]
    private static partial Regex PlaceholderRegex();
}
