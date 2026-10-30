using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Passenger.App.Docking;

/// <summary>A window of its own for something taken out of the IDE's (a tool window floating or windowed, editor tabs):
/// it opens where it last stood, fitted to a screen there is now, else over the IDE. The IDE closes it quietly when it
/// puts what it shows back, and it goes quietly with the window it floats over; closed by the person, it says so.</summary>
internal sealed class FloatWindow : IdeWindow
{
    private bool closingQuietly;

    /// <summary>Whether its last close was asked as the window it floats over closed, which, gone through, leaves what it
    /// shows as it was.</summary>
    private bool closingWithOwner;

    /// <summary>Whether its last close was the application's or the system's shutting down (the application menu's Quit, a
    /// logout), never the person's own.</summary>
    public bool ShutDown { get; private set; }

    /// <summary>Asked as the person closes it (never as the IDE closes it quietly, nor as the application quits): false
    /// keeps it open, what it shows having had something it could not finish (a file it could not save).</summary>
    public Func<bool>? MayClose { get; init; }

    /// <summary>One that last stood at <paramref name="kept"/> (screen pixels for its place, its own units for its
    /// size), or that is new and opens <paramref name="size"/> large over <paramref name="ide"/>.</summary>
    public FloatWindow(Visual ide, PixelRect? kept, Size size)
    {
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Panel));
        if (kept is { } was && StoodOn(was) is { } screen)
        {
            Position = was.Position;
            (Width, Height) = (was.Width, was.Height);
            FitWithin(screen.WorkingArea, screen.Scaling, keepPlace: true);
        }
        else
        {
            // Never placed, or placed on a screen no longer there (one unplugged): over the IDE.
            (Width, Height) = kept is { } placed ? (placed.Width, placed.Height) : (size.Width, size.Height);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            if (TopLevel.GetTopLevel(ide) is Window over)
            {
                FitOver(over);
            }
        }
        Closing += (_, e) =>
        {
            closingWithOwner = e.CloseReason == WindowCloseReason.OwnerWindowClosing;
            ShutDown = e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
            if (!closingQuietly && e.CloseReason != WindowCloseReason.ApplicationShutdown && MayClose?.Invoke() == false)
            {
                e.Cancel = true;
            }
        };
        Closed += (_, _) =>
        {
            if (!closingQuietly && !closingWithOwner)
            {
                Dismissed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>The person or the system closed it, not the IDE (<see cref="ShutDown"/> tells which).</summary>
    public event EventHandler? Dismissed;

    /// <summary>Where it stands: its position in screen pixels, its size in its own units.</summary>
    public PixelRect Placement => new(Position.X, Position.Y, (int)Bounds.Width, (int)Bounds.Height);

    public void CloseQuietly()
    {
        closingQuietly = true;
        Close();
    }

    /// <summary>The screen a kept window stood on most, where it opens again; null when none of it stood on a screen
    /// there is now.</summary>
    private Screen? StoodOn(PixelRect kept)
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
        return on;
    }
}
