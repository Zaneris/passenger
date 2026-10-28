using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>A row's icon as one control, drawn pixel for pixel as a PathIcon of its size draws it: a list styles a row
/// it brings back into view again whole, and a PathIcon is five styled controls. Its geometry is stretched to the size
/// it measures at, rounded up, then fitted into the box, centred and clipped to the fit, as the PathIcon's Viewbox does.
/// Its size is a PathIcon's (16, or 10 for <see cref="Chevron"/>), its brush the theme's icon colour unless given, by
/// <see cref="Foreground"/> or a theme key (<see cref="ForegroundKey"/>), and its geometry <see cref="Data"/> or a key's.</summary>
public sealed class RowIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<RowIcon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<string?> DataKeyProperty = AvaloniaProperty.Register<RowIcon, string?>(nameof(DataKey));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<RowIcon, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<string?> ForegroundKeyProperty = AvaloniaProperty.Register<RowIcon, string?>(nameof(ForegroundKey));
    public static readonly StyledProperty<bool> TurnedProperty = AvaloniaProperty.Register<RowIcon, bool>(nameof(Turned));

    /// <summary>Each geometry as a PathIcon's path holds it stretched, made once for every icon drawing it so.</summary>
    private static readonly ConditionalWeakTable<Geometry, Dictionary<Matrix, Geometry>> Stretched = [];

    static RowIcon()
    {
        WidthProperty.OverrideDefaultValue<RowIcon>(16);
        HeightProperty.OverrideDefaultValue<RowIcon>(16);
        AffectsRender<RowIcon>(DataProperty, DataKeyProperty, ForegroundProperty, ForegroundKeyProperty, TurnedProperty);
    }

    public RowIcon()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>A tree row's chevron, 10 square as Shell.axaml's PathIcon.chevron, <see cref="Turned"/> down while its row
    /// is open.</summary>
    public static RowIcon Chevron() => new() { Width = 10, Height = 10, DataKey = IconKeys.ChevronRight };

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>The theme's geometry of a key, drawn when <see cref="Data"/> is null.</summary>
    public string? DataKey
    {
        get => GetValue(DataKeyProperty);
        set => SetValue(DataKeyProperty, value);
    }

    /// <summary>Whether it is drawn turned a quarter clockwise about its middle, as a PathIcon turned 90 degrees about its
    /// centre is.</summary>
    public bool Turned
    {
        get => GetValue(TurnedProperty);
        set => SetValue(TurnedProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The theme's brush of a key, used when <see cref="Foreground"/> is null; the icon colour's when neither is.</summary>
    public string? ForegroundKey
    {
        get => GetValue(ForegroundKeyProperty);
        set => SetValue(ForegroundKeyProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        // As hit as the PathIcon's Border, whose background is transparent.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        Geometry? data = Data ?? (DataKey is { } key ? Resource(key) as Geometry : null);
        IBrush? brush = Foreground ?? Resource(ForegroundKey ?? TokenKeys.Icon) as IBrush;
        if (data is null)
        {
            return;
        }
        Draw(context, data, brush, Bounds.Size, LayoutHelper.ApplyLayoutConstraints(this, Bounds.Size), Turned, LayoutHelper.GetLayoutScale(this));
    }

    /// <summary>Draws a geometry as a PathIcon arranged at <paramref name="bounds"/>, of size <paramref name="box"/>, draws
    /// it, turned a quarter about its middle as one under a RotateTransform of 90 about its centre is.</summary>
    internal static void Draw(DrawingContext context, Geometry data, IBrush? brush, Size bounds, Size box, bool turned, double scale)
    {
        Rect extent = data.Bounds;
        if (extent.Width <= 0 || extent.Height <= 0)
        {
            return;
        }
        // The path's measure (its geometry's size) rounded up by the path and again by the Viewbox's container.
        Size path = LayoutHelper.RoundLayoutSizeUp(LayoutHelper.RoundLayoutSizeUp(extent.Size, scale), scale);
        // The Viewbox: the icon's size, the path scaled to fit it and centred, its own bounds the fit.
        Size available = LayoutHelper.RoundLayoutSizeUp(bounds, scale);
        box = LayoutHelper.RoundLayoutSizeUp(box, scale);
        Vector fit = Stretch.Uniform.CalculateScaling(box, path, StretchDirection.Both);
        Size shown = new Size(path.Width * fit.X, path.Height * fit.Y).Constrain(box);
        Point at = LayoutHelper.RoundLayoutPoint(new Point((available.Width - shown.Width) / 2, (available.Height - shown.Height) / 2), scale);
        // The path arranged at its measure, rounded up, its geometry stretched to fill it.
        Size arranged = LayoutHelper.RoundLayoutSizeUp(LayoutHelper.RoundLayoutSizeUp(path, scale), scale);
        double stretch = Math.Min(arranged.Width / extent.Width, arranged.Height / extent.Height);
        Geometry drawn = StretchedOf(data, Matrix.CreateTranslation(-(Vector)extent.Position) * Matrix.CreateScale(stretch, stretch));
        Matrix turn = turned ? Turn(bounds) : Matrix.Identity;
        // The clip pushed before the turn, so it stands square to the device's pixels.
        using (context.PushClip(Whole(new Rect(shown).TransformToAABB(Matrix.CreateTranslation(at.X, at.Y) * turn), scale)))
        using (context.PushTransform(turn))
        using (context.PushTransform(Matrix.CreateTranslation(at.X, at.Y)))
        using (context.PushTransform(Matrix.CreateScale(fit.X, fit.Y)))
        {
            context.DrawGeometry(brush, null, drawn);
        }
    }

    /// <summary>A clip on the whole device pixels the compositor's clip to a visual's bounds takes, each edge on the
    /// nearest boundary (a middle up) in the device's single precision, since one pushed in drawing is antialiased; the
    /// control stands on whole pixels as laid out, so its place in the window moves no edge across a pixel's middle.</summary>
    private static Rect Whole(Rect clip, double scale)
    {
        double Edge(double at) => MathF.Floor((float)(at * scale) + 0.5f) / scale;
        return new Rect(new Point(Edge(clip.X), Edge(clip.Y)), new Point(Edge(clip.Right), Edge(clip.Bottom)));
    }

    /// <summary>A quarter turn clockwise about a box's middle, as a RotateTransform of 90 about a centre origin is.</summary>
    private static Matrix Turn(Size box)
    {
        Matrix origin = Matrix.CreateTranslation(box.Width / 2, box.Height / 2);
        return -origin * Matrix.CreateRotation(Matrix.ToRadians(90)) * origin;
    }

    /// <summary>A geometry as a Shape renders it under a transform: a copy carrying it after its own.</summary>
    private static Geometry StretchedOf(Geometry geometry, Matrix transform)
    {
        if (transform == Matrix.Identity)
        {
            return geometry;
        }
        Dictionary<Matrix, Geometry> made = Stretched.GetOrCreateValue(geometry);
        if (!made.TryGetValue(transform, out Geometry? drawn))
        {
            drawn = geometry.Clone();
            drawn.Transform = new MatrixTransform(drawn.Transform is { } own && own.Value != Matrix.Identity ? own.Value * transform : transform);
            made[transform] = drawn;
        }
        return drawn;
    }

    private object? Resource(string key) => this.TryFindResource(key, ActualThemeVariant, out object? value) ? value : null;
}
