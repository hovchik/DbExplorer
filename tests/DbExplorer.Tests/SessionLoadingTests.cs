using System.Collections.Concurrent;
using System.Reflection;
using DbExplorer.Application;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

/// <summary>Connecting and reading the catalog must never run on the caller's (UI) thread, must stop on cancel and
/// must say how far it got.</summary>
public sealed class SessionLoadingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-load-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SshTunnelService Tunnels() => new(new KnownHostsStore(new AppPaths(_root)), new RejectUnknownHostKeys());

    private MetadataService Metadata()
    {
        var paths = new AppPaths(_root);
        return new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
    }

    private static ConnectionProfile Profile() =>
        new() { ProviderKey = FakeFactory.ProviderKey, Host = "fake", Database = "db-" + Guid.NewGuid().ToString("N"), Name = "fake" };

    [Fact]
    public async Task Connect_reads_the_server_and_catalog_off_the_callers_context()
    {
        var provider = FakeProvider.Create();
        var sessions = new SessionService(new ProviderRegistry([new FakeFactory(provider)]), Metadata(), Tunnels());
        var ui = new MarkerContext();

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ui);
        Task<DatabaseSession> connect;
        try
        {
            connect = sessions.ConnectAsync(Profile());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await using var session = await connect;

        Assert.NotEmpty(provider.Handler.Contexts);
        Assert.DoesNotContain(provider.Handler.Contexts, c => ReferenceEquals(c, ui));
        Assert.Single(session.Snapshot.Objects);
    }

    [Fact]
    public async Task Cancel_stops_a_slow_catalog_read_at_once_and_releases_the_connection()
    {
        var provider = FakeProvider.Create(hangOn: nameof(IDatabaseProvider.GetColumnsAsync));
        var sessions = new SessionService(new ProviderRegistry([new FakeFactory(provider)]), Metadata(), Tunnels());
        using var cts = new CancellationTokenSource();

        var connect = sessions.ConnectAsync(Profile(), cts.Token);
        await provider.Handler.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(provider.Handler.Disposed);
    }

    [Fact]
    public async Task Catalog_load_reports_each_part_as_it_finishes()
    {
        var provider = FakeProvider.Create();
        var reports = new ConcurrentQueue<string>();

        await Metadata().LoadAsync(Profile(), (IDatabaseProvider)provider, forceRefresh: true, progress: new Collect(reports));

        var all = reports.ToList();
        Assert.StartsWith("Reading catalog", all[0]);
        Assert.Equal(6, all.Count); // the start, then one per finished part
        Assert.Contains(all, r => r.Contains("done, preparing", StringComparison.Ordinal));
    }

    private sealed class Collect(ConcurrentQueue<string> into) : IProgress<string>
    {
        public void Report(string value) => into.Enqueue(value);
    }

    private sealed class MarkerContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ThreadPool.QueueUserWorkItem(_ => d(state));
    }

    private sealed class FakeFactory(FakeProvider provider) : IDatabaseProviderFactory
    {
        public const string ProviderKey = "Fake";
        public string Key => ProviderKey;
        public string DisplayName => "Fake";
        public int DefaultPort => 0;
        public bool SupportsIntegratedSecurity => false;
        public bool SupportsReadOnlyIntent => false;
        public IDatabaseProvider Create(ConnectionProfile profile) => (IDatabaseProvider)(object)provider;
        public Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>An <see cref="IDatabaseProvider"/> that answers the catalog reads with one row each, notes the
    /// synchronization context each call starts on, and can hang one read until it is cancelled.</summary>
    public class FakeProvider : DispatchProxy
    {
        public FakeHandler Handler { get; private set; } = null!;

        public static FakeProvider Create(string? hangOn = null)
        {
            var proxy = (FakeProvider)(object)Create<IDatabaseProvider, FakeProvider>();
            proxy.Handler = new FakeHandler(hangOn);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler.Invoke(method!, args ?? []);
    }

    public sealed class FakeHandler(string? hangOn)
    {
        public ConcurrentBag<SynchronizationContext?> Contexts { get; } = [];
        public TaskCompletionSource Hanging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public object? Invoke(MethodInfo method, object?[] args)
        {
            switch (method.Name)
            {
                case nameof(IDatabaseProvider.DisposeAsync):
                    Disposed = true;
                    return ValueTask.CompletedTask;
                case "get_ProviderKey":
                    return FakeFactory.ProviderKey;
                case nameof(IDatabaseProvider.QuoteIdentifier):
                    return args[0];
            }

            Contexts.Add(SynchronizationContext.Current);
            var ct = args.OfType<CancellationToken>().FirstOrDefault();
            if (method.Name == hangOn) return Hang(method, ct);

            return method.Name switch
            {
                nameof(IDatabaseProvider.GetServerVersionAsync) => Task.FromResult("Fake 1.0"),
                nameof(IDatabaseProvider.GetObjectsAsync) => Rows(new DbObject { Database = "db", Schema = "dbo", Name = "t", Type = DbObjectType.Table }),
                nameof(IDatabaseProvider.GetColumnsAsync) => Rows<DbColumn>(),
                nameof(IDatabaseProvider.GetModulesAsync) => Rows<DbModule>(),
                nameof(IDatabaseProvider.GetForeignKeysAsync) => Rows<DbForeignKey>(),
                nameof(IDatabaseProvider.GetIndexesAsync) => Rows<DbIndex>(),
                _ => throw new NotSupportedException(method.Name)
            };
        }

        private object? Hang(MethodInfo method, CancellationToken ct)
        {
            Hanging.TrySetResult();
            // Task<IReadOnlyList<T>> that only ends when cancelled, typed to what the method returns.
            var resultType = method.ReturnType.GetGenericArguments()[0];
            var tcsType = typeof(TaskCompletionSource<>).MakeGenericType(resultType);
            var tcs = Activator.CreateInstance(tcsType, TaskCreationOptions.RunContinuationsAsynchronously)!;
            ct.Register(() => tcsType.GetMethod("TrySetCanceled", [typeof(CancellationToken)])!.Invoke(tcs, [ct]));
            return tcsType.GetProperty("Task")!.GetValue(tcs);
        }

        private static Task<IReadOnlyList<T>> Rows<T>(params T[] rows) => Task.FromResult<IReadOnlyList<T>>(rows);
    }
}
