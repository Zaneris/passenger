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
        catch (Exception failed) when (failed is TimeoutException or IOException or System.ComponentModel.Win32Exception)
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
