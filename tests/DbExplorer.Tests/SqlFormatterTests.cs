using DbExplorer.Application;
using DbExplorer.Application.Query;
using DbExplorer.Application.Shortcuts;

namespace DbExplorer.Tests;

public class SqlFormatterTests
{
    private static string F(string sql, SqlFormatOptions? options = null) => SqlFormatter.Format(sql, options);

    /// <summary>Formatting only changes whitespace and keyword case, and a second pass changes nothing.</summary>
    private static string Checked(string sql, SqlFormatOptions? options = null)
    {
        var once = F(sql, options);
        Assert.Equal(once, F(once, options));
        if ((options ?? SqlFormatOptions.Default).Commas == SqlCommaStyle.EndOfLine)
            Assert.Equal(Squeeze(sql), Squeeze(once));
        return once;

        static string Squeeze(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
    }

    [Fact]
    public void Ctes_subqueries_and_case_are_indented_in_their_parentheses()
    {
        var formatted = Checked(
            "with recent as (select customer_id, max(created_at) as last_order from orders group by customer_id) " +
            "select c.id, case when r.last_order is null then 'idle' when r.last_order < now() then 'old' else 'new' end as segment " +
            "from customers c left join recent r on r.customer_id = c.id " +
            "where c.active = true and exists (select 1 from orders o where o.customer_id = c.id and o.status in ('paid', 'shipped') and o.total > 10)");
        Assert.Equal("""
            WITH recent AS (
                SELECT
                    customer_id,
                    MAX(created_at) AS last_order
                FROM orders
                GROUP BY customer_id
            )
            SELECT
                c.id,
                CASE
                    WHEN r.last_order IS NULL THEN 'idle'
                    WHEN r.last_order < NOW() THEN 'old'
                    ELSE 'new'
                END AS segment
            FROM customers c
            LEFT JOIN recent r
                ON r.customer_id = c.id
            WHERE c.active = TRUE
                AND EXISTS (
                    SELECT 1
                    FROM orders o
                    WHERE o.customer_id = c.id
                        AND o.status IN ('paid', 'shipped')
                        AND o.total > 10
                )
            """, formatted);
    }

    [Fact]
    public void Several_ctes_are_a_list_at_the_statement_level()
    {
        Assert.Equal("""
            WITH a AS (
                SELECT
                    x,
                    y,
                    z
                FROM t
            ),
            b AS (
                SELECT x
                FROM a
            )
            SELECT *
            FROM b
            """, Checked("with a as (select x, y, z from t), b as (select x from a) select * from b"));
    }

    [Fact]
    public void Short_case_and_subqueries_stay_on_one_line()
    {
        Assert.Equal("""
            SELECT
                id,
                CASE WHEN x > 0 THEN 'pos' ELSE 'neg' END AS sign,
                (SELECT MAX(v) FROM w WHERE w.id = t.id) AS mx
            FROM t
            WHERE id IN (SELECT id FROM u)
            """, Checked("select id, case when x > 0 then 'pos' else 'neg' end as sign, (select max(v) from w where w.id = t.id) as mx from t where id in (select id from u)"));
    }

    [Fact]
    public void Insert_update_delete_and_upserts()
    {
        Assert.Equal("""
            INSERT INTO t (a, b)
            VALUES
                (1, 'x'),
                (2, 'y');
            INSERT INTO t (a, b)
            VALUES (1, 'x')
            ON CONFLICT (a) DO UPDATE
            SET b = excluded.b;
            UPDATE t
            SET
                a = a + 1,
                b = 'z'
            WHERE id = -1;
            DELETE FROM t
            WHERE id IN (SELECT id FROM u);
            """, Checked("insert into t(a,b) values (1,'x'),(2,'y'); insert into t (a,b) values (1,'x') on conflict (a) do update set b = excluded.b; " +
                         "update t set a=a + 1, b='z' where id=-1; delete from t where id in (select id from u);"));
    }

    [Fact]
    public void Select_modifiers_stay_on_the_select_line_and_row_locks_are_not_clauses()
    {
        Assert.Equal("""
            SELECT TOP 10 *
            FROM dbo.T WITH (NOLOCK)
            WHERE x <> 1
            """, Checked("select top 10 * from dbo.T with (nolock) where x <> 1"));
        Assert.Equal("""
            SELECT DISTINCT
                a,
                b
            FROM t
            WHERE a IS DISTINCT FROM b
            FOR UPDATE
            """.Replace("\nFOR", " FOR"), Checked("select distinct a, b from t where a is distinct from b for update"));
        Assert.Equal("INSERT OR REPLACE INTO t (id)\nVALUES (1)", Checked("insert or replace into t (id) values (1)"));
        Assert.Equal("SELECT LEFT(name, 3)\nFROM t", Checked("select left(name, 3) from t"));
    }

    [Fact]
    public void Merge_actions_are_indented_under_their_when()
    {
        Assert.Equal("""
            MERGE INTO t
            USING s
                ON t.id = s.id
            WHEN MATCHED THEN
                UPDATE
                SET t.v = s.v
            WHEN NOT MATCHED THEN
                INSERT (id, v)
                VALUES (s.id, s.v);
            """, Checked("merge into t using s on t.id = s.id when matched then update set t.v = s.v when not matched then insert (id, v) values (s.id, s.v);"));
    }

    [Fact]
    public void Operators_casts_and_parameters_are_spaced_consistently()
    {
        Assert.Equal("SELECT *\nFROM t\nWHERE x::int = 1\n    AND y = :p\n    AND z >= $1\n    AND w <> -1",
            Checked("select * from t where x :: int=1 and y=:p and z>=$1 and w<>-1"));
        Assert.Equal("SELECT f(a => 1)", Checked("select f(a=>1)"));
    }

    [Fact]
    public void Names_that_are_keywords_are_not_cased_next_to_a_dot()
    {
        Assert.Equal("SELECT\n    u.user,\n    t.date\nFROM t", Checked("select u.user, t.date from t"));
    }

    [Fact]
    public void Comments_keep_their_place()
    {
        var formatted = Checked("""
            -- header
            select a, -- first
              b /* second */, c
            from t -- the table
            where x = 1 -- cond
              and y = 2
            """);
        Assert.Equal("""
            -- header
            SELECT
                a, -- first
                b /* second */,
                c
            FROM t -- the table
            WHERE x = 1 -- cond
                AND y = 2
            """, formatted);
    }

    [Fact]
    public void A_comma_after_a_line_comment_goes_before_the_comment()
    {
        var formatted = F("select a -- first\n, b from t");
        Assert.Equal("SELECT\n    a, -- first\n    b\nFROM t", formatted);
        Assert.Equal(formatted, F(formatted));
    }

    [Fact]
    public void Statements_keep_their_blank_lines_go_lines_and_delimiters()
    {
        Assert.Equal("SELECT 1;\n\nSELECT 2\nGO\nSELECT 3", Checked("select 1;\n\n\n\nselect 2\nGO\nselect 3"));
        Assert.Equal("SELECT 1;\nSELECT 2;", Checked("select 1; select 2;"));
    }

    [Fact]
    public void Create_table_gets_one_column_per_line_with_the_types_lined_up()
    {
        Assert.Equal("""
            CREATE TABLE public.orders (
                id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                customer_id int NOT NULL REFERENCES customers (id),
                total       numeric(12, 2) DEFAULT 0,
                CONSTRAINT ck CHECK (total >= 0)
            );
            """, Checked("create table public.orders (id bigint generated always as identity primary key, customer_id int not null references customers(id), total numeric(12,2) default 0, constraint ck check (total >= 0));"));
    }

    [Fact]
    public void Create_table_with_comments_keeps_its_lines()
    {
        const string sql = "CREATE TABLE t (\n    id int, -- key\n    name text\n);";
        Assert.Equal(sql, Checked(sql));
    }

    [Fact]
    public void Create_view_lays_out_its_query()
    {
        Assert.Equal("CREATE VIEW v AS\nSELECT\n    a,\n    b\nFROM t\nWHERE a > 0;",
            Checked("create view v as select a, b from t where a > 0;"));
        Assert.Equal("CREATE TABLE t2 AS\nSELECT *\nFROM t", Checked("create table t2 as select * from t"));
    }

    [Fact]
    public void Other_ddl_keeps_its_lines_with_keywords_cased()
    {
        Assert.Equal("CREATE INDEX ix ON t (a);\nALTER TABLE t ADD COLUMN note text;",
            Checked("create index ix on t (a);\nalter table t add column note text;"));
    }

    [Fact]
    public void Sql_server_procedure_written_on_one_line_opens_up_by_block()
    {
        var formatted = Checked("create procedure dbo.p @id int as begin set nocount on; if @id is null begin raiserror('no id', 16, 1); return; end " +
                                "select a from t where id = @id; end");
        Assert.Equal("""
            CREATE PROCEDURE dbo.p @id int AS
            BEGIN
                SET NOCOUNT ON;
                IF @id IS NULL BEGIN
                    RAISERROR('no id', 16, 1);
                    RETURN;
                END
                SELECT a
                FROM t
                WHERE id = @id;
            END
            """, formatted);
    }

    [Fact]
    public void Sql_server_blocks_without_semicolons_are_reindented_and_keep_their_lines()
    {
        var formatted = Checked("create procedure dbo.q\nas\nbegin\nset nocount on\nbegin try\n  select 1\n    from t\nend try\nbegin catch\nselect error_message()\nend catch\nend");
        Assert.Equal("""
            CREATE PROCEDURE dbo.q
            AS
            BEGIN
                SET NOCOUNT ON
                BEGIN TRY
                    SELECT 1
                        FROM t
                END TRY
                BEGIN CATCH
                    SELECT error_message()
                END CATCH
            END
            """, formatted);
    }

    [Fact]
    public void Plpgsql_function_body_in_dollar_quotes_is_formatted()
    {
        var formatted = Checked("""
            create or replace function f(p int) returns int language plpgsql as $$
            declare
            v int;
            begin
            if p > 1 then
            v := 1;
            elsif p = 0 then
            v := 0;
            else
            select count(*) into v from t where id = p and x = 1;
            end if;
            for r in select * from t loop
            raise notice '%', r.id;
            end loop;
            return v;
            exception when others then
            return -1;
            end;
            $$;
            """);
        Assert.Equal("""
            CREATE OR REPLACE FUNCTION f(p int) RETURNS int LANGUAGE plpgsql AS $$
            DECLARE
                v int;
            BEGIN
                IF p > 1 THEN
                    v := 1;
                ELSIF p = 0 THEN
                    v := 0;
                ELSE
                    SELECT COUNT(*)
                    INTO v
                    FROM t
                    WHERE id = p
                        AND x = 1;
                END IF;
                FOR r IN SELECT * FROM t LOOP
                    RAISE NOTICE '%', r.id;
                END LOOP;
                RETURN v;
            EXCEPTION
                WHEN OTHERS THEN
                    RETURN -1;
            END;
            $$;
            """, formatted);
    }

    [Fact]
    public void Do_block_on_one_line_opens_up()
    {
        Assert.Equal("""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'x') THEN
                    CREATE role x;
                END IF;
            END
            $$;
            """, Checked("do $$ begin if not exists (select 1 from pg_roles where rolname = 'x') then create role x; end if; end $$;"));
    }

    [Fact]
    public void Plpgsql_declare_section_written_on_one_line_opens_up()
    {
        Assert.Equal("CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$\nDECLARE\n    v int;\nBEGIN\n    RETURN v;\nEND;\n$$;",
            Checked("create function f() returns int language plpgsql as $$ declare v int; begin return v; end; $$;"));
        // T-SQL: DECLARE is a statement.
        Assert.Equal("DECLARE @v int;\nSET @v = 1;", Checked("declare @v int; set @v = 1;"));
    }

    [Fact]
    public void Bodies_in_other_languages_are_left_alone()
    {
        const string sql = "CREATE FUNCTION f() RETURNS int LANGUAGE plpython3u AS $$\nif x:\n  return 1\n$$;";
        Assert.Equal(sql, Checked(sql));
    }

    [Fact]
    public void Mysql_procedure_with_delimiter_and_end_while_stays_one_statement()
    {
        var formatted = Checked("""
            DELIMITER $$
            CREATE PROCEDURE p(IN x INT)
            BEGIN
            DECLARE done INT DEFAULT 0;
            WHILE x > 0 DO
            SET x = x - 1;
            END WHILE;
            IF x = 0 THEN SELECT 'zero'; ELSE SELECT 'other'; END IF;
            END$$
            DELIMITER ;
            """);
        Assert.Equal("""
            DELIMITER $$
            CREATE PROCEDURE p(IN x INT)
            BEGIN
                DECLARE done INT DEFAULT 0;
                WHILE x > 0 DO
                    SET x = x - 1;
                END WHILE;
                IF x = 0 THEN
                    SELECT 'zero';
                ELSE
                    SELECT 'other';
                END IF;
            END$$
            DELIMITER ;
            """, formatted);
    }

    [Fact]
    public void Multi_line_strings_and_comments_inside_blocks_are_not_reindented()
    {
        var formatted = Checked("create procedure p as\nbegin\n  select 'multi\n   line' as x;\n  /* block\n     comment */\n  select 1;\nend");
        Assert.Contains("'multi\n   line'", formatted);
        Assert.Contains("/* block\n     comment */", formatted);
    }

    [Fact]
    public void Tsql_if_without_begin_keeps_its_indented_body()
    {
        Assert.Equal("IF @b = 2\n    PRINT 'b'\nELSE\n    PRINT 'c'", Checked("if @b = 2\n  print 'b'\nelse\n  print 'c'"));
    }

    [Fact]
    public void Half_typed_sql_never_throws()
    {
        foreach (var sql in new[] { "select a, (b from t where (x = 1", "select case when", "begin", "end end", "(((", "'unterminated", "/* open", "select\n--", "$$", ")" })
            Assert.NotNull(F(sql));
    }

    [Fact]
    public void Windows_line_ends_are_kept()
    {
        Assert.Equal("SELECT\r\n    a,\r\n    b\r\nFROM t;\r\nSELECT 1\r\n", F("select a,b from t;\r\nselect 1\r\n"));
    }

    // ----- Options -----

    [Fact]
    public void Lower_case_keywords_two_spaces_and_leading_commas()
    {
        var options = new SqlFormatOptions { KeywordCase = SqlKeywordCase.Lower, IndentSize = 2, Commas = SqlCommaStyle.StartOfLine };
        Assert.Equal("""
            select
              a -- first
              , b
              , c
            from t
            where x = 1
              and y = 2
            order by
              a
              , b
            """, Checked("SELECT a, -- first\n b, c FROM t WHERE x = 1 AND y = 2 ORDER BY a, b", options));
    }

    [Fact]
    public void Tabs_items_on_one_line_and_and_or_inline()
    {
        var options = new SqlFormatOptions { UseTabs = true, OneItemPerLine = false, BreakAndOr = false, CompactShortExpressions = false };
        Assert.Equal("SELECT a, b\nFROM t\nWHERE x IN (\n\tSELECT y\n\tFROM z\n) AND q = 1\nORDER BY a, b",
            Checked("select a, b from t where x in (select y from z) and q = 1 order by a, b", options));
    }

    [Fact]
    public void Keywords_as_typed_are_left_alone()
    {
        Assert.Equal("Select a\nFrom t\nwhere b = 1", Checked("Select a From t where b = 1", new SqlFormatOptions { KeywordCase = SqlKeywordCase.AsTyped }));
    }

    [Fact]
    public void Leading_commas_in_create_table()
    {
        Assert.Equal("CREATE TABLE t (\n    id   int\n    , name text\n)",
            Checked("create table t (id int, name text)", new SqlFormatOptions { Commas = SqlCommaStyle.StartOfLine }));
    }

    [Fact]
    public void Options_out_of_range_are_normalized()
    {
        var options = new SqlFormatOptions { IndentSize = 99, KeywordCase = (SqlKeywordCase)42 }.Normalized();
        Assert.Equal(SqlFormatOptions.MaxIndentSize, options.IndentSize);
        Assert.Equal(SqlKeywordCase.Upper, options.KeywordCase);
    }

    // ----- Ranges, caret, one line -----

    [Fact]
    public void A_selection_inside_indented_code_keeps_the_indentation_of_its_line()
    {
        const string document = "BEGIN\n    select a, b from t;\nEND";
        var start = document.IndexOf("select", StringComparison.Ordinal);
        var edit = SqlFormatter.FormatRange(document, start - 4, document.IndexOf(';') + 1 - (start - 4));
        Assert.Equal(start, edit.Start);
        Assert.Equal("SELECT\n        a,\n        b\n    FROM t;", edit.Text);
    }

    [Fact]
    public void The_caret_stays_on_the_same_token()
    {
        const string before = "select a,b from t";
        var after = F(before);
        var caret = before.IndexOf("from", StringComparison.Ordinal) + 2;
        Assert.Equal("FROM", after.Substring(SqlFormatter.MapOffset(before, after, caret) - 2, 4));
    }

    [Fact]
    public void Compact_joins_a_statement_into_one_line()
    {
        Assert.Equal("SELECT a, b /* first */ FROM t WHERE (x = 1) AND y = 'a  b';",
            SqlFormatter.Compact("SELECT a,\n    b -- first\nFROM t\nWHERE ( x = 1 )\n    AND y = 'a  b';"));
        Assert.Equal("select 1\nGO\nselect 2", SqlFormatter.Compact("select 1\nGO\nselect 2"));
    }

    [Fact]
    public void Indent_lines_skips_lines_inside_strings()
    {
        Assert.Equal("  a\n  'x\ny'\n\n  b", SqlFormatter.IndentLines("a\n'x\ny'\n\nb", "  "));
    }

    // ----- Splitting -----

    [Fact]
    public void End_if_and_end_loop_do_not_end_the_enclosing_block()
    {
        const string sql = "CREATE PROCEDURE p() BEGIN WHILE x > 0 DO SET x = x - 1; END WHILE; IF x THEN SET y = 1; END IF; SET z = 1; END;\nSELECT 1;";
        Assert.Equal(2, SqlScriptTools.SplitStatements(sql).Count);
        // T-SQL has no END IF: there END is the block's and IF starts the next statement.
        Assert.Equal(2, SqlScriptTools.SplitStatements("IF @a = 1 BEGIN SELECT 1 END IF @b = 2 SELECT 2;\nSELECT 3;").Count);
    }

    // ----- Settings and shortcuts -----

    [Fact]
    public void Format_options_are_saved_with_the_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbx-format-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var settings = new AppSettingsService(paths);
            Assert.Equal(SqlFormatOptions.Default, settings.SqlFormat);
            settings.SetSqlFormat(new SqlFormatOptions { KeywordCase = SqlKeywordCase.Lower, UseTabs = true, Commas = SqlCommaStyle.StartOfLine });
            var again = new AppSettingsService(paths);
            Assert.Equal(SqlKeywordCase.Lower, again.SqlFormat.KeywordCase);
            Assert.True(again.SqlFormat.UseTabs);
            Assert.Equal(SqlCommaStyle.StartOfLine, again.SqlFormat.Commas);
            again.ResetPreferences();
            Assert.Equal(SqlFormatOptions.Default, new AppSettingsService(paths).SqlFormat);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Format_statement_and_join_into_one_line_have_shortcuts()
    {
        var all = ShortcutCommands.All;
        Assert.Contains(all, c => c.Id == ShortcutCommands.FormatStatement && c.Defaults.Any(k => k.ToString() == "Ctrl+Alt+F"));
        Assert.Contains(all, c => c.Id == ShortcutCommands.CompactSql && c.Defaults.Any(k => k.ToString() == "Ctrl+Alt+Shift+F"));
    }
}
