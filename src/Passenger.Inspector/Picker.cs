using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;

namespace Passenger.Inspector;

/// <summary>Select Element: while it is on, the element under the pointer in any of the app's windows is outlined, and
/// a click chooses it (the app never sees that click) and ends it; Escape ends it choosing nothing. The element chosen
/// in Passenger is outlined the same way: an adorner of the inspector's own, in colours of its own, since Passenger's
/// theme tokens are not the app's.</summary>
internal sealed class Picker(Inspector inspector, Connection connection) : Picking<Visual>(connection)
{
    private static readonly IBrush Fill = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0x3D, 0x8B, 0xFF));
    private static readonly IBrush Edge = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x6F, 0xE8));

    private readonly List<(TopLevel Top, IDisposable Moved, IDisposable Pressed, IDisposable Released, IDisposable Keys)> hooked = [];
    private Border? outline;

    public bool IsOwn(Visual visual) => visual == outline;

    public override void Choose(bool on)
    {
        foreach ((TopLevel _, IDisposable moved, IDisposable pressed, IDisposable released, IDisposable keys) in hooked)
        {
            moved.Dispose();
            pressed.Dispose();
            released.Dispose();
            keys.Dispose();
        }
        hooked.Clear();
        if (!on)
        {
            return;
        }
        foreach (TopLevel top in inspector.TopLevels())
        {
            hooked.Add((top,
                top.AddDisposableHandler(InputElement.PointerMovedEvent, (_, e) => Hover(top, e), RoutingStrategies.Tunnel, handledEventsToo: true),
                top.AddDisposableHandler(InputElement.PointerPressedEvent, (_, e) => Pick(top, e), RoutingStrategies.Tunnel, handledEventsToo: true),
                top.AddDisposableHandler(InputElement.PointerReleasedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel, handledEventsToo: true),
                top.AddDisposableHandler(InputElement.KeyDownEvent, (_, e) => Escape(e), RoutingStrategies.Tunnel, handledEventsToo: true)));
        }
    }

    public override void Outline(Visual? element)
    {
        if (outline is { } old)
        {
            (old.GetVisualParent() as AdornerLayer)?.Children.Remove(old);
            outline = null;
        }
        if (element is null || AdornerLayer.GetAdornerLayer(element) is not { } layer)
        {
            return;
        }
        Border shown = new() { Background = Fill, BorderBrush = Edge, BorderThickness = new Thickness(1), IsHitTestVisible = false };
        AdornerLayer.SetAdornedElement(shown, element);
        outline = shown;
        layer.Children.Add(shown);
    }

    private Visual? At(TopLevel top, PointerEventArgs e) =>
        top.GetVisualAt(e.GetPosition(top), v => !IsOwn(v) && v.IsVisible) is { } hit && hit != top ? hit : null;

    private void Hover(TopLevel top, PointerEventArgs e)
    {
        Outline(At(top, e));
    }

    private void Pick(TopLevel top, PointerPressedEventArgs e)
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
}
