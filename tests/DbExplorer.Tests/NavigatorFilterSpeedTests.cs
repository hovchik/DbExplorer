using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;
using Xunit.Abstractions;

namespace DbExplorer.Tests;

[Collection(nameof(HeadlessUiCollection))]
public class NavigatorFilterSpeedTests(ITestOutputHelper output)
{
    internal static DatabaseSession BigSession(string name, int databases, int tablesPerDb, int columnsPerTable)
    {
        var objects = new List<DbObject>();
        var columns = new List<DbColumn>();
        var words = new[] { "Account", "Order", "Customer", "Invoice", "Payment", "Ledger", "Product", "Audit" };
        for (var d = 0; d < databases; d++)
        for (var t = 0; t < tablesPerDb; t++)
        {
            var db = $"{name}Db{d}";
            var table = $"{words[t % words.Length]}{t:00000}";
            var schema = $"s{t % 4}";
            objects.Add(new DbObject { Database = db, Schema = schema, Name = table, Type = t % 5 == 0 ? DbObjectType.View : DbObjectType.Table });
            for (var c = 0; c < columnsPerTable; c++)
                columns.Add(new DbColumn { Database = db, Schema = schema, Table = table, Name = $"c{c}", Ordinal = c + 1, DataType = "int", BaseType = "int" });
        }
        var snapshot = new MetadataSnapshot
        {
            Objects = objects, Columns = columns, Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        };
        return new DatabaseSession(new ConnectionProfile { Name = name, ProviderKey = SqlDialect.SqlServerKey, Database = $"{name}Db0" },
            null!, DesktopTabTests.ScriptedProvider.Create(new(), SqlDialect.SqlServerKey), "1", snapshot);
    }

    /// <summary>Runs the dispatcher until <paramref name="task"/> is done and the window is laid out; returns how long
    /// the UI thread itself was busy (not waiting for the pause or the worker).</summary>
    internal static double Pump(Task task, Window window)
    {
        var busy = 0.0;
        while (true)
        {
            var sw = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            busy += sw.Elapsed.TotalMilliseconds;
            if (task.IsCompleted) return busy;
            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// A large tree (two open connections, 8,000 tables): keys typed into the filter cost almost nothing on the UI
    /// thread, the search runs once after the pause, and the result goes into the tree in short steps. Before the
    /// change every key rebuilt the whole result on the UI thread (about a second per key on a tree this size, and
    /// several seconds to clear the box). Timings are printed; the limits are loose so a slow CI machine passes.
    /// </summary>
    [AvaloniaFact]
    public void Typing_in_the_filter_of_a_large_tree_never_blocks_for_long()
    {
        var open = new OpenConnections();
        var sessions = Enumerable.Range(0, 2).Select(i => BigSession($"C{i}", 2, 2000, 5)).ToList();
        var vm = new NavigatorViewModel(null!, open) { Profiles = [.. sessions.Select(s => s.Profile)] };
        foreach (var s in sessions) open.Add(s);
        vm.Attach(sessions[0]);
        var view = new NavigatorView { DataContext = vm };
        var window = new Window { Width = 320, Height = 900, Content = view };
        window.Show();
        Pump(vm.FilterApplied, window);

        var steps = new TimedContext(SynchronizationContext.Current!, window);
        SynchronizationContext.SetSynchronizationContext(steps);
        try
        {
            // Typing quickly: each key only restarts the pause.
            var keys = new List<double>();
            foreach (var text in new[] { "a", "ac", "acc", "acco", "accou", "account" })
            {
                var sw = Stopwatch.StartNew();
                vm.FilterText = text;
                Dispatcher.UIThread.RunJobs();
                keys.Add(sw.Elapsed.TotalMilliseconds);
            }
            output.WriteLine("keys: " + string.Join(", ", keys.Select(k => $"{k:F1} ms")));
            Assert.All(keys, k => Assert.True(k < 150, $"a key took {k:F0} ms"));

            foreach (var text in new[] { "account", "account0", "accoun", "order", "" })
            {
                steps.Steps.Clear();
                var sw = Stopwatch.StartNew();
                vm.FilterText = text;
                var setter = sw.Elapsed.TotalMilliseconds;
                var busy = Pump(vm.FilterApplied, window);
                var longest = steps.Steps.Append(setter).Max();
                output.WriteLine($"'{text}': shown after {sw.Elapsed.TotalMilliseconds:F0} ms, UI thread busy {busy:F0} ms in " +
                                 $"{steps.Steps.Count} steps, longest {longest:F0} ms; {vm.FilterSummary}");
                Assert.True(longest < 1500, $"'{text}' blocked the UI thread for {longest:F0} ms");
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(steps.Inner);
        }

        // A result put in over several steps is complete: every match is shown, under its connection.
        vm.FilterText = "order01";
        Pump(vm.FilterApplied, window);
        var expected = sessions.SelectMany(s => s.Snapshot.Objects).Where(o => o.Name.Contains("order01", StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Database + "." + o.Name).Order().ToList();
        var shown = vm.Roots.SelectMany(r => r.SelfAndDescendants()).Where(n => n.Kind == NavigatorNodeKind.Object)
            .Select(n => n.Object!.Database + "." + n.Title).Order().ToList();
        Assert.InRange(expected.Count, 41, NavigatorViewModel.MaxMatches);
        Assert.Equal($"{expected.Count} matches", vm.FilterSummary);
        Assert.Equal(expected, shown);
        window.Close();
    }

    /// <summary>Times every job posted through it, with the layout it causes.</summary>
    private sealed class TimedContext(SynchronizationContext inner, Window window) : SynchronizationContext
    {
        public SynchronizationContext Inner => inner;
        public readonly List<double> Steps = [];

        public override void Post(SendOrPostCallback d, object? state) => inner.Post(s =>
        {
            var sw = Stopwatch.StartNew();
            var previous = Current;
            SetSynchronizationContext(this);
            d(s);
            window.UpdateLayout();
            SetSynchronizationContext(previous);
            Steps.Add(sw.Elapsed.TotalMilliseconds);
        }, state);

        public override void Send(SendOrPostCallback d, object? state) => inner.Send(d, state);
        public override SynchronizationContext CreateCopy() => this;
    }
}
