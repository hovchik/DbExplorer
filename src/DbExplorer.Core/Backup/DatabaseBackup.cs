namespace DbExplorer.Core.Backup;

/// <summary>Where the backup file is read and written.</summary>
public enum BackupFileLocation
{
    /// <summary>On this computer (PostgreSQL and MySQL: the engine's dump tools run here).</summary>
    ThisComputer,

    /// <summary>On the database server, as its service account sees it (SQL Server's BACKUP and RESTORE).</summary>
    Server
}

/// <summary>PostgreSQL's pg_dump output formats.</summary>
public enum BackupFormat
{
    /// <summary>Compressed archive read by pg_restore (pg_dump -Fc); can restore single objects.</summary>
    Custom,

    /// <summary>A plain SQL script (pg_dump -Fp, mysqldump), readable and restorable by any SQL client.</summary>
    PlainSql
}

/// <summary>
/// What backup and restore can do on a connection, and what the user must do first when they can't (install the
/// client tools). <see cref="Tool"/> is the dump program that will run, for the window to show.
/// </summary>
public sealed record BackupSupport
{
    public bool IsAvailable { get; init; }

    /// <summary>Why backups can't run (e.g. pg_dump is missing or too old for the server).</summary>
    public string? Problem { get; init; }

    /// <summary>What to install or set up, when <see cref="Problem"/> is set.</summary>
    public string? Setup { get; init; }

    public BackupFileLocation Location { get; init; }

    /// <summary>"pg_dump 16.4 in C:\Program Files\PostgreSQL\16\bin", or "BACKUP DATABASE on the server".</summary>
    public string Tool { get; init; } = "";

    /// <summary>The folder the tools were found in (null for SQL Server).</summary>
    public string? ToolFolder { get; init; }

    /// <summary>The folder new backups go to by default (SQL Server: the instance's backup directory).</summary>
    public string? DefaultFolder { get; init; }

    /// <summary>The server's path separator, for building server-side paths ("\" on Windows, "/" on Linux).</summary>
    public char PathSeparator { get; init; } = System.IO.Path.DirectorySeparatorChar;

    public IReadOnlyList<BackupFormat> Formats { get; init; } = [BackupFormat.PlainSql];

    /// <summary>The backup can leave out the rows (not SQL Server, whose backups are always whole).</summary>
    public bool SupportsSchemaOnly { get; init; }

    /// <summary>SQL Server: COPY_ONLY, COMPRESSION and RESTORE VERIFYONLY.</summary>
    public bool SupportsCopyOnly { get; init; }
    public bool SupportsCompression { get; init; }
    public bool SupportsVerify { get; init; }

    /// <summary>PostgreSQL: skip ALTER OWNER and GRANT statements on restore (--no-owner --no-privileges).</summary>
    public bool SupportsSkipOwnership { get; init; }

    /// <summary>Other sessions can be disconnected from the target before a restore.</summary>
    public bool SupportsCloseConnections { get; init; }

    /// <summary>What restoring into an existing database does on this engine, in one sentence.</summary>
    public string RestoreOverExisting { get; init; } = "";

    /// <summary>File name extension (no dot) per format.</summary>
    public string ExtensionFor(BackupFormat format) =>
        Location == BackupFileLocation.Server ? "bak" : format == BackupFormat.Custom ? "dump" : "sql";
}

public sealed record BackupRequest
{
    public string Database { get; init; } = "";

    /// <summary>The file to write; on <see cref="BackupFileLocation.Server"/> a path on the server.</summary>
    public string Path { get; init; } = "";

    public BackupFormat Format { get; init; } = BackupFormat.Custom;

    /// <summary>Tables, views, routines and so on without any rows.</summary>
    public bool SchemaOnly { get; init; }

    /// <summary>SQL Server: COPY_ONLY, so the backup does not change the differential base of scheduled backups.</summary>
    public bool CopyOnly { get; init; } = true;

    public bool Compress { get; init; } = true;

    /// <summary>SQL Server: RESTORE VERIFYONLY after writing (checksums are always written).</summary>
    public bool Verify { get; init; } = true;
}

public sealed record RestoreRequest
{
    public string Path { get; init; } = "";

    /// <summary>The database to restore into.</summary>
    public string Database { get; init; } = "";

    /// <summary>The database does not exist yet and is created first.</summary>
    public bool CreateDatabase { get; init; }

    /// <summary>PostgreSQL: leave out ALTER OWNER and GRANT, so a backup from another server restores without its roles.</summary>
    public bool SkipOwnership { get; init; } = true;

    /// <summary>Disconnect every other session from the target first (open transactions are rolled back).</summary>
    public bool CloseConnections { get; init; } = true;
}

/// <summary>One line of progress: a tool's output line, and the share done when known (0–100).</summary>
public sealed record BackupProgress(string Line, double? Percent = null);

/// <summary>What a backup file holds, read before restoring it.</summary>
public sealed record BackupFileInfo
{
    /// <summary>The database the backup was taken of, when the file says.</summary>
    public string? Database { get; init; }

    /// <summary>"pg_dump custom archive", "mysqldump script", "Full database backup".</summary>
    public string Kind { get; init; } = "";

    public DateTime? TakenAt { get; init; }

    /// <summary>Engine and version the backup came from.</summary>
    public string? Server { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>Other facts worth showing (file list, object count).</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>Items in a pg_dump archive's table of contents, for counting restore progress (0 when unknown).</summary>
    public int EntryCount { get; init; }

    /// <summary>The format, for picking the restore tool (PostgreSQL archive or script).</summary>
    public BackupFormat Format { get; init; } = BackupFormat.PlainSql;
}

/// <summary>A backup or restore that the server or the tool refused; <see cref="Exception.Message"/> says why in a
/// sentence, and <see cref="Output"/> keeps the last lines the tool printed.</summary>
public sealed class BackupException(string message, string? output = null, Exception? inner = null) : Exception(message, inner)
{
    public string? Output { get; } = output;
}

/// <summary>
/// Backup and restore of whole databases, implemented by providers whose engine has a way to do it: SQL Server with
/// BACKUP / RESTORE on the server, PostgreSQL and MySQL with their dump tools (pg_dump, pg_restore, psql, mysqldump,
/// mysql) run on this computer. <paramref name="toolFolder"/> is the folder the user picked for those tools, tried
/// before the usual install folders and PATH.
/// </summary>
public interface IDatabaseBackupProvider
{
    Task<BackupSupport> GetBackupSupportAsync(string? toolFolder, CancellationToken ct = default);

    Task BackupAsync(BackupRequest request, string? toolFolder, IProgress<BackupProgress> progress, CancellationToken ct = default);

    /// <summary>Reads what a backup file holds without restoring anything.</summary>
    Task<BackupFileInfo> InspectBackupAsync(string path, string? toolFolder, CancellationToken ct = default);

    /// <summary>What a restore will run, for the confirmation: the T-SQL script, or the tool's command line (never the
    /// password).</summary>
    Task<string> DescribeRestoreAsync(RestoreRequest request, string? toolFolder, CancellationToken ct = default);

    Task RestoreAsync(RestoreRequest request, string? toolFolder, IProgress<BackupProgress> progress, CancellationToken ct = default);
}
