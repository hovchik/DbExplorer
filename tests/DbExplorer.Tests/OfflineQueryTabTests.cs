using System.Text.RegularExpressions;
using DbExplorer.Application;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>The Query tab with nothing connected: which tabs stay usable, where the window goes on disconnect and
/// connect, and what the editor suggests from the engine and the saved catalog.</summary>
public class OfflineQueryTabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-offline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static CompletionResult Complete(OfflineCatalog catalog, string sql, string? database = null) =>
        new SqlCompletionEngine(catalog.SnapshotFor(database), catalog.Quote, catalog.ProviderKey).Complete(sql, sql.Length, explicitRequest: true);

    [Fact]
    public void Query_comparer_and_er_model_work_offline_and_nothing_else_does()
    {
        var offline = Enum.GetValues<AppTab>().Where(t => !OfflineTabs.NeedsConnection(t)).ToList();
        Assert.Equal([AppTab.Query, AppTab.Comparer, AppTab.ErModel], offline);
    }

    [Theory]
    [InlineData(AppTab.Objects, AppTab.Query)]
    [InlineData(AppTab.Security, AppTab.Query)]
    [InlineData(AppTab.Query, null)]
    [InlineData(AppTab.Comparer, null)]
    [InlineData(AppTab.ErModel, null)]
    public void Disconnecting_moves_off_a_tab_that_needs_a_connection(AppTab current, AppTab? expected) =>
        Assert.Equal(expected, OfflineTabs.TabWhenOffline(current));

    [Fact]
    public void Connecting_goes_back_to_the_tab_left_unless_the_query_tab_was_used()
    {
        Assert.Equal(AppTab.Objects, OfflineTabs.TabOnConnect(AppTab.Objects, AppTab.Query, queryChanged: false));
        // They wrote something offline and now want to run it.
        Assert.Null(OfflineTabs.TabOnConnect(AppTab.Objects, AppTab.Query, queryChanged: true));
        // They moved on to another tab themselves.
        Assert.Null(OfflineTabs.TabOnConnect(AppTab.Objects, AppTab.ErModel, queryChanged: false));
        Assert.Null(OfflineTabs.TabOnConnect(null, AppTab.Query, queryChanged: false));
    }

    [Fact]
    public void Fingerprint_changes_with_any_script()
    {
        Assert.Equal(OfflineTabs.Fingerprint(["a", "b"]), OfflineTabs.Fingerprint(["a", "b"]));
        Assert.NotEqual(OfflineTabs.Fingerprint(["a", "b"]), OfflineTabs.Fingerprint(["a", "bc"]));
        Assert.NotEqual(OfflineTabs.Fingerprint(["a"]), OfflineTabs.Fingerprint(["a", ""]));
    }

    [Theory]
    [InlineData("SqlServer", "[Order]]s]")]
    [InlineData("Postgres", "\"Order]s\"")]
    [InlineData("MySql", "`Order]s`")]
    public void Identifiers_are_quoted_the_engines_way(string provider, string expected) =>
        Assert.Equal(expected, OfflineCatalog.Quote(provider, "Order]s"));

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("Postgres")]
    [InlineData("MySql")]
    public void Without_a_saved_catalog_keywords_and_ddl_templates_are_still_offered(string provider)
    {
        var catalog = new OfflineCatalog(provider, "Shop", Snapshot: null);

        Assert.Contains(Complete(catalog, "SEL").Items, i => i.Label == "SELECT");
        Assert.Contains(Complete(catalog, "CRE").Items, i => i.Kind == CompletionKind.Ddl && i.Label == "CREATE TABLE");
        Assert.DoesNotContain(Complete(catalog, "SELECT * FROM ").Items, i => i.Kind == CompletionKind.Table);
        Assert.Contains("no saved catalog yet", catalog.Describe(catalog.SnapshotFor(null)));
    }

    [Fact]
    public void A_saved_catalog_gives_table_and_column_names()
    {
        var catalog = new OfflineCatalog("SqlServer", "Shop", TestSnapshots.Shop());

        Assert.Contains(Complete(catalog, "SELECT * FROM ").Items, i => i.Kind == CompletionKind.Table && i.Label == "Customers");
        Assert.Contains(Complete(catalog, "SELECT * FROM dbo.Customers c WHERE c.").Items, i => i.Label == "Email");
        Assert.StartsWith("Offline · suggestions: 8 objects saved", catalog.Describe(catalog.SnapshotFor(null)));
    }

    [Fact]
    public void A_whole_server_catalog_is_narrowed_to_the_tabs_database()
    {
        var catalog = new OfflineCatalog("SqlServer", "Server", TestSnapshots.TwoDatabases());

        var billing = Complete(catalog, "SELECT * FROM ", "Billing").Items.Where(i => i.Kind == CompletionKind.Table).Select(i => i.InsertText).ToList();
        Assert.Contains("dbo.Invoices", billing);
        Assert.DoesNotContain("dbo.Customers", billing);
        // A database the catalog does not know: everything, rather than nothing.
        Assert.Same(catalog.Snapshot, catalog.SnapshotFor("Elsewhere"));
    }

    [Fact]
    public async Task The_catalog_saved_on_connect_is_read_back_for_the_same_connection()
    {
        var cache = new MetadataCache(new AppPaths(_root));
        var profile = new ConnectionProfile { ProviderKey = "Postgres", Host = "db", Port = 5432, Database = "shop", Name = "Shop" };
        await cache.SaveAsync(MetadataCache.CacheKey(profile), TestSnapshots.Shop());

        var loaded = await cache.TryLoadAsync(MetadataCache.CacheKey(profile));
        var other = await cache.TryLoadAsync(MetadataCache.CacheKey(new ConnectionProfile { ProviderKey = "Postgres", Host = "db", Database = "crm" }));

        Assert.NotNull(loaded);
        Assert.Contains(loaded.Objects, o => o.Name == "Customers");
        Assert.Null(other);
    }

    private static string Xaml(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "DbExplorer.Desktop", "Views", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbExplorer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Query_tab_item_is_never_disabled_and_run_buttons_follow_the_connection()
    {
        var main = Xaml("MainWindow.axaml");
        Assert.Matches(new Regex(@"<TabItem Header=""Query"">"), main);

        var query = Xaml("QueryView.axaml");
        Assert.Matches(new Regex(@"Content=""▶ Run""[^>]*IsEnabled=""\{Binding IsConnected\}"""), query);
        Assert.Matches(new Regex(@"Content=""Explain ▾""[^>]*IsEnabled=""\{Binding IsConnected\}"""), query);
        Assert.Contains(@"x:Name=""OfflineBar""", query);
    }
}
