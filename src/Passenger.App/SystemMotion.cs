using System.Runtime.InteropServices;
using Avalonia;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>What the system asks of motion and carets, read in this one place: macOS's Reduce Motion, its non-blinking
/// cursor and its insertion point's blink; Windows' animations and caret blink time (INFINITE for none); GNOME's
/// enable-animations, cursor-blink and its cycle; elsewhere Passenger's own. Read as Passenger starts and again as a window
/// comes in front (<see cref="LookAgain"/>), and put in force for every window where it changed: every caret's blink
/// (<see cref="Carets"/>) and, with less motion asked, the still styles (<see cref="StillMotion"/>).
/// <c>PASSENGER_SYSTEM_MOTION</c> stands in for the system (<see cref="MotionAsked.Parse"/>), as a test run's does.</summary>
public static partial class SystemMotion
{
    /// <summary>Names what the system asks in its place: <c>full</c> or <c>reduced</c> and <c>blink=none</c>,
    /// <c>blink=MS</c> or <c>blink=SHOWN/HIDDEN</c>, comma-separated; a malformed value is fatal as the IDE starts.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_SYSTEM_MOTION";

    /// <summary>What is in force, Passenger's own until the first read lands.</summary>
    public static MotionAsked InForce { get; private set; } = MotionAsked.Default;

    /// <summary>What a test stands in for the system, over the variable's, asked at each look.</summary>
    private static Func<MotionAsked>? standIn;

    /// <summary>Whether the log has said what is in force.</summary>
    private static bool told;

    /// <summary>The still styles, made once they are first laid.</summary>
    private static StillMotion? still;

    /// <summary>A read of GNOME's settings under way, which another look waits out rather than begins again.</summary>
    private static Task? reading;

    /// <summary>What the variable names in the system's place; null where it names nothing.</summary>
    public static MotionAsked? Named
    {
        get
        {
            try
            {
                return Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? MotionAsked.Parse(value) : null;
            }
            catch (FormatException error)
            {
                throw new FormatException($"{Variable} {error.Message}", error);
            }
        }
    }

    /// <summary>Has the system's answer read in place of the system's own until the scope ends, put in force at once: for a test.</summary>
    public static IDisposable StandIn(MotionAsked asked) => StandIn(() => asked);

    /// <summary>Has <paramref name="system"/> asked in place of the system at each look until the scope ends, its answer put
    /// in force at once: for a test of what a look finds changed.</summary>
    public static IDisposable StandIn(Func<MotionAsked> system)
    {
        UiThread.Check();
        Func<MotionAsked>? before = standIn;
        standIn = system;
        LookAgain();
        return new Ending(() =>
        {
            standIn = before;
            LookAgain();
        });
    }

    /// <summary>Reads what the system asks and puts it in force where it changed: a stand-in's at once, macOS's and
    /// Windows' on the window's thread, GNOME's off it.</summary>
    public static void LookAgain()
    {
        UiThread.Check();
        if ((standIn?.Invoke() ?? Named) is { } given)
        {
            PutInForce(given);
            return;
        }
        if (!OperatingSystem.IsLinux())
        {
            PutInForce(ReadAtOnce());
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
        MotionAsked read = await BackgroundWork.Run(ReadGnomeAsync);
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

    /// <summary>What the system asks now, read from the system itself whatever stands in for it.</summary>
    public static Task<MotionAsked> ReadSystemAsync() => OperatingSystem.IsLinux() ? ReadGnomeAsync() : Task.FromResult(ReadAtOnce());

    private static MotionAsked ReadAtOnce() =>
        OperatingSystem.IsMacOS() ? ReadMac()
        : OperatingSystem.IsWindows() ? ReadWindows()
        : MotionAsked.Default;

    /// <summary>Lays what is asked over every window, where it differs from what is in force.</summary>
    private static void PutInForce(MotionAsked asked)
    {
        if (Application.Current is not { } app)
        {
            return;
        }
        if (asked != InForce || !told)
        {
            told = true;
            IdeErrors.Write($"System motion: {asked}");
        }
        InForce = asked;
        if (!app.Resources.TryGetValue(Carets.BlinkKey, out object? blinking) || !Equals(blinking, asked.Blink))
        {
            Carets.UseBlink(app, asked.Blink);
        }
        bool laid = still is not null && app.Styles.Contains(still);
        if (asked.ReducesMotion && !laid)
        {
            app.Styles.Add(still ??= new StillMotion());
        }
        else if (!asked.ReducesMotion && laid)
        {
            app.Styles.Remove(still!);
        }
    }

    /// <summary>macOS's answer, the Accessibility framework's non-blinking cursor read where the system has it (macOS 15 on).</summary>
    private static MotionAsked ReadMac()
    {
        nint pool = MacObjC.BeginPool();
        try
        {
            nint workspace = MacObjC.Send(MacObjC.Class("NSWorkspace"), MacObjC.Selector("sharedWorkspace"));
            bool reduces = MacObjC.IsTrue(workspace, MacObjC.Selector("accessibilityDisplayShouldReduceMotion"));
            nint defaults = MacObjC.Send(MacObjC.Class("NSUserDefaults"), MacObjC.Selector("standardUserDefaults"));
            return MotionAsked.OfMac(reduces, PrefersSteadyCaret(), Period("NSTextInsertionPointBlinkPeriodOn"), Period("NSTextInsertionPointBlinkPeriodOff"));

            double? Period(string key)
            {
                nint name = MacObjC.Send(MacObjC.Class("NSString"), MacObjC.Selector("stringWithUTF8String:"), key);
                return MacObjC.Send(defaults, MacObjC.Selector("objectForKey:"), name) == 0 ? null
                    : MacObjC.Number(defaults, MacObjC.Selector("doubleForKey:"), name);
            }
        }
        finally
        {
            MacObjC.EndPool(pool);
        }
    }

    private static unsafe bool PrefersSteadyCaret() =>
        NativeLibrary.TryLoad("/System/Library/Frameworks/Accessibility.framework/Accessibility", out nint framework)
        && NativeLibrary.TryGetExport(framework, "AXPrefersNonBlinkingTextInsertionIndicator", out nint asked)
        && ((delegate* unmanaged<byte>)asked)() != 0;

    private const uint ClientAreaAnimation = 0x1042;

    private static MotionAsked ReadWindows()
    {
        bool animates = !SystemParametersInfo(ClientAreaAnimation, 0, out int on, 0) || on != 0;
        return MotionAsked.OfWindows(animates, GetCaretBlinkTime());
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, out int value, uint update);

    [LibraryImport("user32.dll", EntryPoint = "GetCaretBlinkTime")]
    private static partial uint GetCaretBlinkTime();

    /// <summary>GNOME's interface settings as gsettings lists them; Passenger's own where there is no gsettings or schema.</summary>
    private static async Task<MotionAsked> ReadGnomeAsync()
    {
        if (Programs.OnPath("gsettings") is not { } gsettings)
        {
            return MotionAsked.Default;
        }
        try
        {
            ChildResult listed = await ChildProcesses.RunAsync(ChildProcesses.Command(gsettings, ["list-recursively", "org.gnome.desktop.interface"]),
                limit: Waits.Of(TimeSpan.FromSeconds(10)));
            return listed.Succeeded ? MotionAsked.OfGnome(listed.Output) : MotionAsked.Default;
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            IdeErrors.Write($"System motion: GNOME's interface settings could not be read, so Passenger's own stand: {error.Message}");
            return MotionAsked.Default;
        }
    }

    private sealed class Ending(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}
