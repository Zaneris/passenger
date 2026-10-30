using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Passenger.App.Telemetry;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>The dashboard a container run that collects telemetry runs beside its services (its configuration's Collect logs
/// and traces): Aspire's standalone dashboard image, a service of Passenger's override with Passenger's labels (so Stop and
/// the folder's end take it down with the run), its frontend and telemetry API published on this machine's loopback alone
/// and asked with a key of the run's, its OTLP endpoint reached on the run's network alone and sent to with another. Each
/// .NET service the run starts is told to send it OpenTelemetry (<see cref="Tell"/>) unless its own files say where.</summary>
public sealed record ContainerTelemetry(string OtlpKey, string ApiKey)
{
    /// <summary>The dashboard's image, at the Aspire release Passenger reads.</summary>
    public const string Image = "mcr.microsoft.com/dotnet/aspire-dashboard:13.6";

    /// <summary>Its service's name in the run's Compose project.</summary>
    public const string Service = "passenger-dashboard";

    /// <summary>The image's frontend port, which serves the telemetry API Passenger reads.</summary>
    public const int ApiPort = 18888;

    /// <summary>The image's OTLP ports, over gRPC and over HTTP.</summary>
    public const int OtlpPort = 18889;
    public const int OtlpHttpPort = 18890;

    /// <summary>A dashboard of a run's own, its two keys new.</summary>
    public static ContainerTelemetry New() => new(Key(), Key());

    private static string Key() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>The dashboard's service in Passenger's override: on the networks the .NET services it hears from join (the
    /// project's default when none names any), its API asked with its key and nothing else of it published.</summary>
    public JsonObject Entry(JsonObject labels, IReadOnlyCollection<string> networks)
    {
        JsonObject entry = new()
        {
            ["image"] = Image,
            ["labels"] = labels,
            ["environment"] = new JsonObject
            {
                ["Dashboard__Otlp__AuthMode"] = "ApiKey",
                ["Dashboard__Otlp__PrimaryApiKey"] = OtlpKey,
                ["Dashboard__Api__Enabled"] = "true",
                ["Dashboard__Api__AuthMode"] = "ApiKey",
                ["Dashboard__Api__PrimaryApiKey"] = ApiKey,
            },
            ["ports"] = new JsonArray(new JsonObject { ["target"] = ApiPort, ["host_ip"] = "127.0.0.1", ["protocol"] = "tcp" }),
        };
        if (networks.Count > 0)
        {
            entry["networks"] = new JsonObject(networks.Select(network => KeyValuePair.Create(network, (JsonNode?)null)));
        }
        return entry;
    }

    /// <summary>Tells a .NET service where to send its logs and traces, and as what, adding to the environment Passenger's
    /// override gives it: what its own files set of OpenTelemetry's endpoint, name and headers stays theirs, so a service
    /// that names its own endpoint is left to it. Batches go every second, so a request shows at once. Started after the
    /// dashboard, which it depends on, so nothing it sends as it starts is lost.</summary>
    public bool Tell(ComposeService service, JsonObject entry, JsonObject environment)
    {
        if (service.Environment.ContainsKey(OtelVariables.ExporterEndpoint))
        {
            return false;
        }
        bool http = service.Environment.TryGetValue(OtelVariables.ExporterProtocol, out string? protocol) && protocol.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        environment[OtelVariables.ExporterEndpoint] = string.Create(CultureInfo.InvariantCulture, $"http://{Service}:{(http ? OtlpHttpPort : OtlpPort)}");
        string header = $"x-otlp-api-key={OtlpKey}";
        environment[OtelVariables.ExporterHeaders] = service.Environment.TryGetValue(OtelVariables.ExporterHeaders, out string? headers) && headers.Length > 0
            ? $"{headers},{header}" : header;
        foreach ((string name, string value) in (ReadOnlySpan<(string, string)>)[(OtelVariables.ServiceName, service.Name), (OtelVariables.LogsDelay, "1000"),
            (OtelVariables.SpansDelay, "1000"), (OtelVariables.MetricsInterval, "1000")])
        {
            if (!service.Environment.ContainsKey(name))
            {
                environment[name] = value;
            }
        }
        entry["depends_on"] = new JsonObject { [Service] = new JsonObject { ["condition"] = "service_started" } };
        return true;
    }

    /// <summary>Where the run's logs and traces are read once it is up: the dashboard's API port as published on this machine
    /// (<c>compose port</c>), with its key; null, saying why in <c>Why</c>, when it cannot be read from here.</summary>
    public async Task<(TelemetrySource? Source, string? Why)> ReadAsync(ContainerEngine engine, IReadOnlyList<string> compose, string folder, CancellationToken cancel)
    {
        (int? published, string said) = await engine.PublishedPortAsync(compose, Service, ApiPort, folder, cancel);
        if (published is not { } port)
        {
            return (null, $"its dashboard's port could not be read ({said})");
        }
        // An engine on another machine publishes on its own loopback, which this machine cannot reach.
        if (await engine.StateAsync(cancel: cancel) is { Endpoint: { } endpoint } && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? machine)
            && machine.Scheme is "tcp" or "ssh" or "http" or "https" && machine.Host.Length > 0 && !machine.IsLoopback)
        {
            return (null, $"its dashboard runs on {endpoint}, whose loopback this machine cannot read");
        }
        return (new TelemetrySource(new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}")), ApiKey), null);
    }
}
