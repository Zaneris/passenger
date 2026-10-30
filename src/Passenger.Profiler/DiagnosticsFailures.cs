using System.Net.Sockets;
using Microsoft.Diagnostics.NETCore.Client;

namespace Passenger.Profiler;

/// <summary>What a call at a runtime's diagnostic port throws when that runtime is gone, cannot be reached or answers
/// otherwise, told from a fault of Passenger's own, which a catch of these leaves to go on to the boundary.</summary>
internal static class DiagnosticsFailures
{
    /// <summary>The diagnostics library's own (no port, an error the runtime answered, a command or protocol it lacks); the
    /// port's connection failing (IOException, SocketException, UnauthorizedAccessException for another user's,
    /// TimeoutException for a pipe that never answered, PlatformNotSupportedException for a socket the system lacks); an
    /// ArgumentException for a process id no process has or a socket's path past what an address holds; and an
    /// InvalidOperationException for a process gone as it was looked at, or a session let go under the call.</summary>
    public static bool Of(Exception error) => error is DiagnosticsClientException or IOException or SocketException or UnauthorizedAccessException
        or TimeoutException or PlatformNotSupportedException or ArgumentException or InvalidOperationException;
}
