using System.Reflection.PortableExecutable;
using Passenger.App.LiveTree;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>Where a desktop app runs that a Passenger nobody sees launches (<see cref="LaunchPlan.Unseen"/>), so no window
/// of it reaches the person's screen: an Avalonia app on Avalonia's headless platform, which its inspector gives it before
/// its Main (<see cref="Passenger.Inspector.Wire.UnseenVariable"/>); on Windows every desktop app on a desktop of its own
/// too (<see cref="ChildProcesses.OnDesktopOfItsOwn"/>); elsewhere any other desktop app is refused, saying why. A program
/// with no windows of its own (a console's, a web app's) runs as it is.</summary>
public static class UnseenApps
{
    /// <summary>An app built with Avalonia, as <see cref="DesktopOf"/> names it.</summary>
    public const string Avalonia = "an Avalonia app";

    /// <summary>The UI frameworks whose assembly beside a program's makes it a desktop app, with what such an app is called.</summary>
    private static readonly (string File, string Called)[] Frameworks =
    [
        ("Avalonia.Base.dll", Avalonia), ("Microsoft.WinUI.dll", "a WinUI app"), ("Microsoft.Maui.dll", "a .NET MAUI app"),
        ("Uno.UI.dll", "an Uno Platform app"), ("Eto.dll", "an Eto.Forms app"), ("GtkSharp.dll", "a GTK app"),
    ];

    /// <summary>What desktop app a built program is, in words ("an Avalonia app"): one of a UI framework found beside it, a
    /// WindowsDesktop one (WPF, Windows Forms), or one built as a windowed program (OutputType WinExe); null for any other.</summary>
    public static string? DesktopOf(string built)
    {
        string folder = Path.GetDirectoryName(built)!;
        foreach ((string file, string called) in Frameworks)
        {
            if (File.Exists(Path.Combine(folder, file)))
            {
                return called;
            }
        }
        try
        {
            string config = Path.ChangeExtension(built, ".runtimeconfig.json");
            if (File.Exists(config) && File.ReadAllText(config).Contains(FrameworkNames.WindowsDesktop, StringComparison.Ordinal))
            {
                return "a Windows desktop app (WPF or Windows Forms)";
            }
            using FileStream stream = File.OpenRead(built);
            using PEReader reader = new(stream);
            return reader.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsGui ? "a windowed program (OutputType WinExe)" : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Whether a plan's program starts on a desktop of its own: on Windows a desktop app a Passenger nobody sees
    /// launches outside the Terminal, whose tab starts what it runs itself.</summary>
    public static bool TakesOwnDesktop(LaunchPlan plan) => OperatingSystem.IsWindows() && plan.Console != ConsoleKind.Terminal && Hidden(plan) is not null;

    /// <summary>Why a plan's program cannot run here where nobody sees it, its windows bound for the person's screen; null
    /// where it can (an Avalonia app headless, a desktop app on Windows on a desktop of its own), or where people see the
    /// window that runs it.</summary>
    public static string? WhyNot(LaunchPlan plan, string verb)
    {
        if (Hidden(plan) is not { } desktop || desktop == Avalonia && File.Exists(LiveApp.InspectorPath) || TakesOwnDesktop(plan))
        {
            return null;
        }
        string unseen = OperatingSystem.IsWindows()
            ? "Its configuration's Run console, not the Terminal, starts it on a desktop of its own, where nobody sees it"
            : desktop == Avalonia
            ? "This build of Passenger carries no inspector to start it on Avalonia's headless platform"
            : $"Such an app runs where nobody sees it only on Windows, on a desktop of its own; an Avalonia {StartupHooks.AvaloniaMajor} app runs so everywhere, on Avalonia's headless platform";
        return $"Could not {verb} {plan.ProgramName} where nobody sees it: it is {desktop}, whose windows would open on the person's screen, and a Passenger nobody "
            + $"sees opens none there. {unseen}; a person's Passenger (--mcp-attach) runs it on their screen";
    }

    /// <summary>What desktop app a plan runs where nobody is to see it; null for a plan people see run, a Godot game's (whose
    /// launch hides it itself), or a program with no windows of its own.</summary>
    private static string? Hidden(LaunchPlan plan) =>
        plan.Unseen && plan.Godot is null && plan.Built is { } built && File.Exists(built) ? DesktopOf(built) : null;
}
