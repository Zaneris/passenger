using System.Net;
using System.Net.NetworkInformation;
using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>The loopback port a headless Godot editor's GDScript server is told (<c>--lsp-port</c>), free as it is chosen and
/// bound by Godot only once its editor is ready, and what holds it meanwhile so Godot cannot listen there. Godot says where its
/// server listens, and that it could not, only in its editor's own log, which a headless editor shows nowhere, so the editor is
/// started again on another port where Passenger sees the port held (<see cref="HeldBy"/>).</summary>
public static class GodotPorts
{
    /// <summary>How many headless editors one start runs at most, each on a port of its own.</summary>
    public const int Tries = 3;

    private static int next;

    /// <summary>Has the next headless editor told the port, once, in place of one chosen: a port a test holds.</summary>
    public static void GiveNext(int port) => Volatile.Write(ref next, port);

    /// <summary>A loopback port free now, unless a test gave the next one.</summary>
    public static int Choose()
    {
        if (Interlocked.Exchange(ref next, 0) is int given and > 0)
        {
            return given;
        }
        return WebAddress.FreeLoopbackPort();
    }

    /// <summary>What holds the port so Godot cannot listen on it, said, else null. On Windows Godot listens without
    /// SO_REUSEADDR (its NetSocketWinSock sets no reuse), so a connection's end on the port, an outgoing one's too, fails its
    /// listen; elsewhere it sets SO_REUSEADDR, so only another listener can, which a connect reaches as it would Godot's.</summary>
    public static string? HeldBy(int port) => OperatingSystem.IsWindows()
        ? Holding(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Select(c => (c.LocalEndPoint, c.RemoteEndPoint)), port)
        : null;

    /// <summary>The first connection whose local end is on the port, said, else null.</summary>
    public static string? Holding(IEnumerable<(IPEndPoint Local, IPEndPoint Remote)> connections, int port) =>
        connections.FirstOrDefault(connection => connection.Local.Port == port) is ({ } local, { } remote) ? $"a connection from {local} to {remote}" : null;
}
