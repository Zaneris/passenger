using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Passenger.App.Godot;

/// <summary>A Godot game drawn in the Game window on macOS, as Godot's editor shows one it embeds: the game, started on its
/// embedded display, shares the layer it draws to across processes by a context's id (<c>game_view:set_context_id</c>), and
/// a CALayerHost (QuartzCore's, which Godot's editor and Chromium use) given that id shows it, a sublayer of an NSView of
/// Passenger's own hosted here, its top left at the view's. The view takes no input of its own: AppKit hands what it is
/// sent on to Passenger's window, so the pane hears the pointer and keys and sends them on. Of the UI thread, AppKit's.</summary>
public sealed partial class GameLayer : NativeControlHost
{
    private nint view;
    private nint host;
    private uint context;
    private double scale = 1;

    /// <summary>Whether this macOS has the CALayerHost a game's layer is shown through: a class of QuartzCore's no SDK names.</summary>
    public static bool Hosts => OperatingSystem.IsMacOS() && MacObjC.Class("CALayerHost") != 0;

    /// <summary>Shows the layer a game's context of an id shares, at the screens' greatest scale, from now; 0 shows none.</summary>
    public void Show(uint shared, double contentsScale)
    {
        UiThread.Check();
        context = shared;
        scale = contentsScale;
        Attach();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        view = MacObjC.Send(MacObjC.Send(MacObjC.Class("NSView"), MacObjC.Selector("alloc")), MacObjC.Selector("initWithFrame:"), default(MacObjC.CGRect));
        MacObjC.Send(view, MacObjC.Selector("setWantsLayer:"), true);
        Attach();
        return new PlatformHandle(view, "NSView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Detach();
        if (view != 0)
        {
            MacObjC.Send(view, MacObjC.Selector("release"));
            view = 0;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);
        Place(arranged.Height);
        return arranged;
    }

    /// <summary>The host made over the view's layer for the context told, once both are there; one for another context let go.</summary>
    private void Attach()
    {
        if (view == 0 || context == 0)
        {
            Detach();
            return;
        }
        nint layer = MacObjC.Send(view, MacObjC.Selector("layer"));
        nint hostClass = MacObjC.Class("CALayerHost");
        if (layer == 0 || hostClass == 0)
        {
            return;
        }
        if (host != 0 && (uint)MacObjC.Send(host, MacObjC.Selector("contextId")) != context)
        {
            Detach();
        }
        if (host == 0)
        {
            host = MacObjC.Send(MacObjC.Send(hostClass, MacObjC.Selector("alloc")), MacObjC.Selector("init"));
            MacObjC.Send(host, MacObjC.Selector("setContextId:"), context);
            MacObjC.Send(layer, MacObjC.Selector("addSublayer:"), host);
        }
        MacObjC.Send(host, MacObjC.Selector("setContentsScale:"), scale);
        Place(Bounds.Height);
    }

    /// <summary>The host's place: the view's top left, in the layer's points from its bottom, where the game's layer hangs from.</summary>
    private void Place(double height)
    {
        if (host == 0)
        {
            return;
        }
        nint transaction = MacObjC.Class("CATransaction");
        MacObjC.Send(transaction, MacObjC.Selector("begin"));
        MacObjC.Send(transaction, MacObjC.Selector("setDisableActions:"), true);
        MacObjC.Send(host, MacObjC.Selector("setPosition:"), new MacObjC.CGPoint(0, height));
        MacObjC.Send(transaction, MacObjC.Selector("commit"));
    }

    /// <summary>The display the view's window is on, as CoreGraphics numbers it (NSScreen's NSScreenNumber); the main
    /// display's while the view is in no window.</summary>
    public uint DisplayId()
    {
        nint window = view == 0 ? 0 : MacObjC.Send(view, MacObjC.Selector("window"));
        nint screen = window == 0 ? 0 : MacObjC.Send(window, MacObjC.Selector("screen"));
        if (screen == 0)
        {
            return MainDisplay();
        }
        nint described = MacObjC.Send(screen, MacObjC.Selector("deviceDescription"));
        nint key = MacObjC.Send(MacObjC.Class("NSString"), MacObjC.Selector("stringWithUTF8String:"), "NSScreenNumber");
        nint number = MacObjC.Send(described, MacObjC.Selector("objectForKey:"), key);
        return number == 0 ? MainDisplay() : (uint)MacObjC.Send(number, MacObjC.Selector("unsignedIntValue"));
    }

    /// <summary>The pointer put at a place of the screen, in its pixels at a window's scale, as a captured pointer is held:
    /// moved with no event of its own, and the pointer let move again at once, as a move made so holds it still a moment.</summary>
    public static void Warp(PixelPoint at, double windowScale)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        WarpPointer(new MacObjC.CGPoint(at.X / windowScale, at.Y / windowScale));
        AssociatePointer(true);
    }

    private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [LibraryImport(Graphics, EntryPoint = "CGMainDisplayID")]
    private static partial uint MainDisplay();

    [LibraryImport(Graphics, EntryPoint = "CGWarpMouseCursorPosition")]
    private static partial int WarpPointer(MacObjC.CGPoint at);

    [LibraryImport(Graphics, EntryPoint = "CGAssociateMouseAndMouseCursorPosition")]
    private static partial int AssociatePointer([MarshalAs(UnmanagedType.U1)] bool connected);

    private void Detach()
    {
        if (host == 0)
        {
            return;
        }
        MacObjC.Send(host, MacObjC.Selector("removeFromSuperlayer"));
        MacObjC.Send(host, MacObjC.Selector("release"));
        host = 0;
    }
}
