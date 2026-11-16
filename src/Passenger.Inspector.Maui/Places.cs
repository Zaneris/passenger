using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MauiElement = Microsoft.Maui.Controls.Element;

namespace Passenger.Inspector.Maui;

/// <summary>Where the elements of one of a MAUI app's windows are, in the window's units from its top left: as its platform
/// laid out the view MAUI made of each (on Android and UIKit, where that view is in its window), else from MAUI's own frames,
/// each in what holds it, added up to the window. Read on the app's UI thread, for one answer.</summary>
internal sealed class Places
{
    private readonly Window window;

    public Places(Window window)
    {
        this.window = window;
        Density = ((IWindow)window).RequestDisplayDensity();
        Client = ClientOf(window, Density);
    }

    /// <summary>The window's pixels a unit.</summary>
    public double Density { get; }

    /// <summary>The window's area, from its top left.</summary>
    public Box Client { get; }

    /// <summary>An element's box in the window; null for one MAUI made no view of (a shell's item), or whose view is in no
    /// window now (a page not shown).</summary>
    public Box? Of(MauiElement element)
    {
        if (element == window)
        {
            return Client;
        }
        if (element is not IView { Handler.PlatformView: { } platform } view)
        {
            return null;
        }
        if (AndroidViews.Here)
        {
            return AndroidViews.InWindow(platform) is { } at
                ? Placing.InUnits(at.X, at.Y, AndroidViews.Size(platform)?.Width ?? Pixels(view.Frame.Width), AndroidViews.Size(platform)?.Height ?? Pixels(view.Frame.Height), Density)
                : null;
        }
        if (AppleViews.Here)
        {
            return AppleViews.InWindow(platform);
        }
        return Summed(element, view);
    }

    private int Pixels(double units) => (int)Math.Round(units * Density);

    /// <summary>A view's box from MAUI's frames: its own in what holds it, and each holder's in its own, up to the window, less
    /// what a scroll viewer has scrolled.</summary>
    private Box Summed(MauiElement element, IView view)
    {
        double x = 0;
        double y = 0;
        for (MauiElement? at = element; at is not null && at != window; at = at.Parent)
        {
            if (at is IView placed)
            {
                x += placed.Frame.X;
                y += placed.Frame.Y;
            }
            if (at.Parent is ScrollView scroller)
            {
                x -= scroller.ScrollX;
                y -= scroller.ScrollY;
            }
        }
        return Words.Boxed(x, y, view.Frame.Width, view.Frame.Height);
    }

    /// <summary>The window's area: on UIKit its window's size in the points its views are placed in (on Mac Catalyst not the
    /// Mac's, which MAUI's window gives), else its own size where its platform says it, else its activity's on Android, else
    /// its page's.</summary>
    private static Box ClientOf(Window window, double density)
    {
        if (AppleViews.Size(window.Handler?.PlatformView) is { Width: > 0, Height: > 0 } shown)
        {
            return Words.Boxed(0, 0, shown.Width, shown.Height);
        }
        if (window.Width > 0 && window.Height > 0)
        {
            return Words.Boxed(0, 0, window.Width, window.Height);
        }
        if (AndroidViews.Size(AndroidViews.Decor(window.Handler?.PlatformView)) is { } decor)
        {
            return Placing.InUnits(0, 0, decor.Width, decor.Height, density);
        }
        return window.Page is IView { Frame: { Width: > 0, Height: > 0 } frame } ? Words.Boxed(0, 0, frame.Width, frame.Height) : Words.Boxed(0, 0, 0, 0);
    }
}
