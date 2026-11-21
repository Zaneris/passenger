using System.Runtime.InteropServices;
using Avalonia;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Whether the system asks for high contrast, read in this one place: Windows' contrast themes
/// (SPI_GETHIGHCONTRAST, dark or light by the window colour they give), macOS's Increase contrast (NSWorkspace's
/// accessibilityDisplayShouldIncreaseContrast) and GNOME's High Contrast (org.gnome.desktop.a11y.interface's
/// high-contrast); elsewhere none. Read as Passenger starts and again as a window comes in front (<see cref="LookAgain"/>),
/// as <see cref="SystemMotion"/> is, and put in force through <see cref="Themes.UseContrast"/>. A Passenger nobody sees
/// reads only what stands in: <c>PASSENGER_SYSTEM_CONTRAST</c> (<see cref="ContrastAsked.Parse"/>) or a test's.</summary>
public static partial class SystemContrast
{
    /// <summary>Names what the system asks in its place: <c>none</c>, <c>high</c>, <c>high-dark</c> or <c>high-light</c>;
    /// a malformed value is fatal as the IDE starts.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_SYSTEM_CONTRAST";

    /// <summary>What is in force, none until the first read lands.</summary>
    public static ContrastAsked InForce { get; private set; } = ContrastAsked.None;

    /// <summary>What a test stands in for the system, over the variable's, asked at each look.</summary>
    private static Func<ContrastAsked>? standIn;

    /// <summary>Whether the log has said what is in force.</summary>
    private static bool told;

    /// <summary>A read of GNOME's settings under way, which another look waits out rather than begins again.</summary>
    private static Task? reading;

    /// <summary>What the variable names in the system's place; null where it names nothing.</summary>
    public static ContrastAsked? Named
    {
        get
        {
            try
            {
                return Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? ContrastAsked.Parse(value) : null;
            }
            catch (FormatException error)
            {
                throw new FormatException($"{Variable} {error.Message}", error);
            }
        }
    }

    /// <summary>Has <paramref name="asked"/> stand in for the system until the scope ends, put in force at once: for a test.</summary>
    public static IDisposable StandIn(ContrastAsked asked)
    {
        UiThread.Check();
        Func<ContrastAsked>? before = standIn;
        standIn = () => asked;
        LookAgain();
        return new Ending(() =>
        {
            standIn = before;
            LookAgain();
        });
    }

    /// <summary>Reads what the system asks and puts it in force where it changed: a stand-in's at once, macOS's and
    /// Windows' on the window's thread, GNOME's off it; nothing of the system's own in a Passenger nobody sees.</summary>
    public static void LookAgain()
    {
        UiThread.Check();
        if ((standIn?.Invoke() ?? Named) is { } given)
        {
            PutInForce(given);
            return;
        }
        if (PassengerApp.ShowsNoWindow)
        {
            PutInForce(ContrastAsked.None);
            return;
        }
        if (!OperatingSystem.IsLinux())
        {
            PutInForce(OperatingSystem.IsMacOS() ? ReadMac() : OperatingSystem.IsWindows() ? ReadWindows() : ContrastAsked.None);
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
        ContrastAsked read = await BackgroundWork.Run(ReadGnomeAsync);
        await UiThread.InvokeAsync(() =>
        {
            // A stand-in set while it read is what stands.
            if (standIn is null && Named is null)
            {
                PutInForce(read);
            }
            return true;
        });
    }

    private static void PutInForce(ContrastAsked asked)
    {
        if (asked != InForce || !told)
        {
            told = true;
            IdeErrors.Write($"System contrast: {asked}");
        }
        InForce = asked;
        if (Application.Current is { } app)
        {
            Themes.UseContrast(app, asked);
        }
    }

    private static ContrastAsked ReadMac()
    {
        nint pool = MacObjC.BeginPool();
        try
        {
            nint workspace = MacObjC.Send(MacObjC.Class("NSWorkspace"), MacObjC.Selector("sharedWorkspace"));
            return ContrastAsked.Of(MacObjC.IsTrue(workspace, MacObjC.Selector("accessibilityDisplayShouldIncreaseContrast")));
        }
        finally
        {
            MacObjC.EndPool(pool);
        }
    }

    private const uint GetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x1;
    private const int WindowColour = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    private static unsafe ContrastAsked ReadWindows()
    {
        HighContrast asked = new() { Size = (uint)sizeof(HighContrast) };
        bool read = SystemParametersInfo(GetHighContrast, asked.Size, ref asked, 0);
        return ContrastAsked.OfWindows(read && (asked.Flags & HighContrastOn) != 0, GetSysColor(WindowColour));
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint update);

    [LibraryImport("user32.dll", EntryPoint = "GetSysColor")]
    private static partial uint GetSysColor(int index);

    /// <summary>GNOME's High Contrast as gsettings says it; none where there is no gsettings or schema.</summary>
    private static async Task<ContrastAsked> ReadGnomeAsync()
    {
        if (Programs.OnPath("gsettings") is not { } gsettings)
        {
            return ContrastAsked.None;
        }
        try
        {
            ChildResult said = await ChildProcesses.RunAsync(ChildProcesses.Command(gsettings, ["get", "org.gnome.desktop.a11y.interface", "high-contrast"]),
                limit: Waits.Of(TimeSpan.FromSeconds(10)));
            return said.Succeeded ? ContrastAsked.OfGnome(said.Output) : ContrastAsked.None;
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            IdeErrors.Write($"System contrast: GNOME's accessibility settings could not be read, so none is asked: {error.Message}");
            return ContrastAsked.None;
        }
    }

    private sealed class Ending(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}
