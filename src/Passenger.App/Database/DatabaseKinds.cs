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

    public static int Port(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres => 5432,
        DatabaseKind.SqlServer => 1433,
        _ => 3306,
    };
}
