using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Core.Backup;
using DbExplorer.Core.Connections;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Providers.SqlServer;

/// <summary>
/// SQL Server backups with BACKUP DATABASE and RESTORE DATABASE, which the server runs itself: the .bak file is read and
/// written by the SQL Server service on the server's own disks. Backups are COPY_ONLY with checksums by default (they
/// don't change the differential base of scheduled backups) and verified afterwards. Restores move the files next to
/// the target database's own files, or into the server's default data folders for a new database.
/// </summary>
public static partial class SqlServerBackup
{
    private const string AppName = "DbExplorer backup";

    /// <summary>What the server is and where it keeps files.</summary>
    public sealed record ServerFacts(int EngineEdition, string? BackupPath, string? DataPath, string? LogPath, string Version)
    {
        public bool IsAzureSqlDatabase => EngineEdition is 5 or 6 or 9;
        public bool IsManagedInstance => EngineEdition == 8;
        public bool IsExpress => EngineEdition == 4;
        public char Separator => (BackupPath ?? DataPath ?? "").StartsWith('/') ? '/' : '\\';
    }

    private static string ConnectionString(ConnectionProfile p) =>
        new SqlConnectionStringBuilder(SqlServerSql.BuildConnectionString(p, AppName, "master", forceReadWrite: true))
        {
            CommandTimeout = 0
        }.ConnectionString;

    private const string FactsSql = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS int),
               CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000)),
               CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)),
               CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000)),
               CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128));
        """;

    /// <summary>Before SQL Server 2019 the backup folder is not a server property: the folder of the last backup
    /// taken is the next best guess.</summary>
    private const string LastBackupFolderSql = """
        SELECT TOP (1) physical_device_name FROM msdb.dbo.backupmediafamily
        WHERE device_type = 2 ORDER BY media_set_id DESC;
        """;

    public static async Task<ServerFacts> ReadFactsAsync(ConnectionProfile p, CancellationToken ct)
    {
        await using var cn = new SqlConnection(ConnectionString(p));
        await cn.OpenAsync(ct);
        ServerFacts facts;
        await using (var cmd = new SqlCommand(FactsSql, cn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            facts = new ServerFacts(r.IsDBNull(0) ? 0 : r.GetInt32(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4) ?? "");
        }
        if (facts.BackupPath is null && !facts.IsAzureSqlDatabase)
        {
            try
            {
                await using var cmd = new SqlCommand(LastBackupFolderSql, cn);
                if (await cmd.ExecuteScalarAsync(ct) is string last && FolderOf(last) is { Length: > 0 } folder)
                    facts = facts with { BackupPath = folder };
            }
            catch (SqlException)
            {
                // No access to msdb: the user types a folder.
            }
        }
        return facts;
    }

    private static string? Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i) is { Length: > 0 } s ? s : null;

    private static string? FolderOf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut <= 0 ? null : path[..(cut + 1)];
    }

    public static BackupSupport Support(ServerFacts facts)
    {
        var support = new BackupSupport
        {
            Location = BackupFileLocation.Server,
            Tool = "BACKUP DATABASE and RESTORE DATABASE, run by the server",
            Formats = [BackupFormat.Custom],
            SupportsCopyOnly = true,
            SupportsCompression = !facts.IsExpress,
            SupportsVerify = true,
            SupportsCloseConnections = true,
            DefaultFolder = facts.BackupPath,
            PathSeparator = facts.Separator,
            RestoreOverExisting = "The whole database is replaced by the backup (RESTORE … WITH REPLACE)."
        };
        if (facts.IsAzureSqlDatabase)
            return support with
            {
                Problem = "Azure SQL Database does not take BACKUP DATABASE to a file.",
                Setup = "Use the Azure portal's automatic backups and point-in-time restore, or export a .bacpac with SqlPackage."
            };
        if (facts.IsManagedInstance)
            return support with
            {
                Problem = "Azure SQL Managed Instance backs up only to Azure Blob Storage (BACKUP … TO URL), not to a file.",
                Setup = "Run BACKUP DATABASE … TO URL from a Query tab with a credential for your storage account."
            };
        return support with { IsAvailable = true };
    }

    public static string Literal(string text) => "N'" + text.Replace("'", "''") + "'";

    public static string BackupScript(BackupRequest r, bool canCompress)
    {
        var with = new List<string>();
        if (r.CopyOnly) with.Add("COPY_ONLY");
        // FORMAT, INIT: the file holds this backup only, so a restore from it can't pick up an older one.
        with.AddRange(["FORMAT", "INIT", "CHECKSUM"]);
        if (canCompress) with.Add(r.Compress ? "COMPRESSION" : "NO_COMPRESSION");
        with.Add("NAME = " + Literal($"{r.Database} full backup ({DateTime.Now:yyyy-MM-dd HH:mm}, DbExplorer)"));
        with.Add("STATS = 5");
        return $"BACKUP DATABASE {SqlServerSql.Quote(r.Database)}\nTO DISK = {Literal(r.Path)}\nWITH {string.Join(", ", with)};";
    }

    public static string VerifyScript(string path) => $"RESTORE VERIFYONLY FROM DISK = {Literal(path)} WITH CHECKSUM;";

    public static async Task BackupAsync(ConnectionProfile p, BackupRequest r, IProgress<BackupProgress> progress, CancellationToken ct)
    {
        var facts = await ReadFactsAsync(p, ct);
        var support = Support(facts);
        if (!support.IsAvailable) throw new BackupException(support.Problem + " " + support.Setup);

        var script = BackupScript(r, support.SupportsCompression);
        progress.Report(new BackupProgress(script, 0));
        await RunAsync(p, script, progress, scale: r.Verify ? 0.8 : 1, offset: 0, ct);
        if (r.Verify)
        {
            var verify = VerifyScript(r.Path);
            progress.Report(new BackupProgress(verify, 80));
            await RunAsync(p, verify, progress, scale: 0.2, offset: 80, ct);
        }
        progress.Report(new BackupProgress($"Backup finished: {r.Path} on the server.", 100));
    }

    [GeneratedRegex(@"^\s*(\d{1,3}) percent (processed|complete)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PercentLine();

    /// <summary>The share done from a STATS message ("10 percent processed."), or null for other messages.</summary>
    public static double? PercentOf(string message) =>
        PercentLine().Match(message) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>Runs one script with its messages streamed as progress. FireInfoMessageEventOnUserErrors makes the
    /// STATS lines arrive while the backup runs instead of at the end; errors are then collected from the same event
    /// and thrown after the command finishes.</summary>
    private static async Task RunAsync(ConnectionProfile p, string sql, IProgress<BackupProgress> progress, double scale, double offset,
        CancellationToken ct)
    {
        await using var cn = new SqlConnection(ConnectionString(p)) { FireInfoMessageEventOnUserErrors = true };
        var errors = new List<SqlError>();
        cn.InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors)
            {
                if (error.Class > 10)
                {
                    lock (errors) errors.Add(error);
                    progress.Report(new BackupProgress($"Error {error.Number}: {error.Message}"));
                    continue;
                }
                var percent = PercentOf(error.Message);
                progress.Report(new BackupProgress(error.Message, percent is { } v ? offset + v * scale : null));
            }
        };
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
        lock (errors)
        {
            if (errors.Count == 0) return;
            // The last error is usually the summary ("BACKUP DATABASE is terminating abnormally"); the first says why.
            var first = errors[0];
            throw new BackupException(
                $"The server refused it: {first.Message}" + (errors.Count > 1 ? " " + errors[^1].Message : ""),
                string.Join(Environment.NewLine, errors.Select(e => $"Msg {e.Number}, Level {e.Class}: {e.Message}")));
        }
    }

    /// <summary>One backup set in a .bak file (RESTORE HEADERONLY).</summary>
    public sealed record BackupSet(int Position, int Type, string Database, DateTime? Finished, long? Size, string ServerVersion);

    /// <summary>One database file in a backup set (RESTORE FILELISTONLY).</summary>
    public sealed record BackupFile(string LogicalName, string PhysicalName, char Type, long FileId);

    /// <summary>One file of an existing database (sys.master_files).</summary>
    public sealed record ExistingFile(string PhysicalName, char Type, int FileId);

    public static async Task<(BackupSet Set, IReadOnlyList<BackupFile> Files)> ReadBackupAsync(ConnectionProfile p, string path, CancellationToken ct)
    {
        await using var cn = new SqlConnection(ConnectionString(p));
        await cn.OpenAsync(ct);
        var sets = new List<BackupSet>();
        try
        {
            await using (var cmd = new SqlCommand($"RESTORE HEADERONLY FROM DISK = {Literal(path)};", cn) { CommandTimeout = 0 })
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var major = Get<object>(r, "SoftwareVersionMajor");
                    var minor = Get<object>(r, "SoftwareVersionMinor");
                    sets.Add(new BackupSet(
                        Convert.ToInt32(Get<object>(r, "Position"), CultureInfo.InvariantCulture),
                        Convert.ToInt32(Get<object>(r, "BackupType"), CultureInfo.InvariantCulture),
                        Get<string>(r, "DatabaseName") ?? "",
                        Get<DateTime?>(r, "BackupFinishDate"),
                        Get<object>(r, "CompressedBackupSize") is { } c ? Convert.ToInt64(c, CultureInfo.InvariantCulture)
                            : Get<object>(r, "BackupSize") is { } b ? Convert.ToInt64(b, CultureInfo.InvariantCulture) : null,
                        major is null ? "" : $"{major}.{minor}"));
                }
            }
            if (sets.Count == 0) throw new BackupException("The file holds no backup.");
            var set = sets.MaxBy(s => s.Position)!;

            var files = new List<BackupFile>();
            await using (var cmd = new SqlCommand($"RESTORE FILELISTONLY FROM DISK = {Literal(path)} WITH FILE = {set.Position};", cn) { CommandTimeout = 0 })
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    files.Add(new BackupFile(
                        Get<string>(r, "LogicalName") ?? "", Get<string>(r, "PhysicalName") ?? "",
                        (Get<string>(r, "Type") ?? "D")[0],
                        Get<object>(r, "FileId") is { } id ? Convert.ToInt64(id, CultureInfo.InvariantCulture) : 0));
            }
            return (set, files);
        }
        catch (SqlException ex)
        {
            throw new BackupException("The server could not read the backup file: " + ex.Message, inner: ex);
        }
    }

    private static T? Get<T>(SqlDataReader r, string column)
    {
        int i;
        try { i = r.GetOrdinal(column); }
        catch (IndexOutOfRangeException) { return default; }
        return r.IsDBNull(i) ? default : (T)r.GetValue(i);
    }

    public static string TypeName(int backupType) => backupType switch
    {
        1 => "Full database backup",
        2 => "Transaction log backup",
        5 => "Differential backup",
        4 => "File backup",
        _ => $"Backup (type {backupType})"
    };

    public static async Task<BackupFileInfo> InspectAsync(ConnectionProfile p, string path, CancellationToken ct)
    {
        var (set, files) = await ReadBackupAsync(p, path, ct);
        var details = files.Select(f => $"{(f.Type == 'L' ? "Log" : "Data")} file {f.LogicalName}: {f.PhysicalName}").ToList();
        if (set.Type != 1) details.Insert(0, "Only full database backups can be restored here.");
        return new BackupFileInfo
        {
            Kind = TypeName(set.Type),
            Database = set.Database,
            TakenAt = set.Finished,
            Server = set.ServerVersion.Length > 0 ? "SQL Server " + set.ServerVersion : null,
            SizeBytes = set.Size,
            Details = details,
            Format = BackupFormat.Custom
        };
    }

    /// <summary>
    /// Where each file in the backup goes: over the target database's own file of the same kind and order when it
    /// exists, otherwise into the server's default data or log folder, named after the target ("shop2.mdf",
    /// "shop2_log.ldf", "shop2_Archive.ndf").
    /// </summary>
    public static IReadOnlyList<(BackupFile File, string To)> PlanMoves(
        string target, IReadOnlyList<BackupFile> files, IReadOnlyList<ExistingFile> existing, string? dataPath, string? logPath, char separator)
    {
        var moves = new List<(BackupFile, string)>();
        string Folder(string? path) => string.IsNullOrEmpty(path) ? "" : path.EndsWith(separator) ? path : path + separator;
        var safeTarget = string.Concat(target.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '\\' or '/' or ':' ? '_' : c));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in files.GroupBy(f => f.Type == 'L' ? 'L' : f.Type == 'S' ? 'S' : 'D'))
        {
            var mine = existing.Where(e => (e.Type == 'L' ? 'L' : e.Type == 'S' ? 'S' : 'D') == group.Key).OrderBy(e => e.FileId).ToList();
            var index = 0;
            foreach (var file in group.OrderBy(f => f.FileId))
            {
                string to;
                if (index < mine.Count) to = mine[index].PhysicalName;
                else
                {
                    var folder = Folder(group.Key == 'L' ? logPath ?? dataPath : dataPath);
                    var first = index == 0;
                    to = group.Key switch
                    {
                        'L' => folder + safeTarget + (first ? "_log" : "_" + file.LogicalName) + ".ldf",
                        'S' => folder + safeTarget + "_" + file.LogicalName,
                        _ => folder + safeTarget + (first ? "" : "_" + file.LogicalName) + (first ? ".mdf" : ".ndf")
                    };
                }
                if (!used.Add(to)) to = to.Insert(to.LastIndexOf('.') is var dot and > 0 ? dot : to.Length, "_" + file.FileId);
                moves.Add((file, to));
                index++;
            }
        }
        return moves;
    }

    public static string RestoreScript(RestoreRequest r, int position, IReadOnlyList<(BackupFile File, string To)> moves, bool exists)
    {
        var db = SqlServerSql.Quote(r.Database);
        var sb = new StringBuilder();
        if (exists && r.CloseConnections)
            sb.AppendLine($"ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        sb.AppendLine($"RESTORE DATABASE {db}");
        sb.AppendLine($"FROM DISK = {Literal(r.Path)}");
        var with = new List<string> { $"FILE = {position}" };
        with.AddRange(moves.Select(m => $"MOVE {Literal(m.File.LogicalName)} TO {Literal(m.To)}"));
        if (exists) with.Add("REPLACE");
        with.AddRange(["RECOVERY", "STATS = 5"]);
        sb.Append("WITH ").AppendJoin(",\n     ", with).AppendLine(";");
        if (exists && r.CloseConnections) sb.Append($"ALTER DATABASE {db} SET MULTI_USER;");
        return sb.ToString().TrimEnd();
    }

    private static async Task<(bool Exists, IReadOnlyList<ExistingFile> Files)> ExistingAsync(ConnectionProfile p, string database, CancellationToken ct)
    {
        await using var cn = new SqlConnection(ConnectionString(p));
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT physical_name, type, file_id FROM sys.master_files WHERE database_id = DB_ID(@db) ORDER BY file_id;", cn);
        cmd.Parameters.AddWithValue("@db", database);
        await using var dbCmd = new SqlCommand("SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;", cn);
        dbCmd.Parameters.AddWithValue("@db", database);
        var exists = Convert.ToInt32(await dbCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
        var files = new List<ExistingFile>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                files.Add(new ExistingFile(r.GetString(0), Convert.ToByte(r.GetValue(1), CultureInfo.InvariantCulture) switch
                {
                    1 => 'L', 2 => 'S', _ => 'D'
                }, r.GetInt32(2)));
        return (exists, files);
    }

    private static async Task<(string Script, bool Exists)> PlanRestoreAsync(ConnectionProfile p, RestoreRequest r, CancellationToken ct)
    {
        var facts = await ReadFactsAsync(p, ct);
        var support = Support(facts);
        if (!support.IsAvailable) throw new BackupException(support.Problem + " " + support.Setup);
        var (set, files) = await ReadBackupAsync(p, r.Path, ct);
        if (set.Type != 1)
            throw new BackupException($"This file holds a {TypeName(set.Type).ToLowerInvariant()}. Only full database backups can be restored here; " +
                                      "restore the full backup first, then this one from a Query tab WITH NORECOVERY.");
        var (exists, existing) = await ExistingAsync(p, r.Database, ct);
        if (r.CreateDatabase && exists)
            throw new BackupException($"A database named {r.Database} already exists. Pick it as the target to replace it, or choose another name.");
        var moves = PlanMoves(r.Database, files, existing, facts.DataPath, facts.LogPath, facts.Separator);
        return (RestoreScript(r, set.Position, moves, exists), exists);
    }

    public static async Task<string> DescribeRestoreAsync(ConnectionProfile p, RestoreRequest r, CancellationToken ct) =>
        (await PlanRestoreAsync(p, r, ct)).Script;

    public static async Task RestoreAsync(ConnectionProfile p, RestoreRequest r, IProgress<BackupProgress> progress, CancellationToken ct)
    {
        var (script, exists) = await PlanRestoreAsync(p, r, ct);
        progress.Report(new BackupProgress(script, 0));
        // The app's own pooled connections to the database would block the restore.
        SqlConnection.ClearAllPools();
        try
        {
            await RunAsync(p, script, progress, scale: 1, offset: 0, ct);
        }
        catch (Exception) when (exists && r.CloseConnections)
        {
            // Don't leave the old database stuck in single-user mode when the restore failed.
            try
            {
                await using var cn = new SqlConnection(ConnectionString(p));
                await cn.OpenAsync(CancellationToken.None);
                await using var cmd = new SqlCommand($"IF DB_ID({Literal(r.Database)}) IS NOT NULL ALTER DATABASE {SqlServerSql.Quote(r.Database)} SET MULTI_USER;", cn);
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // The restore's error is the one to show.
            }
            throw;
        }
        finally
        {
            SqlConnection.ClearAllPools();
        }
        progress.Report(new BackupProgress($"Restore into {r.Database} finished.", 100));
    }
}
