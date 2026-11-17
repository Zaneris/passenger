using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Passenger.Core;

namespace Passenger.Editor.UiAutomation;

/// <summary>The window of Passenger's own UI Automation finds a <see cref="UiaTextElement"/> by: a child of the window
/// holding its control, laid over the control and kept there through every layout, never drawn (wholly transparent),
/// passing every click through to the window beneath and the keys back to it; WM_GETOBJECT answered with the element.
/// Taken out of the window while its control is out of it, as UI Automation reads it only where it stands, and taken down
/// once its control is let go of (<see cref="LetGo"/>). UI thread only.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class UiaTextWindow
{
    private const uint ChildStyle = 0x40000000;
    private const uint ClipSiblingsStyle = 0x04000000;
    private const uint LayeredStyle = 0x00080000;
    private const uint TransparentStyle = 0x00000020;
    private const uint NoActivateStyle = 0x08000000;
    private const uint PopupStyle = 0x80000000;
    private const uint ToolWindowStyle = 0x00000080;
    private const uint WmDestroy = 0x0002;
    private const uint WmSetFocus = 0x0007;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmGetObject = 0x003D;
    private const uint WmWindowAtPoint = 0x0084;
    private const nint HitTransparent = -1;
    private const nint NoActivate = 3;
    private const uint AlphaLayered = 0x2;
    private const uint NoSize = 0x1;
    private const uint NoMove = 0x2;
    private const uint NoZOrder = 0x4;
    private const uint NoActivateWindow = 0x10;
    private const uint ShowWindow = 0x40;
    private const uint HideWindow = 0x80;
    private const uint NoOwnerZOrder = 0x200;

    /// <summary>Each window of Passenger's own by its handle, for the window procedure, until Windows takes it down; each
    /// holds its control only weakly.</summary>
    private static readonly Dictionary<nint, UiaTextWindow> windows = [];

    /// <summary>What takes each control's windows down, kept beside the control.</summary>
    private static readonly ConditionalWeakTable<Control, LetGo> lettingGo = [];

    private static ushort windowClass;

    /// <summary>The parent of every such window held out of sight: a window never shown, made once.</summary>
    private static nint aside;

    private readonly UiaTextElement element;

    /// <summary>The window it stands in, <see cref="aside"/>'s while its control stands in none.</summary>
    private nint parent;

    /// <summary>Where it stands in its parent, in its pixels; null while hidden.</summary>
    private PixelRect? placed;

    /// <summary>The window of Avalonia's it stands in, whose close takes it out first, as Windows takes down a window's
    /// children with it; held weakly, as a client keeping the element keeps this.</summary>
    private WeakReference<TopLevel>? holding;

    public UiaTextWindow(UiaTextElement element)
    {
        this.element = element;
        parent = Aside();
        Handle = Made(LayeredStyle | TransparentStyle | NoActivateStyle, ChildStyle | ClipSiblingsStyle, parent);
        SetLayeredWindowAttributes(Handle, 0, 0, AlphaLayered);
        windows[Handle] = this;
        Provider = Uia.Of<IRawElementProviderSimple>(element);
        Control owner = element.Owner;
        lettingGo.GetValue(owner, static kept => new LetGo(kept.Dispatcher)).Add(this);
        owner.LayoutUpdated += (_, _) => Follow();
        owner.AttachedToVisualTree += (_, _) => Follow();
        owner.DetachedFromVisualTree += (_, _) => Follow();
        Follow();
    }

    /// <summary>The window's handle; zero once Windows took it down.</summary>
    public nint Handle { get; private set; }

    /// <summary>The element as UI Automation calls it, a reference this window holds until it is taken down.</summary>
    public nint Provider { get; private set; }

    /// <summary>The handle of the window of Windows' own a control stands in, zero for none.</summary>
    public static nint HandleOf(Control control) => TopLevel.GetTopLevel(control) is { } top ? HandleOf(top) : 0;

    private static nint HandleOf(TopLevel top) => top.TryGetPlatformHandle() is { HandleDescriptor: "HWND", Handle: not 0 and var handle } ? handle : 0;

    /// <summary>Lays the window over its control as it stands now: in the window holding it, over its bounds, hidden while
    /// it is not shown; moved only where that changed.</summary>
    private void Follow()
    {
        if (Handle == 0)
        {
            return;
        }
        Control owner = element.Owner;
        TopLevel? top = TopLevel.GetTopLevel(owner);
        if (!ReferenceEquals(top, Holding))
        {
            Holding?.Closed -= TakeOut;
            holding = top is null ? null : new WeakReference<TopLevel>(top);
            top?.Closed += TakeOut;
            nint holder = top is null ? 0 : HandleOf(top);
            Place(null);
            parent = holder == 0 ? Aside() : holder;
            SetParent(Handle, parent);
        }
        Place(top is null || parent == aside || !owner.IsEffectivelyVisible ? null : Over(owner, top));
    }

    /// <summary>Where a control stands in its top level, in the top level's pixels: its bounds through every transform
    /// between them (the interface's scale among them), at least a pixel each way; null where it stands in another.</summary>
    internal static PixelRect? Over(Control owner, TopLevel top)
    {
        if (owner.TransformToVisual(top) is not { } toTop)
        {
            return null;
        }
        Rect over = new Rect(owner.Bounds.Size).TransformToAABB(toTop);
        double scale = top.RenderScaling;
        int left = (int)Math.Round(over.Left * scale);
        int upper = (int)Math.Round(over.Top * scale);
        return new PixelRect(left, upper, Math.Max(1, (int)Math.Round(over.Right * scale) - left), Math.Max(1, (int)Math.Round(over.Bottom * scale) - upper));
    }

    /// <summary>Gives the keys UI Automation gave this window (focusing its element) to the window beneath, and so to the
    /// control, the element's own.</summary>
    private void HandFocusBack()
    {
        SetFocus(parent);
        if (element.Holds(out Control? owner))
        {
            owner.Dispatcher.Post(() => owner.Focus());
        }
    }

    private TopLevel? Holding => holding is not null && holding.TryGetTarget(out TopLevel? top) ? top : null;

    /// <summary>Takes the window out of a window closing, held aside out of sight.</summary>
    private void TakeOut(object? sender, EventArgs e)
    {
        if (Handle == 0 || parent == aside)
        {
            return;
        }
        Place(null);
        parent = Aside();
        SetParent(Handle, parent);
    }

    private void Place(PixelRect? at)
    {
        if (at == placed)
        {
            return;
        }
        placed = at;
        if (at is { } rect)
        {
            SetWindowPos(Handle, 0, rect.X, rect.Y, rect.Width, rect.Height, NoZOrder | NoActivateWindow | NoOwnerZOrder | ShowWindow);
        }
        else
        {
            SetWindowPos(Handle, 0, 0, 0, 0, 0, NoMove | NoSize | NoZOrder | NoActivateWindow | NoOwnerZOrder | HideWindow);
        }
    }

    /// <summary>Takes the window down once its control is let go of, Windows' WM_DESTROY letting go of the element.</summary>
    private void TakeDown()
    {
        if (Handle != 0)
        {
            DestroyWindow(Handle);
        }
    }

    /// <summary>Lets go of the element, its window taken down by Windows, with the window it stood in or once its control
    /// is let go of.</summary>
    private void Destroyed()
    {
        Holding?.Closed -= TakeOut;
        holding = null;
        windows.Remove(Handle);
        Uia.UiaReturnRawElementProvider(Handle, 0, 0, 0);
        Uia.UiaDisconnectProvider(Provider);
        Marshal.Release(Provider);
        Provider = 0;
        Handle = 0;
        element.Unhost();
    }

    /// <summary>The window every such window is held in out of sight, made the first time one is.</summary>
    private static nint Aside() => aside != 0 ? aside : aside = Made(ToolWindowStyle | NoActivateStyle, PopupStyle, 0);

    /// <summary>A window of the class registered for these, of the styles given, in a parent; refused with no error code
    /// where Windows makes no layered child window (a process whose manifest names no Windows from 8).</summary>
    private static nint Made(uint extendedStyle, uint style, nint parent)
    {
        nint made = CreateWindowEx(extendedStyle, (char*)Register(), null, style, 0, 0, 0, 0, parent, 0, GetModuleHandle(null), 0);
        if (made != 0)
        {
            return made;
        }
        int error = Marshal.GetLastPInvokeError();
        throw error != 0 ? new Win32Exception(error) : new Win32Exception(error, "Windows made no window and gave no reason");
    }

    /// <summary>The window class every such window is made of, registered once.</summary>
    private static nint Register()
    {
        if (windowClass != 0)
        {
            return windowClass;
        }
        fixed (char* name = "PassengerAccessibleText")
        {
            WindowClass description = new()
            {
                Size = (uint)sizeof(WindowClass),
                Procedure = &WindowProcedure,
                Instance = GetModuleHandle(null),
                ClassName = name,
            };
            windowClass = RegisterClassEx(&description);
        }
        if (windowClass == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return windowClass;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmGetObject when (int)lParam == Uia.RootObjectId && windows.TryGetValue(handle, out UiaTextWindow? window) && window.Provider != 0:
                    return Uia.UiaReturnRawElementProvider(handle, wParam, lParam, window.Provider);
                case WmWindowAtPoint:
                    return HitTransparent;
                case WmMouseActivate:
                    return NoActivate;
                case WmEraseBackground:
                    return 1;
                case WmSetFocus when windows.TryGetValue(handle, out UiaTextWindow? focused):
                    focused.HandFocusBack();
                    return 0;
                case WmDestroy when windows.TryGetValue(handle, out UiaTextWindow? gone):
                    gone.Destroyed();
                    break;
            }
        }
        catch (Exception failed)
        {
            ReportLater(failed);
        }
        return DefWindowProc(handle, message, wParam, lParam);
    }

    /// <summary>Reports an error of the window procedure on the UI thread's next turn, as none may cross back into
    /// Windows.</summary>
    private static void ReportLater(Exception failed) => Dispatcher.UIThread.Post(() => PassengerLog.Report(failed));

    /// <summary>Takes a control's windows down on its UI thread once the control is let go of: kept beside the control
    /// alone (<see cref="lettingGo"/>), it is finalized only after the control is collected.</summary>
    private sealed class LetGo(Dispatcher dispatcher)
    {
        private readonly List<UiaTextWindow> made = [];

        public void Add(UiaTextWindow window) => made.Add(window);

        ~LetGo() => dispatcher.Post(() => made.ForEach(window => window.TakeDown()), DispatcherPriority.Background);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(WindowClass* description);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static partial nint CreateWindowEx(uint extendedStyle, char* className, char* windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint handle, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint handle);

    [LibraryImport("user32.dll", EntryPoint = "SetFocus")]
    private static partial nint SetFocus(nint handle);

    [LibraryImport("user32.dll", EntryPoint = "SetParent")]
    private static partial nint SetParent(nint child, nint parent);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SetLayeredWindowAttributes")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint handle, uint key, byte alpha, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);
}
