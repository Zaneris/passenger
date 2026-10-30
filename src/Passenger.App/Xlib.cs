using System.Runtime.InteropServices;

namespace Passenger.App;

/// <summary>X11's client library, declared once: a display connection of the caller's own, a window's hints, and a
/// message to the window manager. A system without libX11 throws DllNotFoundException at the first call.</summary>
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
}
