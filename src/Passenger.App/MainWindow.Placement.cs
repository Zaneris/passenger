using Avalonia;
using Avalonia.Controls;
using Passenger.App.State;

namespace Passenger.App;

public partial class MainWindow
{
    private (PixelPoint Position, Size Size) normalPlacement;
    private WindowState lastWindowState;
    private bool placementQueued;

    internal void RestoreWindowPlacement()
    {
        WindowPlacementState saved = Store().WindowPlacement.ValueBeforeShowing();
        bool hasBounds = saved.Width > 0 && saved.Height > 0;
        if (hasBounds)
        {
            Width = saved.Width;
            Height = saved.Height;
            Position = new PixelPoint(saved.X, saved.Y);
        }
        FitToScreen(keepPlace: hasBounds);
        if (saved.Mode != WindowState.Normal)
        {
            Opened += (_, _) => WindowState = saved.Mode;
        }
    }

    private void FollowWindowPlacement()
    {
        Opened += (_, _) => RememberNormalPlacement();
        PositionChanged += (_, _) => QueuePlacement();
        Resized += (_, _) => QueuePlacement();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == WindowStateProperty && WindowState != WindowState.Minimized)
            {
                lastWindowState = WindowState;
                QueuePlacement();
            }
        };
    }

    private void QueuePlacement()
    {
        if (placementQueued)
        {
            return;
        }
        placementQueued = true;
        // A platform may report a maximized resize before it reports the new state.
        UiThread.Post(() =>
        {
            placementQueued = false;
            RememberNormalPlacement();
        });
    }

    private void RememberNormalPlacement()
    {
        if (IsVisible && WindowState == WindowState.Normal && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            normalPlacement = (Position, ClientSize);
        }
    }

    private void KeepWindowPlacement()
    {
        RememberNormalPlacement();
        (PixelPoint position, Size size) = normalPlacement;
        if (size.Width > 0)
        {
            state!.WindowPlacement.Set(new WindowPlacementState
            {
                X = position.X,
                Y = position.Y,
                Width = size.Width,
                Height = size.Height,
                Mode = lastWindowState,
            });
        }
    }
}
