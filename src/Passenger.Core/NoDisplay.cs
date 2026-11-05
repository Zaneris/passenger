namespace Passenger.Core;

/// <summary>A Linux desktop's display as the environment names it, taken from a process nobody sees, so that nothing it
/// starts reaches the person's screen, whatever toolkit draws it and however it is started: with no DISPLAY an X11 client
/// finds no server, and WAYLAND_DISPLAY names a socket that is not there, since a Wayland client given no name at all
/// connects to the session's own <c>wayland-0</c>.</summary>
public static class NoDisplay
{
    /// <summary>The variable that names an X display to a program.</summary>
    public const string X11 = "DISPLAY";

    /// <summary>The variable that names a Wayland socket to a program.</summary>
    public const string Wayland = "WAYLAND_DISPLAY";

    /// <summary>A compositor's connection handed to a program it starts, taken before any name.</summary>
    private const string WaylandConnection = "WAYLAND_SOCKET";

    /// <summary>What <see cref="Wayland"/> names instead: no compositor's socket.</summary>
    public const string Nowhere = "passenger-no-display";

    private static volatile bool taken;

    /// <summary>Takes the display from this process's environment, for all it starts from now; on Linux alone, the one system
    /// whose windows a variable leads to the screen.</summary>
    public static void Take()
    {
        taken = OperatingSystem.IsLinux();
        Again();
    }

    /// <summary>Takes it again where it was taken: something set over the environment (the login shell's) may have given it back.</summary>
    public static void Again()
    {
        if (taken)
        {
            OwnEnvironment.Set(X11, null);
            OwnEnvironment.Set(WaylandConnection, null);
            OwnEnvironment.Set(Wayland, Nowhere);
        }
    }

    /// <summary>Variables set over a program's environment with no display in them: a configuration names one in vain.</summary>
    public static void Over(IDictionary<string, string> variables)
    {
        variables.Remove(X11);
        variables.Remove(WaylandConnection);
        variables[Wayland] = Nowhere;
    }
}
