using DbExplorer.Application;
using DbExplorer.Core.Backup;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Providers.MySql;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;

namespace DbExplorer.Tests;

/// <summary>Backup and restore without a server: tool discovery, the arguments and scripts each engine runs, and what
/// is read back from backup files.</summary>
public sealed class BackupTests
{
    private static ConnectionProfile Pg(Action<ConnectionProfile>? change = null)
    {
        var p = new ConnectionProfile { ProviderKey = "Postgres", Host = "db.example", Port = 5433, UserName = "app", Password = "s3cret", Database = "shop" };
        change?.Invoke(p);
        return p;
    }

    // ----- Tools -----

    [Theory]
    [InlineData("pg_dump (PostgreSQL) 16.4", "16.4")]
    [InlineData("pg_dump (PostgreSQL) 17.0 (Ubuntu 17.0-1.pgdg24.04+1)", "17.0")]
    [InlineData("mysqldump  Ver 8.0.36 for Linux on x86_64 (MySQL Community Server - GPL)", "8.0.36")]
    [InlineData("mariadb-dump from 11.4.2-MariaDB, client 10.19 for Win64 (AMD64)", "11.4.2")]
    public void Tool_versions_are_read_from_their_version_line(string line, string expected)
    {
        Assert.Equal(Version.Parse(expected), ClientTools.ParseVersion(line)!.Value.Version);
    }

    [Fact]
    public void Install_folders_expand_newest_version_first()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbx-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var v in new[] { "9.6", "16", "13" }) Directory.CreateDirectory(Path.Combine(root, v, "bin"));
            Directory.CreateDirectory(Path.Combine(root, "12")); // no bin folder: skipped
            var found = ClientTools.Expand(Path.Combine(root, "*", "bin")).ToList();
            Assert.Equal(["16", "13", "9.6"], found.Select(f => Path.GetFileName(Path.GetDirectoryName(f))));
            Assert.Empty(ClientTools.Expand(Path.Combine(root, "missing", "*")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_users_folder_is_searched_first_and_path_last()
    {
        var folders = ClientTools.CandidateFolders("/opt/my tools", []);
        Assert.Equal("/opt/my tools", folders[0]);
    }

    [Fact]
    public void A_private_option_file_is_deleted_after_use()
    {
        string path;
        using (var file = ClientTools.WritePrivateFile("[client]\npassword=x\n"))
        {
            path = file.Path;
            Assert.Equal("[client]\npassword=x\n", File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.False(File.Exists(path));
    }

    // ----- PostgreSQL -----

    [Fact]
    public void Pg_dump_gets_the_password_and_tls_mode_through_the_environment_only()
    {
        var p = Pg(x => { x.Encrypt = true; x.TrustServerCertificate = false; });
        var args = PostgresBackup.DumpArguments(p, new BackupRequest { Database = "shop", Path = "/b/shop.dump", Format = BackupFormat.Custom });
        Assert.Equal(["--host", "db.example", "--port", "5433", "--username", "app", "--no-password", "--dbname", "shop",
            "--format", "custom", "--file", "/b/shop.dump", "--verbose"], args);
        Assert.DoesNotContain(args, a => a.Contains("s3cret"));

        var env = PostgresBackup.ToolEnvironment(p);
        Assert.Equal("s3cret", env["PGPASSWORD"]);
        Assert.Equal("verify-full", env["PGSSLMODE"]);
        Assert.Equal("system", env["PGSSLROOTCERT"]);

        Assert.Equal("prefer", PostgresBackup.ToolEnvironment(Pg())["PGSSLMODE"]);
        Assert.Null(PostgresBackup.ToolEnvironment(Pg())["PGSSLROOTCERT"]);
        Assert.Equal("require", PostgresBackup.ToolEnvironment(Pg(x => x.Encrypt = true))["PGSSLMODE"]);
        Assert.Equal("verify-ca", PostgresBackup.ToolEnvironment(Pg(x => { x.Encrypt = true; x.TrustServerCertificate = false; x.CertificateHostName = "db"; }))["PGSSLMODE"]);
        Assert.Null(PostgresBackup.ToolEnvironment(Pg(x => x.IntegratedSecurity = true))["PGPASSWORD"]);
    }

    [Fact]
    public void Plain_pg_dumps_leave_out_owners_and_schema_only_leaves_out_rows()
    {
        var args = PostgresBackup.DumpArguments(Pg(), new BackupRequest { Database = "shop", Path = "s.sql", Format = BackupFormat.PlainSql, SchemaOnly = true });
        Assert.Contains("plain", args);
        Assert.Contains("--no-owner", args);
        Assert.Contains("--no-privileges", args);
        Assert.Contains("--schema-only", args);
    }

    [Fact]
    public void Database_names_that_look_like_connection_strings_are_quoted()
    {
        Assert.Equal("shop", PostgresBackup.DatabaseArgument("shop"));
        Assert.Equal(@"dbname='a=b\'c'", PostgresBackup.DatabaseArgument("a=b'c"));
    }

    [Fact]
    public void Pg_restore_runs_in_one_transaction_and_cleans_only_existing_databases()
    {
        var existing = PostgresBackup.RestoreArguments(Pg(), new RestoreRequest { Path = "s.dump", Database = "shop" });
        Assert.Contains("--single-transaction", existing);
        Assert.Contains("--exit-on-error", existing);
        Assert.Contains("--clean", existing);
        Assert.Contains("--no-owner", existing);
        Assert.Equal("s.dump", existing[^1]);

        var fresh = PostgresBackup.RestoreArguments(Pg(), new RestoreRequest { Path = "s.dump", Database = "copy", CreateDatabase = true, SkipOwnership = false });
        Assert.DoesNotContain("--clean", fresh);
        Assert.DoesNotContain("--no-owner", fresh);

        var script = PostgresBackup.ScriptArguments(Pg(), new RestoreRequest { Path = "s.sql", Database = "shop" });
        Assert.Contains("ON_ERROR_STOP=1", script);
        Assert.Contains("--single-transaction", script);
        Assert.Equal(["--file", "-"], script.TakeLast(2));
    }

    [Fact]
    public void A_pg_restore_list_tells_the_database_date_version_and_tables()
    {
        var info = PostgresBackup.ParseArchiveList("""
            ;
            ; Archive created at 2026-11-03 07:05:23 UTC
            ;     dbname: shop
            ;     TOC Entries: 9
            ;     Compression: gzip
            ;     Format: CUSTOM
            ;     Dumped from database version: 16.4
            ;     Dumped by pg_dump version: 16.15
            ;
            ;
            ; Selected TOC Entries:
            ;
            215; 1259 16390 TABLE public customers postgres
            216; 1259 16391 TABLE public orders postgres
            217; 1259 16392 VIEW public big_orders postgres
            3401; 0 16390 TABLE DATA public customers postgres
            3402; 0 16391 TABLE DATA public orders postgres
            3250; 2606 16395 CONSTRAINT public orders orders_pkey postgres
            """.Split('\n'));
        Assert.Equal("shop", info.Database);
        Assert.Equal("pg_dump custom archive", info.Kind);
        Assert.Equal("PostgreSQL 16.4", info.Server);
        Assert.Equal(new DateTime(2026, 11, 3, 7, 5, 23), info.TakenAt);
        Assert.Equal(6, info.EntryCount);
        Assert.Equal("2 table(s), rows of 2 table(s), 6 item(s) in all", info.Details[0]);
        Assert.Equal("Made by pg_dump 16.15", info.Details[1]);
    }

    [Fact]
    public void A_plain_script_that_switches_database_is_flagged()
    {
        var info = PostgresBackup.ParseScriptHead("""
            --
            -- PostgreSQL database dump
            --
            -- Dumped from database version 16.4
            -- Dumped by pg_dump version 16.4
            CREATE DATABASE shop WITH TEMPLATE = template0;
            \connect shop
            """.Split('\n'));
        Assert.Equal("pg_dump SQL script", info.Kind);
        Assert.Equal("PostgreSQL 16.4", info.Server);
        Assert.Contains(info.Details, d => d.Contains("creates or switches to its own database"));

        Assert.Equal("SQL script", PostgresBackup.ParseScriptHead(["CREATE TABLE t (x int);"]).Kind);
    }

    [Fact]
    public void Archives_are_told_from_scripts_by_their_first_bytes()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "PGDMP\u0001\u000e\u0000");
            Assert.True(PostgresBackup.IsArchive(file));
            File.WriteAllText(file, "--\n-- PostgreSQL database dump\n");
            Assert.False(PostgresBackup.IsArchive(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ----- SQL Server -----

    [Fact]
    public void Sql_server_backups_are_copy_only_checksummed_and_report_progress()
    {
        var script = SqlServerBackup.BackupScript(new BackupRequest { Database = "Shop]", Path = @"D:\Backups\it's.bak" }, canCompress: true);
        Assert.StartsWith("BACKUP DATABASE [Shop]]]\nTO DISK = N'D:\\Backups\\it''s.bak'\nWITH COPY_ONLY, FORMAT, INIT, CHECKSUM, COMPRESSION, NAME = N'Shop]", script);
        Assert.EndsWith("STATS = 5;", script);

        var express = SqlServerBackup.BackupScript(new BackupRequest { Database = "Shop", Path = "x.bak", CopyOnly = false }, canCompress: false);
        Assert.DoesNotContain("COPY_ONLY", express);
        Assert.DoesNotContain("COMPRESSION", express);

        Assert.Equal("RESTORE VERIFYONLY FROM DISK = N'x.bak' WITH CHECKSUM;", SqlServerBackup.VerifyScript("x.bak"));
        Assert.Equal(10, SqlServerBackup.PercentOf("10 percent processed."));
        Assert.Equal(100, SqlServerBackup.PercentOf("100 percent processed."));
        Assert.Null(SqlServerBackup.PercentOf("Processed 512 pages for database 'Shop'"));
    }

    [Fact]
    public void Sql_server_support_follows_the_edition_and_server_platform()
    {
        var windows = SqlServerBackup.Support(new(3, @"C:\Program Files\Microsoft SQL Server\MSSQL16\MSSQL\Backup\", @"C:\Data\", @"C:\Logs\", "16.0"));
        Assert.True(windows.IsAvailable);
        Assert.Equal(BackupFileLocation.Server, windows.Location);
        Assert.Equal('\\', windows.PathSeparator);
        Assert.True(windows.SupportsCompression);
        Assert.Equal("bak", windows.ExtensionFor(BackupFormat.Custom));

        var linuxExpress = SqlServerBackup.Support(new(4, "/var/opt/mssql/data/", "/var/opt/mssql/data/", null, "16.0"));
        Assert.Equal('/', linuxExpress.PathSeparator);
        Assert.False(linuxExpress.SupportsCompression);

        var azure = SqlServerBackup.Support(new(5, null, null, null, "12.0"));
        Assert.False(azure.IsAvailable);
        Assert.Contains("Azure SQL Database", azure.Problem);
        Assert.False(SqlServerBackup.Support(new(8, null, null, null, "16.0")).IsAvailable);
    }

    private static readonly SqlServerBackup.BackupFile[] ShopFiles =
    [
        new("Shop", @"C:\Data\Shop.mdf", 'D', 1),
        new("Shop_log", @"C:\Data\Shop_log.ldf", 'L', 2),
        new("Archive", @"C:\Data\Shop_Archive.ndf", 'D', 3)
    ];

    [Fact]
    public void A_new_database_gets_its_own_files_in_the_default_folders()
    {
        var moves = SqlServerBackup.PlanMoves("Shop Copy", ShopFiles, [], @"E:\Data", @"F:\Logs\", '\\');
        Assert.Equal(
        [
            ("Shop", @"E:\Data\Shop Copy.mdf"),
            ("Archive", @"E:\Data\Shop Copy_Archive.ndf"),
            ("Shop_log", @"F:\Logs\Shop Copy_log.ldf")
        ], moves.Select(m => (m.File.LogicalName, m.To)));

        var linux = SqlServerBackup.PlanMoves("shop2", ShopFiles.Take(2).ToList(), [], "/var/opt/mssql/data/", null, '/');
        Assert.Equal(["/var/opt/mssql/data/shop2.mdf", "/var/opt/mssql/data/shop2_log.ldf"], linux.Select(m => m.To));
    }

    [Fact]
    public void Restoring_over_a_database_reuses_its_files_by_kind_and_order()
    {
        var existing = new SqlServerBackup.ExistingFile[] { new(@"G:\Old\Target.mdf", 'D', 1), new(@"G:\Old\Target_log.ldf", 'L', 2) };
        var moves = SqlServerBackup.PlanMoves("Target", ShopFiles, existing, @"E:\Data\", null, '\\');
        Assert.Equal([@"G:\Old\Target.mdf", @"E:\Data\Target_Archive.ndf", @"G:\Old\Target_log.ldf"], moves.Select(m => m.To));

        var script = SqlServerBackup.RestoreScript(new RestoreRequest { Path = @"D:\b\shop.bak", Database = "Target" }, 1, moves, exists: true);
        Assert.Equal("""
            ALTER DATABASE [Target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            RESTORE DATABASE [Target]
            FROM DISK = N'D:\b\shop.bak'
            WITH FILE = 1,
                 MOVE N'Shop' TO N'G:\Old\Target.mdf',
                 MOVE N'Archive' TO N'E:\Data\Target_Archive.ndf',
                 MOVE N'Shop_log' TO N'G:\Old\Target_log.ldf',
                 REPLACE,
                 RECOVERY,
                 STATS = 5;
            ALTER DATABASE [Target] SET MULTI_USER;
            """.ReplaceLineEndings("\n"), script.ReplaceLineEndings("\n"));

        var fresh = SqlServerBackup.RestoreScript(new RestoreRequest { Path = "x.bak", Database = "New", CreateDatabase = true, CloseConnections = true }, 2, moves, exists: false);
        Assert.DoesNotContain("SINGLE_USER", fresh);
        Assert.DoesNotContain("REPLACE", fresh);
        Assert.Contains("FILE = 2", fresh);
    }

    // ----- MySQL -----

    [Fact]
    public void The_mysql_option_file_holds_the_login_in_the_tools_dialect()
    {
        var p = new ConnectionProfile { Host = "db", Port = 3307, UserName = "app", Password = "p\"a\\ss", Encrypt = true, TrustServerCertificate = false };
        var mysql = MySqlBackup.OptionFile(p, mariaDbTools: false);
        Assert.Contains("host=\"db\"\n", mysql);
        Assert.Contains("port=3307\n", mysql);
        Assert.Contains("password=\"p\\\"a\\\\ss\"\n", mysql);
        Assert.Contains("ssl-mode=VERIFY_IDENTITY\n", mysql);

        var maria = MySqlBackup.OptionFile(p, mariaDbTools: true);
        Assert.Contains("ssl\n", maria);
        Assert.Contains("\nssl-verify-server-cert\n", maria);
        Assert.DoesNotContain("ssl-mode", maria);
        Assert.Contains("skip-ssl-verify-server-cert", MySqlBackup.OptionFile(new ConnectionProfile { Host = "db" }, mariaDbTools: true));
        Assert.Contains("ssl-mode=PREFERRED", MySqlBackup.OptionFile(new ConnectionProfile { Host = "db" }, mariaDbTools: false));
    }

    [Fact]
    public void Mysqldump_reads_one_snapshot_with_routines_and_no_database_name_in_the_script()
    {
        var args = MySqlBackup.DumpArguments("/tmp/x.cnf", new BackupRequest { Database = "shop", Path = "/b/shop.sql" }, mariaDbTools: false, mariaDbServer: true);
        Assert.Equal("--defaults-extra-file=/tmp/x.cnf", args[0]);
        Assert.Contains("--single-transaction", args);
        Assert.Contains("--routines", args);
        Assert.Contains("--triggers", args);
        Assert.Contains("--events", args);
        Assert.Contains("--set-gtid-purged=OFF", args);
        Assert.Contains("--column-statistics=0", args);
        Assert.DoesNotContain("--databases", args);
        Assert.Equal("shop", args[^1]);

        var maria = MySqlBackup.DumpArguments("x", new BackupRequest { Database = "shop", Path = "s.sql", SchemaOnly = true }, mariaDbTools: true, mariaDbServer: true);
        Assert.DoesNotContain("--set-gtid-purged=OFF", maria);
        Assert.Contains("--no-data", maria);

        Assert.Equal("mysqldump --defaults-extra-file=<login options> \"--result-file=C:\\My Backups\\s.sql\" shop",
            MySqlBackup.Shown("mysqldump", ["--defaults-extra-file=/tmp/secret.cnf", @"--result-file=C:\My Backups\s.sql", "shop"]));
    }

    [Fact]
    public void A_mysqldump_header_tells_the_database_server_and_completion()
    {
        var head = """
            -- MySQL dump 10.13  Distrib 8.0.36, for Linux (x86_64)
            --
            -- Host: 127.0.0.1    Database: shop
            -- ------------------------------------------------------
            -- Server version	8.0.36
            """.Split('\n');
        var info = MySqlBackup.ParseHead(head, "\n-- Dump completed on 2026-11-03  7:15:01\n");
        Assert.Equal("shop", info.Database);
        Assert.Equal("mysqldump script", info.Kind);
        Assert.Equal("MySQL 8.0.36", info.Server);
        Assert.Equal(new DateTime(2026, 11, 3, 7, 15, 1), info.TakenAt);

        var cut = MySqlBackup.ParseHead([.. head, "USE `shop`;"], "INSERT INTO t VALUES (1");
        Assert.Contains(cut.Details, d => d.Contains("may be cut short"));
        Assert.Contains(cut.Details, d => d.Contains("picks its own database"));
        Assert.Equal("MariaDB 10.11.6", MySqlBackup.ParseHead(["-- MariaDB dump 10.19  Distrib 10.11.6-MariaDB", "-- Server version\t10.11.6-MariaDB"], "-- Dump completed").Server);
    }

    // ----- Window and settings -----

    [Fact]
    public void Suggested_file_names_carry_the_database_and_time()
    {
        Assert.Equal("shop_20261103_0715.dump", BackupRestoreViewModel.FileName("shop", new DateTime(2026, 11, 3, 7, 15, 0), "dump"));
        Assert.Equal("my_db_1_20261103_0715.bak", BackupRestoreViewModel.FileName("my db/1", new DateTime(2026, 11, 3, 7, 15, 0), "bak"));
        Assert.Equal("2.5 MB", BackupRestoreViewModel.FormatSize(2_621_440));
        Assert.Equal(
            "Full database backup of Shop\ntaken 2026-11-03 07:15 · from SQL Server 16.0 · 1 KB\nData file Shop: C:\\Shop.mdf",
            BackupRestoreViewModel.Describe(new BackupFileInfo
            {
                Kind = "Full database backup", Database = "Shop", TakenAt = new DateTime(2026, 11, 3, 7, 15, 0),
                Server = "SQL Server 16.0", SizeBytes = 1024, Details = ["Data file Shop: C:\\Shop.mdf"]
            }));
    }

    [Fact]
    public void Tool_folders_and_the_last_backup_folder_are_remembered()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbx-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettingsService(new AppPaths(root));
            settings.SetBackupToolFolder("Postgres", @"C:\pg\17\bin");
            settings.SetLastBackupFolder(@"D:\Backups");
            var again = new AppSettingsService(new AppPaths(root));
            Assert.Equal(@"C:\pg\17\bin", again.BackupToolFolder("postgres"));
            Assert.Null(again.BackupToolFolder("MySql"));
            Assert.Equal(@"D:\Backups", again.LastBackupFolder);
            again.SetBackupToolFolder("Postgres", null);
            Assert.Null(new AppSettingsService(new AppPaths(root)).BackupToolFolder("Postgres"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }
}
