using System.Data.Common;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A connection string a compose run gives its app, read for the server it names and given again at the host and
/// port this machine reaches that server on (the Migrations window's dotnet ef), nothing else in it changed.</summary>
public class ConnectionServerTests
{
    [Fact]
    public void A_URL_with_a_password_and_no_user_name_moves_to_another_host_and_port_with_the_rest_as_written()
    {
        ConnectionServer server = Read("redis://:secret@cache:6379");
        Assert.Equal(("cache", 6379), (server.Host, server.Port));
        Assert.Equal("redis://:secret@localhost:32768", server.At("localhost", 32768));
        Assert.Equal("redis://:s%20ecret@localhost:32768/0", Read("redis://:s%20ecret@cache:6379/0").At("localhost", 32768));
    }

    [Fact]
    public void A_URL_keeps_its_users_escapes_path_and_query_as_written_and_gains_a_port_it_did_not_name()
    {
        ConnectionServer server = Read("postgresql://user:pa%3Bss@db/app?sslmode=disable");
        Assert.Equal(("db", (int?)null), (server.Host, server.Port));
        Assert.Equal("postgresql://user:pa%3Bss@localhost:5433/app?sslmode=disable", server.At("localhost", 5433));
        Assert.Equal("mongodb://u:p%2Fw@localhost:1/x#f", Read("mongodb://u:p%2Fw@mongo:27017/x#f").At("localhost", 1));
        Assert.Equal("amqp://app:p@ss@localhost:2", Read("amqp://app:p@ss@rabbit:5672").At("localhost", 2));
    }

    [Fact]
    public void A_keyword_string_with_a_quoted_semicolon_changes_only_its_host_and_port()
    {
        const string Written = "Host=db;Password=\"a;b\"";
        ConnectionServer server = Read(Written);
        Assert.Equal(("db", (int?)null), (server.Host, server.Port));
        AssertMoved(Written, server.At("localhost", 32768), ("host", "localhost"), ("port", "32768"));
        AssertMoved("Host=db;Port=5432;Database=app;Username=postgres;Password='x\"y'", Read("Host=db;Port=5432;Database=app;Username=postgres;Password='x\"y'").At("localhost", 1),
            ("host", "localhost"), ("port", "1"));
    }

    [Fact]
    public void SQL_Servers_host_keeps_its_protocol_and_its_port_after_a_comma()
    {
        const string Written = "Server=tcp:sql,1433;Database=app;User Id=sa;Password=p;TrustServerCertificate=True";
        ConnectionServer server = Read(Written);
        Assert.Equal(("sql", (int?)1433), (server.Host, server.Port));
        AssertMoved(Written, server.At("localhost", 32768), ("server", "tcp:localhost,32768"));
        AssertMoved("Server=sql;Database=app", Read("Server=sql;Database=app").At("localhost", 2, besideHost: true), ("server", "localhost,2"));
        AssertMoved("Server=mysql;Database=app", Read("Server=mysql;Database=app").At("localhost", 3), ("server", "localhost"), ("port", "3"));
    }

    [Fact]
    public void A_keyword_string_no_reader_takes_names_its_host_yet_cannot_be_written_again()
    {
        ConnectionServer server = Read("Host=db;Port=5432;Password=\"a;b");
        Assert.Equal(("db", (int?)5432), (server.Host, server.Port));
        Assert.Null(server.At("localhost", 1));
    }

    [Fact]
    public void A_string_that_names_no_server_has_none()
    {
        string[] none = ["file:///data/app.db", "redis://", "redis://cache:port", "redis://cache:70000", "mongodb://a:1,b:2/x", "cache:6379,password=secret",
            "Database=app", "plain words", ""];
        Assert.All(none, value => Assert.Null(ConnectionServer.Of(value)));
    }

    private static ConnectionServer Read(string value) => ConnectionServer.Of(value) ?? throw new InvalidOperationException($"{value} was read as naming no server");

    /// <summary>A string written again has every keyword and value it had but those changed, which it has as given.</summary>
    private static void AssertMoved(string written, string? moved, params (string Key, string Value)[] changed)
    {
        Assert.NotNull(moved);
        DbConnectionStringBuilder before = new() { ConnectionString = written };
        DbConnectionStringBuilder after = new() { ConnectionString = moved };
        foreach ((string key, string value) in changed)
        {
            before[key] = value;
        }
        Assert.True(before.EquivalentTo(after), $"{written} was written again as {moved}, other than with only its host and port changed");
    }
}
