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

    public static int Port(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres => 5432,
        DatabaseKind.SqlServer => 1433,
        _ => 3306,
    };
}
