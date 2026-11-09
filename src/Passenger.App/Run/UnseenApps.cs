using Passenger.App.LiveTree;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>Where a desktop app runs that a Passenger nobody sees launches (<see cref="LaunchPlan.Unseen"/>), so no window
/// of it reaches the person's screen: an Avalonia app on Avalonia's headless platform, which its inspector gives it before
/// its Main (<see cref="Passenger.Inspector.Wire.UnseenVariable"/>); on Windows every desktop app on a desktop of its own
/// too (<see cref="ChildProcesses.OnDesktopOfItsOwn"/>); elsewhere any other desktop app is refused, saying why. Which app
/// that is, is read from what the plan starts (<see cref="DesktopPrograms"/>). A program with no windows of its own (a
/// console's, a web app's) runs as it is; on Linux one not read as a desktop app finds no display (<see cref="NoDisplay"/>).</summary>
public static class UnseenApps
{
    /// <summary>Whether a plan's program starts on a desktop of its own: on Windows a desktop app a Passenger nobody sees
    /// launches outside the Terminal, whose tab starts what it runs itself.</summary>
    public static bool TakesOwnDesktop(LaunchPlan plan) => OperatingSystem.IsWindows() && plan.Console != ConsoleKind.Terminal && Hidden(plan) is not null;

    /// <summary>The file of the Avalonia app a plan starts where nobody is to see it, which takes the inspector whichever
    /// program the plan names; null for any other plan.</summary>
    public static string? Inspected(LaunchPlan plan) => Hidden(plan) is { Called: DesktopPrograms.Avalonia, PastHooks: null } app ? app.File : null;

    /// <summary>Why a plan's program cannot run here where nobody sees it, its windows bound for the person's screen; null
    /// where it can (an Avalonia app headless, a desktop app on Windows on a desktop of its own), or where people see the
    /// window that runs it. Under <paramref name="watched"/> dotnet watch starts it, which no inspector goes through.</summary>
    public static string? WhyNot(LaunchPlan plan, string verb, bool watched = false)
    {
        if (Hidden(plan) is not { } app || TakesOwnDesktop(plan))
        {
            return null;
        }
        string? past = watched ? "dotnet watch starts it without the inspector that puts it on Avalonia's headless platform, which a run without Hot Reload gives it" : app.PastHooks;
        if (app.Called == DesktopPrograms.Avalonia && past is null && File.Exists(LiveApp.InspectorPath))
        {
            return null;
        }
        string unseen = OperatingSystem.IsWindows()
            ? "Its configuration's Run console, not the Terminal, starts it on a desktop of its own, where nobody sees it"
            : app.Called == DesktopPrograms.Avalonia
            ? past ?? "This build of Passenger carries no inspector to start it on Avalonia's headless platform"
            : $"Such an app runs where nobody sees it only on Windows, on a desktop of its own; an Avalonia {StartupHooks.AvaloniaMajor} app runs so everywhere, on Avalonia's headless platform";
        return $"Could not {verb} {Path.GetFileNameWithoutExtension(app.File)} where nobody sees it: it is {app.Called}, whose windows would open on the person's screen, and a Passenger nobody "
            + $"sees opens none there. {unseen}; a person's Passenger (--mcp-attach) runs it on their screen";
    }

    /// <summary>What desktop app a plan starts where nobody is to see it, its apphost read as the assembly it runs; null for a
    /// plan people see run, a Godot game's (whose launch hides it itself), or a program with no windows of its own.</summary>
    private static DesktopProgram? Hidden(LaunchPlan plan) =>
        plan.Unseen && plan.Godot is null
            ? DesktopPrograms.Started(plan.ByAppHost && plan.Built is { } assembly ? assembly : plan.Program, plan.Arguments, plan.WorkingDirectory, plan.Built, plan.Target,
                plan.DesktopProject, plan.NamedProject)
            : null;
}
