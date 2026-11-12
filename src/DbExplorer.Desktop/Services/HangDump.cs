using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;

namespace DbExplorer.Desktop.Services;

/// <summary>
/// Saves a memory dump of the app while its UI thread is stuck, next to errors.log, so the exact place it is stuck can
/// be read from the dump (dotnet-dump analyze, clrstack) instead of guessed. At most one per run, only the newest is kept
/// (older hang-*.dmp files are deleted first), and DBEXPLORER_HANG_DUMP=off turns it off.
/// The dump is taken by the .NET runtime's own diagnostics channel, which keeps working while the UI thread is stuck.
/// </summary>
public static class HangDump
{
    public const string Prefix = "hang-";
    private static int _written;

    public static bool Enabled(string? environment) =>
        environment?.Trim().ToLowerInvariant() is not ("off" or "0" or "false" or "no");

    /// <summary>Called on the watchdog's timer thread once a hang has lasted long enough. Never throws.</summary>
    public static void Write(TimeSpan stuckFor)
    {
        if (!Enabled(Environment.GetEnvironmentVariable("DBEXPLORER_HANG_DUMP"))) return;
        if (Interlocked.Exchange(ref _written, 1) == 1) return;
        try
        {
            var folder = Path.GetDirectoryName(ErrorLog.FilePath)!;
            Directory.CreateDirectory(folder);
            DeleteOld(folder);
            var path = Path.Combine(folder, Prefix + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".dmp");
            new DiagnosticsClient(Environment.ProcessId).WriteDump(DumpType.WithHeap, path, logDumpGeneration: false);
            var size = File.Exists(path) ? $" ({new FileInfo(path).Length / (1024 * 1024):N0} MB)" : "";
            ErrorLog.Note("ui watchdog",
                $"Saved a memory dump of the stuck app after {stuckFor.TotalSeconds:0} s: {path}{size}. " +
                "It shows where the UI thread is stuck; send it along with this log. It holds the app's memory, including " +
                "query results that were open, so share it only with whoever fixes the app. Delete it any time.");
        }
        catch (Exception ex)
        {
            ErrorLog.Note("ui watchdog", "Could not save a memory dump of the stuck app: " + ex.Message);
        }
    }

    /// <summary>Deletes earlier hang dumps in <paramref name="folder"/>: each is hundreds of MB.</summary>
    public static void DeleteOld(string folder)
    {
        foreach (var old in Directory.EnumerateFiles(folder, Prefix + "*.dmp"))
        {
            try { File.Delete(old); }
            catch { }
        }
    }
}
