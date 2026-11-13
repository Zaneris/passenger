using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Passenger.App.Aspire;

/// <summary>The project configuration and request overlays; null arguments retain the launch profile's arguments.</summary>
public sealed record AspireProjectLaunch(
    string DcpInstanceId,
    string SessionId,
    string ProjectPath,
    string Mode,
    string? LaunchProfile,
    bool DisableLaunchProfile,
    ImmutableArray<AspireEnvironmentVariable> Environment,
    ImmutableArray<string>? Arguments)
{
    /// <summary>Whether the AppHost asks for an Azure Functions app (<see cref="FunctionsHost.LaunchType"/>), which Azure
    /// Functions Core Tools host, rather than a project that runs as itself.</summary>
    public bool Functions { get; init; }
}

public sealed record AspireEnvironmentVariable(string Name, string Value);

/// <summary>An owned runner handle; its ID need not equal the protocol session ID.</summary>
public sealed record AspireProjectSession(string Id, int? ProcessId);

internal sealed record AspireRunSessionRequest(
    [property: JsonPropertyName("launch_configurations")] ImmutableArray<AspireProjectConfiguration?> LaunchConfigurations,
    [property: JsonPropertyName("env")] ImmutableArray<AspireEnvironmentEntry?>? Environment,
    [property: JsonPropertyName("args")] ImmutableArray<string?>? Arguments);

internal sealed record AspireProjectConfiguration(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("project_path")] string? ProjectPath,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("launch_profile")] string? LaunchProfile,
    [property: JsonPropertyName("disable_launch_profile")] bool DisableLaunchProfile);

internal sealed record AspireEnvironmentEntry(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("value")] string? Value);

internal sealed record AspireEndpointInfo(
    [property: JsonPropertyName("protocols_supported")] string[] ProtocolsSupported,
    [property: JsonPropertyName("supported_launch_configurations")] string[] SupportedLaunchConfigurations);

internal sealed record AspireErrorResponse([property: JsonPropertyName("error")] AspireError Error);

internal sealed record AspireError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

internal sealed record AspireNotification(
    [property: JsonPropertyName("notification_type")] string Type,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("pid")] int? ProcessId = null,
    [property: JsonPropertyName("exit_code")] uint? ExitCode = null,
    [property: JsonPropertyName("is_std_err")] bool? IsStandardError = null,
    [property: JsonPropertyName("log_message")] string? LogMessage = null);
