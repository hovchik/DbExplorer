namespace DbExplorer.Application;

public sealed class AppPaths
{
    public AppPaths()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbExplorer");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDirectory);
    }

    public string Root { get; }
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string ConnectionsFile => Path.Combine(Root, "connections.json");
}
