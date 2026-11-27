using System.Diagnostics;

namespace Passenger.Core;

/// <summary>The temporary folder of what Passenger runs of the .NET SDK that builds none of the person's projects (the SDK
/// commands it <see cref="Takes"/>) and what that starts. These leave folders in it (the .NET CLI one at each, a workload
/// install's downloads in it), so it is one of this process's own in the state's (<see cref="UserTemporary.OfThisProcess"/>),
/// which goes with it. A build keeps the person's: its compiler server meets theirs by a lock in the temporary folder on
/// macOS and a pipe in /tmp, which a second server at another lock would take over. So does a program of the person's
/// (dotnet run, watch, test, exec), where a debugger and the diagnostics tools look for its runtime's socket and pipes.</summary>
public static class SdkTemporary
{
    /// <summary>The state folder whose own folder of <see cref="Kind"/> holds this process's (<see cref="UserTemporary.Of"/>),
    /// as the IDE names it once asked, given as it starts: until then, or where none is given, what it runs keeps the
    /// person's temporary folder.</summary>
    public static Func<string?>? State { get; set; }

    /// <summary>The kind of a state's own folder that holds each process's (<see cref="UserTemporary.Of"/>).</summary>
    public const string Kind = "sdk";

    /// <summary>The variables a program takes its temporary folder from: on Windows TMP, else TEMP; elsewhere TMPDIR.</summary>
    private static readonly string[] Variables = OperatingSystem.IsWindows() ? ["TMP", "TEMP"] : ["TMPDIR"];

    /// <summary>The SDK's commands that build none of the person's projects nor run a program of theirs, so start no compiler
    /// server: templates, workloads, solutions, references and packages, NuGet's own, and what the SDK tells of itself.</summary>
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "new", "workload", "sln", "add", "remove", "package", "list", "nuget", "--info",
    };

    private static readonly Lock Gate = new();
    private static string? own;
    private static bool made;

    /// <summary>Whether dotnet with these arguments runs one of the SDK's commands that build nothing of the person's.</summary>
    public static bool Takes(IReadOnlyList<string> arguments) => arguments is [var command, ..] && Commands.Contains(command);

    /// <summary>A start given this process's folder as its temporary folder (made as first given, those of Passengers no
    /// longer running removed), NuGet's scratch staying where Passenger's other restores take it, its extraction locks
    /// shared with theirs; as it was before <see cref="State"/> is given, or where the folder cannot be the user's own.</summary>
    public static ProcessStartInfo Give(ProcessStartInfo start) => Own() is { } folder ? Give(start, folder) : start;

    /// <summary><see cref="Give(ProcessStartInfo)"/> of a given folder.</summary>
    internal static ProcessStartInfo Give(ProcessStartInfo start, string folder)
    {
        if (!(start.Environment.TryGetValue(NuGetScratch.Variable, out string? named) && !string.IsNullOrEmpty(named)) && NuGetScratch.Shared() is { } scratch)
        {
            start.Environment[NuGetScratch.Variable] = scratch;
        }
        foreach (string variable in Variables)
        {
            start.Environment[variable] = folder;
        }
        return start;
    }

    /// <summary>This process's folder, made once <see cref="State"/> is given; null before, or where it cannot be made.</summary>
    private static string? Own()
    {
        if (State is not { } named)
        {
            return null;
        }
        lock (Gate)
        {
            if (!made && named() is { } state)
            {
                made = true;
                try
                {
                    own = UserTemporary.OfThisProcess(UserTemporary.Own(UserTemporary.Of(state, Kind)));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    PassengerLog.Write($"What Passenger runs of the .NET SDK keeps the person's temporary folder: {error.Message}");
                }
            }
            return own;
        }
    }
}
