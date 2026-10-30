using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Every window the IDE opens, fitted to the working area of a screen by one rule (<see cref="WindowFit"/>): no
/// larger than the area, down to its least size, and wholly on it. One shown over an owner (a dialog, a diff, a floating
/// tool window) fits its owner's screen as it shows, centred over the owner as it asked, kept where it was placed
/// otherwise; one shown with no owner (a project window, an editor window, a tool window in a window of its own) is
/// fitted by its opener before it shows (<see cref="FitToScreen"/>, <see cref="FitWithin"/>,
/// <see cref="FitOver"/>).</summary>
public abstract class IdeWindow : Window
{
    private bool fitted;

    /// <summary>Fits the window to the screen under its place when it keeps one, else the primary, before it is shown.</summary>
    public void FitToScreen(bool keepPlace)
    {
        Screen? screen = (keepPlace ? Screens.ScreenFromPoint(Position) : null) ?? Screens.Primary;
        if (screen is not null)
        {
            FitWithin(screen.WorkingArea, screen.Scaling, keepPlace);
        }
    }

    /// <summary>Fits the window inside a screen's working area (in its pixels, at its scaling) before it is shown: centred
    /// on the area, or where it keeps its place (a window cascaded past the area's edge, one placed on a larger screen)
    /// only moved back onto it.</summary>
    public void FitWithin(PixelRect area, double scaling, bool keepPlace) =>
        Fit(area, scaling, area, keepPlace ? Position : null, Asked());

    /// <summary>Fits the window to the screen of another of the IDE's windows, centred over it, before it is shown.</summary>
    public void FitOver(Window over)
    {
        if (ScreenOf(over) is { } screen)
        {
            Fit(screen.WorkingArea, screen.Scaling, FrameOf(over), null, Asked());
        }
    }

    /// <summary>An owner is given as the window shows, once its size is laid out (its content's, where it sizes to it) and
    /// before it is placed: it fits the owner's screen then, the first time it shows.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OwnerProperty && change.NewValue is Window owner && !fitted && ScreenOf(owner) is { } screen)
        {
            fitted = true;
            PixelPoint? at = WindowStartupLocation == WindowStartupLocation.Manual ? Position : null;
            PixelRect over = WindowStartupLocation == WindowStartupLocation.CenterOwner ? FrameOf(owner) : screen.WorkingArea;
            // Placed by hand on a screen other than its owner's, it fits the screen it was placed on.
            Screen on = at is { } place ? Screens.ScreenFromPoint(place) ?? screen : screen;
            Fit(on.WorkingArea, on.Scaling, over, at, ClientSize);
        }
    }

    /// <summary>The size the window asks for before it is shown: its Width and Height, else its least.</summary>
    private Size Asked() => new(double.IsNaN(Width) ? MinWidth : Width, double.IsNaN(Height) ? MinHeight : Height);

    private void Fit(PixelRect area, double scaling, PixelRect over, PixelPoint? at, Size wanted)
    {
        Size frame = Frame();
        WindowFit.Fitted fit = WindowFit.Within(Box(area), scaling, (wanted.Width, wanted.Height), (MinWidth, MinHeight),
            (frame.Width, frame.Height), Box(over), at is { } place ? (place.X, place.Y) : null);
        Size size = new(fit.Width, fit.Height);
        if (size != wanted)
        {
            // Shown, it is laid out: a side it sizes to its content keeps to the area as that content changes.
            if (IsVisible)
            {
                MaxWidth = SizeToContent.HasFlag(SizeToContent.Width) ? Math.Min(MaxWidth, fit.Width) : MaxWidth;
                MaxHeight = SizeToContent.HasFlag(SizeToContent.Height) ? Math.Min(MaxHeight, fit.Height) : MaxHeight;
                ClientSize = size;
            }
            Width = double.IsNaN(Width) && SizeToContent.HasFlag(SizeToContent.Width) ? Width : fit.Width;
            Height = double.IsNaN(Height) && SizeToContent.HasFlag(SizeToContent.Height) ? Height : fit.Height;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(fit.X, fit.Y);
    }

    /// <summary>The most a system's frame adds round a window's client area (its title bar and borders), kept free for it
    /// before the window is shown, when the frame's own size is not yet known.</summary>
    private static readonly Size SystemFrame = new(16, 40);

    /// <summary>What the window's frame adds round its client area: nothing where its content takes the title bar's
    /// place.</summary>
    private Size Frame() => ExtendClientAreaToDecorationsHint ? default : SystemFrame;

    private static Screen? ScreenOf(Window window) =>
        window.Screens.ScreenFromWindow(window) ?? window.Screens.ScreenFromPoint(window.Position) ?? window.Screens.Primary;

    /// <summary>A shown window's frame in its screen's pixels.</summary>
    private static PixelRect FrameOf(Window window) =>
        new(window.Position, PixelSize.FromSize(window.FrameSize ?? window.ClientSize, window.DesktopScaling));

    private static WindowFit.Box Box(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
