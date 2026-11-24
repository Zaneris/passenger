using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Passenger.App.Godot;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A Godot game drawn in Passenger's Game window, as Godot's own editor embeds one: started with the arguments of a
/// native window's embedding at a place (<see cref="GameWindows.ArgumentsAt"/>), its window is owned by that window
/// (transient for it under X11) and held over the place, takes a key, and follows the place as it grows. The host is a
/// native window the test makes, as a headless window has no handle; Windows, or Linux with an X11 session (DISPLAY), and a
/// Godot 4.4 or newer are needed, or the test skips saying why, as it does in Godot's words where none of Godot's renderers
/// starts; stopped as a debugger's stop holds it, its last picture is read from the screen and, its window let go of, the
/// host is in front of it and its moves wait for none of it. Godot runs with a window, on its Compatibility renderer, so
/// run alone, and never on the Mac. Past its one wait for Godot's version it runs on one thread, the host's, whose messages
/// it handles as it waits.</summary>
[Collection(Alone.Name)]
public sealed partial class GameWindowsTests
{
    private const string Keys = "extends Node2D\n\nvar _last := Vector2i.ZERO\n\n\nfunc _ready() -> void:\n\tprint(\"TEST: ready\")\n\n\n"
        + "func _process(_delta: float) -> void:\n\tvar size := DisplayServer.window_get_size()\n\tif size != _last:\n\t\t_last = size\n"
        + "\t\tprint(\"TEST: size %dx%d\" % [size.x, size.y])\n\n\nfunc _input(event: InputEvent) -> void:\n\tvar key := event as InputEventKey\n"
        + "\tif key != null and key.pressed:\n\t\tprint(\"TEST: key %s\" % OS.get_keycode_string(key.keycode))\n";

    [Fact]
    public async Task A_games_window_is_owned_by_passengers_window_held_over_the_pane_takes_a_key_follows_the_pane_and_holds_none_of_its_moves_while_stopped()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(),
            "Passenger holds a game's window over its Game window on Windows and under X11; macOS draws an embedded game as a layer");
        Assert.SkipWhen(OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "No X11 session: DISPLAY is unset, so there is no window to hold a game's over");
        GodotFound? godot = GodotExecutables.Find(null);
        Assert.SkipWhen(godot is not { Exists: true, WhyNotScripts: null }, "No Godot 4 was found (GODOT, the PATH, Applications) to run a game of GDScript");
        string? version = await GodotExecutables.VersionAsync(godot!, TestContext.Current.CancellationToken);
        Assert.SkipUnless(GodotExecutables.Embeds(version), $"Godot {version} draws a game only in a window of its own: embedding takes Godot 4.4 or newer");

        string folder = TestFolders.Create("game-windows-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Keys\"\nrun/main_scene=\"res://main.tscn\"\n\n"
            + "[display]\n\nwindow/size/viewport_width=640\nwindow/size/viewport_height=360\n\n[rendering]\n\nenvironment/defaults/default_clear_color=Color(0.1, 0.6, 0.3, 1)\n");
        TestFolders.Write(folder, "main.tscn", "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://keys.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n");
        TestFolders.Write(folder, "keys.gd", Keys);

        using Host host = Host.Make();
        using GameWindows windows = GameWindows.Over(host.Kind, host.Handle) ?? throw new Xunit.Sdk.XunitException($"No embedding over a {host.Kind} window here");
        PixelRect place = new(host.X + 40, host.Y + 60, 640, 360);
        // Compatibility draws where OpenGL or ANGLE over Direct3D 11 does (WARP too), as a VM's GPU without D3D12's shader models.
        ProcessStartInfo start = ChildProcesses.Command(godot!.Executable, [.. windows.ArgumentsAt(place), "--rendering-method", "gl_compatibility", "--path", folder], folder);
        foreach ((string name, string value) in GodotTests.Game.Home(folder + "-home"))
        {
            start.Environment[name] = value;
        }
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        ConcurrentQueue<string> said = new();
        using Process game = ChildProcesses.Start(start);
        Task heard = Task.CompletedTask;
        try
        {
            heard = ChildProcesses.Hear(game, said.Enqueue, said.Enqueue);
            string Said() => string.Join(" | ", said.TakeLast(12));
            bool Told(string words) => said.Any(l => l.Contains(words, StringComparison.Ordinal));
            // Its scene never ran, and Godot named a renderer of its that could not start (it says so last in a dialog of its own).
            bool NoRenderer() => !Told("TEST: ready") && said.Any(l => l.Contains("(drivers/", StringComparison.Ordinal)
                || l.Contains("video card drivers", StringComparison.Ordinal) || l.Contains("Could not initialize", StringComparison.Ordinal));
            nint window = 0;
            bool came = Waiting.Until(() => host.Pump() && (window = windows.Find(game.Id)) != 0, TimeSpan.FromMinutes(1));
            Assert.SkipWhen(!came && NoRenderer(), $"No renderer of Godot's starts here, so its game never ran (Godot said: {Said()})");
            Assert.True(came, $"No window of the game's owned by the host's came: the search saw {windows.Seen} (Godot said: {Said()})");
            Assert.Equal(host.Handle, windows.OwnerOf(window));

            // Held once its scene runs, past the sizing Godot does as it starts, as the Game window holds it again then.
            bool ready = Waiting.Until(() => host.Pump() && Told("TEST: ready"), TimeSpan.FromSeconds(30));
            Assert.SkipWhen(!ready && NoRenderer(), $"No renderer of Godot's starts here, so its game never ran (Godot said: {Said()})");
            Assert.True(ready, $"The game's scene never ran (Godot said: {Said()})");
            windows.Hold(window, place, shown: true);
            bool held = Waiting.Until(() => host.Pump() && Told("TEST: size 640x360"), TimeSpan.FromSeconds(30));
            Assert.True(held, $"The game's window was not held at 640x360 over the pane's place: it is {windows.PlaceOf(window)}, the search saw {windows.Seen} (Godot said: {Said()})");
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(place, windows.PlaceOf(window));
            }

            host.Press(window);
            Assert.True(Waiting.Until(() => host.Pump() && Told("TEST: key A"), TimeSpan.FromSeconds(30)), $"The game took no key A (Godot said: {Said()})");

            windows.Hold(window, place.WithWidth(800).WithHeight(450), shown: true);
            Assert.True(Waiting.Until(() => host.Pump() && Told("TEST: size 800x450"), TimeSpan.FromSeconds(30)),
                $"The game's window did not follow the pane's place as it grew to 800x450: it is {windows.PlaceOf(window)} (Godot said: {Said()})");

            // Stopped as a debugger's stop holds it, its window let go of as the Game window lets it go: the host waits for none of it.
            OwnProcess stopped = OwnProcess.Of(game);
            stopped.Suspend("the game, as a debugger's stop holds it");
            GamePicture? last = windows.Picture(place);
            windows.Disown(window);
            if (OperatingSystem.IsWindows())
            {
                // The game's clear colour, green, read from the screen at the middle of its place.
                byte[]? middle = last is { } p ? p.Bgra.AsSpan(((p.Height / 2 * p.Width) + (p.Width / 2)) * 4, 3).ToArray() : null;
                Assert.True(last is not null && last.Width == place.Width && last.Height == place.Height && middle is [var blue, var green, var red] && green > red + 60 && green > blue + 30,
                    $"The stopped game's last picture was not read from the screen at its place, so the Game window would show nothing of it: {(middle is null ? "none read" : $"its middle is {Convert.ToHexString(middle)} (blue, green, red)")}");
                Assert.False(host.Behind(window), "The stopped game's window, let go of, still stood in front of the host, hiding what the Game window shows of it");
            }
            bool freed = false;
            using CancellationTokenSource answered = new();
            Task freeing = Waiting.Apart(async () =>
            {
                await Waiting.ForAsync(TimeSpan.FromSeconds(15), "the host's moves, which a stopped game would hold", answered.Token).Ended();
                if (!answered.IsCancellationRequested)
                {
                    freed = true;
                    stopped.Resume("the game, so a move of the host's it held ends");
                }
            }, TestContext.Current.CancellationToken);
            // Nothing is asked of the window let go of, as the Game window asks nothing of it until it is taken back.
            host.Raise();
            answered.Cancel();
            Assert.True(Waiting.Until(() => host.Pump() && freeing.IsCompleted, TimeSpan.FromSeconds(30)), "The wait for the host's moves never ended");
            Assert.False(freed, "The host's coming in front waited for the game a debugger stopped until it ran again: Passenger would freeze at a "
                + "breakpoint in a game its Game window holds");

            // Run on, it is owned again once it answers and in front of the host, and takes a key.
            stopped.Resume("the game, as its debugger lets it run on");
            bool returned = false;
            Task back = Waiting.Apart(() => returned = windows.Return(window, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.True(Waiting.Until(() => host.Pump() && back.IsCompleted && host.Behind(window), TimeSpan.FromSeconds(30)),
                $"The game's window, run on, did not come in front of the host again: taken back {(back.IsCompleted ? returned.ToString() : "not yet")}, owned by "
                + $"0x{windows.OwnerOf(window):X}, in front {host.Behind(window)} (Godot said: {Said()})");
            Assert.True(returned, "The game run on was never taken back");
            Assert.Equal(host.Handle, windows.OwnerOf(window));
            int keys = said.Count(l => l.Contains("TEST: key A", StringComparison.Ordinal));
            host.Press(window);
            Assert.True(Waiting.Until(() => host.Pump() && said.Count(l => l.Contains("TEST: key A", StringComparison.Ordinal)) > keys, TimeSpan.FromSeconds(30)),
                $"The game run on after its stop took no key A (Godot said: {Said()})");

            // Stopped again once taken back: the host's moves still wait for none of it.
            stopped.Suspend("the game, stopped again");
            windows.Disown(window);
            bool freedAgain = false;
            using CancellationTokenSource answeredAgain = new();
            Task freeingAgain = Waiting.Apart(async () =>
            {
                await Waiting.ForAsync(TimeSpan.FromSeconds(15), "the host's move, which the game stopped again would hold", answeredAgain.Token).Ended();
                if (!answeredAgain.IsCancellationRequested)
                {
                    freedAgain = true;
                    stopped.Resume("the game stopped again, so a move of the host's it held ends");
                }
            }, TestContext.Current.CancellationToken);
            host.Raise();
            answeredAgain.Cancel();
            Assert.True(Waiting.Until(() => host.Pump() && freeingAgain.IsCompleted, TimeSpan.FromSeconds(30)), "The wait for the host's move never ended");
            Assert.False(freedAgain, "The host's coming in front waited for the game stopped a second time: its window taken back joined the host to it");
            stopped.Resume("the game stopped again, as its debugger lets it run on");
        }
        finally
        {
            ChildProcesses.Kill(game);
            Waiting.Until(() => heard.IsCompleted, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>A native window of the test's own to hold a game's over, as Passenger's main window is: Win32's or X11's.</summary>
    private abstract class Host : IDisposable
    {
        public abstract string Kind { get; }

        public abstract nint Handle { get; }

        public abstract int X { get; }

        public abstract int Y { get; }

        public static Host Make() => OperatingSystem.IsWindows() ? new Win32Host() : new X11Host();

        /// <summary>Handles what the system sent the host's thread; true, for a wait's condition to go on.</summary>
        public abstract bool Pump();

        /// <summary>A press and release of A sent to a game's window.</summary>
        public abstract void Press(nint window);

        /// <summary>The host brought in front of the windows, as Passenger's is called in front at a debugger's stop.</summary>
        public abstract void Raise();

        /// <summary>Whether a game's window is in front of the host's.</summary>
        public abstract bool Behind(nint game);

        public abstract void Dispose();
    }

    private sealed partial class Win32Host : Host
    {
        private const string User32 = "user32.dll";
        private const uint Overlapped = 0x00CF0000;
        private const uint Visible = 0x10000000;
        private const uint KeyDown = 0x100;
        private const uint KeyUp = 0x101;
        private const nint KeyA = 0x41;

        /// <summary>GW_HWNDPREV: the window just in front of one.</summary>
        private const uint InFront = 3;
        private readonly nint window;

        public Win32Host()
        {
            window = CreateWindowExW(0, "STATIC", "Passenger's game host", Overlapped | Visible, 100, 100, 1000, 700, 0, 0, 0, 0);
            Assert.True(window != 0, $"The host window could not be made: {Marshal.GetLastPInvokeError()}");
        }

        public override string Kind => "HWND";

        public override nint Handle => window;

        public override int X => 100;

        public override int Y => 100;

        public override unsafe bool Pump()
        {
            Message message;
            while (PeekMessageW(&message, 0, 0, 0, 1) != 0)
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            return true;
        }

        public override void Press(nint game)
        {
            PostMessageW(game, KeyDown, KeyA, 1);
            PostMessageW(game, KeyUp, KeyA, unchecked((nint)0xC0000001));
        }

        public override void Raise() => BringWindowToTop(window);

        public override bool Behind(nint game)
        {
            for (nint front = GetWindow(window, InFront); front != 0; front = GetWindow(front, InFront))
            {
                if (front == game)
                {
                    return true;
                }
            }
            return false;
        }

        public override void Dispose() => DestroyWindow(window);

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public nint Window;
            public uint Kind;
            public nint WParam;
            public nint LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        [LibraryImport(User32, StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial nint CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height,
            nint parent, nint menu, nint instance, nint parameter);

        [LibraryImport(User32)]
        private static partial int DestroyWindow(nint window);

        [LibraryImport(User32)]
        private static unsafe partial int PeekMessageW(Message* message, nint window, uint first, uint last, uint remove);

        [LibraryImport(User32)]
        private static unsafe partial int TranslateMessage(Message* message);

        [LibraryImport(User32)]
        private static unsafe partial nint DispatchMessageW(Message* message);

        [LibraryImport(User32)]
        private static partial int PostMessageW(nint window, uint message, nint wParam, nint lParam);

        [LibraryImport(User32)]
        private static partial int BringWindowToTop(nint window);

        [LibraryImport(User32)]
        private static partial nint GetWindow(nint window, uint relation);
    }

    private sealed unsafe partial class X11Host : Host
    {
        private const string Library = "libX11.so.6";
        private const int KeyPress = 2;
        private const int KeyRelease = 3;
        private const long KeyPressMask = 1;
        private const long KeyReleaseMask = 2;
        private const nint LetterA = 0x61;
        private readonly nint display;
        private readonly nint window;

        public X11Host()
        {
            display = XOpenDisplay(0);
            Assert.True(display != 0, $"No X11 display could be opened at {Environment.GetEnvironmentVariable("DISPLAY")}");
            window = XCreateSimpleWindow(display, XDefaultRootWindow(display), 100, 100, 1000, 700, 0, 0, 0);
            XMapWindow(display, window);
            XFlush(display);
        }

        public override string Kind => "XID";

        public override nint Handle => window;

        public override int X => 100;

        public override int Y => 100;

        public override bool Pump()
        {
            XFlush(display);
            return true;
        }

        public override void Press(nint game)
        {
            foreach (int kind in (int[])[KeyPress, KeyRelease])
            {
                KeyEvent key = new()
                {
                    Type = kind,
                    SendEvent = 1,
                    Display = display,
                    Window = game,
                    Root = XDefaultRootWindow(display),
                    KeyCode = XKeysymToKeycode(display, LetterA),
                    SameScreen = 1,
                };
                XSendEvent(display, game, 1, kind == KeyPress ? KeyPressMask : KeyReleaseMask, ref key);
            }
            XFlush(display);
        }

        public override void Raise()
        {
            XRaiseWindow(display, window);
            XFlush(display);
        }

        /// <summary>Always: the window manager keeps a window transient for the host in front of it.</summary>
        public override bool Behind(nint game) => true;

        public override void Dispose()
        {
            XDestroyWindow(display, window);
            XCloseDisplay(display);
        }

        /// <summary>XKeyEvent inside XEvent's 24 longs, as a 64-bit Linux lays it out.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 192)]
        private struct KeyEvent
        {
            [FieldOffset(0)] public int Type;
            [FieldOffset(8)] public nuint Serial;
            [FieldOffset(16)] public int SendEvent;
            [FieldOffset(24)] public nint Display;
            [FieldOffset(32)] public nint Window;
            [FieldOffset(40)] public nint Root;
            [FieldOffset(48)] public nint Subwindow;
            [FieldOffset(56)] public nuint Time;
            [FieldOffset(64)] public int X;
            [FieldOffset(68)] public int Y;
            [FieldOffset(72)] public int XRoot;
            [FieldOffset(76)] public int YRoot;
            [FieldOffset(80)] public uint State;
            [FieldOffset(84)] public uint KeyCode;
            [FieldOffset(88)] public int SameScreen;
        }

        [LibraryImport(Library)]
        private static partial nint XOpenDisplay(nint name);

        [LibraryImport(Library)]
        private static partial int XCloseDisplay(nint display);

        [LibraryImport(Library)]
        private static partial nint XDefaultRootWindow(nint display);

        [LibraryImport(Library)]
        private static partial nint XCreateSimpleWindow(nint display, nint parent, int x, int y, uint width, uint height, uint border, nuint borderColour, nuint background);

        [LibraryImport(Library)]
        private static partial int XMapWindow(nint display, nint window);

        [LibraryImport(Library)]
        private static partial int XRaiseWindow(nint display, nint window);

        [LibraryImport(Library)]
        private static partial int XDestroyWindow(nint display, nint window);

        [LibraryImport(Library)]
        private static partial int XFlush(nint display);

        [LibraryImport(Library)]
        private static partial byte XKeysymToKeycode(nint display, nint keysym);

        [LibraryImport(Library)]
        private static partial int XSendEvent(nint display, nint window, int propagate, long mask, ref KeyEvent message);
    }
}
