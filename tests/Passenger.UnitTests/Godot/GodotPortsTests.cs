using System.Net;
using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>A connection whose local end is on the port a headless Godot editor was told is said as what holds it, an
/// outgoing one's to another host too, so the editor, which on Windows cannot listen there, is started again on another
/// port; a connection only to the port, as a client of Godot's server, or on other ports, holds nothing.</summary>
public sealed class GodotPortsTests
{
    /// <summary>Only a connection whose local end is on the port holds it.</summary>
    [Fact]
    public void A_connection_on_the_port_holds_it() => Cases.All(
        (nameof(A_connection_whose_local_end_is_on_the_port_holds_it_and_is_said), A_connection_whose_local_end_is_on_the_port_holds_it_and_is_said),
        (nameof(A_connection_to_the_port_or_on_other_ports_holds_nothing), A_connection_to_the_port_or_on_other_ports_holds_nothing));

    private void A_connection_whose_local_end_is_on_the_port_holds_it_and_is_said()
    {
        (IPEndPoint, IPEndPoint)[] connections =
        [
            (new IPEndPoint(IPAddress.Loopback, 50100), new IPEndPoint(IPAddress.Loopback, 52341)),
            (new IPEndPoint(IPAddress.Parse("192.168.1.5"), 52341), new IPEndPoint(IPAddress.Parse("140.82.112.3"), 443)),
        ];
        Assert.Equal("a connection from 192.168.1.5:52341 to 140.82.112.3:443", GodotPorts.Holding(connections, 52341));
    }

    private void A_connection_to_the_port_or_on_other_ports_holds_nothing()
    {
        (IPEndPoint, IPEndPoint)[] connections =
        [
            (new IPEndPoint(IPAddress.Loopback, 50100), new IPEndPoint(IPAddress.Loopback, 52341)),
            (new IPEndPoint(IPAddress.Loopback, 52342), new IPEndPoint(IPAddress.Loopback, 6006)),
        ];
        Assert.Null(GodotPorts.Holding(connections, 52341));
        Assert.Null(GodotPorts.Holding([], 52341));
    }
}
