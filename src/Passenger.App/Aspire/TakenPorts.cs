using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>Whether an AppHost said a loopback port Passenger chose for it was taken before it bound it: ASP.NET Core's
/// server ends its start throwing "Failed to bind to address http://127.0.0.1:5000: address already in use.", an address
/// on this machine named (<see cref="WebAddress.OnThisMachine"/>), which the AppHost's console shows as its start fails.</summary>
public static class TakenPorts
{
    private const string Failed = "Failed to bind to address ";

    private const string InUse = ": address already in use";

    /// <summary>Whether the line says the port was in use at an address on this machine as the server bound it.</summary>
    public static bool SaidIn(string line, int port)
    {
        int begins = line.IndexOf(Failed, StringComparison.Ordinal);
        if (begins < 0)
        {
            return false;
        }
        begins += Failed.Length;
        int ends = line.IndexOf(InUse, begins, StringComparison.Ordinal);
        return ends > begins && Uri.TryCreate(line[begins..ends], UriKind.Absolute, out Uri? address) && WebAddress.OnThisMachine(address) && address.Port == port;
    }
}
