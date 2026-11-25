using System.Globalization;
using Passenger.App.Aspire;
using Passenger.App.Containers;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Migrations;

/// <summary>The database a run of the startup project uses, as this machine reaches it: the connection strings to give
/// dotnet ef as the environment the app's configuration reads (never on its command line, which any process can read), and
/// where it is, in words.</summary>
public sealed record EfRunDatabase(IReadOnlyDictionary<string, string> Environment, string Where);

/// <summary>Finds the database a compose run or an AppHost gives the startup project, as this machine reaches it. A
/// compose connection string names its database by a service only the containers reach, so it is given again at the port
/// Compose published (<see cref="ContainerPort.LinkOn"/>); an AppHost's reaches this machine already.</summary>
public static class EfDatabases
{
    private const string Prefix = "ConnectionStrings__";

    /// <summary>The database a compose run gives a .NET service built from the startup project; null when the run has none
    /// of it, or none of its connection strings names a service of the run. One that cannot be given again as this machine
    /// reaches it is given as the run writes it, which where it is says.</summary>
    public static async Task<EfRunDatabase?> OfComposeAsync(ContainerRun run, ProjectModel startup, CancellationToken cancel)
    {
        if (run.IsStopped || run.Plan.Dotnet.FirstOrDefault(d => PathNames.Same(d.Project, startup.Path)) is not { } service)
        {
            return null;
        }
        ComposeModel model;
        try
        {
            model = await ComposeModel.ReadAsync(run.Engine, run.Plan.Compose, run.Plan.Folder, cancel);
        }
        catch (Exception failed) when (failed is ContainerCommandException or System.Text.Json.JsonException or TimeoutException or IOException)
        {
            return null;
        }
        if (model[service.Service] is not { } app)
        {
            return null;
        }
        string? endpoint = (await run.Engine.StateAsync(cancel: cancel)).Endpoint;
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        string? where = null;
        List<string> kept = [];
        foreach ((string name, string value) in app.Environment.Where(e => e.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (await ReachAsync(run, model, value, endpoint, cancel) is not { } reached)
            {
                continue;
            }
            environment[name] = reached.Value ?? value;
            if (reached.Value is null)
            {
                kept.Add($"{name} as {run.Name} writes it, which could not be given again to reach {reached.Where} from this machine");
            }
            else
            {
                where ??= $"{reached.Where}, as {run.Name} publishes it";
            }
        }
        return environment.Count == 0 ? null : new EfRunDatabase(environment, string.Join("; ", new[] { where }.Concat(kept).OfType<string>()));
    }

    /// <summary>The key every Cosmos DB emulator signs in with, the same for each, as its documentation gives it.</summary>
    private const string CosmosEmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    /// <summary>Each database a compose run's services run from their images (PostgreSQL, SQL Server, MySQL and MariaDB,
    /// ClickHouse, MongoDB, Cosmos DB's emulator), whoever uses it: its connection string as this machine reaches it, at the port Compose published, signed in
    /// as its image's own variables make its user, with the driver's package that names its kind, and where it is. None
    /// where the run has stopped or its model cannot be read.</summary>
    public static async Task<IReadOnlyList<(string Service, string Value, string[] Packages, string Where)>> OfServicesAsync(ContainerRun run, CancellationToken cancel)
    {
        if (run.IsStopped)
        {
            return [];
        }
        ComposeModel model;
        try
        {
            model = await ComposeModel.ReadAsync(run.Engine, run.Plan.Compose, run.Plan.Folder, cancel);
        }
        catch (Exception failed) when (failed is ContainerCommandException or System.Text.Json.JsonException or TimeoutException or IOException)
        {
            return [];
        }
        string? endpoint = null;
        List<(string, string, string[], string)> found = [];
        foreach (ComposeService service in model.Services)
        {
            IReadOnlyDictionary<string, string> told = service.Environment;
            string? Told(params string[] names) => names.Select(name => told.GetValueOrDefault(name)).FirstOrDefault(v => v is { Length: > 0 });
            string image = service.Image ?? string.Empty;
            (int Port, string[] Packages, Func<string, int, string> Write)? database = Kind(service) switch
            {
                "postgres" => (5432, [], (host, port) => $"Host={host};Port={port};Database={Told("POSTGRES_DB") ?? Told("POSTGRES_USER") ?? "postgres"};"
                    + $"Username={Told("POSTGRES_USER") ?? "postgres"};Password={Told("POSTGRES_PASSWORD") ?? string.Empty}"),
                "sqlserver" => (1433, ["Microsoft.Data.SqlClient"], (host, port) => $"Server={host},{port};Database=master;User Id=sa;"
                    + $"Password={Told("MSSQL_SA_PASSWORD", "SA_PASSWORD") ?? string.Empty};TrustServerCertificate=True"),
                _ when image.Contains("clickhouse", StringComparison.OrdinalIgnoreCase) => (8123, ["ClickHouse.Driver"], (host, port) =>
                    $"Host={host};Port={port};Database={Told("CLICKHOUSE_DB") ?? "default"};Username={Told("CLICKHOUSE_USER") ?? "default"};"
                    + $"Password={Told("CLICKHOUSE_PASSWORD") ?? string.Empty}"),
                "mongo" => (27017, ["MongoDB.Driver"], (host, port) => "mongodb://"
                    + (Told("MONGO_INITDB_ROOT_USERNAME") is { } root ? $"{Uri.EscapeDataString(root)}:{Uri.EscapeDataString(Told("MONGO_INITDB_ROOT_PASSWORD") ?? string.Empty)}@" : string.Empty)
                    + $"{host}:{port}/{Told("MONGO_INITDB_DATABASE") ?? string.Empty}" + (Told("MONGO_INITDB_ROOT_USERNAME") is null ? string.Empty : "?authSource=admin")),
                // Every emulator signs in with the one key its documentation gives; the older one serves HTTPS on its own certificate.
                _ when image.Contains("azure-cosmos-emulator", StringComparison.OrdinalIgnoreCase) => (8081, ["Microsoft.Azure.Cosmos"], (host, port) =>
                    image.Contains("vnext", StringComparison.OrdinalIgnoreCase) && !string.Equals(Told("PROTOCOL"), "https", StringComparison.OrdinalIgnoreCase)
                        ? $"AccountEndpoint=http://{host}:{port}/;AccountKey={CosmosEmulatorKey}"
                        : $"AccountEndpoint=https://{host}:{port}/;AccountKey={CosmosEmulatorKey};DisableServerCertificateValidation=True"),
                _ when Default(service) == 3306 => (3306, ["MySqlConnector"], (host, port) => $"Server={host};Port={port};"
                    + $"Database={Told("MYSQL_DATABASE", "MARIADB_DATABASE") ?? string.Empty};"
                    + (Told("MYSQL_ROOT_PASSWORD", "MARIADB_ROOT_PASSWORD") is { } root ? $"User ID=root;Password={root}"
                        : $"User ID={Told("MYSQL_USER", "MARIADB_USER") ?? "root"};Password={Told("MYSQL_PASSWORD", "MARIADB_PASSWORD") ?? string.Empty}")),
                _ => null,
            };
            if (database is not { } kind || await PublishedAsync(run, service, kind.Port, cancel) is not { } published)
            {
                continue;
            }
            endpoint ??= (await run.Engine.StateAsync(cancel: cancel)).Endpoint ?? string.Empty;
            string host = Host(published.Link, endpoint.Length > 0 ? endpoint : null);
            found.Add((service.Name, kind.Write(host, published.Port), kind.Packages, $"{service.Name} at {host}:{published.Port}"));
        }
        return found;
    }

    /// <summary>The database an AppHost that runs now gives the startup project as one of its resources; null when none does.</summary>
    public static EfRunDatabase? OfAspire(AspireController aspire, ProjectModel startup)
    {
        foreach (AspireAppRun app in aspire.Apps)
        {
            foreach (AspireResourceRun resource in app.Services.Where(r => PathNames.Same(r.Project, startup.Path)))
            {
                Dictionary<string, string> environment = resource.Plan.Environment.Where(e => e.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
                if (environment.Count > 0)
                {
                    return new EfRunDatabase(environment, $"the database {resource.ResourceName ?? startup.Name} of {app.Name} has");
                }
            }
        }
        return null;
    }

    /// <summary>A connection string the containers read given again as this machine reaches its database: the host a
    /// service of the run (<see cref="ConnectionServer"/>), at the port Compose published its port on, with where that is;
    /// its value null where the string cannot be written again, where then the service. Null for a host that is no service
    /// of the run, or a port it does not publish.</summary>
    private static async Task<(string? Value, string Where)?> ReachAsync(ContainerRun run, ComposeModel model, string value, string? endpoint, CancellationToken cancel)
    {
        if (ConnectionServer.Of(value) is not { } server
            || (model[server.Host] ?? model.Services.FirstOrDefault(s => s.Name.Equals(server.Host, StringComparison.OrdinalIgnoreCase))) is not { } service
            || (server.Port ?? Default(service)) is not { } inside || await PublishedAsync(run, service, inside, cancel) is not { } published)
        {
            return null;
        }
        string host = Host(published.Link, endpoint);
        // SQL Server's port follows its host after a comma.
        return server.At(host, published.Port, besideHost: Kind(service) == "sqlserver") is { } moved
            ? (moved, $"{service.Name} at {host}:{published.Port}") : (null, service.Name);
    }

    /// <summary>The port a service's port is published on on the engine's machine, as Compose says it (<c>compose port</c>,
    /// which knows an ephemeral one too); null for one it does not publish.</summary>
    private static async Task<(ContainerPort Link, int Port)?> PublishedAsync(ContainerRun run, ComposeService service, int target, CancellationToken cancel)
    {
        int? published;
        try
        {
            (published, _) = await run.Engine.PublishedPortAsync(run.Plan.Compose, service.Name, target, run.Plan.Folder, cancel);
        }
        catch (Exception failed) when (ChildFailures.Of(failed))
        {
            return null;
        }
        return published is { } port
            ? (new ContainerPort(port.ToString(CultureInfo.InvariantCulture), target.ToString(CultureInfo.InvariantCulture), "tcp"), port)
            : null;
    }

    private static string Host(ContainerPort published, string? endpoint) => published.LinkOn(endpoint)?.Host ?? "localhost";

    private static string? Kind(ComposeService service) => BackingService.OfImage(service.Image)?.Service;

    /// <summary>The port a backing service's image listens on, by its kind.</summary>
    private static int? Default(ComposeService service) => Kind(service) switch
    {
        "postgres" => 5432,
        "sqlserver" => 1433,
        "mongo" => 27017,
        "redis" => 6379,
        "rabbitmq" => 5672,
        _ => service.Image?.Contains("mysql", StringComparison.OrdinalIgnoreCase) == true || service.Image?.Contains("mariadb", StringComparison.OrdinalIgnoreCase) == true ? 3306 : null,
    };
}
