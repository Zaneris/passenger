using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The system asked for the person's attention while a window waits on them and may not have come forward, each
/// as its own desktop marks a window that wants it, since Avalonia 12 has no call for it: on macOS the Dock icon bounces
/// (Activate is refused while another app is in front, macOS 14's cooperative activation), on Windows the taskbar button
/// flashes, on Linux under X11 the window takes the urgency hint, each until the request is taken back. Wayland lets an
/// app ask for none, and the headless platform has nothing to mark. UI thread only (AppKit's main thread on macOS).</summary>
internal static partial class Attention
{
    private const string User32 = "user32.dll";

    /// <summary>NSRequestUserAttentionType's NSCriticalRequest: until the app is active, not one bounce.</summary>
    private const long Critical = 0;

    /// <summary>FlashWindowEx's FLASHW_ALL with FLASHW_TIMERNOFG: caption and taskbar button, until the window is in front.</summary>
    private const uint FlashUntilInFront = 0x3 | 0xC;

    private const uint FlashStop = 0;

    /// <summary>XWMHints' XUrgencyHint flag.</summary>
    private const long Urgency = 1L << 8;

    /// <summary>Asks for the person's attention for a window, taken back when the result is disposed.</summary>
    public static IDisposable Ask(Window window)
    {
        try
        {
            return window.TryGetPlatformHandle() switch
            {
                { HandleDescriptor: "NSWindow" } when OperatingSystem.IsMacOS() => Bounce(),
                { HandleDescriptor: "HWND" } handle when OperatingSystem.IsWindows() => Flash(handle.Handle),
                { HandleDescriptor: "XID" } handle when OperatingSystem.IsLinux() => Urge(handle.Handle),
                _ => Nothing.Instance,
            };
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return Nothing.Instance;
        }
    }

    private static IDisposable Bounce()
    {
        nint app = MacObjC.Send(MacObjC.Class("NSApplication"), MacObjC.Selector("sharedApplication"));
        if (app == 0)
        {
            return Nothing.Instance;
        }
        nint request = MacObjC.Send(app, MacObjC.Selector("requestUserAttention:"), (nint)Critical);
        return new Taken(() => MacObjC.Send(app, MacObjC.Selector("cancelUserAttentionRequest:"), request));
    }

    private static IDisposable Flash(nint window)
    {
        FlashWindowEx(new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = FlashUntilInFront });
        return new Taken(() => FlashWindowEx(new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = FlashStop }));
    }

    /// <summary>The window's urgency hint set through a display connection of its own (a window's id is the server's,
    /// whoever asks), and cleared again the same way.</summary>
    private static IDisposable Urge(nint window)
    {
        if (!SetUrgency(window, true))
        {
            return Nothing.Instance;
        }
        return new Taken(() => SetUrgency(window, false));
    }

    private static unsafe bool SetUrgency(nint window, bool urgent)
    {
        nint display = Xlib.OpenDisplay(0);
        if (display == 0)
        {
            return false;
        }
        try
        {
            // XWMHints begins with its flags, a C long; the rest is kept as the window has it.
            long* hints = (long*)Xlib.GetWMHints(display, window);
            if (hints is null)
            {
                return false;
            }
            *hints = urgent ? *hints | Urgency : *hints & ~Urgency;
            Xlib.SetWMHints(display, window, (nint)hints);
            Xlib.Free((nint)hints);
            Xlib.Flush(display);
            return true;
        }
        finally
        {
            Xlib.CloseDisplay(display);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport(User32, EntryPoint = "FlashWindowEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(in FlashInfo info);

    /// <summary>A request taken back once, when disposed.</summary>
    private sealed class Taken(Action takeBack) : IDisposable
    {
        private bool taken;

        public void Dispose()
        {
            if (!taken)
            {
                taken = true;
                takeBack();
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
