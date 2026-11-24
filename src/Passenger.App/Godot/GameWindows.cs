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
    public IReadOnlyList<string> Arguments => ArgumentsAt(null);

    /// <summary><see cref="Arguments"/>, and where the place the game's window is to be held on is known (in the screen's
    /// pixels), its position and size as Godot's own editor gives them, in Godot's screen space (from <see cref="Origin"/>):
    /// a game opens its window there, so the sizing Godot does as it starts keeps it on the place, not its own size.</summary>
    public IReadOnlyList<string> ArgumentsAt(PixelRect? place)
    {
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        List<string> arguments = ["--wid", Host.ToString(invariant)];
        if (this is X11)
        {
            arguments.AddRange(["--display-driver", "x11"]);
        }
        if (place is { Width: > 0, Height: > 0 } at)
        {
            PixelPoint origin = Origin;
            arguments.AddRange(["--position", $"{(at.X - origin.X).ToString(invariant)},{(at.Y - origin.Y).ToString(invariant)}",
                "--resolution", $"{at.Width.ToString(invariant)}x{at.Height.ToString(invariant)}"]);
        }
        return arguments;
    }

    /// <summary>Where Godot's screen space begins on this system's: the top left of all screens together, which Godot counts
    /// its windows' positions from.</summary>
    protected virtual PixelPoint Origin => default;

    /// <summary>What the last <see cref="Find"/> saw of the process's windows, for the log: each window's handle, owner,
    /// place and whether it showed; empty before a search.</summary>
    public string Seen { get; protected set; } = string.Empty;

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

    /// <summary>A game's window held on a place of the screen in its pixels, shown, or hidden, asked of it without waiting for
    /// its answer: a game a debugger stopped answers nothing.</summary>
    public abstract void Hold(nint window, PixelRect place, bool shown);

    /// <summary>A game's window let go of while every thread of its game is held by a debugger: on Windows owned by none, as
    /// each move of the host among the windows (its activation, coming in front) waits for every window it owns to answer,
    /// which a stopped game's never does, and the host put in front of it, so what the host shows over its place is seen.
    /// Nothing under X11, whose requests wait for no client.</summary>
    public virtual void Disown(nint window)
    {
    }

    /// <summary>What the screen shows on a place of it, in its pixels, read from the screen alone, never asked of the window
    /// there (a stopped game's answers nothing): the game's last picture as it stops. Null where the screen cannot be read so.</summary>
    public virtual GamePicture? Picture(PixelRect place) => null;

    /// <summary>A game's window let go of while its game was stopped (<see cref="Disown"/>) taken back once the game runs on:
    /// once its window answers, owned by the host again, its input kept apart from the host's, and in front of the host,
    /// where the host's moves left it meanwhile. It waits for the game, so never on the host's thread: false where the game
    /// never answered (stopped again, ended, the wait <paramref name="cancel"/>led).</summary>
    public virtual bool Return(nint window, CancellationToken cancel) => true;

    /// <summary>A game's window given the keys, brought in front of the host.</summary>
    public abstract void Focus(nint window);

    /// <summary>Whether a game's window has the keys.</summary>
    public abstract bool HasKeys(nint window);

    /// <summary>Where a game's window is on the screen, in its pixels; null for one gone.</summary>
    public abstract PixelRect? PlaceOf(nint window);

    public virtual void Dispose()
    {
    }

    /// <summary>A game's window over Passenger's on Windows: a popup owned by the host's window, its input queue its own. Every
    /// place it reads or sets is in the screen's own pixels, as Godot's are (<see cref="Physical"/>).</summary>
    private sealed partial class Win32(nint host) : GameWindows(host)
    {
        /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
        private const nint PerMonitorAware = -4;
        private const string User32 = "user32.dll";
        private const string Gdi32 = "gdi32.dll";
        private const uint Owner = 4;

        /// <summary>GW_HWNDPREV: the window just in front of one.</summary>
        private const uint InFront = 3;

        /// <summary>WM_NULL: a message a window answers by doing nothing, which tells whether its thread runs.</summary>
        private const uint Nothing = 0;

        /// <summary>SMTO_ABORTIFHUNG: no wait for a window Windows already counts as not responding.</summary>
        private const uint AbortIfHung = 0x2;

        /// <summary>SRCCOPY with CAPTUREBLT: the screen's pixels as shown, windows layered over them included.</summary>
        private const uint CopyFromScreen = 0x00CC0020 | 0x40000000;

        /// <summary>GWLP_HWNDPARENT: a top-level window's owner.</summary>
        private const int OwnerIndex = -8;

        /// <summary>GWL_EXSTYLE, and its WS_EX_TOPMOST: a window kept over all others.</summary>
        private const int ExtendedStyle = -20;
        private const nint Topmost = 0x8;

        /// <summary>ERROR_INVALID_WINDOW_HANDLE: the window is gone, its game with it.</summary>
        private const int InvalidWindow = 1400;
        private const uint NoSize = 0x1;
        private const uint NoMove = 0x2;
        private const uint NoZOrder = 0x4;
        private const uint NoActivate = 0x10;
        private const uint AsyncPosition = 0x4000;
        private const int Hide = 0;
        private const int ShowUnactivated = 8;

        private const int VirtualScreenX = 76;
        private const int VirtualScreenY = 77;

        protected override PixelPoint Origin
        {
            get
            {
                using Physical pixels = new();
                return new(GetSystemMetrics(VirtualScreenX), GetSystemMetrics(VirtualScreenY));
            }
        }

        public override unsafe nint Find(int processId)
        {
            using Physical pixels = new();
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
            Seen = seeking.Seen.Count == 0 ? $"no top-level window of process {processId}"
                : $"process {processId}'s top-level windows: {string.Join("; ", seeking.Seen)} (the host is 0x{Host:X})";
            return seeking.Found;
        }

        [UnmanagedCallersOnly]
        private static int Each(nint window, nint state)
        {
            Seeking seeking = (Seeking)GCHandle.FromIntPtr(state).Target!;
            if (GetWindowThreadProcessId(window, out uint process) == 0 || process != seeking.ProcessId)
            {
                return 1;
            }
            nint owner = GetWindow(window, Owner);
            string place = GetWindowRect(window, out Rect at) != 0 ? $"{at.Right - at.Left}x{at.Bottom - at.Top} at {at.Left},{at.Top}" : "no place";
            seeking.Seen.Add($"0x{window:X} owned by 0x{owner:X}, {place}{(IsWindowVisible(window) != 0 ? ", shown" : ", hidden")}");
            if (owner == seeking.Host)
            {
                seeking.Found = window;
                return 0;
            }
            return 1;
        }

        public override nint OwnerOf(nint window) => GetWindow(window, Owner);

        public override void Hold(nint window, PixelRect place, bool shown)
        {
            using Physical pixels = new();
            if (SetWindowPos(window, 0, place.X, place.Y, place.Width, place.Height, NoZOrder | NoActivate | AsyncPosition) == 0)
            {
                IdeLog.Write(IdeLog.Ide, $"Game window: Windows refused to hold 0x{window:X} at {place.Width}x{place.Height} at {place.X},{place.Y} (error {Marshal.GetLastPInvokeError()})");
            }
            if ((IsWindowVisible(window) != 0) != shown)
            {
                ShowWindowAsync(window, shown ? ShowUnactivated : Hide);
            }
        }

        public override void Disown(nint window)
        {
            SetOwner(window, 0);
            if (InFrontOf(Host, window) && SetWindowPos(Host, GetWindow(window, InFront), 0, 0, 0, 0, NoMove | NoSize | NoActivate) == 0)
            {
                IdeLog.Write(IdeLog.Ide, $"Game window: Windows refused to put 0x{Host:X} in front of 0x{window:X} (error {Marshal.GetLastPInvokeError()})");
            }
        }

        /// <summary>Whether <paramref name="window"/> is in front of <paramref name="behind"/> among the windows.</summary>
        private static bool InFrontOf(nint behind, nint window)
        {
            for (nint front = GetWindow(behind, InFront); front != 0; front = GetWindow(front, InFront))
            {
                if (front == window)
                {
                    return true;
                }
            }
            return false;
        }

        public override unsafe GamePicture? Picture(PixelRect place)
        {
            if (place.Width <= 0 || place.Height <= 0)
            {
                return null;
            }
            using Physical pixels = new();
            nint screen = GetDC(0);
            nint drawn = CreateCompatibleDC(screen);
            nint bitmap = CreateCompatibleBitmap(screen, place.Width, place.Height);
            nint was = SelectObject(drawn, bitmap);
            try
            {
                if (BitBlt(drawn, 0, 0, place.Width, place.Height, screen, place.X, place.Y, CopyFromScreen) == 0)
                {
                    return null;
                }
                SelectObject(drawn, was);
                byte[] bgra = new byte[place.Width * place.Height * 4];
                // A top-down 32-bit DIB: its rows as the screen's, from the top.
                BitmapInfo format = new() { Size = sizeof(BitmapInfo), Width = place.Width, Height = -place.Height, Planes = 1, BitCount = 32 };
                fixed (byte* into = bgra)
                {
                    return GetDIBits(drawn, bitmap, 0, (uint)place.Height, into, &format, 0) == place.Height ? new GamePicture(place.Width, place.Height, bgra) : null;
                }
            }
            finally
            {
                SelectObject(drawn, was);
                DeleteObject(bitmap);
                DeleteDC(drawn);
                ReleaseDC(0, screen);
            }
        }

        public override bool Return(nint window, CancellationToken cancel)
        {
            // Owned again only once it runs: one owned again while its thread was held held the host's thread as it ran on.
            while (SendMessageTimeoutW(window, Nothing, 0, 0, AbortIfHung, 250, out _) == 0)
            {
                if (IsWindow(window) == 0 || cancel.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)))
                {
                    return false;
                }
            }
            SetOwner(window, Host);
            // As Godot keeps them as it makes the window: a thread whose input is joined to a stopped one's waits on it.
            AttachThreadInput(GetWindowThreadProcessId(window, out _), GetWindowThreadProcessId(Host, out _), 0);
            // Just in front of the host: behind the window in front of it, or first of the windows kept under those on top.
            nint front = GetWindow(Host, InFront);
            nint after = front != 0 && (GetWindowLongPtrW(front, ExtendedStyle) & Topmost) == 0 ? front : 0;
            if (front != window && SetWindowPos(window, after, 0, 0, 0, 0, NoMove | NoSize | NoActivate) == 0 && Marshal.GetLastPInvokeError() != InvalidWindow)
            {
                IdeLog.Write(IdeLog.Ide, $"Game window: Windows refused to bring 0x{window:X} in front of 0x{Host:X} (error {Marshal.GetLastPInvokeError()})");
            }
            return true;
        }

        /// <summary>A game's window given an owner, or none (0), without waiting for its game to answer.</summary>
        private static void SetOwner(nint window, nint owner)
        {
            if (SetWindowLongPtrW(window, OwnerIndex, owner) == 0 && Marshal.GetLastPInvokeError() is var error and not (0 or InvalidWindow))
            {
                IdeLog.Write(IdeLog.Ide, $"Game window: Windows refused 0x{window:X} the owner 0x{owner:X} (error {error})");
            }
        }

        public override void Focus(nint window) => SetForegroundWindow(window);

        public override bool HasKeys(nint window) => GetForegroundWindow() == window;

        public override PixelRect? PlaceOf(nint window)
        {
            using Physical pixels = new();
            return GetWindowRect(window, out Rect at) != 0 ? new PixelRect(at.Left, at.Top, at.Right - at.Left, at.Bottom - at.Top) : null;
        }

        /// <summary>The calling thread's places in the screen's own pixels while it lasts, whatever the process's DPI awareness
        /// (a process Windows scales, as a test's is, reads and sets another's window in its scaled units), as before after.</summary>
        private readonly struct Physical : IDisposable
        {
            private readonly nint before;

            public Physical() => before = SetThreadDpiAwarenessContext(PerMonitorAware);

            public void Dispose()
            {
                if (before != 0)
                {
                    SetThreadDpiAwarenessContext(before);
                }
            }
        }

        private sealed class Seeking(int processId, nint host)
        {
            public int ProcessId { get; } = processId;

            public nint Host { get; } = host;

            public nint Found { get; set; }

            public List<string> Seen { get; } = [];
        }

        /// <summary>BITMAPINFOHEADER, with no colour table after it (a 32-bit DIB needs none).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPixelsPerMeter;
            public int YPixelsPerMeter;
            public int ColoursUsed;
            public int ColoursImportant;
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

        [LibraryImport(User32, SetLastError = true)]
        private static partial int SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

        [LibraryImport(User32)]
        private static partial int GetSystemMetrics(int index);

        [LibraryImport(User32)]
        private static partial nint SetThreadDpiAwarenessContext(nint context);

        [LibraryImport(User32)]
        private static partial int ShowWindowAsync(nint window, int show);

        [LibraryImport(User32)]
        private static partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

        [LibraryImport(User32)]
        private static partial int IsWindow(nint window);

        [LibraryImport(User32)]
        private static partial int AttachThreadInput(uint attach, uint to, int attached);

        [LibraryImport(User32)]
        private static partial nint GetDC(nint window);

        [LibraryImport(User32)]
        private static partial int ReleaseDC(nint window, nint context);

        [LibraryImport(Gdi32)]
        private static partial nint CreateCompatibleDC(nint context);

        [LibraryImport(Gdi32)]
        private static partial nint CreateCompatibleBitmap(nint context, int width, int height);

        [LibraryImport(Gdi32)]
        private static partial nint SelectObject(nint context, nint chosen);

        [LibraryImport(Gdi32)]
        private static partial int BitBlt(nint into, int x, int y, int width, int height, nint from, int fromX, int fromY, uint operation);

        [LibraryImport(Gdi32)]
        private static unsafe partial int GetDIBits(nint context, nint bitmap, uint first, uint lines, byte* bits, BitmapInfo* format, uint usage);

        [LibraryImport(Gdi32)]
        private static partial int DeleteObject(nint chosen);

        [LibraryImport(Gdi32)]
        private static partial int DeleteDC(nint context);

        [LibraryImport(User32, SetLastError = true)]
        private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

        [LibraryImport(User32)]
        private static partial nint GetWindowLongPtrW(nint window, int index);

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

        public override nint Find(int processId)
        {
            List<string> seen = [];
            nint found = Find(Xlib.DefaultRootWindow(display), processId, 0, seen);
            Seen = seen.Count == 0 ? $"no window of process {processId} (by _NET_WM_PID)"
                : $"process {processId}'s windows: {string.Join("; ", seen)} (the host is 0x{Host:X})";
            return found;
        }

        private nint Find(nint window, int processId, int depth, List<string> seen)
        {
            if (ProcessOf(window) == processId)
            {
                nint owner = OwnerOf(window);
                seen.Add($"0x{window:X} transient for 0x{owner:X}");
                if (owner == Host)
                {
                    return window;
                }
            }
            if (depth >= Deepest || Xlib.QueryTree(display, window, out _, out _, out nint* children, out uint count) == 0)
            {
                return 0;
            }
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    if (Find(children[i], processId, depth + 1, seen) is var found and not 0)
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

/// <summary>A picture of a place of the screen: its pixels, blue, green, red and a fourth byte, row after row from the top.</summary>
public sealed record GamePicture(int Width, int Height, byte[] Bgra);
