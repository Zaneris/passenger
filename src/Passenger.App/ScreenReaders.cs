using System.Runtime.InteropServices;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Whether the system says a screen reader runs, read in this one place: macOS's VoiceOver (NSWorkspace's
/// voiceOverEnabled); Windows' screen reader flag (SPI_GETSCREENREADER, which Narrator, NVDA and JAWS set); on Linux the
/// accessibility bus's ScreenReaderEnabled, which Orca sets, else GNOME's screen-reader-enabled. Read as Passenger starts
/// and again as a window comes in front (<see cref="LookAgain"/>); a test stands in for the system
/// (<see cref="StandIn"/>).</summary>
public static partial class ScreenReaders
{
    /// <summary>Whether one runs, as last read; none until the first read lands.</summary>
    public static bool Running { get; private set; }

    /// <summary>What a test stands in for the system, while it does.</summary>
    private static bool? standIn;

    /// <summary>A read of the accessibility bus under way, which another look waits out rather than begins again.</summary>
    private static Task? reading;

    /// <summary>Has <paramref name="running"/> stand for the system's answer until the scope ends: for a test.</summary>
    public static IDisposable StandIn(bool running)
    {
        UiThread.Check();
        bool? before = standIn;
        standIn = running;
        LookAgain();
        return new Ending(() =>
        {
            standIn = before;
            LookAgain();
        });
    }

    /// <summary>Reads whether one runs: a stand-in's at once, macOS's and Windows' on the window's thread, Linux's off it.</summary>
    public static void LookAgain()
    {
        UiThread.Check();
        if (standIn is { } given)
        {
            Running = given;
            return;
        }
        if (!OperatingSystem.IsLinux())
        {
            Running = OperatingSystem.IsMacOS() ? VoiceOver() : OperatingSystem.IsWindows() && WindowsReader();
            return;
        }
        if (reading is { IsCompleted: false })
        {
            return;
        }
        reading = LookOnLinuxAsync();
        IdeErrors.Observe(reading);
    }

    private static async Task LookOnLinuxAsync()
    {
        bool read = await BackgroundWork.Run(ReadLinuxAsync);
        await UiThread.InvokeAsync(() =>
        {
            // A stand-in set while it read is what stands.
            if (standIn is null)
            {
                Running = read;
            }
            return true;
        });
    }

    private static bool VoiceOver()
    {
        nint pool = MacObjC.BeginPool();
        try
        {
            nint workspace = MacObjC.Send(MacObjC.Class("NSWorkspace"), MacObjC.Selector("sharedWorkspace"));
            return MacObjC.IsTrue(workspace, MacObjC.Selector("isVoiceOverEnabled"));
        }
        finally
        {
            MacObjC.EndPool(pool);
        }
    }

    private const uint ScreenReaderFlag = 0x0046;

    private static bool WindowsReader() => SystemParametersInfo(ScreenReaderFlag, 0, out int on, 0) && on != 0;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, out int value, uint update);

    /// <summary>The accessibility bus's word through gdbus, else GNOME's setting through gsettings; none where neither answers.</summary>
    private static async Task<bool> ReadLinuxAsync()
    {
        try
        {
            if (Programs.OnPath("gdbus") is { } gdbus)
            {
                ChildResult asked = await ChildProcesses.RunAsync(ChildProcesses.Command(gdbus, ["call", "--session", "--dest", "org.a11y.Bus",
                    "--object-path", "/org/a11y/bus", "--method", "org.freedesktop.DBus.Properties.Get", "org.a11y.Status", "ScreenReaderEnabled"]),
                    limit: Waits.Of(TimeSpan.FromSeconds(10)));
                if (asked.Succeeded)
                {
                    return asked.Output.Contains("true", StringComparison.Ordinal);
                }
            }
            if (Programs.OnPath("gsettings") is { } gsettings)
            {
                ChildResult asked = await ChildProcesses.RunAsync(ChildProcesses.Command(gsettings, ["get", "org.gnome.desktop.a11y.applications", "screen-reader-enabled"]),
                    limit: Waits.Of(TimeSpan.FromSeconds(10)));
                return asked.Succeeded && asked.Output.Trim() == "true";
            }
            return false;
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            IdeErrors.Write($"Screen readers: whether one runs could not be read, so none is taken to: {error.Message}");
            return false;
        }
    }

    private sealed class Ending(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}
