using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Passenger.App.Docking;

/// <summary>A window of its own for something taken out of the IDE's (a tool window floating or windowed, editor tabs):
/// it opens where it last stood, fitted to a screen there is now, else over the IDE. The IDE closes it quietly when it
/// puts what it shows back; closed by the person, it says so.</summary>
internal sealed class FloatWindow : Window
{
    private bool closingQuietly;

    /// <summary>Asked as the person closes it (never as the IDE closes it quietly, nor as the application quits): false
    /// keeps it open, what it shows having had something it could not finish (a file it could not save).</summary>
    public Func<bool>? MayClose { get; init; }

    /// <summary>One that last stood at <paramref name="kept"/> (screen pixels for its place, its own units for its
    /// size), or that is new and opens <paramref name="size"/> large over <paramref name="ide"/>.</summary>
    public FloatWindow(Visual ide, PixelRect? kept, Size size)
    {
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Panel));
        if (kept is { } was && OnScreen(was) is { } at)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = at.Position;
            (Width, Height) = (at.Size.Width, at.Size.Height);
        }
        else
        {
            // Never placed, or placed on a screen no longer there (one unplugged): over the IDE.
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Screen? screen = (TopLevel.GetTopLevel(ide) is null ? null : Screens.ScreenFromVisual(ide)) ?? Screens.Primary;
            Size fitted = Fitted(kept is { } placed ? new Size(placed.Width, placed.Height) : size, screen);
            (Width, Height) = (fitted.Width, fitted.Height);
        }
        Closing += (_, e) =>
        {
            if (!closingQuietly && e.CloseReason != WindowCloseReason.ApplicationShutdown && MayClose?.Invoke() == false)
            {
                e.Cancel = true;
            }
        };
        Closed += (_, _) =>
        {
            if (!closingQuietly)
            {
                Dismissed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>The person or the system closed it, not the IDE.</summary>
    public event EventHandler? Dismissed;

    /// <summary>Where it stands: its position in screen pixels, its size in its own units.</summary>
    public PixelRect Placement => new(Position.X, Position.Y, (int)Bounds.Width, (int)Bounds.Height);

    public void CloseQuietly()
    {
        closingQuietly = true;
        Close();
    }

    /// <summary>Where a kept window opens again: moved and shrunk into the working area of the screen it stood on most;
    /// null when none of it stood on a screen there is now.</summary>
    private (PixelPoint Position, Size Size)? OnScreen(PixelRect kept)
    {
        Screen? on = null;
        long most = 0;
        foreach (Screen screen in Screens.All)
        {
            PixelRect shown = new PixelRect(kept.X, kept.Y, (int)(kept.Width * screen.Scaling), (int)(kept.Height * screen.Scaling)).Intersect(screen.WorkingArea);
            if ((long)shown.Width * shown.Height > most)
            {
                (on, most) = (screen, (long)shown.Width * shown.Height);
            }
        }
        if (on is null)
        {
            return null;
        }
        Size size = Fitted(new Size(kept.Width, kept.Height), on);
        PixelRect area = on.WorkingArea;
        int x = Math.Clamp(kept.X, area.X, Math.Max(area.X, area.Right - (int)Math.Ceiling(size.Width * on.Scaling)));
        int y = Math.Clamp(kept.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)Math.Ceiling(size.Height * on.Scaling)));
        return (new PixelPoint(x, y), size);
    }

    /// <summary>A size in a window's own units, no larger than a screen's working area.</summary>
    private static Size Fitted(Size size, Screen? screen) => screen is null ? size
        : new Size(Math.Min(size.Width, screen.WorkingArea.Width / screen.Scaling), Math.Min(size.Height, screen.WorkingArea.Height / screen.Scaling));
}
