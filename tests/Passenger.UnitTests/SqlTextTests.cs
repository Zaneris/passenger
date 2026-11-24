using Passenger.App.Database;
using Passenger.TestTables;

namespace Passenger.UnitTests;

/// <summary>A console's SQL split into what runs, as each database reads it: a semicolon inside a string, a quoted name, a
/// comment or a dollar quote ends nothing, a routine's body is one statement, and SQL Server's GO ends a batch.</summary>
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
    }
}
