using Avalonia;
using Avalonia.Input;

namespace Passenger.App.Docking;

/// <summary>Where a drag would drop what it carries: the place to light up, in its overlay's coordinates, and what goes
/// there when there is a name to give; the overlay is the gesture's own unless another is named (a tab over another
/// window's editors lights up that window's).</summary>
public readonly record struct DropZone(Rect Place, string? Name = null, DropOverlay? Overlay = null);

/// <summary>Something pressed on to be dragged to a drop place (a tool window by its stripe button or header, an editor
/// tab): it is a drag, and no click, once the pointer has gone <see cref="Threshold"/> from the press; meanwhile the
/// overlay lights up the place under the pointer that <paramref name="placeAt"/> gives, and the release hands the point
/// to <paramref name="drop"/>. Points are in <paramref name="surface"/>'s coordinates, the overlay's own.</summary>
public sealed class DragGesture<T>(Visual surface, DropOverlay overlay, Func<T, Point, DropZone?> placeAt, Action<T, Point> drop)
{
    public const double Threshold = 6;

    private (T Item, Point Start, IInputElement? Handle, bool Dragging)? pressed;
    private DropOverlay? lit;

    /// <summary>A left press on what may be dragged. The handle given takes the pointer until the release; a button,
    /// which takes it itself and is released by losing it, gives none.</summary>
    public void Begin(T item, PointerPressedEventArgs e, IInputElement? handle = null)
    {
        if (!e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed)
        {
            return;
        }
        pressed = (item, e.GetPosition(surface), handle, false);
        if (handle is not null)
        {
            e.Pointer.Capture(handle);
        }
    }

    /// <summary>Moves a drag on; true once it has gone far enough to be one.</summary>
    public bool Continue(PointerEventArgs e)
    {
        if (pressed is not { } current)
        {
            return false;
        }
        Point at = e.GetPosition(surface);
        Vector moved = at - current.Start;
        if (!current.Dragging && moved.Length < Threshold)
        {
            return false;
        }
        if (!current.Dragging)
        {
            pressed = current with { Dragging = true };
            DraggingChanged?.Invoke(this, (current.Item, true));
        }
        if (placeAt(current.Item, at) is { } place)
        {
            DropOverlay on = place.Overlay ?? overlay;
            if (lit != on)
            {
                lit?.Hide();
            }
            on.Show(place.Place, place.Name);
            lit = on;
        }
        else
        {
            Unlight();
        }
        return true;
    }

    /// <summary>Ends a press: dragged, what it carried is dropped where the pointer is. True when it was a drag, which a
    /// button then takes as no click.</summary>
    public bool End(PointerEventArgs e)
    {
        if (pressed is not { } current)
        {
            return false;
        }
        pressed = null;
        Unlight();
        if (current.Dragging)
        {
            drop(current.Item, e.GetPosition(surface));
            DraggingChanged?.Invoke(this, (current.Item, false));
        }
        if (current.Handle is not null)
        {
            e.Pointer.Capture(null);
        }
        return current.Dragging;
    }

    /// <summary>Forgets a press whose release was never seen (it went outside what takes the gesture's events), dropping nothing.</summary>
    public void Cancel()
    {
        (T Item, Point Start, IInputElement? Handle, bool Dragging)? was = pressed;
        pressed = null;
        Unlight();
        if (was is { Dragging: true } dragged)
        {
            DraggingChanged?.Invoke(this, (dragged.Item, false));
        }
    }

    /// <summary>A press became a drag (true), or a drag ended, dropped where it was let go or forgotten (false), with
    /// what it carries.</summary>
    public event EventHandler<(T Item, bool Dragging)>? DraggingChanged;

    private void Unlight()
    {
        lit?.Hide();
        lit = null;
    }
}
