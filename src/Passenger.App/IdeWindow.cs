using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Every window the IDE opens, fitted to the working area of a screen by one rule (<see cref="WindowFit"/>): no
/// larger than the area, down to its least size, and wholly on it. One shown over an owner (a dialog, a diff, a floating
/// tool window) fits its owner's screen as it shows, centred over the owner as it asked, kept where it was placed
/// otherwise; one shown with no owner (a project window, an editor window, a tool window in a window of its own) is
/// fitted by its opener before it shows (<see cref="FitToScreen"/>, <see cref="FitWithin"/>, <see cref="FitOver"/>).
/// Fitted before the platform reports its frame, it is fitted again with the frame the platform reports.</summary>
public abstract class IdeWindow : Window
{
    private bool fitted;

    /// <summary>Whether the platform has shown the window, so that its frame is the one the platform reports.</summary>
    private bool opened;

    /// <summary>The fit made with the frame guessed, made again once the window shows and again as the platform first
    /// reports its frame; null once made with the frame reported.</summary>
    private Fitting? following;

    /// <param name="asks">Whether it asks the person, taking their keys (a dialog, a diff): one an agent's action would open
    /// as it runs is refused, and one work it started opens later comes without the keys (<see cref="AgentAsks.Dialog"/>),
    /// a modal one refused as it shows. A tool window's own window asks nothing.</param>
    protected IdeWindow(bool asks = true)
    {
        if (asks && AgentAsks.Dialog())
        {
            ShowActivated = false;
        }
    }

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

    /// <summary>Once the window shows, the fit made with the frame guessed is made again with the frame the platform
    /// reports, or with none where it reports none yet.</summary>
    protected override void OnOpened(EventArgs e)
    {
        opened = true;
        if (following is { } fitting)
        {
            Fit(fitting, ClientSize);
            // X11's window manager gives the frame only once it has mapped the window: fitted again then.
            following = FrameSize is null ? fitting : null;
        }
        base.OnOpened(e);
    }

    /// <summary>The platform reports the frame of a window shown with none reported: the window is fitted again with
    /// it, unless it has been maximized or minimized since.</summary>
    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        if (opened && following is { } fitting && FrameSize is not null && WindowState == WindowState.Normal)
        {
            Fit(fitting, ClientSize);
        }
    }

    /// <summary>The size the window asks for before it is shown: its Width and Height, else its least.</summary>
    private Size Asked() => new(double.IsNaN(Width) ? MinWidth : Width, double.IsNaN(Height) ? MinHeight : Height);

    private void Fit(PixelRect area, double scaling, PixelRect over, PixelPoint? at, Size wanted) =>
        Fit(new Fitting(area, scaling, over, at), wanted);

    private void Fit(Fitting fitting, Size wanted)
    {
        following = opened ? null : fitting;
        (PixelRect area, double scaling, PixelRect over, PixelPoint? at) = fitting;
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

    /// <summary>What the window's frame adds round its client area: once shown, what the platform reports, nothing
    /// where it reports no frame; before, nothing where its content takes the title bar's place, else the system's
    /// most.</summary>
    private Size Frame()
    {
        if (!opened)
        {
            return ExtendClientAreaToDecorationsHint ? default : SystemFrame;
        }
        Size reported = FrameSize ?? ClientSize;
        return new Size(Math.Max(0, reported.Width - ClientSize.Width), Math.Max(0, reported.Height - ClientSize.Height));
    }

    private static Screen? ScreenOf(Window window) =>
        window.Screens.ScreenFromWindow(window) ?? window.Screens.ScreenFromPoint(window.Position) ?? window.Screens.Primary;

    /// <summary>A shown window's frame in its screen's pixels.</summary>
    private static PixelRect FrameOf(Window window) =>
        new(window.Position, PixelSize.FromSize(window.FrameSize ?? window.ClientSize, window.DesktopScaling));

    private static WindowFit.Box Box(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>What a fit is made within: a working area at its scaling, what the window is centred over, and the
    /// place it keeps, if any.</summary>
    private sealed record Fitting(PixelRect Area, double Scaling, PixelRect Over, PixelPoint? At);
}
