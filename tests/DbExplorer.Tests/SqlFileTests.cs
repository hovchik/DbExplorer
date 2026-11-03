using System.Text;
using Avalonia.Headless.XUnit;
using DbExplorer.Application;
using DbExplorer.Application.Query;
using DbExplorer.Desktop;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Tests;

/// <summary>.sql files: encodings kept on save, files changed by other programs, the Recent list, command-line files
/// and the channel that hands them to the running window.</summary>
public class SqlFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-sqlfile-" + Guid.NewGuid().ToString("N"));

    public SqlFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_root, name);

    private const string Sample = "-- Привет café\r\nSELECT 'ü€' AS x;\r\n";

    [Theory]
    [InlineData(SqlFileEncodings.Utf8)]
    [InlineData(SqlFileEncodings.Utf8Bom)]
    [InlineData(SqlFileEncodings.Utf16Le)]
    [InlineData(SqlFileEncodings.Utf16Be)]
    [InlineData(SqlFileEncodings.Utf32Le)]
    public void Each_encoding_round_trips_byte_for_byte(string encoding)
    {
        var (bytes, written) = SqlFile.Encode(Sample, encoding);
        Assert.Equal(encoding, written);
        var (text, detected) = SqlFile.Decode(bytes);
        Assert.Equal(Sample, text);
        Assert.Equal(encoding, detected);
        Assert.Equal(bytes, SqlFile.Encode(text, detected).Bytes);
    }

    [Fact]
    public void Ssms_unicode_files_are_utf16_with_bom()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("SELECT 1;")).ToArray();
        Assert.Equal(("SELECT 1;", SqlFileEncodings.Utf16Le), SqlFile.Decode(bytes));
    }

    [Fact]
    public void Ansi_files_are_read_as_windows_1252_and_saved_back_the_same()
    {
        // "café" and "€" in Windows-1252: not valid UTF-8.
        byte[] bytes = [.. "SELECT 'caf"u8, 0xE9, (byte)'\'', (byte)' ', 0x80, (byte)';'];
        var (text, encoding) = SqlFile.Decode(bytes);
        Assert.Equal(SqlFileEncodings.Windows1252, encoding);
        Assert.Equal("SELECT 'café' €;", text);
        Assert.Equal(bytes, SqlFile.Encode(text, encoding).Bytes);
    }

    [Fact]
    public void Text_that_does_not_fit_the_old_encoding_is_saved_as_utf8_with_bom()
    {
        var (bytes, encoding) = SqlFile.Encode("SELECT '🙂';", SqlFileEncodings.Windows1252);
        Assert.Equal(SqlFileEncodings.Utf8Bom, encoding);
        Assert.Equal(("SELECT '🙂';", SqlFileEncodings.Utf8Bom), SqlFile.Decode(bytes));
    }

    [Fact]
    public void Binary_files_are_refused()
    {
        var ex = Assert.Throws<SqlFileException>(() => SqlFile.Decode([0x4D, 0x5A, 0x00, 0x01, 0x02], "tool.exe"));
        Assert.Contains("tool.exe does not look like a text file", ex.Message);
    }

    [Fact]
    public void Empty_and_unknown_encodings_are_utf8()
    {
        Assert.Equal(("", SqlFileEncodings.Utf8), SqlFile.Decode([]));
        Assert.Equal(SqlFileEncodings.Utf8, SqlFileEncodings.Normalize(null));
        Assert.Equal(SqlFileEncodings.Utf8, SqlFileEncodings.Normalize("ebcdic"));
        Assert.Equal("UTF-16 LE", SqlFileEncodings.Label(SqlFileEncodings.Utf16Le));
    }

    [Fact]
    public async Task Huge_files_are_refused_before_reading()
    {
        var path = PathOf("dump.sql");
        await using (var fs = File.Create(path)) fs.SetLength(SqlFile.MaxBytes + 1);
        var ex = await Assert.ThrowsAsync<SqlFileException>(() => SqlFile.ReadAsync(path));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public async Task Write_keeps_the_encoding_and_returns_the_new_stamp()
    {
        var path = PathOf("a.sql");
        var write = await SqlFile.WriteAsync(path, "SELECT 1;", SqlFileEncodings.Utf16Le);
        Assert.False(write.EncodingChanged);
        Assert.Equal(FileStamp.Of(path), write.Stamp);
        var read = await SqlFile.ReadAsync(path);
        Assert.Equal(("SELECT 1;", SqlFileEncodings.Utf16Le), (read.Text, read.Encoding));
        Assert.Equal([0xFF, 0xFE], (await File.ReadAllBytesAsync(path))[..2]);
        Assert.Null(FileStamp.Of(PathOf("missing.sql")));
    }

    // ----- Recent files -----

    [Fact]
    public void Recent_list_puts_the_newest_first_once_and_stays_short()
    {
        var list = new List<string>();
        for (var i = 0; i < RecentFiles.MaxFiles + 5; i++) list = RecentFiles.Push(list, PathOf($"{i}.sql"));
        Assert.Equal(RecentFiles.MaxFiles, list.Count);
        Assert.Equal(PathOf($"{RecentFiles.MaxFiles + 4}.sql"), list[0]);

        list = RecentFiles.Push(list, PathOf("10.sql"));
        Assert.Equal(PathOf("10.sql"), list[0]);
        Assert.Single(list, p => p == PathOf("10.sql"));
    }

    [Fact]
    public async Task Recent_list_is_saved_and_a_damaged_file_reads_as_empty()
    {
        var paths = new AppPaths(PathOf("data"));
        var store = new RecentFiles(paths);
        await store.AddAsync(PathOf("a.sql"));
        await store.AddAsync(PathOf("b.sql"));
        Assert.Equal([PathOf("b.sql"), PathOf("a.sql")], await new RecentFiles(paths).LoadAsync());
        Assert.Equal([PathOf("a.sql")], await store.RemoveAsync(PathOf("b.sql")));

        await File.WriteAllTextAsync(Path.Combine(paths.Root, "recent-files.json"), "{ not json");
        Assert.Empty(await store.LoadAsync());
    }

    // ----- Command line -----

    [Fact]
    public void Command_line_files_are_full_paths_without_options_or_repeats()
    {
        var files = FileArguments.Parse(["a.sql", "--software-rendering", "", Path.Combine("sub", "b.sql"), "a.sql", "\"c.sql\""], _root);
        Assert.Equal([PathOf("a.sql"), Path.Combine(_root, "sub", "b.sql"), PathOf("c.sql")], files);
    }

    // ----- Files sent from a second start -----

    [Fact]
    public async Task A_second_start_hands_its_files_to_the_first()
    {
        var name = "dbx-test-" + Guid.NewGuid().ToString("N")[..12];
        using var first = new FileOpenChannel(name);
        Assert.True(first.TryBecomePrimary()); // on the test's thread, which lives until the test ends
        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        first.Listen(paths => received.TrySetResult(paths));

        // Another thread: a mutex is re-entrant on the thread that owns it, and the second start is another process.
        using var second = new FileOpenChannel(name);
        var secondIsPrimary = true;
        var thread = new Thread(() => secondIsPrimary = second.TryBecomePrimary());
        thread.Start();
        thread.Join();
        Assert.False(secondIsPrimary);
        Assert.True(await second.TrySendAsync([PathOf("x.sql"), PathOf("y y.sql")], TimeSpan.FromSeconds(5)));
        var paths = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([PathOf("x.sql"), PathOf("y y.sql")], paths);
    }

    [Fact]
    public async Task With_nobody_listening_the_send_gives_up()
    {
        using var lone = new FileOpenChannel("dbx-test-" + Guid.NewGuid().ToString("N")[..12]);
        Assert.False(await lone.TrySendAsync([PathOf("x.sql")], TimeSpan.FromMilliseconds(300)));
    }

    // ----- The Query tabs -----

    private (ServiceProvider Provider, QueryWorkspaceViewModel Workspace) Workspace()
    {
        var services = App.ConfigureServices();
        services.AddSingleton(new AppPaths(PathOf("data")));
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<QueryWorkspaceViewModel>());
    }

    [AvaloniaFact]
    public async Task Opening_a_file_twice_shows_the_open_tab_and_saving_keeps_its_encoding()
    {
        var (provider, workspace) = Workspace();
        await using var _ = provider;
        var path = PathOf("orders.sql");
        await File.WriteAllBytesAsync(path, SqlFile.Encode("SELECT 1;\r\n", SqlFileEncodings.Utf16Le).Bytes);

        await workspace.OpenPathAsync(path);
        var tab = Assert.Single(workspace.Documents); // the empty scratch tab was reused
        Assert.Equal("orders.sql", tab.Title);
        Assert.Equal(SqlFileEncodings.Utf16Le, tab.FileEncoding);

        workspace.NewTab();
        await workspace.OpenPathAsync(path);
        Assert.Equal(2, workspace.Documents.Count);
        Assert.Same(tab, workspace.SelectedDocument);
        Assert.Equal(path, Assert.Single(workspace.RecentFiles).Path);

        tab.Sql = "SELECT 2;\r\n";
        Assert.True(await workspace.SaveAsync(tab, path, "orders.sql"));
        Assert.Equal(SqlFile.Encode("SELECT 2;\r\n", SqlFileEncodings.Utf16Le).Bytes, await File.ReadAllBytesAsync(path));
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task A_file_changed_on_disk_reloads_a_clean_tab_and_warns_a_tab_with_edits()
    {
        var (provider, workspace) = Workspace();
        await using var _ = provider;
        var path = PathOf("report.sql");
        await File.WriteAllTextAsync(path, "SELECT 1;");
        await workspace.OpenPathAsync(path);
        var tab = workspace.SelectedDocument!;

        await File.WriteAllTextAsync(path, "SELECT 'changed elsewhere';");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.True(await tab.CheckDiskAsync());
        Assert.Equal("SELECT 'changed elsewhere';", tab.Sql);
        Assert.False(tab.IsDirty);
        Assert.False(tab.HasDiskNotice);

        tab.Document.Text = "SELECT 'my edit';"; // typed in the editor
        await File.WriteAllTextAsync(path, "SELECT 'second change';");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        Assert.False(await tab.CheckDiskAsync());
        Assert.Equal("SELECT 'my edit';", tab.Sql);
        Assert.True(tab.HasDiskNotice);
        Assert.True(tab.CanReloadFromDisk);

        tab.KeepMineCommand.Execute(null);
        Assert.False(tab.HasDiskNotice);
        Assert.False(await tab.CheckDiskAsync());
        Assert.False(tab.HasDiskNotice);

        File.Delete(path);
        await tab.CheckDiskAsync();
        Assert.Contains("was deleted or moved", tab.DiskNotice);
        Assert.False(tab.CanReloadFromDisk);
    }

    [AvaloniaFact]
    public async Task Restored_tabs_keep_the_encoding_and_reload_files_changed_while_closed()
    {
        var path = PathOf("nightly.sql");
        await File.WriteAllBytesAsync(path, SqlFile.Encode("SELECT 1;", SqlFileEncodings.Utf8Bom).Bytes);
        var (provider, workspace) = Workspace();
        await using (provider)
        {
            await workspace.OpenPathAsync(path);
            Assert.True(await workspace.SaveTabsAsync());
        }

        await File.WriteAllBytesAsync(path, SqlFile.Encode("SELECT 2;", SqlFileEncodings.Utf8Bom).Bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var (provider2, restored) = Workspace();
        await using var _ = provider2;
        await restored.RestoreTabsAsync();
        var tab = Assert.Single(restored.Documents);
        Assert.Equal(SqlFileEncodings.Utf8Bom, tab.FileEncoding);
        Assert.Equal(path, Assert.Single(restored.RecentFiles).Path);
        await restored.CheckFilesOnDiskAsync();
        Assert.Equal("SELECT 2;", tab.Sql);
        Assert.False(tab.IsDirty);
    }
}
