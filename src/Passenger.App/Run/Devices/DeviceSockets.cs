using System.Globalization;
using System.Security.Cryptography;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>The abstract sockets a start names in an app on an Android device (its relay's, its inspector's, its update
/// handlers' hook's), each named for the Passenger that made it, its process and when that started, so the forwards and
/// reverses adb keeps to them are known by their Passenger: a start takes out those of a Passenger no longer running
/// (<see cref="DeviceLaunches"/>), never another program's, nor a running Passenger's.</summary>
public static class DeviceSockets
{
    private const string Prefix = "passenger-";

    /// <summary>What an adb rule names an abstract socket on the device by.</summary>
    public const string Address = "localabstract:";

    /// <summary>How far apart two reads of one process's start may fall, as each system rounds it.</summary>
    private const long StartsAlike = 2;

    /// <summary>This process and its start, as its sockets' names carry them.</summary>
    private static readonly Lazy<string> Own = new(() => OwnerOf(Environment.ProcessId, Started(Environment.ProcessId) ?? 0));

    /// <summary>A socket of <paramref name="kind"/> (a word with no dash) for a start of this Passenger, named afresh.</summary>
    public static string Named(string kind) => $"{Prefix}{kind}-{Own.Value}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";

    /// <summary>A socket of <paramref name="kind"/> as the Passenger of process <paramref name="process"/>, started at
    /// <paramref name="started"/> (whole seconds since 1970), names it: a test's stand-in for one that no longer runs.</summary>
    public static string Named(string kind, int process, long started) => $"{Prefix}{kind}-{OwnerOf(process, started)}-left";

    /// <summary>Whether a socket's name (an adb rule's <c>localabstract:</c> address, or the name alone) is one a Passenger
    /// named that no longer runs: its process gone, or its id another process's since.</summary>
    public static bool LeftBehind(string name)
    {
        string socket = name.StartsWith(Address, StringComparison.Ordinal) ? name[Address.Length..] : name;
        if (socket.Split('-') is not [_, _, var id, var start, _] || !socket.StartsWith(Prefix, StringComparison.Ordinal)
            || !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int process)
            || !long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out long started))
        {
            return false;
        }
        return OwnerOf(process, started) != Own.Value && !(Started(process) is { } now && Math.Abs(now - started) <= StartsAlike);
    }

    private static string OwnerOf(int process, long started) => string.Create(CultureInfo.InvariantCulture, $"{process}-{started}");

    /// <summary>When a process started, in whole seconds since 1970; null where it has gone or cannot be read.</summary>
    private static long? Started(int process) => DotnetProcesses.StartedAt(process) is { } at ? new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeSeconds() : null;
}
