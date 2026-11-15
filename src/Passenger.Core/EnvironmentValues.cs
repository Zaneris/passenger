using System.Globalization;

namespace Passenger.Core;

/// <summary>What Passenger's own environment variables give, read as a flag is: a value that is not what its variable takes
/// is fatal, never passed over for the default.</summary>
public static class EnvironmentValues
{
    /// <summary>The time <paramref name="variable"/> gives as a whole number of milliseconds above 0, or
    /// <paramref name="otherwise"/> while it is unset or empty; any other value throws a <see cref="FormatException"/>
    /// saying what the variable takes.</summary>
    public static TimeSpan Milliseconds(string variable, TimeSpan otherwise) =>
        Milliseconds(variable, Environment.GetEnvironmentVariable(variable), otherwise);

    /// <summary>The time the value <paramref name="given"/> for <paramref name="variable"/> says, as
    /// <see cref="Milliseconds(string, TimeSpan)"/> reads it.</summary>
    internal static TimeSpan Milliseconds(string variable, string? given, TimeSpan otherwise) =>
        given is { Length: > 0 }
            ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds) && milliseconds > 0
                ? TimeSpan.FromMilliseconds(milliseconds)
                : throw new FormatException($"{variable} takes a whole number of milliseconds above 0, not '{given}'")
            : otherwise;

    /// <summary>The address <paramref name="variable"/> names in place of a host Passenger asks (a stand-in's), or null while
    /// it is unset or empty: https, or http to this machine alone (<see cref="WebAddress.Secure"/>), as what comes from it is
    /// taken as the host's own. Any other value throws a <see cref="FormatException"/> saying the variable takes the address
    /// <paramref name="what"/> ("Open VSX's API is asked at instead").</summary>
    public static Uri? Address(string variable, string what) => Address(variable, Environment.GetEnvironmentVariable(variable), what);

    /// <summary>The address the value <paramref name="given"/> for <paramref name="variable"/> names, as
    /// <see cref="Address(string, string)"/> reads it.</summary>
    internal static Uri? Address(string variable, string? given, string what) =>
        given is { Length: > 0 }
            ? WebAddress.Of(given) is { } address && WebAddress.Secure(address)
                ? address
                : throw new FormatException($"{variable} takes the https address (http on this machine) {what}, not '{given}'")
            : null;
}
