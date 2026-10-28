using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Passenger.App.Containers;

/// <summary>The host ports Passenger chooses for a container run's published ports whose files leave the host's port to
/// the engine, which would choose another at each restart and leave a browser open at the old one: each chosen once for
/// the plan and kept, never one the files publish themselves, from below the ephemeral range of Linux, macOS and Windows
/// (whose ports the machine gives its outgoing connections at any moment), and free at every address the engine binds it
/// at. One the engine still finds taken as it publishes it is chosen again (<see cref="Again"/>), and never chosen again.</summary>
public sealed partial class ChosenPorts(IEnumerable<ComposeService> services)
{
    /// <summary>Where a port is chosen from: above the ports servers commonly take, below 32768, where Linux's ephemeral range
    /// begins (macOS's and Windows's begin at 49152).</summary>
    private const int Lowest = 20000;

    private const int Highest = 32767;

    /// <summary>How many ports are tried before the system is left to choose one itself.</summary>
    private const int Tries = 100;

    private readonly Dictionary<(string Service, int Index), (int Port, ComposePort Published)> chosen = [];

    /// <summary>The ports the files publish themselves (every port of a range), and those the engine found taken.</summary>
    private readonly HashSet<int> unusable = [.. services.SelectMany(s => s.Ports).SelectMany(p => Range(p.Published))];

    /// <summary>The host port of a service's published port (by its place among the service's ports), chosen the first time
    /// it is asked for.</summary>
    public int Of(string service, int index, ComposePort published)
    {
        if (!chosen.TryGetValue((service, index), out (int Port, ComposePort) was))
        {
            chosen[(service, index)] = was = (Choose(published), published);
        }
        return was.Port;
    }

    /// <summary>The host ports chosen for a service, in the order of its ports.</summary>
    public IReadOnlyList<int> Of(string service) => [.. chosen.Where(c => c.Key.Service == service).OrderBy(c => c.Key.Index).Select(c => c.Value.Port)];

    /// <summary>Chooses again each chosen port that a failed <c>up</c>'s last lines (<paramref name="said"/>) say the engine
    /// could not bind: each with its service, its old port and its new one; none when they name none of them.</summary>
    public IReadOnlyList<(string Service, int Was, int Now)> Again(string said)
    {
        HashSet<int> named = [.. said.Split('\n').Where(l => l.Contains("port", StringComparison.OrdinalIgnoreCase) || l.Contains("bind", StringComparison.OrdinalIgnoreCase))
            .SelectMany(l => PortAfterColon().Matches(l)).Select(m => int.Parse(m.Groups["port"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture))];
        List<(string, int, int)> again = [];
        foreach (((string service, int index), (int port, ComposePort published)) in chosen.Where(c => named.Contains(c.Value.Port)).ToList())
        {
            unusable.Add(port);
            int now = Choose(published);
            chosen[(service, index)] = (now, published);
            again.Add((service, port, now));
        }
        return again;
    }

    private int Choose(ComposePort published)
    {
        for (int tried = 0; tried < Tries; tried++)
        {
            int port = Random.Shared.Next(Lowest, Highest + 1);
            if (!unusable.Contains(port) && !chosen.Values.Any(c => c.Port == port) && Free(port, published))
            {
                return port;
            }
        }
        // None free in the range: the system's own choice, chosen again should the engine find it taken.
        using Socket any = new(AddressFamily.InterNetwork, Udp(published) ? SocketType.Dgram : SocketType.Stream, Udp(published) ? ProtocolType.Udp : ProtocolType.Tcp);
        any.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)any.LocalEndPoint!).Port;
    }

    /// <summary>Whether nothing on this machine holds a port at any address the engine would publish it at: the files' host
    /// address alone, else every address, IPv4's and IPv6's and their loopbacks (a program bound at a loopback alone would
    /// answer this machine's browser ahead of the engine). An address this machine lacks holds nothing.</summary>
    private static bool Free(int port, ComposePort published)
    {
        IPAddress[] addresses = published.HostIp is { Length: > 0 } given && IPAddress.TryParse(given, out IPAddress? at) && !at.Equals(IPAddress.Any)
            && !at.Equals(IPAddress.IPv6Any) ? [at]
            : Socket.OSSupportsIPv6 ? [IPAddress.Any, IPAddress.Loopback, IPAddress.IPv6Any, IPAddress.IPv6Loopback] : [IPAddress.Any, IPAddress.Loopback];
        foreach (IPAddress address in addresses)
        {
            try
            {
                using Socket socket = new(address.AddressFamily, Udp(published) ? SocketType.Dgram : SocketType.Stream, Udp(published) ? ProtocolType.Udp : ProtocolType.Tcp);
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    socket.DualMode = false;
                }
                socket.Bind(new IPEndPoint(address, port));
                if (!Udp(published))
                {
                    socket.Listen();
                }
            }
            catch (SocketException refused) when (refused.SocketErrorCode is not (SocketError.AddressAlreadyInUse or SocketError.AccessDenied))
            {
                // This machine has no such address, or no such family: nothing is there to take the port.
            }
            catch (SocketException)
            {
                return false;
            }
        }
        return true;
    }

    private static bool Udp(ComposePort published) => string.Equals(published.Protocol, "udp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every port of a published port as Compose's model gives it ("8080", or "8080-8081" for a range).</summary>
    private static IEnumerable<int> Range(string? published)
    {
        string[] ends = published?.Split('-') ?? [];
        if (ends.Length is 0 or > 2 || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int first))
        {
            return [];
        }
        int last = ends.Length == 2 && int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out int end) && end >= first ? Math.Min(end, 65535) : first;
        return Enumerable.Range(first, last - first + 1);
    }

    /// <summary>A port as an engine's error names one, after its address's colon ("0.0.0.0:25000", "[::]:25000", ":25000").</summary>
    [GeneratedRegex(@":(?<port>\d{1,5})(?!\d)")]
    private static partial Regex PortAfterColon();
}
