using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace Passenger.App;

/// <summary>The theme's background picture over one area (<see cref="BackgroundPlaces"/>), drawn above what the area holds at
/// the theme's opacity (at most <see cref="ThemeFiles.MostOpacity"/>), so text and its ground take the picture alike and every
/// glyph keeps the rest of its contrast whatever the picture holds; its tint washed over the picture. Fitted to its own area,
/// or for the frame to the window, of which it draws its own share. It takes no pointer, and draws again only as the
/// picture, the variant or its place in the window changes, never as what it lies over does. Draws nothing without one.</summary>
public sealed class ThemeBackdrop : Control
{
    private Rect window;

    public ThemeBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public ThemeBackdrop(BackgroundPlaces place) : this() => Place = place;

    /// <summary>Where it draws.</summary>
    public BackgroundPlaces Place { get; init; }

    /// <summary>How many times it drew a picture, which nothing but the picture, the variant or its place changes.</summary>
    public long Draws { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeBackdrops.Changed += OnChanged;
        ActualThemeVariantChanged += OnChanged;
        if (Place == BackgroundPlaces.Frame)
        {
            EffectiveViewportChanged += OnViewport;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeBackdrops.Changed -= OnChanged;
        ActualThemeVariantChanged -= OnChanged;
        EffectiveViewportChanged -= OnViewport;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The window's area in this one's own coordinates, which moves as the frame's parts move in it.</summary>
    private void OnViewport(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (e.EffectiveViewport != window)
        {
            window = e.EffectiveViewport;
            if (ThemeBackdrops.Any)
            {
                InvalidateVisual();
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        if (ThemeBackdrops.Of(ActualThemeVariant) is not { } shown || !shown.Value.Places.HasFlag(Place) || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }
        Rect own = new(Bounds.Size);
        Rect area = Place == BackgroundPlaces.Frame && window.Width > 0 ? window : own;
        BackgroundValue value = shown.Value;
        Bitmap picture = shown.Picture;
        Size size = picture.Size;
        double scale = value.Fit switch
        {
            BackgroundFit.Cover => Math.Max(area.Width / size.Width, area.Height / size.Height),
            BackgroundFit.Contain => Math.Min(area.Width / size.Width, area.Height / size.Height),
            _ => 1,
        };
        Size drawn = size * scale;
        Rect placed = new(area.X + ((area.Width - drawn.Width) * value.AlignX), area.Y + ((area.Height - drawn.Height) * value.AlignY), drawn.Width, drawn.Height);
        Rect covered = value.Fit == BackgroundFit.Tile ? area : placed;
        // Each draw takes its own opacity: these leave the ground at 1 - Opacity, as one layer of the tinted picture would.
        double tint = value.Opacity * value.TintStrength;
        double seen = value.TintStrength >= 1 ? 0 : value.Opacity * (1 - value.TintStrength) / (1 - tint);
        Draws++;
        using (context.PushClip(own))
        {
            if (seen > 0)
            {
                using (context.PushOpacity(seen))
                {
                    if (value.Fit == BackgroundFit.Tile)
                    {
                        ImageBrush tiles = new(picture)
                        {
                            TileMode = TileMode.Tile,
                            Stretch = Stretch.None,
                            AlignmentX = AlignmentX.Left,
                            AlignmentY = AlignmentY.Top,
                            DestinationRect = new RelativeRect(placed, RelativeUnit.Absolute),
                        };
                        context.FillRectangle(tiles, area);
                    }
                    else
                    {
                        context.DrawImage(picture, new Rect(size), placed);
                    }
                }
            }
            if (tint > 0)
            {
                context.FillRectangle(new ImmutableSolidColorBrush(Color.FromUInt32(value.TintArgb), tint), covered.Intersect(area));
            }
        }
    }
}
