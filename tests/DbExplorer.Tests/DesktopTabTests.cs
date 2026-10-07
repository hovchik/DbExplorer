using System.Reflection;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>Desktop tabs that read from the session: a reconnect replaces the session, and whatever the old one still
/// returns afterwards must not land in the tab (nor keep the new session from loading).</summary>
public class DesktopTabTests
{
    [Fact]
    public async Task Indexes_drop_a_load_of_the_previous_session_and_do_not_block_the_new_one()
    {
        var slow = new TaskCompletionSource<IReadOnlyList<DbIndex>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Session(ScriptedProvider.Create(new() { [nameof(IDatabaseProvider.GetIndexesAsync)] = _ => slow.Task }));
        var fresh = Session(ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.GetIndexesAsync)] = _ => Task.FromResult<IReadOnlyList<DbIndex>>([Index("fresh")])
        }));
        var vm = new IndexesViewModel();

        vm.Attach(old);
        var oldLoad = vm.LoadCommand.ExecuteAsync(null);
        Assert.True(vm.IsLoading);

        vm.Attach(fresh);
        Assert.False(vm.IsLoading);
        Assert.True(vm.LoadCommand.CanExecute(null));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(["fresh"], vm.Indexes.Select(i => i.Name));

        slow.SetResult([Index("old")]);
        await oldLoad;
        Assert.Equal(["fresh"], vm.Indexes.Select(i => i.Name));
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Activity_keeps_the_newest_top_query_load_when_an_older_one_finishes_last()
    {
        var pending = new Queue<TaskCompletionSource<IReadOnlyList<DbQueryStat>>>();
        var session = Session(ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.GetTopQueriesAsync)] = _ =>
            {
                var tcs = new TaskCompletionSource<IReadOnlyList<DbQueryStat>>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Enqueue(tcs);
                return tcs.Task;
            }
        }));
        var vm = new ActivityViewModel();
        vm.Attach(session);

        var first = vm.LoadTopQueriesCommand.ExecuteAsync(null);
        var second = vm.LoadTopQueriesCommand.ExecuteAsync(null);
        var (older, newer) = (pending.Dequeue(), pending.Dequeue());
        newer.SetResult([new DbQueryStat { SqlText = "newer" }]);
        await second;
        older.SetResult([new DbQueryStat { SqlText = "older" }]);
        await first;

        Assert.Equal(["newer"], vm.TopQueries.Select(q => q.SqlText));
        Assert.False(vm.IsLoadingTopQueries);
    }

    private static DbIndex Index(string name) => new() { Schema = "dbo", Table = "t", Name = name };

    internal static DatabaseSession Session(IDatabaseProvider provider, string providerKey = "Fake") =>
        new(new ConnectionProfile { ProviderKey = providerKey, Name = "fake" }, null!, provider, "1", new MetadataSnapshot
        {
            Objects = [], Columns = [], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        });

    /// <summary>An <see cref="IDatabaseProvider"/> that answers each method from a table of handlers.</summary>
    public class ScriptedProvider : DispatchProxy
    {
        private Dictionary<string, Func<object?[], object?>> _handlers = [];
        private string _key = "Fake";

        public static IDatabaseProvider Create(Dictionary<string, Func<object?[], object?>> handlers, string providerKey = "Fake")
        {
            var provider = Create<IDatabaseProvider, ScriptedProvider>();
            var fake = (ScriptedProvider)(object)provider;
            fake._handlers = handlers;
            fake._key = providerKey;
            return provider;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ProviderKey" => _key,
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            var name when _handlers.TryGetValue(name, out var handler) => handler(args ?? []),
            var name => throw new NotSupportedException(name)
        };
    }
}
