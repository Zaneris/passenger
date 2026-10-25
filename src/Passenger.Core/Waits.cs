using System.Globalization;

namespace Passenger.Core;

/// <summary>How much longer the IDE's own bounded waits on what it started are on a slower machine: an Aspire service's
/// start for the profiler or the debugger, a restart's wait for it, an agent's wait for a resource.
/// <see cref="ScaleVariable"/> gives the factor, a number above 0, 1 when unset; any other value is fatal.</summary>
public static class Waits
{
    [IdentifierHome]
    public const string ScaleVariable = "PASSENGER_WAIT_SCALE";

    public static double Scale => Environment.GetEnvironmentVariable(ScaleVariable) is { Length: > 0 } given
        ? double.TryParse(given, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double scale) && scale > 0 && double.IsFinite(scale)
            ? scale
            : throw new FormatException($"{ScaleVariable} takes a number above 0, not '{given}'")
        : 1;

    /// <summary>A wait written for a developer's machine, made as much longer as <see cref="Scale"/> says.</summary>
    public static TimeSpan Of(TimeSpan wait) => wait * Scale;
}
