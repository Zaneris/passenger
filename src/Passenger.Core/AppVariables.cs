namespace Passenger.Core;

/// <summary>ASP.NET Core's and the .NET host's environment variables that Passenger reads or sets in the apps it runs, each
/// named once, and what they say of an app: the environment it runs in (<see cref="EnvironmentOf"/>) and whether it serves
/// HTTPS (<see cref="ServesHttps"/>).</summary>
[IdentifierHome]
public static class AspNetCoreVariables
{
    /// <summary>The environment a web app runs in, which ASP.NET Core reads before <see cref="DotnetEnvironment"/>.</summary>
    public const string AspNetCoreEnvironment = "ASPNETCORE_ENVIRONMENT";

    /// <summary>The environment the .NET host runs an app in.</summary>
    public const string DotnetEnvironment = "DOTNET_ENVIRONMENT";

    /// <summary>The addresses Kestrel listens on, separated by ';'.</summary>
    public const string Urls = "ASPNETCORE_URLS";

    /// <summary>The ports Kestrel serves HTTP on, separated by ';'.</summary>
    public const string HttpPorts = "ASPNETCORE_HTTP_PORTS";

    /// <summary>The ports Kestrel serves HTTPS on, separated by ';'.</summary>
    public const string HttpsPorts = "ASPNETCORE_HTTPS_PORTS";

    /// <summary>The PEM certificate Kestrel serves HTTPS with.</summary>
    public const string CertificatePath = "ASPNETCORE_Kestrel__Certificates__Default__Path";

    /// <summary>The key of <see cref="CertificatePath"/>.</summary>
    public const string CertificateKeyPath = "ASPNETCORE_Kestrel__Certificates__Default__KeyPath";

    /// <summary>The manifest a Development app finds its static web assets by.</summary>
    public const string StaticWebAssets = "ASPNETCORE_STATICWEBASSETS";

    /// <summary>The environment an app runs in, as ASP.NET Core reads it: <see cref="AspNetCoreEnvironment"/>, else
    /// <see cref="DotnetEnvironment"/>, else <paramref name="otherwise"/>; a blank value names none.</summary>
    public static string EnvironmentOf(Func<string, string?> variable, string otherwise) =>
        variable(AspNetCoreEnvironment) is { } named && !string.IsNullOrWhiteSpace(named) ? named
        : variable(DotnetEnvironment) is { } host && !string.IsNullOrWhiteSpace(host) ? host
        : otherwise;

    /// <summary>Whether an app serves HTTPS: Kestrel told an https address (<see cref="Urls"/>) or an HTTPS port
    /// (<see cref="HttpsPorts"/>).</summary>
    public static bool ServesHttps(IReadOnlyDictionary<string, string> variables) =>
        HttpsIn(variables.GetValueOrDefault(Urls)) || !string.IsNullOrWhiteSpace(variables.GetValueOrDefault(HttpsPorts));

    /// <summary>Whether addresses separated by ';' name an https one.</summary>
    public static bool HttpsIn(string? urls) => urls?.Contains("https:", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Addresses separated by ';' with each https one as http.</summary>
    public static string AsHttp(string urls) => urls.Replace("https:", "http:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>OpenTelemetry's environment variables that Passenger reads or sets in the apps it runs, each named once.</summary>
[IdentifierHome]
public static class OtelVariables
{
    /// <summary>The name an app's logs, traces and metrics are told under.</summary>
    public const string ServiceName = "OTEL_SERVICE_NAME";

    /// <summary>An app's resource's attributes, <c>key=value</c> separated by ','.</summary>
    public const string ResourceAttributes = "OTEL_RESOURCE_ATTRIBUTES";

    /// <summary>Where an app sends its telemetry over OTLP.</summary>
    public const string ExporterEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The headers an app sends its telemetry with, <c>key=value</c> separated by ','.</summary>
    public const string ExporterHeaders = "OTEL_EXPORTER_OTLP_HEADERS";

    /// <summary>The protocol an app sends its telemetry over: grpc or http/protobuf.</summary>
    public const string ExporterProtocol = "OTEL_EXPORTER_OTLP_PROTOCOL";

    /// <summary>How long, in milliseconds, an app holds its logs before sending them.</summary>
    public const string LogsDelay = "OTEL_BLRP_SCHEDULE_DELAY";

    /// <summary>How long, in milliseconds, an app holds its spans before sending them.</summary>
    public const string SpansDelay = "OTEL_BSP_SCHEDULE_DELAY";

    /// <summary>How often, in milliseconds, an app sends its metrics.</summary>
    public const string MetricsInterval = "OTEL_METRIC_EXPORT_INTERVAL";
}

/// <summary>Aspire's environment variables that Passenger reads or sets in an AppHost it runs or its CLI, each named once:
/// its dashboard's and resource service's (with the older DOTNET_ names an AppHost still reads), and the IDE execution
/// protocol's (DEBUG_SESSION_), by which the AppHost reaches the IDE.</summary>
[IdentifierHome]
public static class AspireVariables
{
    public const string ResourceServiceEndpoint = "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL";

    public const string DotnetResourceServiceEndpoint = "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL";

    public const string DashboardOtlpEndpoint = "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL";

    public const string DotnetDashboardOtlpEndpoint = "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL";

    public const string DashboardOtlpHttpEndpoint = "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL";

    public const string DotnetDashboardOtlpHttpEndpoint = "DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL";

    /// <summary>The key the dashboard gives the resource service.</summary>
    public const string DashboardResourceServiceKey = "ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY";

    /// <summary>"true" lets the dashboard, telemetry and resource service serve HTTP.</summary>
    public const string AllowUnsecuredTransport = "ASPIRE_ALLOW_UNSECURED_TRANSPORT";

    /// <summary>"1" keeps Aspire's CLI from sending its telemetry.</summary>
    public const string TelemetryOptOut = "ASPIRE_CLI_TELEMETRY_OPTOUT";

    /// <summary>The container runtime the orchestrator runs containers on: docker or podman.</summary>
    public const string ContainerRuntime = "ASPIRE_CONTAINER_RUNTIME";

    public const string DotnetContainerRuntime = "DOTNET_ASPIRE_CONTAINER_RUNTIME";

    /// <summary>Where the IDE execution protocol's server listens, <c>localhost:port</c>.</summary>
    public const string DebugSessionPort = "DEBUG_SESSION_PORT";

    /// <summary>The bearer token the AppHost gives the IDE's server.</summary>
    public const string DebugSessionToken = "DEBUG_SESSION_TOKEN";

    /// <summary>The certificate the IDE's server serves HTTPS with, which the AppHost then trusts.</summary>
    public const string DebugSessionCertificate = "DEBUG_SESSION_SERVER_CERTIFICATE";

    /// <summary>The protocol versions and launch types the IDE's server takes, as JSON.</summary>
    public const string DebugSessionInfo = "DEBUG_SESSION_INFO";

    /// <summary>Debug or NoDebug: how the IDE runs the AppHost's projects.</summary>
    public const string DebugSessionRunMode = "DEBUG_SESSION_RUN_MODE";
}
