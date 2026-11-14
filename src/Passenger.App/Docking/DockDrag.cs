using Avalonia;

namespace Passenger.App.Docking;

/// <summary>Where a tool window dragged by its stripe button or its header drops: on a stripe, into the group under the
/// pointer at its place among the buttons; over the frame, into the place whose part of it the pointer is over, which
/// lights up as about the size the window will have there.</summary>
internal sealed class DockDrag
{
    private readonly DockHost host;
    private readonly Stripe left;
    private readonly Stripe right;

    public DockDrag(DockHost host, Stripe left, Stripe right)
    {
        this.host = host;
        this.left = left;
        this.right = right;
        Gesture = new DragGesture<ToolWindow>(host, Overlay, Zone, Drop);
    }

    /// <summary>The layer over the whole frame, stripes too, that lights the place up.</summary>
    public DropOverlay Overlay { get; } = new();

    public DragGesture<ToolWindow> Gesture { get; }

    /// <summary>The place a point drops a window on: a stripe's group under it, the bottom area over the frame's lower
    /// part, an open side area over that area, and over the editors their outer quarter on a side whose area is closed;
    /// none over the rest of the editors, which takes it as an editor tab.</summary>
    private ToolAnchor? Target(Point at)
    {
        double width = host.Bounds.Width;
        double height = host.Bounds.Height;
        double start = host.LeftStripeWidth;
        double end = width - host.RightStripeWidth;
        if (at.X < start)
        {
            return left.AnchorAt(host.TranslatePoint(at, left) ?? at);
        }
        if (at.X > end)
        {
            return right.AnchorAt(host.TranslatePoint(at, right) ?? at);
        }
        if (at.Y > height * 0.7)
        {
            return (at.X - start) / (end - start) < 0.5 ? ToolAnchor.BottomLeft : ToolAnchor.BottomRight;
        }
        bool upper = at.Y < height * 0.35;
        Rect editors = host.EditorBounds is { Width: > 0 } laid ? laid : new Rect(start, 0, end - start, height);
        bool leftOpen = editors.Left > start + 0.5;
        bool rightOpen = editors.Right < end - 0.5;
        double x = (at.X - editors.Left) / editors.Width;
        return x < (leftOpen ? 0 : 0.25) ? (upper ? ToolAnchor.LeftTop : ToolAnchor.LeftBottom)
            : x > (rightOpen ? 1 : 0.75) ? (upper ? ToolAnchor.RightTop : ToolAnchor.RightBottom)
            : null;
    }

    /// <summary>A drop on a stripe keeps the place among its buttons; anywhere else goes last.</summary>
    private int? StripeIndex(ToolWindow window, ToolAnchor anchor, Point at)
    {
        Stripe? stripe = at.X < host.LeftStripeWidth ? left : at.X > host.Bounds.Width - host.RightStripeWidth ? right : null;
        return stripe?.IndexAt(anchor, host.TranslatePoint(at, stripe) ?? at, window);
    }

    /// <summary>Where a window dragged from elsewhere (its editor tab) would dock at a point, lit in the frame's overlay.</summary>
    public DropZone? ZoneFor(ToolWindow window, Point at) => Zone(window, at) is { } zone ? zone with { Overlay = Overlay } : null;

    /// <summary>Whether a point is over the editors' middle, where a window dropped goes into an editor tab.</summary>
    private bool OverEditors(Point at) => host.EditorBounds is { Width: > 0 } editors && editors.Contains(at) && Target(at) is null;

    private DropZone? Zone(ToolWindow window, Point at)
    {
        if (OverEditors(at) && window.Mode != ToolMode.Editor)
        {
            return new DropZone(host.EditorBounds.Deflate(4), $"{window.Title}: Editor Tab");
        }
        if (Target(at) is not { } place)
        {
            return null;
        }
        double stripe = host.LeftStripeWidth;
        double width = host.Bounds.Width - stripe - host.RightStripeWidth;
        double height = host.Bounds.Height;
        // An open side lights up as wide as it is, a closed one about as wide as it would open.
        double leftSide = host.EditorBounds.Left > stripe + 0.5 ? host.LeftAreaWidth : Math.Max(160, Math.Min(width * 0.25, host.LeftAreaWidth));
        double rightSide = host.EditorBounds.Right < stripe + width - 0.5 ? host.RightAreaWidth : Math.Max(160, Math.Min(width * 0.25, host.RightAreaWidth));
        double bottom = Math.Max(120, Math.Min(height * 0.4, host.BottomAreaHeight));
        double upperHeight = height - bottom;
        Rect zone = place switch
        {
            ToolAnchor.LeftTop => new Rect(stripe, 0, leftSide, upperHeight / 2),
            ToolAnchor.LeftBottom => new Rect(stripe, upperHeight / 2, leftSide, upperHeight / 2),
            ToolAnchor.RightTop => new Rect(stripe + width - rightSide, 0, rightSide, upperHeight / 2),
            ToolAnchor.RightBottom => new Rect(stripe + width - rightSide, upperHeight / 2, rightSide, upperHeight / 2),
            ToolAnchor.BottomLeft => new Rect(stripe, upperHeight, width / 2, bottom),
            _ => new Rect(stripe + (width / 2), upperHeight, width / 2, bottom),
        };
        return new DropZone(zone, $"{window.Title}: {ToolWindow.Name(place)}");
    }

    /// <summary>Docks a window where it was let go: in the place under the point, among its buttons on a stripe, or in an
    /// editor tab, open, over the editors' middle.</summary>
    public void Drop(ToolWindow window, Point at)
    {
        if (OverEditors(at))
        {
            // Dropped among the editors, it opens there as a tab, the one it then stands as.
            host.SetMode(window, ToolMode.Editor);
            host.Show(window.Id);
            return;
        }
        if (Target(at) is { } anchor)
        {
            if (window.Mode == ToolMode.Editor)
            {
                host.SetMode(window, ToolMode.Docked);
            }
            host.Move(window, anchor, StripeIndex(window, anchor, at));
        }
    }
}
