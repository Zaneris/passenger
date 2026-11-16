namespace Passenger.App.Run.Devices;

/// <summary>A Mac Catalyst app built here, as its build says it runs: its bundle, and the program in it the system runs.</summary>
public sealed record MacCatalystApp(string Bundle, string Executable);

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
