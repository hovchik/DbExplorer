namespace DbExplorer.Application;

public sealed class AppPaths
{
    public AppPaths()
        : this(DefaultRoot)
    {
    }

    /// <summary>
    /// <c>%LocalAppData%\DbExplorer</c>, <c>~/.local/share/DbExplorer</c> or <c>~/Library/Application Support/DbExplorer</c>.
    /// The folder is created when missing: without <see cref="Environment.SpecialFolderOption.Create"/> .NET returns ""
    /// for a folder that does not exist yet (a fresh Linux profile), and everything would land in the working directory.
    /// </summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "DbExplorer");

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
    public string KnownHostsFile => Path.Combine(Root, "ssh-known-hosts.json");
}
