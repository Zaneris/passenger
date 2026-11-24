using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Passenger.App.Database;

/// <summary>Each database's name as a person knows it, its connection strings' builder and its server's usual port.</summary>
public static class DatabaseKinds
{
    public static string Title(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Sqlite => "SQLite",
        DatabaseKind.Postgres => "PostgreSQL",
        DatabaseKind.SqlServer => "SQL Server",
        _ => "MySQL or MariaDB",
    };

    /// <summary>The kind's own builder of a connection string, which reads and writes its keys as its driver does.</summary>
    public static DbConnectionStringBuilder Builder(DatabaseKind kind, string connection) => kind switch
    {
        DatabaseKind.Sqlite => new SqliteConnectionStringBuilder(connection),
        DatabaseKind.Postgres => new NpgsqlConnectionStringBuilder(connection),
        DatabaseKind.SqlServer => new SqlConnectionStringBuilder(connection),
        _ => new MySqlConnectionStringBuilder(connection),
    };

    /// <summary>A connection string with another's keys laid over it, each as its driver names it.</summary>
    public static string LaidOver(DatabaseKind kind, string connection, string over)
    {
        if (over.Length == 0)
        {
            return connection;
        }
        DbConnectionStringBuilder laid = Builder(kind, connection);
        DbConnectionStringBuilder given = new() { ConnectionString = over };
        foreach (string key in given.Keys)
        {
            laid[key] = given[key];
        }
        return laid.ConnectionString;
    }

    /// <summary>The host and port a connection string reaches its server at, its driver's default port where it names none.</summary>
    public static (string Host, int Port) Endpoint(DatabaseKind kind, string connection)
    {
        switch (kind)
        {
            case DatabaseKind.Postgres:
                NpgsqlConnectionStringBuilder pg = new(connection);
                return (pg.Host ?? "localhost", pg.Port);
            case DatabaseKind.SqlServer:
                string source = new SqlConnectionStringBuilder(connection).DataSource;
                string address = source.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? source[4..] : source;
                string[] parts = address.Split(',', 2);
                return (parts[0].Split('\\')[0].Trim(), parts.Length == 2 && int.TryParse(parts[1].Trim(), out int number) ? number : Port(kind));
            default:
                MySqlConnectionStringBuilder my = new(connection);
                return (my.Server, (int)my.Port);
        }
    }

    /// <summary>A connection string reaching its server at a loopback port, its tunnel's, where it reached <paramref name="host"/>:
    /// SQL Server still checking its certificate's name as <paramref name="host"/>'s.</summary>
    public static string AtLoopback(DatabaseKind kind, string connection, int port, string host)
    {
        switch (kind)
        {
            case DatabaseKind.Postgres:
                return new NpgsqlConnectionStringBuilder(connection) { Host = "127.0.0.1", Port = port }.ConnectionString;
            case DatabaseKind.SqlServer:
                SqlConnectionStringBuilder sql = new(connection) { DataSource = $"127.0.0.1,{port}" };
                if (string.IsNullOrEmpty(sql.HostNameInCertificate))
                {
                    sql.HostNameInCertificate = host;
                }
                return sql.ConnectionString;
            default:
                return new MySqlConnectionStringBuilder(connection) { Server = "127.0.0.1", Port = (uint)port }.ConnectionString;
        }
    }

    /// <summary>The kind of database a .NET connection's type is of, by its provider; null for another.</summary>
    public static DatabaseKind? OfConnection(string type) =>
        type.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.Sqlite
        : type.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.Postgres
        : type.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.SqlServer
        : type.Contains("MySql", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.MySql
        : null;

    /// <summary>The host and port a connection's data source names, as its driver gives it (PostgreSQL's tcp://host:port,
    /// SQL Server's host,port); MySQL's, its host alone, with no port.</summary>
    public static (string Host, int? Port) EndpointOfSource(DatabaseKind kind, string source) => kind switch
    {
        DatabaseKind.Postgres when Uri.TryCreate(source, UriKind.Absolute, out Uri? address) && address.Scheme == "tcp" =>
            (address.Host, address.Port > 0 ? address.Port : Port(kind)),
        DatabaseKind.Postgres => (source, Port(kind)),
        DatabaseKind.SqlServer => Endpoint(kind, new SqlConnectionStringBuilder { DataSource = source }.ConnectionString),
        _ => (source.Split(',')[0].Trim(), null),
    };

    /// <summary>Whether two hosts name one server: the same name, or both this machine's loopback.</summary>
    public static bool SameHost(string one, string other) =>
        one.Equals(other, StringComparison.OrdinalIgnoreCase) || Loopback(one) && Loopback(other);

    private static bool Loopback(string host) => host.Trim('[', ']') is "localhost" or "127.0.0.1" or "::1" or "." or "(local)";

    public static int Port(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres => 5432,
        DatabaseKind.SqlServer => 1433,
        _ => 3306,
    };
}
