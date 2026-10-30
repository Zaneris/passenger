using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Passenger.Inspector.Wpf;

/// <summary>Select Element: while it is on, the element under the pointer in any of the app's windows is outlined, and
/// a click chooses it (the app never sees that click) and ends it; Escape ends it choosing nothing. The element chosen
/// in Passenger is outlined the same way: an adorner of the inspector's own, in colours of its own, since Passenger's
/// theme tokens are not the app's.</summary>
internal sealed class Picker(Inspector inspector, Connection connection) : Picking<DependencyObject>(connection)
{
    private readonly List<(UIElement Top, MouseEventHandler Moved, MouseButtonEventHandler Pressed, MouseButtonEventHandler Released, KeyEventHandler Keys)> hooked = [];
    private Marker? outline;

    public bool IsOwn(DependencyObject element) => element == outline;

    public override void Choose(bool on)
    {
        foreach ((UIElement top, MouseEventHandler moved, MouseButtonEventHandler pressed, MouseButtonEventHandler released, KeyEventHandler keys) in hooked)
        {
            top.RemoveHandler(UIElement.PreviewMouseMoveEvent, moved);
            top.RemoveHandler(UIElement.PreviewMouseDownEvent, pressed);
            top.RemoveHandler(UIElement.PreviewMouseUpEvent, released);
            top.RemoveHandler(UIElement.PreviewKeyDownEvent, keys);
        }
        hooked.Clear();
        if (!on)
        {
            return;
        }
        foreach (Visual visual in inspector.TopLevels())
        {
            if (visual is not UIElement top)
            {
                continue;
            }
            MouseEventHandler moved = (_, e) => Outline(At(top, e));
            MouseButtonEventHandler pressed = (_, e) => Pick(top, e);
            MouseButtonEventHandler released = (_, e) => e.Handled = true;
            KeyEventHandler keys = (_, e) => Escape(e);
            top.AddHandler(UIElement.PreviewMouseMoveEvent, moved, handledEventsToo: true);
            top.AddHandler(UIElement.PreviewMouseDownEvent, pressed, handledEventsToo: true);
            top.AddHandler(UIElement.PreviewMouseUpEvent, released, handledEventsToo: true);
            top.AddHandler(UIElement.PreviewKeyDownEvent, keys, handledEventsToo: true);
            hooked.Add((top, moved, pressed, released, keys));
        }
    }

    public override void Outline(DependencyObject? element)
    {
        if (outline is { } old)
        {
            (VisualTreeHelper.GetParent(old) as AdornerLayer)?.Remove(old);
            outline = null;
        }
        // An adorner adorns a UI element: a visual that is none (a text block's run of drawing) is outlined by the one holding it.
        UIElement? adorned = null;
        for (DependencyObject? at = element; at is not null && adorned is null; at = Shown.Parent(at))
        {
            adorned = at as UIElement;
        }
        if (adorned is null || AdornerLayer.GetAdornerLayer(adorned) is not { } layer)
        {
            return;
        }
        outline = new Marker(adorned);
        layer.Add(outline);
    }

    /// <summary>The element a pointer event is over in a window: WPF's own hit test, the window itself left out.</summary>
    private DependencyObject? At(UIElement top, MouseEventArgs e)
    {
        DependencyObject? hit = Hit(top, e.GetPosition(top));
        return hit == top ? null : hit;
    }

    /// <summary>What a pointer at a point in a top level would be over: the topmost element WPF hit tests there that shows.</summary>
    public DependencyObject? Hit(UIElement top, Point at)
    {
        DependencyObject? found = null;
        VisualTreeHelper.HitTest(top, candidate => IsOwn(candidate) ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : candidate is UIElement { IsVisible: false } or UIElement { IsHitTestVisible: false } ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result =>
            {
                found = result.VisualHit;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(at));
        return found;
    }

    private void Pick(UIElement top, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Picked(At(top, e));
    }

    private void Escape(KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
        e.Handled = true;
        Picked(null);
    }

    /// <summary>The outline: a box over the element, drawn by the inspector, which hit testing passes through.</summary>
    private sealed class Marker : Adorner
    {
        private static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x3D, 0x8B, 0xFF)));
        private static readonly Pen Edge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x6F, 0xE8))), 1));

        public Marker(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext drawing) => drawing.DrawRectangle(Fill, Edge, new Rect(AdornedElement.RenderSize));

        private static T Frozen<T>(T made) where T : Freezable
        {
            made.Freeze();
            return made;
        }
    }
}
