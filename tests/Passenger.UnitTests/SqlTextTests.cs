using Passenger.App.Database;
using Passenger.TestTables;

namespace Passenger.UnitTests;

/// <summary>A console's SQL split into what runs, as each database reads it: a semicolon inside a string, a quoted name, a
/// comment or a dollar quote ends nothing, a routine's body is one statement, and SQL Server's GO ends a batch; what each
/// run does to its database, which read-only and production connections hold it to; the parameters each database's driver
/// takes in it; the names its connection's schema does not hold; each database's plan read as one tree; its layout by
/// Reformat Code, comments and strings kept; rows written as each database's inserts; and what an import makes of a file's
/// text: a new column's type, a value of a column's type, and which column each of the file's goes into.</summary>
public sealed class SqlTextTests
{
    [Fact]
    public void Each_databases_statements_are_split_where_its_own_reader_ends_them()
    {
        Cases.Each(new (string Holds, DatabaseKind Kind, string Sql, string[] Runs)[]
        {
            ("statements end at their semicolons, blank ones passed over", DatabaseKind.Sqlite,
                "SELECT 1;\n;\n  SELECT 2 ;SELECT 3", ["SELECT 1", "SELECT 2", "SELECT 3"]),
            ("a semicolon in a string, a quoted name or a comment ends nothing", DatabaseKind.Postgres,
                "SELECT 'a;b', \"c;d\" -- e;f\nFROM t /* g; /* nested; */ h; */; SELECT 2",
                ["SELECT 'a;b', \"c;d\" -- e;f\nFROM t /* g; /* nested; */ h; */", "SELECT 2"]),
            ("PostgreSQL's dollar quotes hold a function's body, a parameter's $1 does not", DatabaseKind.Postgres,
                "CREATE FUNCTION f() RETURNS int AS $body$ SELECT 1; $body$ LANGUAGE sql; SELECT $1;",
                ["CREATE FUNCTION f() RETURNS int AS $body$ SELECT 1; $body$ LANGUAGE sql", "SELECT $1"]),
            ("MySQL's # comments, backticks and backslash escapes, and a routine's BEGIN … END with its IF", DatabaseKind.MySql,
                "SELECT 'it\\'s;' # x;\n, `a;b`; CREATE PROCEDURE p() BEGIN IF 1 THEN SELECT 1; END IF; SELECT 2; END; SELECT 3",
                ["SELECT 'it\\'s;' # x;\n, `a;b`", "CREATE PROCEDURE p() BEGIN IF 1 THEN SELECT 1; END IF; SELECT 2; END", "SELECT 3"]),
            ("SQL Server runs each batch between GO lines whole, its brackets quoting, a GO in a word or a string no batch's end",
                DatabaseKind.SqlServer, "DECLARE @n int = 1;\nSELECT [a;GO], 'GO' AS x, @n;\nGO\nSELECT 2;\n  go  \n",
                ["DECLARE @n int = 1;\nSELECT [a;GO], 'GO' AS x, @n;", "SELECT 2;"]),
            ("a transaction's BEGIN opens no block", DatabaseKind.SqlServer,
                "CREATE PROCEDURE p AS BEGIN BEGIN TRAN; UPDATE t SET a = 1; COMMIT; END\nGO", ["CREATE PROCEDURE p AS BEGIN BEGIN TRAN; UPDATE t SET a = 1; COMMIT; END"]),
        }, row => Assert.Equal(row.Runs, SqlText.Runs(row.Sql, row.Kind)), row => row.Holds);
        Cases.Each(new (string Holds, string Sql, int Caret, string? At)[]
        {
            ("the statement the caret stands in", "SELECT 1;\nSELECT 2;\nSELECT 3;", 12, "SELECT 2;"),
            ("the one the caret follows on its line", "SELECT 1;  \nSELECT 2;", 11, "SELECT 1;"),
            ("else the next, past a blank line", "SELECT 1;\n\n  SELECT 2;", 10, "SELECT 2;"),
            ("the last where the caret is past it", "SELECT 1;\nSELECT 2;\n\n", 20, "SELECT 2;"),
            ("none in a file of comments", "-- nothing\n/* here */", 3, null),
        }, row => Assert.Equal(row.At, SqlText.At(row.Sql, DatabaseKind.Postgres, row.Caret) is { } at ? row.Sql[at.Start..at.End] : null), row => row.Holds);
        Cases.Each(new (string Holds, DatabaseKind Kind, string Sql, SqlEffect Effect)[]
        {
            ("a query reads, its keywords as names and a row lock changing nothing", DatabaseKind.Postgres,
                "SELECT comment, t.update, x AS delete FROM t WHERE copy = 'DROP' ORDER BY load DESC FOR UPDATE", SqlEffect.Reads),
            ("an UPDATE, DELETE or INSERT changes data, and a MySQL REPLACE() or INSERT() call does not", DatabaseKind.MySql,
                "SELECT REPLACE(name, 'a', 'b'), INSERT(name, 1, 1, 'x') FROM t; UPDATE t SET a = 1", SqlEffect.ChangesData),
            ("DDL and TRUNCATE change the schema", DatabaseKind.Sqlite, "CREATE TABLE t (a int); TRUNCATE t", SqlEffect.ChangesSchema),
            ("a data-modifying CTE changes data", DatabaseKind.Postgres, "WITH gone AS (DELETE FROM t RETURNING *) SELECT * FROM gone", SqlEffect.ChangesData),
            ("EXPLAIN reads, and runs what it ANALYZEs", DatabaseKind.Postgres, "EXPLAIN DELETE FROM t; EXPLAIN (ANALYZE, BUFFERS) SELECT 1", SqlEffect.Reads),
            ("EXPLAIN ANALYZE of a DELETE changes data", DatabaseKind.Postgres, "EXPLAIN ANALYZE DELETE FROM t", SqlEffect.ChangesData),
            ("a SQL Server batch's statements without semicolons, and SELECT INTO making a table", DatabaseKind.SqlServer,
                "DECLARE @n int = 1\nIF @n > 0 SELECT a INTO copied FROM t", SqlEffect.ChangesSchema),
            ("a SQL Server batch's UPDATE after a SELECT", DatabaseKind.SqlServer, "SELECT 1\nUPDATE t SET a = 2", SqlEffect.ChangesData),
            ("a session's SET and a transaction's statements change nothing", DatabaseKind.Postgres, "SET search_path TO app; BEGIN; COMMIT", SqlEffect.Transaction),
            ("MySQL's SET GLOBAL changes the server", DatabaseKind.MySql, "SET GLOBAL max_connections = 10", SqlEffect.ChangesSchema),
            ("a statement of a kind not known may change data", DatabaseKind.Postgres, "FROBNICATE t", SqlEffect.ChangesData),
        }, row => Assert.Equal(row.Effect, SqlText.EffectOf(row.Sql, row.Kind)), row => row.Holds);
        Cases.Each(new (string Holds, DatabaseKind Kind, string Sql, SqlTransaction? Transaction)[]
        {
            ("COMMIT and its words", DatabaseKind.Postgres, "commit work;", SqlTransaction.Commit),
            ("PostgreSQL's END commits", DatabaseKind.Postgres, "END", SqlTransaction.Commit),
            ("SQL Server's ROLLBACK TRAN", DatabaseKind.SqlServer, "ROLLBACK TRAN", SqlTransaction.Rollback),
            ("a ROLLBACK TO a savepoint is no rollback of the transaction", DatabaseKind.Postgres, "ROLLBACK TO SAVEPOINT a", null),
            ("SQL Server's BEGIN opens a block, BEGIN TRAN a transaction", DatabaseKind.SqlServer, "BEGIN", null),
            ("START TRANSACTION", DatabaseKind.MySql, "START TRANSACTION", SqlTransaction.Begin),
            ("a COMMIT among other statements", DatabaseKind.Sqlite, "UPDATE t SET a = 1; COMMIT", null),
        }, row => Assert.Equal(row.Transaction, SqlText.TransactionOf(row.Sql, row.Kind)), row => row.Holds);
        Cases.Each(new (string Holds, DatabaseKind Kind, string[] Runs, string[] Asked)[]
        {
            ("SQL Server's @name, its batch's DECLAREd variables, @@ globals and a string's @ passed over", DatabaseKind.SqlServer,
                ["DECLARE @n int = 1, @m int; SELECT @n, @id, @@ROWCOUNT, @m FROM t WHERE x = '@no'"], ["@id"]),
            ("PostgreSQL's $n in their order and @name, a cast's :: and an operator's @> none", DatabaseKind.Postgres,
                ["SELECT $2::int, @name, a::text FROM t WHERE b @> '{}' AND c = $1"], ["@name", "$1", "$2"]),
            ("MySQL's ? each in turn across the statements and @name, a user variable they assign passed over", DatabaseKind.MySql,
                ["SET @a = 1", "SELECT ?, @a, @b FROM t WHERE c = ?"], ["@b", "?1", "?2"]),
            ("SQLite's ?, ?NNN, :name, @name and $name, none in a comment", DatabaseKind.Sqlite,
                ["SELECT ?, ?5, :a, @b, $c FROM t -- :no"], [":a", "@b", "$c", "?1", "?5"]),
            ("a routine's definition declares its own", DatabaseKind.SqlServer, ["CREATE PROCEDURE p @id int AS SELECT @id"], []),
        }, row => Assert.Equal(row.Asked, SqlParameters.Asked(SqlParameters.Of(row.Runs, row.Kind))), row => row.Holds);
        Cases.Each(new (string Holds, DatabaseKind Kind, string Written, object Value)[]
        {
            ("NULL is no value", DatabaseKind.MySql, " null ", DBNull.Value),
            ("a whole number", DatabaseKind.SqlServer, "42", 42L),
            ("quoted text, its doubled quote one", DatabaseKind.Sqlite, "'it''s'", "it's"),
            ("PostgreSQL's value is text it reads as its place's type", DatabaseKind.Postgres, "42", "42"),
        }, row => Assert.Equal(row.Value, SqlParameters.Value(row.Written, row.Kind)), row => row.Holds);
        SqlCatalog shop = new([new SqlTableInfo("", "Products", [("Id", "integer"), ("Name", "text"), ("Note", "text")]), new SqlTableInfo("", "Orders", [("Id", "integer"), ("Product", "integer")])]);
        SqlCatalog served = new([new SqlTableInfo("public", "products", [("id", "integer"), ("name", "text")])]);
        Cases.Each(new (string Holds, DatabaseKind Kind, string Sql, string[] Marked)[]
        {
            ("an unknown table, and a known one's unknown column after its alias", DatabaseKind.Sqlite, "SELECT p.Nme FROM Products p JOIN Ordrs o ON o.Id = p.Id", ["Nme", "Ordrs"]),
            ("a column alone where the statement's tables are known, its case as the database reads it", DatabaseKind.Sqlite,
                "select name, nme from products where idd = 1 order by Name", ["nme", "idd"]),
            ("PostgreSQL folds a name not quoted to lower case and reads a quoted one as written", DatabaseKind.Postgres, "SELECT \"Name\", NAME FROM Products", ["\"Name\""]),
            ("an INSERT's columns and an UPDATE's SET", DatabaseKind.Sqlite, "INSERT INTO Orders (Id, Prodct) VALUES (1, 2); UPDATE Products SET Nam = 'x' WHERE Id = 1",
                ["Prodct", "Nam"]),
            ("a CTE's, a derived table's, a function's rows, an alias, a parameter and the system's own passed over", DatabaseKind.Sqlite,
                "WITH c AS (SELECT 1 AS x) SELECT x, n FROM c, (SELECT 2 AS n) d, pragma_table_info('Products') WHERE x = :x; "
                + "SELECT COUNT(*) AS total FROM Products ORDER BY total; SELECT name FROM sqlite_schema", []),
            ("a table the SQL makes, a schema the schema read holds none of, and the system's passed over", DatabaseKind.Postgres,
                "CREATE TABLE made (a int); SELECT a FROM made; SELECT * FROM information_schema.tables; SELECT relname FROM pg_class", []),
        }, row => Assert.Equal(row.Marked, SqlChecks.Of(row.Sql, row.Kind, row.Kind == DatabaseKind.Postgres ? served : shop).Select(p => row.Sql[p.Start..p.End])), row => row.Holds);
        Cases.Each(new (string Holds, Func<PlanStep> Read, string[] Steps, string? Costliest)[]
        {
            ("PostgreSQL's JSON, a join's type with its node's, each step's cost its own less its steps'", () => SqlPlans.FromPostgres("""
                [{"Plan": {"Node Type": "Hash Join", "Join Type": "Inner", "Total Cost": 40.0, "Plan Rows": 10, "Hash Cond": "(o.product = p.id)", "Plans": [
                  {"Node Type": "Seq Scan", "Relation Name": "orders", "Alias": "o", "Total Cost": 30.0, "Plan Rows": 2000},
                  {"Node Type": "Hash", "Total Cost": 5.0, "Plan Rows": 3, "Plans": [{"Node Type": "Seq Scan", "Relation Name": "products", "Alias": "p", "Total Cost": 4.5, "Plan Rows": 3}]}]}}]
                """), ["Inner Hash Join", "Seq Scan", "Hash", "Seq Scan"], "Seq Scan on orders o"),
            ("MariaDB's JSON, its nested loop's tables", () => SqlPlans.FromMySql("""
                {"query_block": {"select_id": 1, "cost": 0.0123, "nested_loop": [{"table": {"table_name": "p", "access_type": "ALL", "rows": 3, "cost": 0.0101}},
                  {"table": {"table_name": "o", "access_type": "ref", "key": "product", "rows": 1, "cost": 0.0022}}]}}
                """), ["query block", "full scan", "key lookup"], "full scan on p"),
            ("MySQL's JSON, its costs as text in cost_info", () => SqlPlans.FromMySql("""
                {"query_block": {"select_id": 1, "cost_info": {"query_cost": "0.55"}, "table": {"table_name": "products", "access_type": "ALL", "rows_examined_per_scan": 3,
                  "cost_info": {"read_cost": "0.25", "eval_cost": "0.30", "prefix_cost": "0.55"}}}}
                """), ["query block", "full scan"], "full scan on products"),
            ("SQL Server's showplan, each RelOp under the nearest above it", () => SqlPlans.FromSqlServer("""
                <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple StatementType="SELECT">
                <QueryPlan><RelOp PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="1" EstimatedTotalSubtreeCost="0.0066"><NestedLoops>
                <RelOp PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="0.0032"><IndexScan><Object Table="[products]" Index="[PK_products]" /></IndexScan></RelOp>
                <RelOp PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimateRows="3" EstimatedTotalSubtreeCost="0.0033"><TableScan><Object Table="[orders]" /></TableScan></RelOp>
                </NestedLoops></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
                """), ["Nested Loops", "Clustered Index Seek", "Table Scan"], "Table Scan on orders"),
            ("SQLite's rows, each under its parent, with no costs", () => SqlPlans.FromSqlite([(2, 0, "SCAN Products"), (5, 0, "SEARCH Orders USING INTEGER PRIMARY KEY (rowid=?)")]),
                ["query plan", "SCAN Products", "SEARCH Orders USING INTEGER PRIMARY KEY (rowid=?)"], null),
        }, row =>
        {
            PlanStep plan = row.Read();
            Assert.Equal(row.Steps, SqlPlans.Each(plan).Select(s => s.Operation));
            Assert.Equal(row.Costliest, SqlPlans.Costliest(plan) is { } costliest ? $"{costliest.Operation} {costliest.Detail}".Trim() : null);
        }, row => row.Holds);
        Cases.Each(new (string Holds, DatabaseKind Kind, string Sql, string Laid)[]
        {
            ("a query's clauses each on a line, its items under the first, AND on a line of its own but BETWEEN's", DatabaseKind.Postgres,
                "select p.name, count(*) as n from products p join orders o on o.product = p.id where p.price between 1 and 5 and p.name like 'a%' "
                + "group by p.name order by n desc limit 10;",
                "SELECT p.name,\n       count(*) AS n\nFROM products p\nJOIN orders o ON o.product = p.id\nWHERE p.price BETWEEN 1 AND 5\n    AND p.name LIKE 'a%'\n"
                + "GROUP BY p.name\nORDER BY n DESC\nLIMIT 10;"),
            ("comments and strings kept as written, a line comment ending its line", DatabaseKind.Sqlite,
                "-- the cheap ones\nselect name, 'a;b' /* kept */ from Products -- trailing\nwhere Id<3;\n",
                "-- the cheap ones\nSELECT name,\n       'a;b' /* kept */\nFROM Products -- trailing\nWHERE Id < 3;\n"),
            ("MySQL's backticks and # comments", DatabaseKind.MySql, "select `a`.`b` from `t` # note\nwhere x = 1",
                "SELECT `a`.`b`\nFROM `t` # note\nWHERE x = 1"),
            ("SQL Server's GO lines, brackets and N'' strings, a SET's items under the first", DatabaseKind.SqlServer,
                "select top 5 [Name] from dbo.[Order Lines]\nGO\nupdate t set a = 1, b = N'x' where id = @id",
                "SELECT TOP 5 [Name]\nFROM dbo.[Order Lines]\nGO\nUPDATE t\nSET a = 1,\n    b = N'x'\nWHERE id = @id"),
            ("a routine's definition kept as written", DatabaseKind.Postgres, "create function f() returns int as $$ select 1 $$ language sql;\nselect 1",
                "create function f() returns int as $$ select 1 $$ language sql;\nSELECT 1"),
            ("VALUES' rows one to a line and a subquery indented, a sign kept against its number", DatabaseKind.Postgres,
                "insert into t (a, b) values (1, 'x'), (2, 'y'); delete from t where a in (select a from u where b = -1);",
                "INSERT INTO t (a, b)\nVALUES (1, 'x'),\n       (2, 'y');\nDELETE FROM t\nWHERE a IN (\n    SELECT a\n    FROM u\n    WHERE b = -1\n);"),
            ("a table's columns one to a line", DatabaseKind.Sqlite, "create table t (id integer primary key, name text not null, price decimal(6,2))",
                "CREATE TABLE t (\n    id integer PRIMARY KEY,\n    name text NOT NULL,\n    price decimal(6, 2)\n)"),
        }, row =>
        {
            Assert.Equal(row.Laid, SqlFormatter.Format(row.Sql, row.Kind));
            Assert.Equal(row.Laid, SqlFormatter.Format(row.Laid, row.Kind));
        }, row => row.Holds);
        object?[] values = [null, true, "it's a \\ path", new byte[] { 10, 255 }, new DateTime(2024, 5, 1, 13, 45, 0), 2.5m];
        Passenger.App.Tables.RowColumn[] columns = [new("id"), new("ok"), new("note"), new("data"), new("at"), new("price")];
        Cases.Each(new (string Holds, DatabaseKind Kind, string Table, string Inserts)[]
        {
            ("PostgreSQL's names double-quoted, TRUE, its bytea's hex", DatabaseKind.Postgres, "\"public\".\"t\"",
                "INSERT INTO \"public\".\"t\" (\"id\", \"ok\", \"note\", \"data\", \"at\", \"price\") VALUES (NULL, TRUE, 'it''s a \\ path', '\\x0aff'::bytea, '2024-05-01 13:45:00', 2.5);\n"),
            ("MySQL's backticks and a backslash doubled in its strings", DatabaseKind.MySql, "`t`",
                "INSERT INTO `t` (`id`, `ok`, `note`, `data`, `at`, `price`) VALUES (NULL, TRUE, 'it''s a \\\\ path', X'0AFF', '2024-05-01 13:45:00', 2.5);\n"),
            ("SQL Server's brackets, N'' text, 1 for true and 0x bytes", DatabaseKind.SqlServer, "[dbo].[t]",
                "INSERT INTO [dbo].[t] ([id], [ok], [note], [data], [at], [price]) VALUES (NULL, 1, N'it''s a \\ path', 0x0AFF, N'2024-05-01 13:45:00', 2.5);\n"),
        }, row => Assert.Equal(row.Inserts, Passenger.App.Tables.RowWriter.Written(into => new SqlInserts(into, new InsertTarget(row.Kind, row.Table), "\n"), columns, [values])),
            row => row.Holds);
        Cases.Each(new (string Sql, string? Table)[]
        {
            ("SELECT * FROM public.\"Order Lines\" o WHERE o.id IN (SELECT id FROM x)", "public.\"Order Lines\""),
            ("SELECT a FROM t JOIN u ON u.id = t.id", null),
            ("SELECT a FROM t, u", null),
            ("WITH c AS (SELECT 1) SELECT * FROM c", null),
        }, row => Assert.Equal(row.Table, SqlText.OnlyTable(row.Sql, DatabaseKind.Postgres)), row => row.Sql);
        Cases.Each(new (string Holds, DatabaseKind Kind, string?[] Values, string Type)[]
        {
            ("true and false", DatabaseKind.Postgres, ["true", null, "FALSE"], "boolean"),
            ("whole numbers", DatabaseKind.SqlServer, ["1", "-20", ""], "bigint"),
            ("numbers with their digits and places", DatabaseKind.MySql, ["12.5", "-1234.25", "7"], "DECIMAL(6,2)"),
            ("dates", DatabaseKind.Sqlite, ["2024-05-01", "1999-12-31"], "DATE"),
            ("dates and times, one with its zone", DatabaseKind.Postgres, ["2024-05-01 13:45:00", "2024-05-01T13:45:00Z"], "timestamptz"),
            ("text where they agree on nothing else", DatabaseKind.SqlServer, ["1", "two"], "nvarchar(max)"),
        }, row => Assert.Equal(row.Type, ImportTypes.Proposed(row.Values, row.Kind)), row => row.Holds);
        Cases.Each(new (string Text, Type Type, object? Value)[]
        {
            ("yes", typeof(bool), true),
            (" 42 ", typeof(int), 42),
            ("2.50", typeof(decimal), 2.50m),
            ("2024-05-01 13:45:00", typeof(DateTime), new DateTime(2024, 5, 1, 13, 45, 0, DateTimeKind.Utc)),
            ("AQL/", typeof(byte[]), new byte[] { 1, 2, 255 }),
            ("0x0aff", typeof(byte[]), new byte[] { 10, 255 }),
            ("forty", typeof(int), null),
            ("300", typeof(byte), null),
        }, row => Assert.Equal(row.Value, ImportTypes.TryConvert(row.Text, row.Type, out object value) ? value : null), row => $"{row.Text} as {row.Type.Name}");
        Cases.Each(new (string Holds, string[] File, bool Named, int?[] Into)[]
        {
            ("by name, case aside, a column the table lacks passed over", ["NAME", "id", "extra"], true, [1, 0, null]),
            ("a file naming none by place, a name matched first", ["column1", "name", "column3"], false, [0, 1, 2]),
        }, row => Assert.Equal(row.Into, ImportTypes.Mapped(row.File, ["id", "name", "price"], row.Named)), row => row.Holds);
    }
}
