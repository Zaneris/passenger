using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.Godot;

/// <summary>A Godot game's window held over a place of Passenger's, as Godot's own editor holds a game it embeds: the game,
/// started with <c>--wid</c> and a window's handle (<see cref="Arguments"/>), makes its window borderless and out of the
/// taskbar, owned by that window on Windows and transient for it on X11, and this finds it by the game's process, holds it
/// on a place on the screen, shows or hides it, and gives it the keys. macOS draws an embedded game as a layer, not a
/// window, and Wayland embeds a game only through a compositor of the embedder's own, so neither has one here.</summary>
public abstract partial class GameWindows : IDisposable
{
    private GameWindows(nint host) => Host = host;

    /// <summary>The window the game's is owned by: Passenger's.</summary>
    public nint Host { get; }

    /// <summary>The embedding a window of Passenger's gives a game, or null where it has none: a window of no handle (one
    /// nobody sees), macOS, or Wayland without X11.</summary>
    public static GameWindows? For(TopLevel window)
    {
        try
        {
            return window.TryGetPlatformHandle() is { } handle ? Over(handle.HandleDescriptor, handle.Handle) : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The embedding over a native window by its kind ("HWND", "XID") and handle; null for another kind or system.</summary>
    public static GameWindows? Over(string? kind, nint host) => kind switch
    {
        "HWND" when OperatingSystem.IsWindows() => new Win32(host),
        "XID" when OperatingSystem.IsLinux() => X11.Open(host),
        _ => null,
    };

    /// <summary>The arguments that have a Godot game make its window this embedding's, placed before the game's own: the
    /// host's handle, and on X11 the X11 display driver, which a Wayland session would not otherwise give it.</summary>
    public IReadOnlyList<string> Arguments => this is X11
        ? ["--wid", Host.ToString(System.Globalization.CultureInfo.InvariantCulture), "--display-driver", "x11"]
        : ["--wid", Host.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <summary>The arguments of a game's own placing of its window, which an embedded game's place takes the place of,
    /// each with whether a value follows it.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Placing = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["--position"] = true, ["--resolution"] = true, ["--screen"] = true, ["-f"] = false, ["--fullscreen"] = false, ["-m"] = false,
        ["--maximized"] = false, ["-t"] = false, ["--always-on-top"] = false,
    };

    /// <summary>The game's window of a process, owned by the host; 0 while it has none.</summary>
    public abstract nint Find(int processId);

    /// <summary>The window a game's is owned by (Windows) or transient for (X11); 0 for none.</summary>
    public abstract nint OwnerOf(nint window);

    /// <summary>A game's window held on a place of the screen in its pixels, shown, or hidden.</summary>
    public abstract void Hold(nint window, PixelRect place, bool shown);

    /// <summary>A game's window given the keys, brought in front of the host.</summary>
    public abstract void Focus(nint window);

    /// <summary>Whether a game's window has the keys.</summary>
    public abstract bool HasKeys(nint window);

    /// <summary>Where a game's window is on the screen, in its pixels; null for one gone.</summary>
    public abstract PixelRect? PlaceOf(nint window);

    public virtual void Dispose()
    {
    }

    /// <summary>A game's window over Passenger's on Windows: a popup owned by the host's window, its input queue its own.</summary>
    private sealed partial class Win32(nint host) : GameWindows(host)
    {
        private const string User32 = "user32.dll";
        private const uint Owner = 4;
        private const uint NoZOrder = 0x4;
        private const uint NoActivate = 0x10;
        private const uint AsyncPosition = 0x4000;
        private const int Hide = 0;
        private const int ShowUnactivated = 8;

        public override unsafe nint Find(int processId)
        {
            Seeking seeking = new(processId, Host);
            GCHandle held = GCHandle.Alloc(seeking);
            try
            {
                EnumWindows(&Each, GCHandle.ToIntPtr(held));
            }
            finally
            {
                held.Free();
            }
            return seeking.Found;
        }

        [UnmanagedCallersOnly]
        private static int Each(nint window, nint state)
        {
            Seeking seeking = (Seeking)GCHandle.FromIntPtr(state).Target!;
            if (GetWindowThreadProcessId(window, out uint process) != 0 && process == seeking.ProcessId && GetWindow(window, Owner) == seeking.Host)
            {
                seeking.Found = window;
                return 0;
            }
            return 1;
        }

        public override nint OwnerOf(nint window) => GetWindow(window, Owner);

        public override void Hold(nint window, PixelRect place, bool shown)
        {
            SetWindowPos(window, 0, place.X, place.Y, place.Width, place.Height, NoZOrder | NoActivate | AsyncPosition);
            if ((IsWindowVisible(window) != 0) != shown)
            {
                ShowWindow(window, shown ? ShowUnactivated : Hide);
            }
        }

        public override void Focus(nint window) => SetForegroundWindow(window);

        public override bool HasKeys(nint window) => GetForegroundWindow() == window;

        public override PixelRect? PlaceOf(nint window) =>
            GetWindowRect(window, out Rect at) != 0 ? new PixelRect(at.Left, at.Top, at.Right - at.Left, at.Bottom - at.Top) : null;

        private sealed class Seeking(int processId, nint host)
        {
            public int ProcessId { get; } = processId;

            public nint Host { get; } = host;

            public nint Found { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [LibraryImport(User32)]
        private static unsafe partial int EnumWindows(delegate* unmanaged<nint, nint, int> each, nint state);

        [LibraryImport(User32)]
        private static partial uint GetWindowThreadProcessId(nint window, out uint process);

        [LibraryImport(User32)]
        private static partial nint GetWindow(nint window, uint relation);

        [LibraryImport(User32)]
        private static partial int SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        [LibraryImport(User32)]
        private static partial int ShowWindow(nint window, int show);

        [LibraryImport(User32)]
        private static partial int IsWindowVisible(nint window);

        [LibraryImport(User32)]
        private static partial int SetForegroundWindow(nint window);

        [LibraryImport(User32)]
        private static partial nint GetForegroundWindow();

        [LibraryImport(User32)]
        private static partial int GetWindowRect(nint window, out Rect rect);
    }

    /// <summary>A game's window over Passenger's under X11, through a display connection of its own (a window's id is the
    /// server's, whoever asks): a top-level window transient for the host's, found by its _NET_WM_PID.</summary>
    private sealed unsafe class X11 : GameWindows
    {
        private const nint Cardinal = 6;
        private const int RevertToParent = 2;

        /// <summary>How deep under the root a game's window is looked for: a window manager's frame or two above it.</summary>
        private const int Deepest = 4;

        private readonly nint display;
        private readonly nint processAtom;

        private X11(nint host, nint display)
            : base(host)
        {
            this.display = display;
            processAtom = Xlib.InternAtom(display, "_NET_WM_PID", 0);
        }

        public static X11? Open(nint host) => Xlib.OpenDisplay(0) is var display and not 0 ? new X11(host, display) : null;

        public override nint Find(int processId) => Find(Xlib.DefaultRootWindow(display), processId, 0);

        private nint Find(nint window, int processId, int depth)
        {
            if (ProcessOf(window) == processId && OwnerOf(window) == Host)
            {
                return window;
            }
            if (depth >= Deepest || Xlib.QueryTree(display, window, out _, out _, out nint* children, out uint count) == 0)
            {
                return 0;
            }
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    if (Find(children[i], processId, depth + 1) is var found and not 0)
                    {
                        return found;
                    }
                }
                return 0;
            }
            finally
            {
                if (children != null)
                {
                    Xlib.Free((nint)children);
                }
            }
        }

        private long ProcessOf(nint window)
        {
            if (Xlib.GetWindowProperty(display, window, processAtom, 0, 1, 0, Cardinal, out _, out int format, out nuint items, out _, out nint data) != 0 || data == 0)
            {
                return -1;
            }
            try
            {
                return format == 32 && items == 1 ? *(long*)data : -1;
            }
            finally
            {
                Xlib.Free(data);
            }
        }

        public override nint OwnerOf(nint window) => Xlib.GetTransientForHint(display, window, out nint parent) != 0 ? parent : 0;

        public override void Hold(nint window, PixelRect place, bool shown)
        {
            if (shown)
            {
                Xlib.MoveResizeWindow(display, window, place.X, place.Y, (uint)Math.Max(1, place.Width), (uint)Math.Max(1, place.Height));
                Xlib.MapWindow(display, window);
            }
            else
            {
                Xlib.UnmapWindow(display, window);
            }
            Xlib.Flush(display);
        }

        public override void Focus(nint window)
        {
            Xlib.RaiseWindow(display, window);
            Xlib.SetInputFocus(display, window, RevertToParent, 0);
            Xlib.Flush(display);
        }

        public override bool HasKeys(nint window) => Xlib.GetInputFocus(display, out nint focused, out _) != 0 && focused == window;

        public override PixelRect? PlaceOf(nint window)
        {
            if (Xlib.GetGeometry(display, window, out nint root, out _, out _, out uint width, out uint height, out _, out _) == 0
                || Xlib.TranslateCoordinates(display, window, root, 0, 0, out int x, out int y, out _) == 0)
            {
                return null;
            }
            return new PixelRect(x, y, (int)width, (int)height);
        }

        public override void Dispose() => Xlib.CloseDisplay(display);
    }
}
