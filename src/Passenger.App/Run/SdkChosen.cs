using Passenger.Text;

namespace Passenger.App.Run;

/// <summary>A folder whose global.json chose a .NET SDK that does not build one of its projects: what Passenger says of it
/// (<see cref="Refusals.Dotnet"/>), the global.json, and the line it asks for the SDK on, where a build's error of it is
/// placed, since that is what a person changes.</summary>
public sealed record SdkChosen(string Words, string GlobalJson, TextPosition At)
{
    /// <summary>The SDK's error that it does not build a project's .NET.</summary>
    public const string TooOld = "NETSDK1045";

    /// <summary>A build's problem as Problems lists it: the SDK's error that it does not build a project's .NET placed at
    /// global.json's ask, with why ahead of the SDK's own words; any other as it came.</summary>
    public Problem Placed(Problem problem) =>
        problem.Code == TooOld ? problem with { Path = GlobalJson, Position = At, Message = $"{Words}\n{problem.Message}" } : problem;

    /// <summary>Whether a problem is the SDK's error placed at this global.json.</summary>
    public static bool IsPlaced(Problem problem) =>
        problem.Code == TooOld && problem.Path is { } path && Path.GetFileName(path).Equals("global.json", StringComparison.OrdinalIgnoreCase);
}
