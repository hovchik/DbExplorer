namespace DbExplorer.Application.Query;

/// <summary>
/// The DDL statements offered by completion at the start of a statement (CREATE DATABASE, ALTER TABLE ADD COLUMN,
/// DROP VIEW, GRANT, …), each a small template for the connected engine. In a template the first placeholder is
/// wrapped in <c>|…|</c>: it is selected after inserting so typing replaces it.
/// </summary>
public static class DdlTemplates
{
    /// <param name="Engines">Which engines have the statement: S = SQL Server, P = PostgreSQL, M = MySQL/MariaDB.</param>
    private sealed record Template(string Label, string Engines, string Text, string Detail);

    // Order is the order shown: the five CREATE statements that were offered before come first.
    private static readonly IReadOnlyList<Template> All =
    [
        // ----- CREATE -----
        new("CREATE TABLE", "S", "CREATE TABLE dbo.|table_name| (\n    id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,\n    name NVARCHAR(100) NOT NULL\n);", "new table"),
        new("CREATE TABLE", "P", "CREATE TABLE |table_name| (\n    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,\n    name TEXT NOT NULL\n);", "new table"),
        new("CREATE TABLE", "M", "CREATE TABLE |table_name| (\n    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,\n    name VARCHAR(100) NOT NULL\n);", "new table"),
        new("CREATE VIEW", "S", "CREATE VIEW dbo.|view_name|\nAS\nSELECT *\nFROM ;", "new view"),
        new("CREATE VIEW", "PM", "CREATE VIEW |view_name| AS\nSELECT *\nFROM ;", "new view"),
        new("CREATE PROCEDURE", "S", "CREATE PROCEDURE dbo.|procedure_name|\n    @id INT\nAS\nBEGIN\n    SET NOCOUNT ON;\n    \nEND", "new stored procedure"),
        new("CREATE PROCEDURE", "P", "CREATE PROCEDURE |procedure_name|(p_id integer)\nLANGUAGE plpgsql\nAS $$\nBEGIN\n    \nEND;\n$$;", "new procedure (PL/pgSQL)"),
        new("CREATE PROCEDURE", "M", "CREATE PROCEDURE |procedure_name|(IN p_id INT)\nBEGIN\n    \nEND;", "new stored procedure"),
        new("CREATE FUNCTION", "S", "CREATE FUNCTION dbo.|function_name| (@value INT)\nRETURNS INT\nAS\nBEGIN\n    RETURN @value;\nEND", "new scalar function"),
        new("CREATE FUNCTION", "P", "CREATE FUNCTION |function_name|(p_value integer)\nRETURNS integer\nLANGUAGE plpgsql\nAS $$\nBEGIN\n    RETURN p_value;\nEND;\n$$;", "new function (PL/pgSQL)"),
        new("CREATE FUNCTION", "M", "CREATE FUNCTION |function_name|(p_value INT)\nRETURNS INT\nDETERMINISTIC\nBEGIN\n    RETURN p_value;\nEND;", "new function"),
        new("CREATE INDEX", "S", "CREATE INDEX |IX_table_column| ON dbo.table_name (column_name);", "new index"),
        new("CREATE INDEX", "PM", "CREATE INDEX |ix_table_column| ON table_name (column_name);", "new index"),
        new("CREATE DATABASE", "SP", "CREATE DATABASE |database_name|;", "new database"),
        new("CREATE DATABASE", "M", "CREATE DATABASE |database_name| CHARACTER SET utf8mb4;", "new database"),
        new("CREATE SCHEMA", "S", "CREATE SCHEMA |schema_name| AUTHORIZATION dbo;", "new schema"),
        new("CREATE SCHEMA", "P", "CREATE SCHEMA IF NOT EXISTS |schema_name|;", "new schema"),
        new("CREATE SCHEMA", "M", "CREATE SCHEMA |schema_name| CHARACTER SET utf8mb4;", "new schema (same as a database)"),
        new("CREATE UNIQUE INDEX", "S", "CREATE UNIQUE INDEX |UX_table_column| ON dbo.table_name (column_name);", "new unique index"),
        new("CREATE UNIQUE INDEX", "PM", "CREATE UNIQUE INDEX |ux_table_column| ON table_name (column_name);", "new unique index"),
        new("CREATE TABLE AS SELECT", "PM", "CREATE TABLE |new_table| AS\nSELECT *\nFROM ;", "new table from a query"),
        new("CREATE MATERIALIZED VIEW", "P", "CREATE MATERIALIZED VIEW |view_name| AS\nSELECT *\nFROM \nWITH DATA;", "new materialized view"),
        new("CREATE SEQUENCE", "S", "CREATE SEQUENCE dbo.|sequence_name|\n    AS BIGINT\n    START WITH 1\n    INCREMENT BY 1;", "new sequence"),
        new("CREATE SEQUENCE", "P", "CREATE SEQUENCE |sequence_name| START WITH 1 INCREMENT BY 1;", "new sequence"),
        new("CREATE TRIGGER", "S", "CREATE TRIGGER dbo.|trigger_name|\nON dbo.table_name\nAFTER INSERT, UPDATE\nAS\nBEGIN\n    SET NOCOUNT ON;\n    \nEND", "new DML trigger"),
        new("CREATE TRIGGER", "P", "CREATE TRIGGER |trigger_name|\nBEFORE INSERT OR UPDATE ON table_name\nFOR EACH ROW\nEXECUTE FUNCTION trigger_function();", "new trigger (calls a function RETURNS trigger)"),
        new("CREATE TRIGGER", "M", "CREATE TRIGGER |trigger_name|\nBEFORE INSERT ON table_name\nFOR EACH ROW\nBEGIN\n    SET NEW.column_name = NULL;\nEND;", "new trigger"),
        new("CREATE TYPE", "S", "CREATE TYPE dbo.|type_name| AS TABLE (\n    id INT NOT NULL\n);", "new table type"),
        new("CREATE TYPE", "P", "CREATE TYPE |type_name| AS ENUM ('value1', 'value2');", "new enum type"),
        new("CREATE SYNONYM", "S", "CREATE SYNONYM dbo.|synonym_name| FOR dbo.object_name;", "new synonym"),
        new("CREATE EXTENSION", "P", "CREATE EXTENSION IF NOT EXISTS |extension_name|;", "install an extension"),
        new("CREATE EVENT", "M", "CREATE EVENT |event_name|\nON SCHEDULE EVERY 1 DAY\nDO\n    DELETE FROM table_name WHERE ;", "new scheduled event"),
        new("CREATE LOGIN", "S", "CREATE LOGIN |login_name| WITH PASSWORD = 'password';", "new server login"),
        new("CREATE USER", "S", "CREATE USER |user_name| FOR LOGIN login_name;", "new database user"),
        new("CREATE USER", "P", "CREATE USER |user_name| WITH PASSWORD 'password';", "new login role"),
        new("CREATE USER", "M", "CREATE USER '|user_name|'@'%' IDENTIFIED BY 'password';", "new account"),
        new("CREATE ROLE", "SM", "CREATE ROLE |role_name|;", "new role"),
        new("CREATE ROLE", "P", "CREATE ROLE |role_name| NOLOGIN;", "new role"),
        new("CREATE OR ALTER VIEW", "S", "CREATE OR ALTER VIEW dbo.|view_name|\nAS\nSELECT *\nFROM ;", "create or replace a view"),
        new("CREATE OR ALTER PROCEDURE", "S", "CREATE OR ALTER PROCEDURE dbo.|procedure_name|\n    @id INT\nAS\nBEGIN\n    SET NOCOUNT ON;\n    \nEND", "create or replace a procedure"),
        new("CREATE OR ALTER FUNCTION", "S", "CREATE OR ALTER FUNCTION dbo.|function_name| (@value INT)\nRETURNS INT\nAS\nBEGIN\n    RETURN @value;\nEND", "create or replace a function"),
        new("CREATE OR REPLACE VIEW", "PM", "CREATE OR REPLACE VIEW |view_name| AS\nSELECT *\nFROM ;", "create or replace a view"),
        new("CREATE OR REPLACE FUNCTION", "P", "CREATE OR REPLACE FUNCTION |function_name|(p_value integer)\nRETURNS integer\nLANGUAGE plpgsql\nAS $$\nBEGIN\n    RETURN p_value;\nEND;\n$$;", "create or replace a function"),
        new("CREATE OR REPLACE PROCEDURE", "P", "CREATE OR REPLACE PROCEDURE |procedure_name|(p_id integer)\nLANGUAGE plpgsql\nAS $$\nBEGIN\n    \nEND;\n$$;", "create or replace a procedure"),

        // ----- ALTER -----
        new("ALTER TABLE", "SPM", "ALTER TABLE |table_name| ", "change a table"),
        new("ALTER TABLE ADD COLUMN", "S", "ALTER TABLE dbo.|table_name| ADD column_name INT NULL;", "add a column"),
        new("ALTER TABLE ADD COLUMN", "PM", "ALTER TABLE |table_name| ADD COLUMN column_name INT NULL;", "add a column"),
        new("ALTER TABLE DROP COLUMN", "S", "ALTER TABLE dbo.|table_name| DROP COLUMN column_name;", "remove a column"),
        new("ALTER TABLE DROP COLUMN", "PM", "ALTER TABLE |table_name| DROP COLUMN column_name;", "remove a column"),
        new("ALTER TABLE ALTER COLUMN", "S", "ALTER TABLE dbo.|table_name| ALTER COLUMN column_name NVARCHAR(200) NOT NULL;", "change a column's type"),
        new("ALTER TABLE ALTER COLUMN", "P", "ALTER TABLE |table_name| ALTER COLUMN column_name TYPE varchar(200);", "change a column's type"),
        new("ALTER TABLE MODIFY COLUMN", "M", "ALTER TABLE |table_name| MODIFY COLUMN column_name VARCHAR(200) NOT NULL;", "change a column's type"),
        new("ALTER TABLE RENAME COLUMN", "S", "EXEC sp_rename '|dbo.table_name.old_name|', 'new_name', 'COLUMN';", "rename a column (sp_rename)"),
        new("ALTER TABLE RENAME COLUMN", "PM", "ALTER TABLE |table_name| RENAME COLUMN old_name TO new_name;", "rename a column"),
        new("ALTER TABLE RENAME TO", "S", "EXEC sp_rename '|dbo.old_name|', 'new_name';", "rename a table (sp_rename)"),
        new("ALTER TABLE RENAME TO", "PM", "ALTER TABLE |old_name| RENAME TO new_name;", "rename a table"),
        new("ALTER TABLE SET DEFAULT", "S", "ALTER TABLE dbo.|table_name| ADD CONSTRAINT DF_table_column DEFAULT (0) FOR column_name;", "give a column a default"),
        new("ALTER TABLE SET DEFAULT", "PM", "ALTER TABLE |table_name| ALTER COLUMN column_name SET DEFAULT 0;", "give a column a default"),
        new("ALTER TABLE ADD PRIMARY KEY", "S", "ALTER TABLE dbo.|table_name| ADD CONSTRAINT PK_table_name PRIMARY KEY (id);", "add a primary key"),
        new("ALTER TABLE ADD PRIMARY KEY", "PM", "ALTER TABLE |table_name| ADD PRIMARY KEY (id);", "add a primary key"),
        new("ALTER TABLE ADD FOREIGN KEY", "S", "ALTER TABLE dbo.|table_name| ADD CONSTRAINT FK_table_other FOREIGN KEY (other_id) REFERENCES dbo.other_table (id);", "add a foreign key"),
        new("ALTER TABLE ADD FOREIGN KEY", "PM", "ALTER TABLE |table_name| ADD CONSTRAINT fk_table_other FOREIGN KEY (other_id) REFERENCES other_table (id);", "add a foreign key"),
        new("ALTER TABLE ADD UNIQUE", "S", "ALTER TABLE dbo.|table_name| ADD CONSTRAINT UQ_table_column UNIQUE (column_name);", "add a unique constraint"),
        new("ALTER TABLE ADD UNIQUE", "PM", "ALTER TABLE |table_name| ADD CONSTRAINT uq_table_column UNIQUE (column_name);", "add a unique constraint"),
        new("ALTER TABLE ADD CHECK", "S", "ALTER TABLE dbo.|table_name| ADD CONSTRAINT CK_table_column CHECK (column_name > 0);", "add a check constraint"),
        new("ALTER TABLE ADD CHECK", "PM", "ALTER TABLE |table_name| ADD CONSTRAINT ck_table_column CHECK (column_name > 0);", "add a check constraint"),
        new("ALTER TABLE DROP CONSTRAINT", "S", "ALTER TABLE dbo.|table_name| DROP CONSTRAINT constraint_name;", "remove a constraint"),
        new("ALTER TABLE DROP CONSTRAINT", "P", "ALTER TABLE |table_name| DROP CONSTRAINT constraint_name;", "remove a constraint"),
        new("ALTER TABLE DROP FOREIGN KEY", "M", "ALTER TABLE |table_name| DROP FOREIGN KEY constraint_name;", "remove a foreign key"),
        new("ALTER VIEW", "S", "ALTER VIEW dbo.|view_name|\nAS\nSELECT *\nFROM ;", "change a view"),
        new("ALTER VIEW", "M", "ALTER VIEW |view_name| AS\nSELECT *\nFROM ;", "change a view"),
        new("ALTER PROCEDURE", "S", "ALTER PROCEDURE dbo.|procedure_name|\n    @id INT\nAS\nBEGIN\n    SET NOCOUNT ON;\n    \nEND", "change a procedure"),
        new("ALTER FUNCTION", "S", "ALTER FUNCTION dbo.|function_name| (@value INT)\nRETURNS INT\nAS\nBEGIN\n    RETURN @value;\nEND", "change a function"),
        new("ALTER INDEX", "S", "ALTER INDEX |index_name| ON dbo.table_name REBUILD;", "rebuild an index"),
        new("ALTER INDEX", "P", "ALTER INDEX |index_name| RENAME TO new_name;", "rename an index"),
        new("ALTER DATABASE", "S", "ALTER DATABASE |database_name| SET RECOVERY SIMPLE;", "change database options"),
        new("ALTER DATABASE", "P", "ALTER DATABASE |database_name| RENAME TO new_name;", "rename a database"),
        new("ALTER DATABASE", "M", "ALTER DATABASE |database_name| CHARACTER SET utf8mb4;", "change the default character set"),
        new("ALTER SCHEMA", "S", "ALTER SCHEMA |target_schema| TRANSFER dbo.object_name;", "move an object to another schema"),
        new("ALTER SCHEMA", "P", "ALTER SCHEMA |schema_name| RENAME TO new_name;", "rename a schema"),
        new("ALTER SEQUENCE", "S", "ALTER SEQUENCE dbo.|sequence_name| RESTART WITH 1;", "restart a sequence"),
        new("ALTER SEQUENCE", "P", "ALTER SEQUENCE |sequence_name| RESTART WITH 1;", "restart a sequence"),
        new("ALTER TRIGGER", "S", "ALTER TRIGGER dbo.|trigger_name|\nON dbo.table_name\nAFTER INSERT, UPDATE\nAS\nBEGIN\n    SET NOCOUNT ON;\n    \nEND", "change a trigger"),
        new("ALTER LOGIN", "S", "ALTER LOGIN |login_name| WITH PASSWORD = 'password';", "change a login's password"),
        new("ALTER USER", "S", "ALTER USER |user_name| WITH DEFAULT_SCHEMA = dbo;", "change a user"),
        new("ALTER USER", "P", "ALTER USER |user_name| WITH PASSWORD 'password';", "change a user's password"),
        new("ALTER USER", "M", "ALTER USER '|user_name|'@'%' IDENTIFIED BY 'password';", "change an account's password"),
        new("ALTER ROLE", "S", "ALTER ROLE |role_name| ADD MEMBER user_name;", "add a role member"),
        new("ALTER ROLE", "P", "ALTER ROLE |role_name| RENAME TO new_name;", "rename a role"),
        new("ALTER EXTENSION", "P", "ALTER EXTENSION |extension_name| UPDATE;", "update an extension"),
        new("REFRESH MATERIALIZED VIEW", "P", "REFRESH MATERIALIZED VIEW |view_name|;", "reload a materialized view"),

        // ----- DROP -----
        new("DROP TABLE", "S", "DROP TABLE IF EXISTS dbo.|table_name|;", "remove a table"),
        new("DROP TABLE", "PM", "DROP TABLE IF EXISTS |table_name|;", "remove a table"),
        new("DROP VIEW", "S", "DROP VIEW IF EXISTS dbo.|view_name|;", "remove a view"),
        new("DROP VIEW", "PM", "DROP VIEW IF EXISTS |view_name|;", "remove a view"),
        new("DROP MATERIALIZED VIEW", "P", "DROP MATERIALIZED VIEW IF EXISTS |view_name|;", "remove a materialized view"),
        new("DROP PROCEDURE", "S", "DROP PROCEDURE IF EXISTS dbo.|procedure_name|;", "remove a procedure"),
        new("DROP PROCEDURE", "PM", "DROP PROCEDURE IF EXISTS |procedure_name|;", "remove a procedure"),
        new("DROP FUNCTION", "S", "DROP FUNCTION IF EXISTS dbo.|function_name|;", "remove a function"),
        new("DROP FUNCTION", "PM", "DROP FUNCTION IF EXISTS |function_name|;", "remove a function"),
        new("DROP INDEX", "S", "DROP INDEX IF EXISTS |index_name| ON dbo.table_name;", "remove an index"),
        new("DROP INDEX", "P", "DROP INDEX IF EXISTS |index_name|;", "remove an index"),
        new("DROP INDEX", "M", "DROP INDEX |index_name| ON table_name;", "remove an index"),
        new("DROP DATABASE", "SPM", "DROP DATABASE IF EXISTS |database_name|;", "remove a database"),
        new("DROP SCHEMA", "S", "DROP SCHEMA IF EXISTS |schema_name|;", "remove an empty schema"),
        new("DROP SCHEMA", "P", "DROP SCHEMA IF EXISTS |schema_name|;", "remove an empty schema (CASCADE drops its objects)"),
        new("DROP SCHEMA", "M", "DROP SCHEMA IF EXISTS |schema_name|;", "remove a schema and everything in it"),
        new("DROP SEQUENCE", "S", "DROP SEQUENCE IF EXISTS dbo.|sequence_name|;", "remove a sequence"),
        new("DROP SEQUENCE", "P", "DROP SEQUENCE IF EXISTS |sequence_name|;", "remove a sequence"),
        new("DROP TRIGGER", "S", "DROP TRIGGER IF EXISTS dbo.|trigger_name|;", "remove a trigger"),
        new("DROP TRIGGER", "P", "DROP TRIGGER IF EXISTS |trigger_name| ON table_name;", "remove a trigger"),
        new("DROP TRIGGER", "M", "DROP TRIGGER IF EXISTS |trigger_name|;", "remove a trigger"),
        new("DROP TYPE", "S", "DROP TYPE IF EXISTS dbo.|type_name|;", "remove a type"),
        new("DROP TYPE", "P", "DROP TYPE IF EXISTS |type_name|;", "remove a type"),
        new("DROP SYNONYM", "S", "DROP SYNONYM IF EXISTS dbo.|synonym_name|;", "remove a synonym"),
        new("DROP EXTENSION", "P", "DROP EXTENSION IF EXISTS |extension_name|;", "remove an extension"),
        new("DROP EVENT", "M", "DROP EVENT IF EXISTS |event_name|;", "remove a scheduled event"),
        new("DROP LOGIN", "S", "DROP LOGIN |login_name|;", "remove a server login"),
        new("DROP USER", "SP", "DROP USER IF EXISTS |user_name|;", "remove a user"),
        new("DROP USER", "M", "DROP USER IF EXISTS '|user_name|'@'%';", "remove an account"),
        new("DROP ROLE", "SPM", "DROP ROLE IF EXISTS |role_name|;", "remove a role"),

        // ----- Other -----
        new("TRUNCATE TABLE", "S", "TRUNCATE TABLE dbo.|table_name|;", "remove all rows"),
        new("TRUNCATE TABLE", "PM", "TRUNCATE TABLE |table_name|;", "remove all rows"),
        new("GRANT", "S", "GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.|table_name| TO user_name;", "give permissions"),
        new("GRANT", "P", "GRANT SELECT, INSERT, UPDATE, DELETE ON |table_name| TO role_name;", "give permissions"),
        new("GRANT", "M", "GRANT SELECT, INSERT, UPDATE, DELETE ON database_name.|table_name| TO 'user_name'@'%';", "give permissions"),
        new("REVOKE", "S", "REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.|table_name| FROM user_name;", "take permissions away"),
        new("REVOKE", "P", "REVOKE SELECT, INSERT, UPDATE, DELETE ON |table_name| FROM role_name;", "take permissions away"),
        new("REVOKE", "M", "REVOKE SELECT, INSERT, UPDATE, DELETE ON database_name.|table_name| FROM 'user_name'@'%';", "take permissions away"),
        new("COMMENT ON TABLE", "P", "COMMENT ON TABLE |table_name| IS 'description';", "describe a table"),
        new("COMMENT ON COLUMN", "P", "COMMENT ON COLUMN |table_name.column_name| IS 'description';", "describe a column")
    ];

    /// <summary>Every label, whatever the engine: lets completion drop plain keywords these templates replace.</summary>
    public static readonly IReadOnlySet<string> Labels = All.Select(t => t.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The templates for <paramref name="providerKey"/> ("SqlServer", "Postgres", "MySql"); with no provider,
    /// one per label (SQL Server's form first).</summary>
    public static IReadOnlyList<CompletionItem> For(string? providerKey)
    {
        var engine = providerKey switch { "SqlServer" => 'S', "Postgres" => 'P', "MySql" => 'M', _ => (char?)null };
        return All.Where(t => engine is null || t.Engines.Contains(engine.Value))
            .DistinctBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .Select(ToItem)
            .ToList();
    }

    private static CompletionItem ToItem(Template t)
    {
        var open = t.Text.IndexOf('|');
        var close = t.Text.IndexOf('|', open + 1);
        var text = t.Text.Remove(close, 1).Remove(open, 1);
        return new CompletionItem(t.Label, text, CompletionKind.Ddl, t.Detail, open, close - open - 1);
    }
}
