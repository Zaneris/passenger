using Avalonia;
using Avalonia.Controls;
using Passenger.App.State;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>Where the person's window stands, kept in the store for the next start (<see cref="WindowPlacementState"/>),
/// one place for every folder: a start's window shows before its folder opens, and keeps its place as it opens another.
/// Each window a person uses keeps its place as it changes and as it closes, so the last one closed is the place kept, which
/// a start's first window opens at. Its bounds while normal are taken only once a move, a resize or a change of state has
/// settled, so what a platform tells as it maximizes (a move before its state, an animation's frames) is never taken for
/// them. A window nobody sees reads and keeps none.</summary>
public partial class MainWindow
{
    /// <summary>How long the window stands still before its bounds are taken.</summary>
    private static readonly TimeSpan PlacementSettles = TimeSpan.FromMilliseconds(500);

    /// <summary>The window's bounds when it was last seen normal: its frame's top-left in the screen's pixels, its client
    /// size in its own units.</summary>
    private (PixelPoint At, Size Size)? normalBounds;

    /// <summary>Normal, maximized or full screen, as the window was last shown: never minimized.</summary>
    private WindowState shownState;

    /// <summary>Takes the window's place once it has settled; null while the window keeps none.</summary>
    private Debounce? placementSettled;

    /// <summary>The window, before it shows, where the person's window last stood: at its normal bounds, fitted to the screen
    /// most of it stands on (wholly on one, however the screens changed), maximized or full screen as it was, and never
    /// minimized; else at its default size, fitted to the primary screen and centred. From then on it keeps its place.</summary>
    public void OpenWherePlaced()
    {
        WindowPlacementState? kept = KeepsPlacement ? Store().Placement.Value : null;
        if (kept is { } place && place.KeepsPlace())
        {
            Width = place.Width;
            Height = place.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(place.X, place.Y);
            FitToScreen(keepPlace: true);
            WindowState = place.State;
        }
        else
        {
            // Its default size is a large screen's: on a smaller one it opens inside the working area, centred.
            FitToScreen(keepPlace: false);
        }
        KeepPlacement();
    }

    /// <summary>The window keeps where it stands, in the store its layout is kept in, as it changes and as it closes: but a
    /// window nobody sees, or one keeping no layout.</summary>
    public void KeepPlacement()
    {
        if (!KeepsPlacement || placementSettled is not null)
        {
            return;
        }
        normalBounds = (Position, new Size(double.IsFinite(Width) ? Width : ClientSize.Width, double.IsFinite(Height) ? Height : ClientSize.Height));
        shownState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        Debounce settled = new(PlacementSettles, () =>
        {
            TakePlacement();
            return Task.CompletedTask;
        });
        placementSettled = settled;
        PositionChanged += (_, _) => settled.Restart();
        Resized += (_, _) => settled.Restart();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == WindowStateProperty)
            {
                shownState = WindowState == WindowState.Minimized ? shownState : WindowState;
                settled.Restart();
            }
        };
        Closed += (_, _) => settled.Stop();
    }

    /// <summary>Whether the window reads and keeps its place: a person's, keeping its layout.</summary>
    private bool KeepsPlacement => keepsWorkspace && !Unseen;

    /// <summary>The window's place kept as it stands: its bounds now where it is normal, else those it had when last normal.</summary>
    private void TakePlacement()
    {
        if (placementSettled is null)
        {
            return;
        }
        placementSettled.Stop();
        if (IsVisible && WindowState == WindowState.Normal && ClientSize is { Width: > 0, Height: > 0 } size)
        {
            normalBounds = (Position, size);
        }
        if (normalBounds is { } bounds)
        {
            Store().Placement.Set(new WindowPlacementState
            {
                X = bounds.At.X,
                Y = bounds.At.Y,
                Width = bounds.Size.Width,
                Height = bounds.Size.Height,
                State = shownState,
            });
        }
    }
}
