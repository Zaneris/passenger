using Microsoft.Diagnostics.NETCore.Client;

namespace Passenger.Core;

/// <summary>A .NET program's diagnostic port, reached by its name. On macOS and Linux that is the socket its runtime made
/// in its temporary folder (<see cref="RuntimeFiles"/>), or a diagnostics router's made for the process beside it, which the
/// client's own search takes first, connected to by its path: so a connection costs the same however many files the
/// folder holds, and never reaches a socket an earlier program of the same id left. On Windows, and where neither is
/// where its name says (a program in another process namespace), the client's own search by the process's id.</summary>
public static class DiagnosticPorts
{
    /// <summary>A client of process <paramref name="processId"/>'s runtime, which connects at each command it is given and
    /// throws <see cref="ServerNotAvailableException"/> there for a port not (or no longer) listening.</summary>
    public static async Task<DiagnosticsClient> ClientAsync(int processId)
    {
        if (!OperatingSystem.IsWindows() && RuntimeFiles.Of(processId)?.Socket is { } socket
            && (RouterOf(processId, socket) is { } router && Named(router) ? router : Named(socket) ? socket : null) is { } port)
        {
            await using DiagnosticsClientConnector connector = await DiagnosticsClientConnector.FromDiagnosticPort(port + ",connect", CancellationToken.None).ConfigureAwait(false);
            return connector.Instance;
        }
        return new DiagnosticsClient(processId);
    }

    /// <summary>Where a diagnostics router of process <paramref name="processId"/> listens, beside its runtime's own socket,
    /// named for the process's start in whole seconds since 1970; null on Windows and for a process gone or unreadable.</summary>
    public static string? RouterOf(int processId) =>
        OperatingSystem.IsWindows() || RuntimeFiles.Of(processId)?.Socket is not { } socket ? null : RouterOf(processId, socket);

    private static string? RouterOf(int processId, string socket) =>
        DotnetProcesses.StartedAt(processId) is { } started && Path.GetDirectoryName(socket) is { } folder
            ? Path.Combine(folder, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"dotnet-diagnostic-dsrouter-{processId}-{new DateTimeOffset(started).ToUnixTimeSeconds()}-socket"))
            : null;

    /// <summary>Whether a socket is there and its path reads back whole from a port's address, which is split at a comma and
    /// read as a URI's path, escaped.</summary>
    private static bool Named(string socket) =>
        !socket.Contains(',', StringComparison.Ordinal) && Uri.TryCreate(socket, UriKind.Absolute, out Uri? read) && read.AbsolutePath == socket && File.Exists(socket);
}
