using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>An app installed on an iOS simulator, as its build says it starts: the simulator's UDID, the simctl of the Xcode
/// its workload builds with, the app's bundle identifier, the built bundle (whose assemblies, each beside its PDB, are those
/// the app loads) and the runtime identifier it was built for. A simulator's app is a process of this Mac: what a start
/// gives it goes in its environment as simctl passes it on (<see cref="Given"/>), what it loads of Passenger's copied into
/// its own data container first (<see cref="Carried"/>).</summary>
public sealed partial record SimulatorApp(string Udid, string Simctl, string Package, string Bundle, string RuntimeIdentifier)
{
    /// <summary>Where a start puts what the app loads of Passenger's (the relay, the inspector, the update handlers' hook): a
    /// folder of the app's own data container, which it reads wherever Passenger is, where a folder macOS keeps private
    /// (Documents, Desktop) refuses it.</summary>
    public string Carried { get; init; } = string.Empty;

    /// <summary>What begins a variable simctl gives the app it launches, the rest of its name the app's variable.</summary>
    public const string ChildPrefix = "SIMCTL_CHILD_";

    /// <summary>Foundation's switch that has simctl write its own words as it says them, its launch's line among them, rather
    /// than when it ends.</summary>
    public const string UnbufferedVariable = "NSUnbufferedIO";

    /// <summary>The relay's file name for an Apple platform, in its runtime identifier's folder of what Passenger carries.</summary>
    public const string RelayFile = "libpassenger-debug.dylib";

    /// <summary>The port on this Mac the relay listens on in the app, chosen afresh for each session.</summary>
    [IdentifierHome]
    public const string RelayPortVariable = "PASSENGER_DEBUG_PORT";

    /// <summary>simctl's arguments starting the app afresh with its output on simctl's own (<c>--console</c>), until the app
    /// ends: what the Run window shows as the program. simctl says the app's process first (<see cref="StartedIn"/>).</summary>
    public IReadOnlyList<string> Launch() => ["launch", "--console", "--terminate-running-process", Udid, Package];

    /// <summary>The environment simctl's launch is given: each of the app's variables under <see cref="ChildPrefix"/>, which
    /// simctl hands the app alone, and its own words unbuffered.</summary>
    public static Dictionary<string, string> Given(IReadOnlyDictionary<string, string> variables)
    {
        Dictionary<string, string> given = new(StringComparer.Ordinal) { [UnbufferedVariable] = "YES" };
        foreach ((string name, string value) in variables)
        {
            given[ChildPrefix + name] = value;
        }
        return given;
    }

    /// <summary>The app's process as simctl said it launched it (its bundle identifier, a colon and the process's number), in
    /// what the console shows of its first lines; null until it has.</summary>
    public string? StartedIn(Passenger.Text.TextSnapshot shown)
    {
        string said = $"{Package}: ";
        for (int line = 0; line < Math.Min(shown.LineCount, FirstLines); line++)
        {
            if (shown.GetLineText(line) is { } text && text.StartsWith(said, StringComparison.Ordinal) && int.TryParse(text.AsSpan(said.Length).Trim(),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int process))
            {
                return process.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return null;
    }

    /// <summary>The lines of the console looked through for the start: the plan's notes, then simctl's first.</summary>
    private const int FirstLines = 64;

    /// <summary>simctl's arguments ending the app, wherever it was started from.</summary>
    public IReadOnlyList<string> Terminate() => ["terminate", Udid, Package];

    /// <summary>The variables a Debug start gives the app: the runtime's profiler, Passenger's relay at
    /// <paramref name="relay"/> listening on <paramref name="port"/> of this Mac for a client that says <paramref name="token"/>.</summary>
    public static IReadOnlyDictionary<string, string> RelayVariables(string relay, int port, string token) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RuntimeVariables.EnableProfiling] = "1",
        [RuntimeVariables.Profiler] = AndroidApp.RelayProfiler,
        [RuntimeVariables.ProfilerPath] = relay,
        [RelayPortVariable] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [AndroidApp.RelayTokenVariable] = token,
        [AndroidApp.RelayWaitVariable] = "60000",
    };
}
