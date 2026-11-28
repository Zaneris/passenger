using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Passenger.App;

/// <summary>A row of tabs wider than its room, scrolled across with no bar of its own: the mouse wheel turned up or down
/// over it moves it across as a sideways turn does, so tabs out of sight come into view without the list of them.</summary>
public static class TabStrips
{
    /// <summary>How far a notch of the wheel moves a strip, as far as it scrolls the editor's lines.</summary>
    private const double WheelStep = 50;

    /// <summary>The scroller of a row of tabs.</summary>
    public static ScrollViewer ScrollingAcross(Control tabs)
    {
        ScrollViewer strip = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = tabs };
        // Seen before the scroller's own handling, which moves a strip only by a sideways turn.
        strip.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => Turned(strip, e), RoutingStrategies.Tunnel);
        return strip;
    }

    private static void Turned(ScrollViewer strip, PointerWheelEventArgs e)
    {
        double room = strip.Extent.Width - strip.Viewport.Width;
        if (strip.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled || room <= 0)
        {
            return;
        }
        double turn = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        strip.Offset = strip.Offset.WithX(Math.Clamp(strip.Offset.X - turn * WheelStep, 0, room));
        e.Handled = true;
    }
}
