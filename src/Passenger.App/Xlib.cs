using System.Runtime.InteropServices;

namespace Passenger.App;

/// <summary>X11's client library, declared once: a display connection of the caller's own, a window's hints, a message to
/// the window manager, and what holds another program's window over one of Passenger's (its tree, its properties, its
/// place and its focus). A system without libX11 throws DllNotFoundException at the first call.</summary>
internal static partial class Xlib
{
    private const string Library = "libX11.so.6";

    /// <summary>XClientMessageEvent inside XEvent's 24 longs, as a 64-bit Linux lays it out.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    public struct ClientMessage
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public nuint Serial;
        [FieldOffset(16)] public int SendEvent;
        [FieldOffset(24)] public nint Display;
        [FieldOffset(32)] public nint Window;
        [FieldOffset(40)] public nint MessageType;
        [FieldOffset(48)] public int Format;
        [FieldOffset(56)] public nint Data0;
        [FieldOffset(64)] public nint Data1;
        [FieldOffset(72)] public nint Data2;
        [FieldOffset(80)] public nint Data3;
        [FieldOffset(88)] public nint Data4;
    }

    [LibraryImport(Library, EntryPoint = "XOpenDisplay")]
    public static partial nint OpenDisplay(nint name);

    [LibraryImport(Library, EntryPoint = "XCloseDisplay")]
    public static partial int CloseDisplay(nint display);

    [LibraryImport(Library, EntryPoint = "XFlush")]
    public static partial int Flush(nint display);

    [LibraryImport(Library, EntryPoint = "XFree")]
    public static partial int Free(nint data);

    [LibraryImport(Library, EntryPoint = "XGetWMHints")]
    public static partial nint GetWMHints(nint display, nint window);

    [LibraryImport(Library, EntryPoint = "XSetWMHints")]
    public static partial int SetWMHints(nint display, nint window, nint hints);

    [LibraryImport(Library, EntryPoint = "XDefaultRootWindow")]
    public static partial nint DefaultRootWindow(nint display);

    [LibraryImport(Library, EntryPoint = "XInternAtom", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint InternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(Library, EntryPoint = "XSendEvent")]
    public static partial int SendEvent(nint display, nint window, int propagate, long mask, ref ClientMessage message);

    [LibraryImport(Library, EntryPoint = "XQueryTree")]
    public static unsafe partial int QueryTree(nint display, nint window, out nint root, out nint parent, out nint* children, out uint count);

    [LibraryImport(Library, EntryPoint = "XGetWindowProperty")]
    public static partial int GetWindowProperty(nint display, nint window, nint property, nint offset, nint length, int delete, nint type,
        out nint actualType, out int actualFormat, out nuint items, out nuint after, out nint data);

    [LibraryImport(Library, EntryPoint = "XGetTransientForHint")]
    public static partial int GetTransientForHint(nint display, nint window, out nint parent);

    [LibraryImport(Library, EntryPoint = "XMoveResizeWindow")]
    public static partial int MoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);

    [LibraryImport(Library, EntryPoint = "XMapWindow")]
    public static partial int MapWindow(nint display, nint window);

    [LibraryImport(Library, EntryPoint = "XUnmapWindow")]
    public static partial int UnmapWindow(nint display, nint window);

    [LibraryImport(Library, EntryPoint = "XRaiseWindow")]
    public static partial int RaiseWindow(nint display, nint window);

    [LibraryImport(Library, EntryPoint = "XSetInputFocus")]
    public static partial int SetInputFocus(nint display, nint window, int revertTo, nint time);

    [LibraryImport(Library, EntryPoint = "XGetInputFocus")]
    public static partial int GetInputFocus(nint display, out nint window, out int revertTo);

    [LibraryImport(Library, EntryPoint = "XGetGeometry")]
    public static partial int GetGeometry(nint display, nint drawable, out nint root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);

    [LibraryImport(Library, EntryPoint = "XTranslateCoordinates")]
    public static partial int TranslateCoordinates(nint display, nint from, nint to, int x, int y, out int toX, out int toY, out nint child);

    [LibraryImport(Library, EntryPoint = "XSync")]
    public static partial int Sync(nint display, int discard);
}
