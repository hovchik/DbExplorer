using DbExplorer.Core.Abstractions;

namespace DbExplorer.Application.Providers;

/// <summary>All registered database engines. New engines are added via DI, no changes here.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IDatabaseProviderFactory> _factories;

    public ProviderRegistry(IEnumerable<IDatabaseProviderFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        All = _factories.Values.OrderBy(f => f.DisplayName).ToList();
    }

    public IReadOnlyList<IDatabaseProviderFactory> All { get; }

    public IDatabaseProviderFactory Get(string key) =>
        _factories.TryGetValue(key, out var f)
            ? f
            : throw new InvalidOperationException($"No database provider is registered for '{key}'.");
}
