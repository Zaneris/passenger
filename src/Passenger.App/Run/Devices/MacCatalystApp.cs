using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>A Mac Catalyst app built here, as its build says it runs: its bundle, and the program in it the system runs. A
/// start that puts Passenger's relay, inspector or update handlers' hook in it copies them into
/// <see cref="Carried"/> and gives the app its variables through the opener, whose environment the system hands the app.</summary>
public sealed partial record MacCatalystApp(string Bundle, string Executable)
{
    /// <summary>The app's bundle identifier, which names its container.</summary>
    public string Package { get; init; } = string.Empty;

    /// <summary>The runtime identifier the app was built for, which picks the relay it loads.</summary>
    public string RuntimeIdentifier { get; init; } = string.Empty;

    /// <summary>Whether its build leaves .NET's own assemblies whole, where Passenger's inspector and update handlers' hook
    /// find every type they name: a build that trims them (a Debug build's default) takes neither, as a startup hook that
    /// cannot load ends the app.</summary>
    public bool Whole { get; init; } = true;

    /// <summary>What a start of an app whose build trims .NET's own assemblies says, <paramref name="name"/> its program.</summary>
    public static string Trimmed(string name) =>
        $"{name}'s build trims .NET's own assemblies, so {KeptOut}. Its Debug build keeps them whole with {LinkNone}";

    /// <summary>What stays out of an app whose build trims .NET's own assemblies, which the banner's edit of it says too.</summary>
    public const string KeptOut = "Passenger's inspector and update handlers' hook, which need them whole, stay out of it: "
        + "the Live Visual Tree and an agent's app tools do not reach it, and Hot Reload calls no update handler";

    /// <summary>The property that keeps a build's assemblies whole, as a project writes it.</summary>
    public const string LinkNone = $"<{MSBuildNames.MtouchLink}>None</{MSBuildNames.MtouchLink}>";

    /// <summary>Where a start puts what the app loads of Passenger's and the sockets it listens on: a folder of its container
    /// where its sandbox keeps it there (<see cref="ContainerTemporary"/>), else one of Passenger's own.</summary>
    public string Carried { get; init; } = string.Empty;

    /// <summary>The moment, on the wall clock as the system keeps a process's start, before the start opened the app: the
    /// app's process is the one started since.</summary>
    public DateTime Since { get; init; }

    /// <summary>The relay's socket in the app, under <see cref="Carried"/>: a name of one letter, as a socket's path is
    /// held to what its address holds and a container's path is long.</summary>
    public string RelaySocket => Path.Combine(Carried, "r");

    /// <summary>The inspector's socket in the app, under <see cref="Carried"/>.</summary>
    public string InspectorSocket => Path.Combine(Carried, "i");

    /// <summary>The update handlers' hook's socket, Passenger's, under <see cref="Carried"/>, which the app connects to.</summary>
    public string HandlersSocket => Path.Combine(Carried, "h");

    /// <summary>The temporary folder of a sandboxed app's container, where its sandbox lets it read files and listen on
    /// sockets, and where a start carries into a folder of its own. <paramref name="home"/> is the user's.</summary>
    public static string ContainerTemporary(string home, string package) => Path.Combine(home, "Library", "Containers", package, "Data", "tmp");

    /// <summary>The name of the folder a start carries into, its own however many Passengers and starts open the app at once.</summary>
    public static string CarriedName(int process, int start) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"passenger-{process}-{start}");

    /// <summary>The folder of the app's bundle whose assemblies it loads, each beside its PDB.</summary>
    public string Assemblies => Path.Combine(Bundle, "Contents", "MonoBundle");

    /// <summary>The relay's file, in its runtime identifier's folder of what Passenger carries.</summary>
    public const string RelayFile = SimulatorApp.RelayFile;

    /// <summary>The variables a Debug start gives the app: the runtime's profiler, Passenger's relay at
    /// <paramref name="relay"/> listening at <paramref name="socket"/> for a client that says <paramref name="token"/>.</summary>
    public static IReadOnlyDictionary<string, string> RelayVariables(string relay, string socket, string token) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RuntimeVariables.EnableProfiling] = "1",
        [RuntimeVariables.Profiler] = AndroidApp.RelayProfiler,
        [RuntimeVariables.ProfilerPath] = relay,
        [AndroidApp.RelaySocketVariable] = socket,
        [AndroidApp.RelayTokenVariable] = token,
        [AndroidApp.RelayWaitVariable] = "60000",
    };

    /// <summary>Whether entitlements, as codesign writes them (an XML property list), give the App Sandbox.</summary>
    public static bool Sandboxed(string entitlements)
    {
        int key = entitlements.IndexOf("<key>com.apple.security.app-sandbox</key>", StringComparison.Ordinal);
        return key >= 0 && entitlements.AsSpan(key + "<key>com.apple.security.app-sandbox</key>".Length).TrimStart().StartsWith("<true/>", StringComparison.Ordinal);
    }
}

/// <summary>A launch plan's Mac Catalyst app, run on this machine: the framework built, and once built, its app.</summary>
public sealed record MacCatalystLaunch(string Framework)
{
    public MacCatalystApp? App { get; init; }
}

/// <summary>A Mac Catalyst app run on this machine as the system opens an app (<c>open</c>, LaunchServices starting it, so
/// it runs as one, its sandbox and all): <see cref="Opener"/> runs it, its output the opener's own, and lasts as long as it
/// does. Where nobody sees the window that runs it, it opens hidden and never in front (<c>open -g -j</c>), its windows
/// never on the person's screen. LaunchServices starts it apart from Passenger, so it is found by its program once it runs
/// and ends with its run.</summary>
public static partial class MacCatalystApps
{
    /// <summary>The program that opens the app: a shell running <see cref="Opener"/>.</summary>
    public const string Shell = "/bin/sh";



    /// <summary>Opens the app with open's arguments, its output and errors through a FIFO each to the shell's own, and waits
    /// for it to end (<c>-W</c>), ending as it ends: open's status, its FIFOs' readers let go where open failed.</summary>
    public const string Opener = """
        d=$(mktemp -d "${TMPDIR:-/tmp}/passenger-app.XXXXXX") || exit 1
        trap 'rm -rf "$d"' EXIT
        mkfifo "$d/out" "$d/err" || exit 1
        cat "$d/out" & o=$!
        cat "$d/err" >&2 & e=$!
        open --stdout "$d/out" --stderr "$d/err" "$@"
        s=$?
        [ "$s" -eq 0 ] || kill "$o" "$e" 2>/dev/null
        wait "$o" "$e"
        exit "$s"
        """;

    /// <summary>The shell's arguments that open <paramref name="app"/> afresh, a new instance however many run, with the
    /// app's own <paramref name="arguments"/>; hidden and behind every window where <paramref name="unseen"/>. How many come
    /// before the app's own is their count less the app's.</summary>
    public static IReadOnlyList<string> Opening(MacCatalystApp app, bool unseen, IReadOnlyList<string> arguments) =>
        ["-c", Opener, "sh", "-n", "-W", .. unseen ? (string[])["-g", "-j"] : [], "-a", app.Bundle, "--args", .. arguments];
}
