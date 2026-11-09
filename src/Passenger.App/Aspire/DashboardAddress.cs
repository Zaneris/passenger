using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Aspire;

/// <summary>Where an AppHost's dashboard is called, from the addresses it listens at (ASPNETCORE_URLS): an address on every
/// interface (<c>0.0.0.0</c>, <c>[::]</c>, <c>+</c> or <c>*</c>) is one to listen at, never to call, so it is called as
/// <see cref="WebAddress.Callable"/> makes it. Where those name no HTTP address, the one Passenger adds is placed by the
/// AppHost's orchestrator and read from the dashboard's resource (<see cref="FromResources"/>).</summary>
public static class DashboardAddress
{
    /// <summary>The address added for an AppHost whose orchestrator places it (<see cref="PlacedIn"/>): loopback at port 0,
    /// a port the orchestrator chooses as it binds it.</summary>
    public const string Unplaced = "http://127.0.0.1:0";

    /// <summary>The resource the dashboard runs as, hidden, whose addresses its snapshots carry.</summary>
    public const string Resource = "aspire-dashboard";

    /// <summary>The first Aspire whose orchestrator proxies the dashboard's addresses, and so places one of port 0.</summary>
    private static readonly Version FirstPlaced = new(9, 4);

    /// <summary>The first HTTP address of a list of them (';' between), as one to call; null where none is HTTP.</summary>
    public static Uri? FromUrls(string urls) => urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(url => Uri.TryCreate(WebAddress.Callable(url), UriKind.Absolute, out Uri? endpoint) ? endpoint : null)
        .FirstOrDefault(endpoint => endpoint?.Scheme == Uri.UriSchemeHttp);

    /// <summary>Whether an AppHost of an Aspire version has its orchestrator place an address of port 0: from 9.4, an unknown
    /// version taken as a current one; before it the dashboard bound it itself, saying the port nowhere Passenger reads.</summary>
    public static bool PlacedIn(string? hostingVersion) => hostingVersion is null
        || !Version.TryParse(hostingVersion.Split('-', '+')[0], out Version? version) || version >= FirstPlaced;

    /// <summary>Where the orchestrator placed the dashboard's HTTP address, as the dashboard's resource says among the
    /// resources' snapshots: the origin of its <c>http</c> URL on this machine; null while none says it.</summary>
    public static Uri? FromResources(IEnumerable<JsonElement> resources) => resources
        .Where(resource => JsonMembers.Text(resource, "displayName") == Resource)
        .SelectMany(resource => JsonMembers.Items(resource, "urls"))
        .Where(url => JsonMembers.Text(url, "name") == Uri.UriSchemeHttp)
        .Select(url => Uri.TryCreate(JsonMembers.Text(url, "url"), UriKind.Absolute, out Uri? placed)
            && placed is { Scheme: "http", Port: > 0 } && WebAddress.OnThisMachine(placed) ? new Uri(placed.GetLeftPart(UriPartial.Authority)) : null)
        .FirstOrDefault(placed => placed is not null);
}
