using Passenger.App.Database;

namespace Passenger.UnitTests;

public sealed class DatabaseSqlTests
{
    [Theory]
    [InlineData(DatabaseKind.Sqlite)]
    [InlineData(DatabaseKind.Postgres)]
    [InlineData(DatabaseKind.MySql)]
    public void Unrestricted_star_queries_receive_a_server_limit(DatabaseKind kind)
    {
        Assert.Equal("select * from Products\nLIMIT 1000 ", DatabaseSql.Limited("select * from Products", kind));
        Assert.Equal("select p.* from Products p\nLIMIT 1000 ; -- done", DatabaseSql.Limited("select p.* from Products p; -- done", kind));
        Assert.Contains("\nLIMIT 1000 FOR UPDATE", DatabaseSql.Limited("SELECT * FROM Products FOR UPDATE", kind), StringComparison.Ordinal);
        Assert.Contains("\nLIMIT 1000", DatabaseSql.Limited("WITH p AS (SELECT * FROM Products LIMIT 2) SELECT * FROM p", kind), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT * FROM Products LIMIT 12")]
    [InlineData("SELECT * FROM Products FETCH FIRST 12 ROWS ONLY")]
    [InlineData("SELECT TOP (12) * FROM Products")]
    [InlineData("SELECT COUNT(*) FROM Products")]
    [InlineData("SELECT 'SELECT * FROM Products' AS Text")]
    [InlineData("SELECT $$* LIMIT$$ FROM Products")]
    [InlineData("UPDATE Products SET Name='*'")]
    [InlineData("SELECT * FROM Products; SELECT * FROM Orders")]
    public void Existing_limits_and_other_statements_are_kept(string sql)
    {
        Assert.Equal(sql, DatabaseSql.Limited(sql, DatabaseKind.Postgres));
    }

    [Fact]
    public void Comments_and_quoted_words_cannot_hide_an_unlimited_query()
    {
        string sql = "SELECT /* LIMIT 1 */ *, 'LIMIT' FROM [TOP] -- FETCH 1";
        Assert.EndsWith("\nLIMIT 1000 ", DatabaseSql.Limited(sql, DatabaseKind.Sqlite), StringComparison.Ordinal);
        Assert.Equal("SELECT DISTINCT TOP (1000) * FROM Products", DatabaseSql.Limited("SELECT DISTINCT * FROM Products", DatabaseKind.SqlServer));
        Assert.Equal("SELECT * FROM Products ORDER BY Id OFFSET 4 ROWS\nFETCH NEXT 1000 ROWS ONLY ;", DatabaseSql.Limited("SELECT * FROM Products ORDER BY Id OFFSET 4 ROWS;", DatabaseKind.SqlServer));
        Assert.EndsWith("ORDER BY (SELECT NULL)\nOFFSET 0 ROWS FETCH NEXT 1000 ROWS ONLY ", DatabaseSql.Limited("SELECT * FROM Products UNION SELECT * FROM Products", DatabaseKind.SqlServer), StringComparison.Ordinal);
    }
}
