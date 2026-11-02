using System.Globalization;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>What .NET the machine or the folder open lacks, worst first.</summary>
public enum DotnetLack
{
    /// <summary>Nothing: an SDK the folder's projects take is there.</summary>
    None,

    /// <summary>No dotnet at all, runtime or SDK.</summary>
    NoDotnet,

    /// <summary>A dotnet with runtimes alone, no SDK.</summary>
    RuntimeOnly,

    /// <summary>The SDK a global.json over the folder asks for is not installed.</summary>
    GlobalJsonSdk,

    /// <summary>The folder's SDK found none for another reason dotnet gives (<see cref="MissingSdk.Said"/>).</summary>
    NoSdk,

    /// <summary>A project targets a .NET newer than the folder's SDK builds.</summary>
    OlderThanProject,

    /// <summary>Nothing, but the SDK was found where neither the PATH nor DOTNET_ROOT names it.</summary>
    OffPath,
}

/// <summary>What .NET the machine (<see cref="DotnetMachine"/>) or the folder open lacks and the SDK that mends it: none
/// at all, a runtime alone, the SDK its global.json asks for, an SDK older than a project's framework; lacking nothing,
/// whether the SDK was found where the shell's PATH does not reach. Made from what was read off the UI thread.</summary>
public sealed partial record DotnetWant(DotnetLack Lack, SdkWanted? Install)
{
    /// <summary>The installation found (its folder), where there is one.</summary>
    public string? Root { get; init; }

    /// <summary>The SDK the folder's projects are evaluated with, or the machine's newest.</summary>
    public string? Sdk { get; init; }

    /// <summary>For <see cref="DotnetLack.OlderThanProject"/>, the project and the .NET it targets ("11.0").</summary>
    public (string Project, string Framework)? Newer { get; init; }

    /// <summary>For <see cref="DotnetLack.GlobalJsonSdk"/> and <see cref="DotnetLack.NoSdk"/>, dotnet's words of the want.</summary>
    public MissingSdk? Missing { get; init; }

    public static readonly DotnetWant Nothing = new(DotnetLack.None, null);

    /// <summary>Whether the folder's projects have no SDK at all to load, build and run them with, so what needs one refuses.</summary>
    public bool NoSdkAtAll => Lack is DotnetLack.NoDotnet or DotnetLack.RuntimeOnly or DotnetLack.GlobalJsonSdk or DotnetLack.NoSdk;

    /// <summary>The want of the machine and the folder: the machine's first, then the folder's SDK as its project host found
    /// it (<paramref name="missing"/>, or the version it loaded), then each project's frameworks against that version.</summary>
    public static DotnetWant Of(DotnetMachine? machine, MissingSdk? missing, string? folderSdk, IEnumerable<(string Name, IReadOnlyList<string> Frameworks)> projects)
    {
        if (machine is null)
        {
            return Nothing;
        }
        if (machine.Found is not { } found)
        {
            return new DotnetWant(DotnetLack.NoDotnet, SdkWanted.Newest);
        }
        if (machine.NewestSdk is not { } newest)
        {
            return new DotnetWant(DotnetLack.RuntimeOnly, SdkWanted.Newest) { Root = found.Root };
        }
        if (missing is { Asked: { } asked })
        {
            return new DotnetWant(DotnetLack.GlobalJsonSdk, new SdkWanted(asked)) { Root = found.Root, Sdk = newest, Missing = missing };
        }
        if (missing is not null)
        {
            return new DotnetWant(missing.NoneInstalled ? DotnetLack.RuntimeOnly : DotnetLack.NoSdk, missing.NoneInstalled ? SdkWanted.Newest : null)
            {
                Root = found.Root,
                Missing = missing,
            };
        }
        string sdk = folderSdk ?? newest;
        if (Channel(sdk) is { } built)
        {
            foreach ((string name, IReadOnlyList<string> frameworks) in projects)
            {
                if (frameworks.Select(Targeted).OfType<Version>().Where(f => f > built).Max() is { } wanted)
                {
                    string channel = wanted.ToString(2);
                    return new DotnetWant(DotnetLack.OlderThanProject, new SdkWanted(Channel: channel)) { Root = found.Root, Sdk = sdk, Newer = (name, channel) };
                }
            }
        }
        return found.Place == DotnetPlace.Installed
            ? new DotnetWant(DotnetLack.OffPath, null) { Root = found.Root, Sdk = sdk }
            : Nothing with { Root = found.Root, Sdk = sdk };
    }

    /// <summary>The .NET an SDK's version builds up to: 10.0 for 10.0.403 and 10.0.100-rc.1.</summary>
    private static Version? Channel(string sdk) =>
        SdkChannel().Match(sdk) is { Success: true } m ? new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;

    /// <summary>The .NET a target framework names (11.0 for net11.0 and net11.0-windows); null for .NET Framework's,
    /// .NET Standard's and the rest.</summary>
    public static Version? Targeted(string framework) =>
        NetFramework().Match(framework) is { Success: true } m && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is >= 5 and var major
            ? new Version(major, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;

    [GeneratedRegex(@"^(\d+)\.(\d+)\.")]
    private static partial Regex SdkChannel();

    [GeneratedRegex(@"^net(\d+)\.(\d+)(?:-|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NetFramework();
}
