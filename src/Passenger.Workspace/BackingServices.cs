using System.Text.Json;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>What a project talks to (<see cref="BackingService.Needs"/>): the backing services its packages point to, and
/// the connection strings of its appsettings.json that reach them.</summary>
public sealed record BackingNeeds(IReadOnlyList<BackingService> Services, IReadOnlyList<BackingConnection> Connections);

/// <summary>A connection string an app reads by <paramref name="Name"/>, its value in appsettings.json, and the backing
/// service that value's shape reaches.</summary>
public sealed record BackingConnection(string Name, string Value, BackingService To);

/// <summary>A database, cache or broker a project's packages say it talks to, which Add > Docker Compose runs beside it and
/// Add > Aspire Orchestration adds to the AppHost: its compose service, image, the packages that point to it, a healthcheck
/// its dependents wait for, how a connection string reaches it, and Aspire's hosting integration for it (its package and
/// the AppHost's method that adds it).</summary>
public sealed partial record BackingService(string Service, string Image, string[] Packages, string[] Words, string Test, string? Note,
    string AspirePackage, string AspireMethod)
{
    public static readonly IReadOnlyList<BackingService> Known =
    [
        new("postgres", "postgres:17", ["Npgsql", "Npgsql.EntityFrameworkCore.PostgreSQL", "Aspire.Npgsql", "Aspire.Npgsql.EntityFrameworkCore.PostgreSQL"],
            ["postgres", "npgsql", "pg"], "[\"CMD-SHELL\", \"pg_isready -U postgres\"]", null, "Aspire.Hosting.PostgreSQL", "AddPostgres"),
        new("sqlserver", "mcr.microsoft.com/mssql/server:2022-latest",
            ["Microsoft.Data.SqlClient", "System.Data.SqlClient", "Microsoft.EntityFrameworkCore.SqlServer", "Aspire.Microsoft.Data.SqlClient", "Aspire.Microsoft.EntityFrameworkCore.SqlServer"],
            ["sql", "mssql"], "[\"CMD-SHELL\", \"/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \\\"$$MSSQL_SA_PASSWORD\\\" -C -Q 'SELECT 1' || exit 1\"]",
            "SQL Server's image has no arm64 build: on an Arm machine (Apple silicon) it runs as amd64 under emulation", "Aspire.Hosting.SqlServer", "AddSqlServer"),
        new("redis", "redis:7", ["StackExchange.Redis", "Microsoft.Extensions.Caching.StackExchangeRedis", "Aspire.StackExchange.Redis"],
            ["redis", "cache"], "[\"CMD\", \"redis-cli\", \"ping\"]", null, "Aspire.Hosting.Redis", "AddRedis"),
        new("rabbitmq", "rabbitmq:4", ["RabbitMQ.Client", "Aspire.RabbitMQ.Client", "MassTransit.RabbitMQ"],
            ["rabbit", "amqp", "bus", "messag", "queue"], "[\"CMD\", \"rabbitmq-diagnostics\", \"-q\", \"ping\"]", null, "Aspire.Hosting.RabbitMQ", "AddRabbitMQ"),
        new("mongo", "mongo:8", ["MongoDB.Driver", "MongoDB.EntityFrameworkCore", "Aspire.MongoDB.Driver"],
            ["mongo"], "[\"CMD\", \"mongosh\", \"--quiet\", \"--eval\", \"db.adminCommand('ping')\"]", null, "Aspire.Hosting.MongoDB", "AddMongoDB"),
    ];

    /// <summary>The services a project's packages point to, in <see cref="Known"/>'s order.</summary>
    public static IReadOnlyList<BackingService> For(IEnumerable<string> packages)
    {
        HashSet<string> named = new(packages, StringComparer.OrdinalIgnoreCase);
        return [.. Known.Where(k => k.Packages.Any(named.Contains))];
    }

    /// <summary>What a project talks to, one reading for Add > Docker Compose and Add > Aspire Orchestration alike: the backing
    /// services its packages point to (of those chosen, when a choice is given) and each connection string its
    /// appsettings.json declares that reaches one of them (<see cref="Reached"/>), by the name the app reads it by.</summary>
    public static BackingNeeds Needs(ProjectModel project, IReadOnlyCollection<string>? chosen = null)
    {
        IReadOnlyList<BackingService> services = [.. For(project.Packages.Select(p => p.Name)).Where(b => chosen?.Contains(b.Service) ?? true)];
        return new BackingNeeds(services, [.. ConnectionStrings(project.Folder)
            .Select(c => (c.Name, c.Value, To: Reached(c.Name, c.Value, services)))
            .Where(c => c.To is not null).Select(c => new BackingConnection(c.Name, c.Value, c.To!))]);
    }

    /// <summary>Whether it keeps databases a connection string names (Postgres, SQL Server, Mongo), which an AppHost adds to its
    /// server one by one (AddDatabase), where the others are reached whole.</summary>
    public bool HasDatabases => Service is "postgres" or "sqlserver" or "mongo";

    /// <summary>The connection strings a project's appsettings.json declares (ConnectionStrings), or another settings file
    /// of its folder (appsettings.Development.json), each name with its value (empty for one that is no string), in its order.</summary>
    public static IReadOnlyList<(string Name, string Value)> ConnectionStrings(string folder, string named = "appsettings.json")
    {
        string settings = System.IO.Path.Combine(folder, named);
        try
        {
            if (FileText.Load(settings) is not { } file)
            {
                return [];
            }
            using JsonDocument read = JsonDocument.Parse(file.Text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return read.RootElement.ValueKind == JsonValueKind.Object && read.RootElement.TryGetProperty("ConnectionStrings", out JsonElement strings)
                && strings.ValueKind == JsonValueKind.Object
                ? [.. strings.EnumerateObject().Select(s => (s.Name, s.Value.ValueKind == JsonValueKind.String ? s.Value.GetString() ?? string.Empty : string.Empty))]
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>A connection string a project's own settings give the app as it runs in an environment: its
    /// appsettings.{environment}.json's, else its appsettings.json's (letter case aside, as configuration keys are), with the
    /// file's name; null where neither gives one that is not empty. A compose file's check and Aspire's alike read it.</summary>
    public static (string File, string Value)? Configured(string folder, string name, string environment)
    {
        foreach (string file in (string[])[$"appsettings.{environment}.json", "appsettings.json"])
        {
            if (ConnectionStrings(folder, file).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } value)
            {
                return (file, value);
            }
        }
        return null;
    }

    /// <summary>The backing service an app's connection string (its name, and its value in appsettings.json) reaches, by
    /// the value's own shape among those the project's packages call for: Host= is Npgsql's (postgres), Server= or Data
    /// Source= with SqlClient SQL Server's (but a file's, SQLite's), mongodb:// Mongo's, amqp:// RabbitMQ's, host:port
    /// with options Redis's; an empty one by the words of its name (Cache: redis). Null for any other, left as the app has it.</summary>
    public static BackingService? Reached(string name, string value, IReadOnlyList<BackingService> backing)
    {
        BackingService? Named(string service) => backing.FirstOrDefault(b => b.Service == service);
        string text = value.Trim();
        if (text.Length == 0)
        {
            return backing.FirstOrDefault(b => b.Words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        if (text.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("mongodb+srv://", StringComparison.OrdinalIgnoreCase))
        {
            return Named("mongo");
        }
        if (text.StartsWith("amqp://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("amqps://", StringComparison.OrdinalIgnoreCase))
        {
            return Named("rabbitmq");
        }
        if (text.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            return Named("redis");
        }
        if (RedisOptions().IsMatch(text))
        {
            // StackExchange.Redis's host:port and its options (localhost:6379,abortConnect=false).
            return Named("redis");
        }
        if (Keywords(text) is not { } keys)
        {
            return null;
        }
        if (keys.ContainsKey("host"))
        {
            return Named("postgres");
        }
        string? source = keys.GetValueOrDefault("server") ?? keys.GetValueOrDefault("data source") ?? keys.GetValueOrDefault("address") ?? keys.GetValueOrDefault("addr");
        if (source is null || IsFile(source))
        {
            return null;
        }
        bool npgsql = keys.ContainsKey("username");
        return npgsql ? Named("postgres") : Named("sqlserver");
    }

    /// <summary>The host a connection string reaches when it is this machine's own (localhost, 127.0.0.1, ::1), which in a
    /// container is the container itself: read in the shapes <see cref="Reached"/> reads (a URL's host, Redis's host:port,
    /// Host=, Server=, Data Source= or Address= but a file's); null for any other host or shape.</summary>
    public static string? LocalHost(string value)
    {
        string text = value.Trim();
        string? host = null;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            host = Uri.TryCreate(text.Replace("mongodb+srv://", "mongodb://", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out Uri? url) ? url.Host : null;
        }
        else if (RedisOptions().IsMatch(text))
        {
            host = text.Split(',')[0];
        }
        else if (Keywords(text) is { } keys
            && (keys.GetValueOrDefault("host") ?? keys.GetValueOrDefault("server") ?? keys.GetValueOrDefault("data source") ?? keys.GetValueOrDefault("address")
                ?? keys.GetValueOrDefault("addr")) is { } source && !IsFile(source))
        {
            // SQL Server's tcp:host,port and host\instance, Npgsql's list of hosts: the first host alone.
            host = source.Split(',')[0].Split('\\')[0];
            host = host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        if (host is null)
        {
            return null;
        }
        host = host.Trim();
        string bare = host.StartsWith('[') && host.IndexOf(']') is int close and > 0 ? host[1..close]
            : host.Count(c => c == ':') == 1 ? host.Split(':')[0] : host;
        return bare.ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" ? bare : null;
    }

    /// <summary>A connection string's keywords, lowercased, with their values; null for a string of another shape.</summary>
    private static IReadOnlyDictionary<string, string>? Keywords(string value)
    {
        if (!value.Contains('=', StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            System.Data.Common.DbConnectionStringBuilder read = new() { ConnectionString = value };
            return read.Keys.Cast<string>().ToDictionary(k => k.ToLowerInvariant(), k => read[k]?.ToString() ?? string.Empty, StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether a data source is a file (SQLite's app.db, a path) or memory, no server's (localhost, .\SQLEXPRESS).</summary>
    private static bool IsFile(string source) =>
        source.Contains(":memory:", StringComparison.OrdinalIgnoreCase) || source.Contains('/', StringComparison.Ordinal)
        || (source.Length > 2 && char.IsAsciiLetter(source[0]) && source[1] == ':' && source[2] is '\\' or '/')
        || System.IO.Path.GetExtension(source).ToLowerInvariant() is ".db" or ".db3" or ".sqlite" or ".sqlite3" or ".sdf" or ".mdf";

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9.\-_\[\]]+(?::\d+)?(?:,[^,=]+(?:=[^,]*)?)*$")]
    private static partial System.Text.RegularExpressions.Regex RedisOptions();

    /// <summary>The database a connection string names (a vhost, Mongo's database), else one named for the string, for
    /// a service that has databases; null for one that has none.</summary>
    public string? Database(string name, string value)
    {
        string text = value.Trim();
        switch (Service)
        {
            case "postgres":
                return Keywords(text)?.GetValueOrDefault("database") is { Length: > 0 } postgres ? postgres : name.ToLowerInvariant();
            case "sqlserver":
                return Keywords(text) is { } keys && (keys.GetValueOrDefault("database") ?? keys.GetValueOrDefault("initial catalog")) is { Length: > 0 } catalog ? catalog : name;
            case "mongo" or "rabbitmq":
                // A URL's path: Mongo's database, RabbitMQ's virtual host.
                return Uri.TryCreate(text.Replace("mongodb+srv://", "mongodb://", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out Uri? url)
                    && url.AbsolutePath.Trim('/') is { Length: > 0 } path ? Uri.UnescapeDataString(path) : null;
            default:
                return null;
        }
    }

    /// <summary>Where a project in the same compose project reaches it, for a connection string named so with the app's
    /// own value: the database the value names is kept; at the compose service of its own name, or the one named
    /// <paramref name="host"/> (one a compose file has of another name).</summary>
    public string ConnectionString(string name, string value, string? host = null) => (Service, Database(name, value), host ?? Service) switch
    {
        ("postgres", var database, var at) => $"Host={at};Port=5432;Database={database};Username=postgres;Password=${{POSTGRES_PASSWORD:-postgres}}",
        ("sqlserver", var database, var at) => $"Server={at},1433;Database={database};User Id=sa;Password=${{MSSQL_SA_PASSWORD:-Passenger_dev1}};TrustServerCertificate=True",
        ("redis", _, var at) => $"{at}:6379",
        ("rabbitmq", { } vhost, var at) => $"amqp://app:${{RABBITMQ_PASSWORD:-app}}@{at}:5672/{Uri.EscapeDataString(vhost)}",
        ("rabbitmq", null, var at) => $"amqp://app:${{RABBITMQ_PASSWORD:-app}}@{at}:5672",
        (_, { } database, var at) => $"mongodb://{at}:27017/{database}",
        (_, _, var at) => $"mongodb://{at}:27017",
    };

    /// <summary>The kind a compose service's image is, by its repository's name (its registry, tag and digest aside:
    /// postgres, docker.io/library/redis:7, mcr.microsoft.com/mssql/server); null for any other.</summary>
    public static BackingService? OfImage(string? image)
    {
        if (image is not { Length: > 0 })
        {
            return null;
        }
        string repository = image.Split('@')[0];
        int slash = repository.LastIndexOf('/');
        int colon = repository.LastIndexOf(':');
        repository = colon > slash ? repository[..colon] : repository;
        return Known.FirstOrDefault(k => k.Image.Split(':')[0] is { } known
            && (repository.Equals(known, StringComparison.OrdinalIgnoreCase) || repository.EndsWith("/" + known, StringComparison.OrdinalIgnoreCase)
                || (known.Contains('/', StringComparison.Ordinal) && repository.EndsWith(known[(known.IndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>What compose.yaml says over it of the databases the apps' connection strings name: SQL Server's image
    /// makes none, nor Postgres' more than POSTGRES_DB's, so the rest are the app's to make.</summary>
    internal IReadOnlyList<string> Comments(IReadOnlyList<string> databases) => Service switch
    {
        "sqlserver" when databases.Count > 0 =>
            [$"SQL Server's image makes no database: {string.Join(", ", databases)} {(databases.Count == 1 ? "is" : "are")} the app's to make (EF Core's Migrate or EnsureCreated) or yours, with sqlcmd"],
        "postgres" when databases.Count > 1 =>
            [$"Postgres makes POSTGRES_DB's database alone: {string.Join(", ", databases.Skip(1))} {(databases.Count == 2 ? "is" : "are")} the app's to make (EF Core's Migrate or EnsureCreated)"],
        _ => [],
    };

    /// <summary>Its settings and where it keeps its data, as compose.yaml writes them under it: Postgres made with the
    /// first database the apps name, RabbitMQ with the first virtual host.</summary>
    internal IReadOnlyList<string> Body(IReadOnlyList<string> databases) => Service switch
    {
        "postgres" => ["environment:", "  POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-postgres}", .. databases.Take(1).Select(d => $"  POSTGRES_DB: {d}"),
            "volumes:", "  - postgres-data:/var/lib/postgresql/data"],
        "sqlserver" => ["platform: linux/amd64", "environment:", "  ACCEPT_EULA: \"Y\"", "  MSSQL_SA_PASSWORD: ${MSSQL_SA_PASSWORD:-Passenger_dev1}", "volumes:", "  - sqlserver-data:/var/opt/mssql"],
        "rabbitmq" => ["environment:", "  RABBITMQ_DEFAULT_USER: app", "  RABBITMQ_DEFAULT_PASS: ${RABBITMQ_PASSWORD:-app}", .. databases.Take(1).Select(v => $"  RABBITMQ_DEFAULT_VHOST: {v}")],
        "mongo" => ["volumes:", "  - mongo-data:/data/db"],
        _ => [],
    };

    /// <summary>The named volume it keeps its data in, declared at the file's foot; null for one that keeps none.</summary>
    internal string? Volume => Service is "postgres" or "sqlserver" or "mongo" ? $"{Service}-data" : null;
}
