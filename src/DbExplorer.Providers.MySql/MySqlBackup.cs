using System.Globalization;
using System.Text;
using DbExplorer.Core.Backup;
using DbExplorer.Core.Connections;
using MySqlConnector;

namespace DbExplorer.Providers.MySql;

/// <summary>
/// MySQL and MariaDB backups with the client tools, run on this computer: mysqldump (mariadb-dump on MariaDB) writes one
/// SQL script with routines, triggers and events, read in one consistent snapshot (--single-transaction). The script
/// does not name its database, so it restores into whichever one is picked; the mysql client runs it. The password goes
/// through a temporary option file only this user can read, never the command line.
/// </summary>
public static class MySqlBackup
{
    public static IReadOnlyList<string> InstallFolders()
    {
        if (OperatingSystem.IsWindows())
            return
            [
                @"%ProgramFiles%\MySQL\MySQL Server *\bin", @"%ProgramFiles%\MariaDB *\bin",
                @"%ProgramFiles%\MySQL\MySQL Workbench *", @"%ProgramFiles%\MySQL\MySQL Shell *\bin",
                @"%ProgramFiles(x86)%\MySQL\MySQL Server *\bin"
            ];
        if (OperatingSystem.IsMacOS())
            return
            [
                "/usr/local/mysql/bin", "/opt/homebrew/opt/mysql-client/bin", "/opt/homebrew/opt/mysql/bin",
                "/opt/homebrew/opt/mariadb/bin", "/usr/local/opt/mysql-client/bin", "/usr/local/opt/mariadb/bin",
                "/Applications/MySQLWorkbench.app/Contents/MacOS"
            ];
        return [];
    }

    /// <summary>The dump and client tools in the order to try: MariaDB's own for a MariaDB server.</summary>
    public static (string[] Dump, string[] Client) ToolNames(bool mariaDb) => mariaDb
        ? (["mariadb-dump", "mysqldump"], ["mariadb", "mysql"])
        : (["mysqldump", "mariadb-dump"], ["mysql", "mariadb"]);

    public static bool IsMariaDbTool(ClientTool tool, string? versionLine = null) =>
        tool.Name.StartsWith("mariadb", StringComparison.OrdinalIgnoreCase) ||
        versionLine?.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The first dump tool found, in <see cref="ToolNames"/> order within each folder, best folder first;
    /// and its client from the same folder.</summary>
    public static async Task<(ClientTool? Dump, string? Client, bool MariaDbTools)> FindAsync(string? toolFolder, bool mariaDb, CancellationToken ct)
    {
        var (dumpNames, clientNames) = ToolNames(mariaDb);
        foreach (var folder in ClientTools.CandidateFolders(toolFolder, InstallFolders()))
        foreach (var name in dumpNames)
        {
            if (ClientTools.ExecutableIn(folder, name) is not { } exe) continue;
            var lines = new List<string>();
            ClientToolResult result;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                result = await ClientTools.RunAsync(exe, ["--version"], null, lines.Add, null, null, timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                continue;
            }
            var text = string.Join(" ", lines);
            if (result.ExitCode != 0 || ClientTools.ParseVersion(text) is not { } v) continue;
            var tool = new ClientTool(exe, v.Version, v.Text);
            var client = clientNames.Select(c => ClientTools.ExecutableIn(folder, c)).FirstOrDefault(c => c is not null);
            return (tool, client, IsMariaDbTool(tool, text));
        }
        return (null, null, false);
    }

    public static async Task<BackupSupport> GetSupportAsync(bool mariaDb, Version serverVersion, string? toolFolder, CancellationToken ct)
    {
        var engine = mariaDb ? "MariaDB" : "MySQL";
        var (dump, client, _) = await FindAsync(toolFolder, mariaDb, ct);
        var support = new BackupSupport
        {
            Location = BackupFileLocation.ThisComputer,
            Formats = [BackupFormat.PlainSql],
            SupportsSchemaOnly = true,
            RestoreOverExisting = "Tables in the backup are dropped and created again; other tables in the database are kept.",
            DefaultFolder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs ? docs : Path.GetTempPath()
        };
        if (dump is null)
            return support with
            {
                Problem = $"{(mariaDb ? "mariadb-dump or mysqldump" : "mysqldump")} was not found on this computer.",
                Setup = $"Install the {engine} client tools ({(mariaDb ? "the MariaDB installer" : "MySQL Installer's \"MySQL Server\" or \"MySQL Shell\", or MySQL Workbench")}), " +
                        "or pick the folder that has them."
            };
        return support with
        {
            IsAvailable = true,
            Tool = dump + (client is null ? " (no mysql client next to it, so restores can't run)" : ""),
            ToolFolder = dump.Folder
        };
    }

    /// <summary>A value in an option file: quoted, with backslashes and quotes escaped.</summary>
    public static string OptionValue(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The [client] section passed with --defaults-extra-file: login and TLS settings, in the dialect of the
    /// tool that reads it (MySQL's ssl-mode or MariaDB's ssl options).</summary>
    public static string OptionFile(ConnectionProfile p, bool mariaDbTools)
    {
        var sb = new StringBuilder("[client]\n");
        sb.Append("host=").Append(OptionValue(p.Host)).Append('\n');
        sb.Append("port=").Append((p.Port ?? 3306).ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (!string.IsNullOrEmpty(p.UserName)) sb.Append("user=").Append(OptionValue(p.UserName!)).Append('\n');
        if (!string.IsNullOrEmpty(p.Password)) sb.Append("password=").Append(OptionValue(p.Password!)).Append('\n');
        var verify = p.Encrypt && !p.TrustServerCertificate;
        if (mariaDbTools)
        {
            if (p.Encrypt) sb.Append("ssl\n");
            // MariaDB 11.4+ clients check certificates by default; follow the connection's own setting instead.
            sb.Append(verify ? "ssl-verify-server-cert\n" : "skip-ssl-verify-server-cert\n");
        }
        else
        {
            sb.Append("ssl-mode=").Append(!p.Encrypt ? "PREFERRED" : !verify ? "REQUIRED" : p.CertificateHostName is null ? "VERIFY_IDENTITY" : "VERIFY_CA").Append('\n');
            if (ConnectionSecurity.AllowMySqlPublicKeyRetrieval(p)) sb.Append("get-server-public-key\n");
        }
        sb.Append("default-character-set=utf8mb4\n");
        return sb.ToString();
    }

    public static IReadOnlyList<string> DumpArguments(string optionFile, BackupRequest r, bool mariaDbTools, bool mariaDbServer)
    {
        var args = new List<string>
        {
            "--defaults-extra-file=" + optionFile,
            "--single-transaction", "--routines", "--triggers", "--events", "--hex-blob",
            // Tablespaces need the PROCESS privilege and are rarely wanted in a copy.
            "--no-tablespaces", "--verbose", "--result-file=" + r.Path
        };
        if (!mariaDbTools)
        {
            // A dump without GTID statements restores on any server; MySQL 8's dump asks MariaDB for column
            // statistics it doesn't have.
            args.Add("--set-gtid-purged=OFF");
            if (mariaDbServer) args.Add("--column-statistics=0");
        }
        if (r.SchemaOnly) args.Add("--no-data");
        args.Add(r.Database);
        return args;
    }

    public static IReadOnlyList<string> ClientArguments(string optionFile, string database) =>
        ["--defaults-extra-file=" + optionFile, "--database=" + database];

    public static async Task BackupAsync(ConnectionProfile p, bool mariaDb, BackupRequest r, string? toolFolder,
        IProgress<BackupProgress> progress, CancellationToken ct)
    {
        var (dump, _, mariaDbTools) = await FindAsync(toolFolder, mariaDb, ct);
        if (dump is null) throw new BackupException("mysqldump was not found on this computer. Install the client tools, or pick the folder that has them.");
        using var options = ClientTools.WritePrivateFile(OptionFile(p, mariaDbTools));
        var args = DumpArguments(options.Path, r, mariaDbTools, mariaDb);
        progress.Report(new BackupProgress(Shown(dump.Name, args)));
        var tables = 0;
        var result = await ClientTools.RunAsync(dump.Path, args, null, line =>
        {
            if (line.StartsWith("-- Sending SELECT query", StringComparison.Ordinal) ||
                line.StartsWith("-- Retrieving table structure", StringComparison.Ordinal) && r.SchemaOnly) tables++;
            progress.Report(new BackupProgress(line));
        }, null, null, ct);
        if (result.ExitCode != 0)
        {
            try { File.Delete(r.Path); } catch (Exception) { /* left for the user */ }
            throw new BackupException($"{dump.Name} stopped with an error: " + LastError(result), result.Output);
        }
        progress.Report(new BackupProgress(r.SchemaOnly ? "Backup finished (schema only)." : $"Backup finished: {tables:N0} table(s) with their rows.", 100));
    }

    /// <summary>The command line as shown to the user: the option file is named, its contents (the password) never.</summary>
    public static string Shown(string tool, IEnumerable<string> args) =>
        tool + " " + string.Join(" ", args.Select(a => a.StartsWith("--defaults-extra-file=", StringComparison.Ordinal)
            ? "--defaults-extra-file=<login options>"
            : a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a));

    internal static string LastError(ClientToolResult result) =>
        (result.LastLines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ??
         result.LastLines.LastOrDefault() ?? $"exit code {result.ExitCode}").Trim();

    public static BackupFileInfo Inspect(string path)
    {
        if (!File.Exists(path)) throw new BackupException("The file does not exist: " + path);
        var head = new List<string>();
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            while (head.Count < 300 && reader.ReadLine() is { } line) head.Add(line);
        return ParseHead(head, Tail(path)) with { SizeBytes = new FileInfo(path).Length };
    }

    private static string Tail(string path)
    {
        using var f = File.OpenRead(path);
        var length = (int)Math.Min(f.Length, 512);
        f.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        f.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer);
    }

    public static BackupFileInfo ParseHead(IReadOnlyList<string> head, string tail)
    {
        string? After(string prefix) => head.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) is { } l ? l[prefix.Length..].Trim() : null;
        var first = head.FirstOrDefault() ?? "";
        var isDump = first.StartsWith("-- MySQL dump", StringComparison.Ordinal) || first.StartsWith("-- MariaDB dump", StringComparison.Ordinal);
        string? database = null;
        if (head.FirstOrDefault(l => l.StartsWith("-- Host:", StringComparison.Ordinal)) is { } host)
        {
            var at = host.IndexOf("Database:", StringComparison.Ordinal);
            if (at >= 0) database = host[(at + 9)..].Trim() is { Length: > 0 } d ? d : null;
        }
        var details = new List<string>();
        if (first.Length > 3 && isDump) details.Add("Made by " + first[3..].Trim());
        var switches = head.FirstOrDefault(l => l.StartsWith("USE `", StringComparison.Ordinal) || l.StartsWith("CREATE DATABASE", StringComparison.Ordinal));
        if (switches is not null)
            details.Add("The script picks its own database (" + switches.Trim().TrimEnd(';') + "), so it restores there whatever target you pick.");
        if (!isDump) details.Add("This doesn't look like a mysqldump script; it is run as SQL with the mysql client all the same.");
        var completed = tail.Contains("-- Dump completed", StringComparison.Ordinal);
        if (isDump && !completed) details.Add("The script doesn't end with \"-- Dump completed\", so it may be cut short.");
        DateTime? taken = null;
        var done = tail.IndexOf("-- Dump completed on", StringComparison.Ordinal);
        if (done >= 0 && DateTime.TryParse(tail[(done + 20)..].Split('\n')[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var t))
            taken = t;
        var server = After("-- Server version");
        return new BackupFileInfo
        {
            Kind = isDump ? (first.Contains("MariaDB", StringComparison.Ordinal) ? "mariadb-dump script" : "mysqldump script") : "SQL script",
            Format = BackupFormat.PlainSql,
            Database = database,
            TakenAt = taken,
            Server = server is null ? null : (server.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? "MariaDB " : "MySQL ") + server.Replace("-MariaDB", ""),
            Details = details
        };
    }

    public static async Task<string> DescribeRestoreAsync(bool mariaDb, RestoreRequest r, string? toolFolder, CancellationToken ct)
    {
        var (_, client, _) = await FindAsync(toolFolder, mariaDb, ct);
        var name = client is null ? "mysql" : Path.GetFileNameWithoutExtension(client);
        var sb = new StringBuilder();
        if (r.CreateDatabase) sb.AppendLine($"CREATE DATABASE {MySqlSql.Quote(r.Database)};");
        sb.Append(Shown(name, ClientArguments("x", r.Database))).Append(" < ").Append(r.Path.Any(char.IsWhiteSpace) ? "\"" + r.Path + "\"" : r.Path);
        return sb.ToString();
    }

    public static async Task RestoreAsync(ConnectionProfile p, bool mariaDb, RestoreRequest r, string? toolFolder,
        IProgress<BackupProgress> progress, CancellationToken ct)
    {
        if (!File.Exists(r.Path)) throw new BackupException("The file does not exist: " + r.Path);
        var (_, client, mariaDbTools) = await FindAsync(toolFolder, mariaDb, ct);
        if (client is null)
            throw new BackupException("The mysql client was not found on this computer. Install the client tools, or pick the folder that has them.");
        if (r.CreateDatabase)
        {
            progress.Report(new BackupProgress($"Creating database {r.Database}…"));
            await ExecuteAsync(p, $"CREATE DATABASE {MySqlSql.Quote(r.Database)}", ct);
        }

        try
        {
            using var options = ClientTools.WritePrivateFile(OptionFile(p, mariaDbTools));
            var args = ClientArguments(options.Path, r.Database);
            var name = Path.GetFileNameWithoutExtension(client);
            progress.Report(new BackupProgress(Shown(name, args) + " < " + r.Path));
            await using var file = File.OpenRead(r.Path);
            var total = Math.Max(1, file.Length);
            long last = -1;
            var fed = new Fed(bytes =>
            {
                var percent = 100 * bytes / total;
                if (percent == last) return;
                last = percent;
                progress.Report(new BackupProgress($"Sent {percent}% of the script", Math.Min(99, percent)));
            });
            var result = await ClientTools.RunAsync(client, args, null, line => progress.Report(new BackupProgress(line)), file, fed, ct);
            if (result.ExitCode != 0)
                throw new BackupException($"{name} stopped with an error. MySQL can't roll back table changes, so the tables before " +
                                          "that point were restored: " + LastError(result), result.Output);
        }
        catch (Exception) when (r.CreateDatabase)
        {
            // The database is new: drop it rather than leave a half-restored copy.
            try { await ExecuteAsync(p, $"DROP DATABASE IF EXISTS {MySqlSql.Quote(r.Database)}", CancellationToken.None); }
            catch (Exception) { /* the restore's error is the one to show */ }
            throw;
        }
        finally
        {
            MySqlConnection.ClearAllPools();
        }
        progress.Report(new BackupProgress($"Restore into {r.Database} finished.", 100));
    }

    private static async Task ExecuteAsync(ConnectionProfile p, string sql, CancellationToken ct)
    {
        await using var cn = new MySqlConnection(MySqlSql.BuildConnectionString(p, ""));
        await cn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class Fed(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
