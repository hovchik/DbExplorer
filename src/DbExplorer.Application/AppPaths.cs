namespace DbExplorer.Application;

public sealed class AppPaths
{
    public AppPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbExplorer"))
    {
    }

    /// <summary>Keeps every file under <paramref name="root"/> (tests use a temporary folder).</summary>
    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDirectory);
    }

    public string Root { get; }
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string ConnectionsFile => Path.Combine(Root, "connections.json");
}
