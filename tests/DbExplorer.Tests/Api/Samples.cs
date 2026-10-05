namespace DbExplorer.Tests.Api;

internal static class Samples
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Api", "Samples", name));
}
