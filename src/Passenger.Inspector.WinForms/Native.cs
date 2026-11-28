using System.Runtime.InteropServices;

namespace Passenger.Inspector.WinForms;

/// <summary>The Windows calls the inspector makes beside Windows Forms' own, and the messages it posts: a window's title, the
/// app's windows on its UI thread, input posted into the app's queue as Windows posts a device's, and the thread's own key
/// state for the modifiers that input holds.</summary>
internal static partial class Native
{
    public const int MouseMove = 0x0200;
    public const int LeftDown = 0x0201;
    public const int LeftUp = 0x0202;
    public const int LeftDouble = 0x0203;
    public const int RightDown = 0x0204;
    public const int RightUp = 0x0205;
    public const int MiddleDown = 0x0207;
    public const int MiddleUp = 0x0208;
    public const int Wheel = 0x020A;
    public const int WheelAcross = 0x020E;
    public const int KeyDown = 0x0100;
    public const int KeyUp = 0x0101;
    public const int Char = 0x0102;
    public const int SystemKeyDown = 0x0104;
    public const int SystemKeyUp = 0x0105;

    /// <summary>A mouse message's state of its buttons and modifiers.</summary>
    public const int LeftHeld = 0x0001;
    public const int RightHeld = 0x0002;
    public const int ShiftHeld = 0x0004;
    public const int ControlHeld = 0x0008;
    public const int MiddleHeld = 0x0010;

    public const byte ShiftKey = 0x10;
    public const byte ControlKey = 0x11;
    public const byte AltKey = 0x12;

    /// <summary>A key state's bit for a key held down.</summary>
    public const byte Down = 0x80;

    public const int ExTransparent = 0x00000020;
    public const int ExToolWindow = 0x00000080;
    public const int ExNoActivate = 0x08000000;

    /// <summary>The parent a message-only window is made under (HWND_MESSAGE).</summary>
    public const nint MessageOnly = -3;

    /// <summary>ChildWindowFromPointEx's skips: hidden and disabled windows, as a pointer passes them by.</summary>
    public const uint SkipHiddenDisabled = 0x0001 | 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetWindowText(nint window, char* text, int most);

    /// <summary>A window's title, as Windows has it.</summary>
    public static unsafe string TitleOf(nint window)
    {
        char* text = stackalloc char[512];
        int length = GetWindowText(window, text, 512);
        return new string(text, 0, Math.Max(0, length));
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    public static partial nint ChildWindowFromPointEx(nint parent, Point at, uint skip);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint window, ref Point at);

    /// <summary>A point on the screen in a window's client area.</summary>
    public static System.Drawing.Point ToClient(nint window, System.Drawing.Point screen)
    {
        Point at = new(screen.X, screen.Y);
        ScreenToClient(window, ref at);
        return new System.Drawing.Point(at.X, at.Y);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetKeyboardState(Span<byte> state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetKeyboardState(ReadOnlySpan<byte> state);

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyW(uint code, uint map);

    private delegate bool EachWindow(nint window, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumThreadWindows(int thread, nint each, nint state);

    [LibraryImport("kernel32.dll")]
    public static partial int GetCurrentThreadId();

    /// <summary>The top-level windows of the calling thread, in Windows' order (the topmost first).</summary>
    public static List<nint> ThreadWindows()
    {
        List<nint> windows = [];
        EachWindow each = (window, _) =>
        {
            windows.Add(window);
            return true;
        };
        nint callback = Marshal.GetFunctionPointerForDelegate(each);
        EnumThreadWindows(GetCurrentThreadId(), callback, 0);
        GC.KeepAlive(each);
        return windows;
    }

    /// <summary>A point as a mouse message's lParam carries it.</summary>
    public static nint Spot(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));

    /// <summary>A key message's lParam: once, its scan code, the previous state and the transition for an up.</summary>
    public static nint KeyWords(int key, bool up, bool alt)
    {
        uint scan = MapVirtualKeyW((uint)key, 0) & 0xFF;
        uint words = 1 | (scan << 16) | (alt ? 1u << 29 : 0) | (up ? (1u << 30) | (1u << 31) : 0);
        return unchecked((nint)(int)words);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }
}
