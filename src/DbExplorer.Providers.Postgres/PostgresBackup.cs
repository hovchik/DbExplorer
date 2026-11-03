using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Core.Backup;
using DbExplorer.Core.Connections;
using Npgsql;

namespace DbExplorer.Providers.Postgres;

/// <summary>
/// PostgreSQL backups with the client tools, run on this computer: pg_dump writes a custom archive (default, compressed,
/// restored with pg_restore) or a plain SQL script (restored with psql). Restores run in one transaction and stop at the
/// first error, so a failed restore leaves the database as it was. pg_dump must be at least the server's major version.
/// </summary>
public static partial class PostgresBackup
{
    public static readonly string[] DumpTools = ["pg_dump", "pg_restore", "psql"];

    /// <summary>Where installers put the client tools, newest first within each.</summary>
    public static IReadOnlyList<string> InstallFolders()
    {
        if (OperatingSystem.IsWindows())
            return
            [
                @"%ProgramFiles%\PostgreSQL\*\bin", @"%ProgramFiles(x86)%\PostgreSQL\*\bin",
                @"%ProgramFiles%\pgAdmin 4\runtime", @"%LocalAppData%\Programs\pgAdmin 4\runtime",
                @"%ProgramFiles%\pgAdmin 4\v*\runtime"
            ];
        if (OperatingSystem.IsMacOS())
            return
            [
                "/Applications/Postgres.app/Contents/Versions/*/bin", "/opt/homebrew/opt/postgresql@*/bin",
                "/opt/homebrew/opt/libpq/bin", "/usr/local/opt/postgresql@*/bin", "/usr/local/opt/libpq/bin",
                "/Applications/pgAdmin 4.app/Contents/SharedSupport"
            ];
        return ["/usr/lib/postgresql/*/bin", "/usr/pgsql-*/bin"];
    }

    /// <summary>The tool folder to use: the one whose pg_dump has the highest version, preferring the user's pick
    /// when it is new enough for the server.</summary>
    public static async Task<(ClientTool? Dump, IReadOnlyList<ClientTool> All)> FindAsync(string? toolFolder, CancellationToken ct)
    {
        var folders = ClientTools.CandidateFolders(toolFolder, InstallFolders());
        var all = await ClientTools.FindAsync(folders, ["pg_dump"], ct);
        return (all.OrderByDescending(t => t.Version.Major).FirstOrDefault(), all);
    }

    public static async Task<BackupSupport> GetSupportAsync(ConnectionProfile profile, Version serverVersion, string? toolFolder, CancellationToken ct)
    {
        var (best, all) = await FindAsync(toolFolder, ct);
        var preferred = toolFolder is null ? null : all.FirstOrDefault(t => SameFolder(t.Folder, toolFolder));
        var dump = preferred is not null && preferred.Version.Major >= serverVersion.Major ? preferred : best;
        var support = new BackupSupport
        {
            Location = BackupFileLocation.ThisComputer,
            Formats = [BackupFormat.Custom, BackupFormat.PlainSql],
            SupportsSchemaOnly = true,
            SupportsSkipOwnership = true,
            SupportsCloseConnections = true,
            RestoreOverExisting = "Objects in the backup are dropped and created again; other objects in the database are kept.",
            DefaultFolder = DefaultLocalFolder()
        };
        if (dump is null)
            return support with
            {
                Problem = "pg_dump was not found on this computer.",
                Setup = $"Install the PostgreSQL {serverVersion.Major} client tools (the PostgreSQL installer's \"Command Line Tools\" " +
                        "or pgAdmin), or pick the folder that has pg_dump, pg_restore and psql."
            };
        if (dump.Version.Major < serverVersion.Major)
            return support with
            {
                Problem = $"pg_dump {dump.VersionText} cannot back up a PostgreSQL {serverVersion.Major} server (it needs version {serverVersion.Major} or newer).",
                Setup = $"Install the PostgreSQL {serverVersion.Major} client tools, or pick the folder of a newer pg_dump.",
                Tool = dump.ToString(), ToolFolder = dump.Folder
            };
        return support with { IsAvailable = true, Tool = dump.ToString(), ToolFolder = dump.Folder };
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string DefaultLocalFolder() =>
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs ? docs : Path.GetTempPath();

    /// <summary>libpq settings for the tools: the password and TLS mode go through the environment, never the command line.</summary>
    public static IReadOnlyDictionary<string, string?> ToolEnvironment(ConnectionProfile p)
    {
        var verify = p.Encrypt && !p.TrustServerCertificate;
        return new Dictionary<string, string?>
        {
            ["PGPASSWORD"] = p.IntegratedSecurity || string.IsNullOrEmpty(p.Password) ? null : p.Password,
            // Same rules as the app's own connections (PostgresSql.BuildConnectionString).
            ["PGSSLMODE"] = !p.Encrypt ? "prefer" : !verify ? "require" : p.CertificateHostName is null ? "verify-full" : "verify-ca",
            // Check the server certificate against the system's trusted roots, like the app does (libpq 16+).
            ["PGSSLROOTCERT"] = verify ? "system" : null,
            ["PGAPPNAME"] = "DbExplorer",
            ["PGCONNECT_TIMEOUT"] = "15",
            ["PGOPTIONS"] = p.ReadOnly ? "-c default_transaction_read_only=on" : null,
            // pg_dump's messages in English, so progress lines can be recognised.
            ["LC_MESSAGES"] = "C",
            ["PGCLIENTENCODING"] = "UTF8"
        };
    }

    /// <summary>--host, --port, --username and --dbname for one database. A name with "=" in it is passed as a
    /// connection string, since libpq would take it for one.</summary>
    public static List<string> ConnectionArguments(ConnectionProfile p, string database)
    {
        var args = new List<string> { "--host", p.Host, "--port", (p.Port ?? 5432).ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(p.UserName)) args.AddRange(["--username", p.UserName!]);
        args.AddRange(["--no-password", "--dbname", DatabaseArgument(database)]);
        return args;
    }

    public static string DatabaseArgument(string database) =>
        database.Contains('=') || database.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) && database.Contains("://")
            ? "dbname='" + database.Replace("\\", "\\\\").Replace("'", "\\'") + "'"
            : database;

    /// <summary>pg_dump's arguments. Plain scripts leave out owners and privileges so they run on any server; custom
    /// archives keep them and the restore decides.</summary>
    public static IReadOnlyList<string> DumpArguments(ConnectionProfile p, BackupRequest r)
    {
        var args = ConnectionArguments(p, r.Database);
        args.AddRange(["--format", r.Format == BackupFormat.Custom ? "custom" : "plain", "--file", r.Path, "--verbose"]);
        if (r.Format == BackupFormat.PlainSql) args.AddRange(["--no-owner", "--no-privileges"]);
        if (r.Format == BackupFormat.Custom && !r.Compress) args.Add("--compress=0");
        if (r.SchemaOnly) args.Add("--schema-only");
        return args;
    }

    public static IReadOnlyList<string> RestoreArguments(ConnectionProfile p, RestoreRequest r)
    {
        var args = ConnectionArguments(p, r.Database);
        args.AddRange(["--verbose", "--single-transaction", "--exit-on-error"]);
        if (!r.CreateDatabase) args.AddRange(["--clean", "--if-exists"]);
        if (r.SkipOwnership) args.AddRange(["--no-owner", "--no-privileges"]);
        args.Add(r.Path);
        return args;
    }

    /// <summary>psql runs a plain script from standard input, so progress can be counted in bytes.</summary>
    public static IReadOnlyList<string> ScriptArguments(ConnectionProfile p, RestoreRequest r)
    {
        var args = ConnectionArguments(p, r.Database);
        args.AddRange(["--quiet", "--no-psqlrc", "--set", "ON_ERROR_STOP=1", "--single-transaction", "--file", "-"]);
        return args;
    }

    /// <summary>A command line to show the user; arguments with spaces are quoted. Never holds the password.</summary>
    public static string CommandLine(string tool, IEnumerable<string> args) =>
        tool + " " + string.Join(" ", args.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));

    public static async Task BackupAsync(ConnectionProfile p, Version serverVersion, BackupRequest r, string? toolFolder,
        IProgress<BackupProgress> progress, CancellationToken ct)
    {
        var support = await GetSupportAsync(p, serverVersion, toolFolder, ct);
        if (!support.IsAvailable) throw new BackupException(support.Problem + " " + support.Setup);
        var exe = ClientTools.ExecutableIn(support.ToolFolder!, "pg_dump")!;
        var args = DumpArguments(p, r);
        progress.Report(new BackupProgress(CommandLine("pg_dump", args)));

        var tables = 0;
        var result = await ClientTools.RunAsync(exe, args, ToolEnvironment(p), line =>
        {
            if (line.Contains("dumping contents of table", StringComparison.Ordinal)) tables++;
            progress.Report(new BackupProgress(line));
        }, null, null, ct);
        if (result.ExitCode != 0)
        {
            TryDelete(r.Path);
            throw new BackupException("pg_dump stopped with an error: " + LastError(result), result.Output);
        }
        progress.Report(new BackupProgress(
            r.SchemaOnly ? "Backup finished (schema only)." : $"Backup finished: {tables:N0} table(s) with their rows.", 100));
    }

    public static async Task<BackupFileInfo> InspectAsync(string path, string? toolFolder, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new BackupException("The file does not exist: " + path);
        var size = new FileInfo(path).Length;
        if (IsArchive(path))
        {
            var (dump, _) = await FindAsync(toolFolder, ct);
            if (dump is null || ClientTools.ExecutableIn(dump.Folder, "pg_restore") is not { } exe)
                return new BackupFileInfo { Kind = "pg_dump archive", Format = BackupFormat.Custom, SizeBytes = size,
                    Details = ["pg_restore was not found, so the archive's contents can't be listed."] };
            var lines = new List<string>();
            var result = await ClientTools.RunAsync(exe, ["--list", path], null, lines.Add, null, null, ct);
            if (result.ExitCode != 0)
                throw new BackupException("pg_restore could not read the archive: " + LastError(result), result.Output);
            return ParseArchiveList(lines) with { SizeBytes = size };
        }

        var head = ReadHead(path, 200);
        if (head.Any(l => l.Contains("PostgreSQL database cluster dump", StringComparison.Ordinal)))
            throw new BackupException("This is a pg_dumpall script of a whole server (all databases and roles), not of one database. Run it with psql instead.");
        return ParseScriptHead(head) with { SizeBytes = size };
    }

    /// <summary>pg_dump custom archives start with "PGDMP"; tar archives have "ustar" at byte 257.</summary>
    public static bool IsArchive(string path)
    {
        using var f = File.OpenRead(path);
        var buffer = new byte[262];
        var read = f.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (read >= 5 && Encoding.ASCII.GetString(buffer, 0, 5) == "PGDMP") return true;
        return read >= 262 && Encoding.ASCII.GetString(buffer, 257, 5) == "ustar";
    }

    internal static List<string> ReadHead(string path, int lines)
    {
        var head = new List<string>();
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (head.Count < lines && reader.ReadLine() is { } line) head.Add(line);
        return head;
    }

    /// <summary>The header and table count of "pg_restore --list".</summary>
    public static BackupFileInfo ParseArchiveList(IReadOnlyList<string> lines)
    {
        string? Header(string key) => lines
            .Select(l => l.TrimStart(';').Trim())
            .FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal)) is { } l ? l[key.Length..].TrimStart(':').Trim() : null;

        var entries = lines.Where(l => l.Length > 0 && !l.StartsWith(';')).ToList();
        var tables = entries.Count(e => TocType(e) == "TABLE");
        var data = entries.Count(e => TocType(e) == "TABLE DATA");
        DateTime? taken = null;
        if (Header("Archive created at") is { } created &&
            DateTime.TryParse(Regex.Replace(created, @"\s+[A-Z]{2,5}$", ""), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var t))
            taken = t;
        var details = new List<string> { $"{tables:N0} table(s), {(data == 0 ? "no rows (schema only)" : $"rows of {data:N0} table(s)")}, {entries.Count:N0} item(s) in all" };
        if (Header("Dumped by pg_dump version") is { } by) details.Add("Made by pg_dump " + by);
        return new BackupFileInfo
        {
            Kind = "pg_dump " + (Header("Format")?.ToLowerInvariant() ?? "custom") + " archive",
            Format = BackupFormat.Custom,
            Database = Header("dbname"),
            TakenAt = taken,
            Server = Header("Dumped from database version") is { } v ? "PostgreSQL " + v : null,
            Details = details,
            EntryCount = entries.Count
        };
    }

    /// <summary>"215; 1259 16390 TABLE public orders postgres" → "TABLE"; "3401; 0 16390 TABLE DATA public orders postgres" → "TABLE DATA".</summary>
    private static string? TocType(string entry)
    {
        var m = TocEntry().Match(entry);
        if (!m.Success) return null;
        var rest = m.Groups["rest"].Value;
        return rest.StartsWith("TABLE DATA ", StringComparison.Ordinal) ? "TABLE DATA" : rest.Split(' ')[0];
    }

    [GeneratedRegex(@"^\d+;\s+\d+\s+\d+\s+(?<rest>.*)$")]
    private static partial Regex TocEntry();

    public static BackupFileInfo ParseScriptHead(IReadOnlyList<string> head)
    {
        string? After(string prefix) => head.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) is { } l ? l[prefix.Length..].Trim() : null;
        var details = new List<string>();
        if (After("-- Dumped by pg_dump version") is { } by) details.Add("Made by pg_dump " + by);
        var connects = head.FirstOrDefault(l => l.StartsWith("\\connect", StringComparison.Ordinal) || l.StartsWith("CREATE DATABASE", StringComparison.Ordinal));
        if (connects is not null)
            details.Add("The script creates or switches to its own database (" + connects.Trim() + "), so parts of it may not go into the database you pick.");
        var isDump = head.Any(l => l.Contains("PostgreSQL database dump", StringComparison.Ordinal));
        if (!isDump) details.Add("This doesn't look like a pg_dump script; it is run as SQL with psql all the same.");
        return new BackupFileInfo
        {
            Kind = isDump ? "pg_dump SQL script" : "SQL script",
            Format = BackupFormat.PlainSql,
            Server = After("-- Dumped from database version") is { } v ? "PostgreSQL " + v : null,
            Details = details
        };
    }

    public static async Task<string> DescribeRestoreAsync(ConnectionProfile p, RestoreRequest r, string? toolFolder, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (r.CreateDatabase) sb.AppendLine($"CREATE DATABASE {PostgresSql.Quote(r.Database)};");
        else if (r.CloseConnections)
            sb.AppendLine($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{r.Database.Replace("'", "''")}' AND pid <> pg_backend_pid();");
        var archive = File.Exists(r.Path) && IsArchive(r.Path);
        sb.Append(archive ? CommandLine("pg_restore", RestoreArguments(p, r)) : CommandLine("psql", ScriptArguments(p, r)) + " < " + Quoted(r.Path));
        await Task.CompletedTask;
        return sb.ToString();
    }

    private static string Quoted(string path) => path.Any(char.IsWhiteSpace) ? "\"" + path + "\"" : path;

    public static async Task RestoreAsync(ConnectionProfile p, RestoreRequest r, string? toolFolder,
        IProgress<BackupProgress> progress, CancellationToken ct)
    {
        if (!File.Exists(r.Path)) throw new BackupException("The file does not exist: " + r.Path);
        var archive = IsArchive(r.Path);
        var (dump, _) = await FindAsync(toolFolder, ct);
        var toolName = archive ? "pg_restore" : "psql";
        if (dump is null || ClientTools.ExecutableIn(dump.Folder, toolName) is not { } exe)
            throw new BackupException($"{toolName} was not found on this computer. Install the PostgreSQL client tools, or pick the folder that has them.");

        var entries = 0;
        if (archive)
        {
            var info = await InspectAsync(r.Path, dump.Folder, ct);
            entries = info.EntryCount;
        }

        await using (var admin = new NpgsqlConnection(PostgresSql.BuildConnectionString(p, PostgresSql.DefaultDatabase)))
        {
            await admin.OpenAsync(ct);
            if (r.CreateDatabase)
            {
                progress.Report(new BackupProgress($"Creating database {r.Database}…"));
                await using var create = new NpgsqlCommand($"CREATE DATABASE {PostgresSql.Quote(r.Database)}", admin);
                await create.ExecuteNonQueryAsync(ct);
            }
            else if (r.CloseConnections)
            {
                await using var kill = new NpgsqlCommand(
                    "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid()", admin);
                kill.Parameters.AddWithValue("db", r.Database);
                var closed = Convert.ToInt64(await kill.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
                progress.Report(new BackupProgress($"Disconnected {closed:N0} other session(s) from {r.Database}."));
            }
        }
        // Pooled connections to the database may have just been closed by the server.
        NpgsqlConnection.ClearAllPools();

        try
        {
            ClientToolResult result;
            if (archive)
            {
                var args = RestoreArguments(p, r);
                progress.Report(new BackupProgress(CommandLine("pg_restore", args)));
                var done = 0;
                result = await ClientTools.RunAsync(exe, args, ToolEnvironment(p), line =>
                {
                    double? percent = null;
                    if (entries > 0 && (line.StartsWith("pg_restore: creating ", StringComparison.Ordinal) ||
                                        line.StartsWith("pg_restore: processing data", StringComparison.Ordinal)))
                        percent = Math.Min(99, 100.0 * ++done / entries);
                    progress.Report(new BackupProgress(line, percent));
                }, null, null, ct);
            }
            else
            {
                var args = ScriptArguments(p, r);
                progress.Report(new BackupProgress(CommandLine("psql", args) + " < " + Quoted(r.Path)));
                await using var file = File.OpenRead(r.Path);
                var total = Math.Max(1, file.Length);
                long lastPercent = -1;
                var fed = new ImmediateProgress<long>(bytes =>
                {
                    var percent = 100 * bytes / total;
                    if (percent == lastPercent) return;
                    lastPercent = percent;
                    progress.Report(new BackupProgress($"Sent {percent}% of the script", Math.Min(99, percent)));
                });
                result = await ClientTools.RunAsync(exe, args, ToolEnvironment(p), line => progress.Report(new BackupProgress(line)), file, fed, ct);
            }

            if (result.ExitCode != 0)
                throw new BackupException(
                    $"{toolName} stopped with an error, and the transaction was rolled back: " + LastError(result), result.Output);
        }
        catch (Exception) when (r.CreateDatabase)
        {
            await DropQuietlyAsync(p, r.Database);
            throw;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
        progress.Report(new BackupProgress($"Restore into {r.Database} finished.", 100));
    }

    /// <summary>A database this restore created, dropped again after a failure (the restore ran in one transaction,
    /// so it is empty).</summary>
    private static async Task DropQuietlyAsync(ConnectionProfile p, string database)
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(PostgresSql.BuildConnectionString(p, PostgresSql.DefaultDatabase));
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {PostgresSql.Quote(database)}", admin);
            await drop.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // The restore's own error matters more; an empty database left behind is harmless.
        }
    }

    /// <summary>The tool's last error line ("pg_dump: error: …"), or its last line.</summary>
    internal static string LastError(ClientToolResult result)
    {
        var error = result.LastLines.LastOrDefault(l => l.Contains("error:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("ERROR:", StringComparison.Ordinal))
                    ?? result.LastLines.LastOrDefault() ?? $"exit code {result.ExitCode}";
        return error.Trim();
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { /* left for the user */ }
    }
}

/// <summary>An <see cref="IProgress{T}"/> that reports on the caller's thread right away (no SynchronizationContext post),
/// for counting inside a tool run.</summary>
internal sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
