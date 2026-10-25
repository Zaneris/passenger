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

    /// <summary>The keywords a connection string names its host by, in the providers' spellings.</summary>
    private static readonly string[] HostKeys = ["host", "server", "data source", "address", "addr", "network address"];

    /// <summary>The database a compose run gives a .NET service built from the startup project; null when the run has none
    /// of it, or none of its connection strings names a service of the run.</summary>
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
        foreach ((string name, string value) in app.Environment.Where(e => e.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (await ReachAsync(run, model, value, endpoint, cancel) is { } reached)
            {
                environment[name] = reached.Value;
                where ??= $"{reached.Where}, as {run.Name} publishes it";
            }
        }
        return environment.Count == 0 ? null : new EfRunDatabase(environment, where!);
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
    /// service of the run (its keyword's, or a URL's), at the port Compose published its port on. Null for a host that is
    /// no service of the run, or a port it does not publish.</summary>
    private static async Task<(string Value, string Where)?> ReachAsync(ContainerRun run, ComposeModel model, string value, string? endpoint, CancellationToken cancel)
    {
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? url) && url.Host.Length > 0 && !url.IsFile)
        {
            if (model[url.Host] is not { } named || (url.IsDefaultPort ? Default(named) : url.Port) is not { } inside
                || await PublishedAsync(run, named, inside, cancel) is not { } at)
            {
                return null;
            }
            string host = Host(at.Link, endpoint);
            UriBuilder moved = new(url) { Host = host, Port = at.Port };
            return (moved.Uri.ToString(), $"{named.Name} at {host}:{at.Port}");
        }
        List<(string Key, string Value)> parts = [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).Select(p => (p[0].Trim(), p[1].Trim()))];
        int hostAt = parts.FindIndex(p => HostKeys.Contains(p.Key, StringComparer.OrdinalIgnoreCase));
        if (hostAt < 0)
        {
            return null;
        }
        // SQL Server's host,port; Postgres' and MySQL's Port keyword.
        string[] written = parts[hostAt].Value.Replace("tcp:", string.Empty, StringComparison.OrdinalIgnoreCase).Split(',', 2);
        int portAt = parts.FindIndex(p => p.Key.Equals("port", StringComparison.OrdinalIgnoreCase));
        if (model[written[0].Trim()] is not { } service)
        {
            return null;
        }
        int? port = written.Length > 1 && int.TryParse(written[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int inHost) ? inHost
            : portAt >= 0 && int.TryParse(parts[portAt].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int keyed) ? keyed
            : Default(service);
        if (port is not { } target || await PublishedAsync(run, service, target, cancel) is not { } published)
        {
            return null;
        }
        string reached = Host(published.Link, endpoint);
        if (written.Length > 1)
        {
            parts[hostAt] = (parts[hostAt].Key, $"{reached},{published.Port}");
        }
        else
        {
            parts[hostAt] = (parts[hostAt].Key, reached);
            if (portAt >= 0)
            {
                parts[portAt] = (parts[portAt].Key, published.Port.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                parts.Insert(hostAt + 1, ("Port", published.Port.ToString(CultureInfo.InvariantCulture)));
            }
        }
        return (string.Join(';', parts.Select(p => $"{p.Key}={p.Value}")), $"{service.Name} at {reached}:{published.Port}");
    }

    /// <summary>The port a service's port is published on on the engine's machine, as Compose says it (<c>compose port</c>,
    /// which knows an ephemeral one too); null for one it does not publish.</summary>
    private static async Task<(ContainerPort Link, int Port)?> PublishedAsync(ContainerRun run, ComposeService service, int target, CancellationToken cancel)
    {
        string port = target.ToString(CultureInfo.InvariantCulture);
        ChildResult said;
        try
        {
            said = await run.Engine.RunAsync([.. run.Plan.Compose, "port", "--", service.Name, port], ContainerEngine.AskLimit, cancel, run.Plan.Folder);
        }
        catch (Exception failed) when (failed is TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        string answer = said.Output.Trim().Split('\n')[0].Trim();
        int colon = answer.LastIndexOf(':');
        return said.Succeeded && colon >= 0 && int.TryParse(answer[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int published) && published > 0
            ? (new ContainerPort(published.ToString(CultureInfo.InvariantCulture), port, "tcp"), published)
            : null;
    }

    private static string Host(ContainerPort published, string? endpoint) => published.LinkOn(endpoint)?.Host ?? "localhost";

    /// <summary>The port a backing service's image listens on, by its kind.</summary>
    private static int? Default(ComposeService service) => BackingService.OfImage(service.Image)?.Service switch
    {
        "postgres" => 5432,
        "sqlserver" => 1433,
        "mongo" => 27017,
        "redis" => 6379,
        "rabbitmq" => 5672,
        _ => service.Image?.Contains("mysql", StringComparison.OrdinalIgnoreCase) == true || service.Image?.Contains("mariadb", StringComparison.OrdinalIgnoreCase) == true ? 3306 : null,
    };
}
