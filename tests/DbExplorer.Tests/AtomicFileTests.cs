using DbExplorer.Application;

namespace DbExplorer.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("dbx-atomic-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Writes_replace_the_file_and_leave_no_temporary_files()
    {
        var file = Path.Combine(_temp, "settings.json");

        AtomicFile.WriteAllText(file, "first");
        AtomicFile.WriteAllText(file, "second");

        Assert.Equal("second", File.ReadAllText(file));
        Assert.Equal([file], Directory.GetFiles(_temp));
    }
}
