using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Passenger.App.Database;

/// <summary>Each database's name as a person knows it, its connection strings' builder and its server's usual port.</summary>
public static partial class DatabaseKinds
{
    public static string Title(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Sqlite => "SQLite",
        DatabaseKind.Postgres => "PostgreSQL",
        DatabaseKind.SqlServer => "SQL Server",
        DatabaseKind.MySql => "MySQL or MariaDB",
        DatabaseKind.DuckDb => "DuckDB",
        DatabaseKind.ClickHouse => "ClickHouse",
        DatabaseKind.MongoDb => "MongoDB",
        DatabaseKind.CosmosDb => "Cosmos DB",
        DatabaseKind.Redis => "Redis",
        DatabaseKind.DynamoDb => "DynamoDB",
        DatabaseKind.Cassandra => "Cassandra",
        _ => "Snowflake",
    };

    /// <summary>The kinds as the connection dialog offers them, in <see cref="DatabaseKind"/>'s order.</summary>
    public static IReadOnlyList<string> Offered { get; } = ["SQLite", "PostgreSQL", "SQL Server", "MySQL / MariaDB", "DuckDB", "ClickHouse", "Snowflake", "MongoDB", "Cosmos DB", "Redis", "DynamoDB", "Cassandra"];

    /// <summary>Whether the kind's database is a file of its own (SQLite's, DuckDB's), opened with no server, port or tunnel.</summary>
    public static bool IsFile(DatabaseKind kind) => kind is DatabaseKind.Sqlite or DatabaseKind.DuckDb;

    /// <summary>Whether a database is its one schema (MySQL's, ClickHouse's), which the explorer leaves out.</summary>
    public static bool SchemaIsDatabase(DatabaseKind kind) => kind is DatabaseKind.MySql or DatabaseKind.ClickHouse || !SpeaksSql(kind);

    /// <summary>Whether the kind holds a statement's changes in a transaction its console can open and end: ClickHouse's
    /// statements each stand alone.</summary>
    public static bool HasTransactions(DatabaseKind kind) => kind != DatabaseKind.ClickHouse && SpeaksSql(kind);

    /// <summary>Whether the explorer's tables take changes row by row (written by their primary key): ClickHouse changes rows
    /// by mutations, Snowflake keeps no key it enforces, and Cassandra writes by its statements alone.</summary>
    public static bool EditsRows(DatabaseKind kind) => kind is not (DatabaseKind.ClickHouse or DatabaseKind.Snowflake or DatabaseKind.Cassandra);

    /// <summary>The kind's own builder of a connection string, which reads and writes its keys as its driver does; DuckDB's,
    /// ClickHouse's and Snowflake's a plain one, their drivers reading the keys it keeps.</summary>
    public static DbConnectionStringBuilder Builder(DatabaseKind kind, string connection) => kind switch
    {
        DatabaseKind.Sqlite => new SqliteConnectionStringBuilder(connection),
        DatabaseKind.Postgres => new NpgsqlConnectionStringBuilder(connection),
        DatabaseKind.SqlServer => new SqlConnectionStringBuilder(connection),
        DatabaseKind.MySql => new MySqlConnectionStringBuilder(connection),
        _ => new DbConnectionStringBuilder { ConnectionString = connection },
    };

    /// <summary>A plain connection string's value for the first of <paramref name="keys"/> it holds, letter case aside; null
    /// where it holds none.</summary>
    public static string? Value(string connection, params ReadOnlySpan<string> keys)
    {
        DbConnectionStringBuilder read = new() { ConnectionString = connection };
        foreach (string key in keys)
        {
            if (read.TryGetValue(key, out object? value) && Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) is { Length: > 0 } text)
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>A plain connection string with one key's value set, written under the name it already has where it has one.</summary>
    public static string With(string connection, string key, string value)
    {
        DbConnectionStringBuilder written = new() { ConnectionString = connection };
        string named = written.Keys.Cast<string>().FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
        written[named] = value;
        return written.ConnectionString;
    }

    /// <summary>The file a file kind's connection string opens; empty for DuckDB's database in memory.</summary>
    public static string FileOf(DatabaseKind kind, string connection) => kind == DatabaseKind.Sqlite
        ? new SqliteConnectionStringBuilder(connection).DataSource
        : Value(connection, "Data Source", "DataSource") is { } file && file != ":memory:" ? file : string.Empty;

    /// <summary>A Snowflake account's host: the one its connection names, else the account's own.</summary>
    public static string SnowflakeHost(string connection) =>
        Value(connection, "host") ?? $"{Value(connection, "account") ?? string.Empty}.snowflakecomputing.com";

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
            case DatabaseKind.ClickHouse:
                return (Value(connection, "Host", "Server") ?? "localhost",
                    int.TryParse(Value(connection, "Port"), System.Globalization.CultureInfo.InvariantCulture, out int given) ? given : Port(kind));
            case DatabaseKind.Snowflake:
                return (SnowflakeHost(connection), int.TryParse(Value(connection, "port"), System.Globalization.CultureInfo.InvariantCulture, out int told) ? told : Port(kind));
            case DatabaseKind.MySql:
                MySqlConnectionStringBuilder my = new(connection);
                return (my.Server, (int)my.Port);
            case DatabaseKind.MongoDb when Value(connection, "Uri") is { } uri:
                MongoDB.Driver.MongoServerAddress first = new MongoDB.Driver.MongoUrl(uri).Servers.FirstOrDefault() ?? new MongoDB.Driver.MongoServerAddress("localhost", Port(kind));
                return (first.Host, first.Port);
            case DatabaseKind.MongoDb:
                return (Value(connection, "Host", "Server") ?? "localhost",
                    int.TryParse(Value(connection, "Port"), System.Globalization.CultureInfo.InvariantCulture, out int named) ? named : Port(kind));
            case DatabaseKind.Redis when Value(connection, "Configuration") is { } configuration:
                StackExchange.Redis.ConfigurationOptions options = StackExchange.Redis.ConfigurationOptions.Parse(configuration);
                return options.EndPoints.FirstOrDefault() switch
                {
                    System.Net.DnsEndPoint dns => (dns.Host, dns.Port == 0 ? Port(kind) : dns.Port),
                    System.Net.IPEndPoint ip => (ip.Address.ToString(), ip.Port == 0 ? Port(kind) : ip.Port),
                    _ => ("localhost", Port(kind)),
                };
            case DatabaseKind.Redis:
                return (Value(connection, "Host", "Server") ?? "localhost",
                    int.TryParse(Value(connection, "Port"), System.Globalization.CultureInfo.InvariantCulture, out int redisPort) ? redisPort : Port(kind));
            case DatabaseKind.Cassandra:
                return ((Value(connection, "Contact Points", "ContactPoints", "Host") ?? "localhost").Split(',')[0].Trim(),
                    int.TryParse(Value(connection, "Port"), System.Globalization.CultureInfo.InvariantCulture, out int cassandraPort) ? cassandraPort : Port(kind));
            case DatabaseKind.DynamoDb when Uri.TryCreate(Value(connection, "ServiceURL", "Endpoint"), UriKind.Absolute, out Uri? service):
                return (service.Host, service.Port);
            case DatabaseKind.DynamoDb:
                return ($"dynamodb.{Value(connection, "Region") ?? "us-east-1"}.amazonaws.com", 443);
            case DatabaseKind.CosmosDb when Uri.TryCreate(Value(connection, "AccountEndpoint"), UriKind.Absolute, out Uri? account):
                return (account.Host, account.Port);
            default:
                return (string.Empty, 0);
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
            case DatabaseKind.ClickHouse:
                return With(With(connection, "Host", "127.0.0.1"), "Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            default:
                return new MySqlConnectionStringBuilder(connection) { Server = "127.0.0.1", Port = (uint)port }.ConnectionString;
        }
    }

    /// <summary>The kind of database a .NET connection's type is of, by its provider; null for another.</summary>
    public static DatabaseKind? OfConnection(string type) =>
        type.Contains("Mongo", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.MongoDb
        : type.Contains("Redis", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.Redis
        : type.Contains("DynamoDB", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.DynamoDb
        : type.Contains("Cassandra", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.Cassandra
        : type.Contains("Cosmos", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.CosmosDb
        : type.Contains("DuckDB", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.DuckDb
        : type.Contains("ClickHouse", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.ClickHouse
        : type.Contains("Snowflake", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.Snowflake
        : type.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? DatabaseKind.Sqlite
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
        DatabaseKind.ClickHouse or DatabaseKind.Snowflake when Uri.TryCreate(source.Contains("://", StringComparison.Ordinal) ? source : $"http://{source}", UriKind.Absolute, out Uri? server) =>
            (server.Host, server.IsDefaultPort && !source.Contains(':', StringComparison.Ordinal) ? null : server.Port),
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
        DatabaseKind.ClickHouse => 8123,
        DatabaseKind.Snowflake => 443,
        DatabaseKind.MongoDb => 27017,
        DatabaseKind.CosmosDb => 8081,
        DatabaseKind.Redis => 6379,
        DatabaseKind.DynamoDb => 8000,
        DatabaseKind.Cassandra => 9042,
        _ => 3306,
    };
}
