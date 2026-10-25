using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Passenger.App.Docking;

/// <summary>The layer over a frame (the tool windows', the editors') that lights up where a drag would drop what it
/// carries: an accent outline over the place, naming what goes there when there is a name to give. It takes no pointer,
/// and stands above the hairline dividers, which stand above the panes.</summary>
public sealed class DropOverlay : Canvas
{
    private readonly Border zone = new() { BorderThickness = new Thickness(2), Classes = { "box" } };
    private readonly TextBlock label = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 8) };

    public DropOverlay()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        ZIndex = 2;
        zone.Child = label;
        zone.Bind(Border.BorderBrushProperty, zone.GetResourceObservable(TokenKeys.Accent));
        zone.Bind(Border.BackgroundProperty, zone.GetResourceObservable(TokenKeys.DropZone));
        Children.Add(zone);
    }

    /// <summary>Lights up a place, in the overlay's own coordinates, its outline drawn just inside it.</summary>
    public void Show(Rect place, string? name)
    {
        SetLeft(zone, place.X + 2);
        SetTop(zone, place.Y + 2);
        zone.Width = Math.Max(0, place.Width - 4);
        zone.Height = Math.Max(0, place.Height - 4);
        label.Text = name;
        label.IsVisible = name is not null;
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;
}
