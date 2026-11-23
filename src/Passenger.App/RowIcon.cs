using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>An icon as one control, drawn pixel for pixel as Avalonia's own PathIcon (a Path in a Viewbox) of its size draws
/// it: a list styles a row it brings back into view again whole, and that PathIcon is five styled controls, so every
/// PathIcon is drawn by one of these (ControlThemes.axaml). Its geometry is stretched to the size it measures at, rounded
/// up, then fitted into the box, centred and clipped to the fit, as the PathIcon's Viewbox does.
/// Its size is one of two, never one of its own: <see cref="Size"/>, an icon beside words (a row's, a tab's), or
/// <see cref="GlyphSize"/>, a glyph (<see cref="Glyph"/>: a chevron, a tab's close). Its brush is the theme's icon colour
/// unless given, by <see cref="Foreground"/> or a theme key (<see cref="ForegroundKey"/>), its geometry <see cref="Data"/> or a key's.</summary>
public sealed class RowIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<RowIcon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<string?> DataKeyProperty = AvaloniaProperty.Register<RowIcon, string?>(nameof(DataKey));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<RowIcon, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<string?> ForegroundKeyProperty = AvaloniaProperty.Register<RowIcon, string?>(nameof(ForegroundKey));
    public static readonly StyledProperty<bool> TurnedProperty = AvaloniaProperty.Register<RowIcon, bool>(nameof(Turned));
    public static readonly StyledProperty<bool> PlainFolderProperty = AvaloniaProperty.Register<RowIcon, bool>(nameof(PlainFolder));

    /// <summary>Each geometry as a PathIcon's path holds it stretched, made once for every icon drawing it so.</summary>
    private static readonly ConditionalWeakTable<Geometry, Dictionary<Matrix, Geometry>> Stretched = [];

    /// <summary>An icon's size beside words: a row's in every list and tree, a tab's, as tall as the words' line is.</summary>
    public const double Size = 14;

    /// <summary>A glyph's size: a tree row's chevron, a tab's close and pin.</summary>
    public const double GlyphSize = 10;

    static RowIcon()
    {
        WidthProperty.OverrideDefaultValue<RowIcon>(Size);
        HeightProperty.OverrideDefaultValue<RowIcon>(Size);
        AffectsRender<RowIcon>(DataProperty, DataKeyProperty, ForegroundProperty, ForegroundKeyProperty, TurnedProperty, PlainFolderProperty);
    }

    public RowIcon()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>A glyph of a theme key's geometry, <see cref="GlyphSize"/> square: a tab's close or pin.</summary>
    public static RowIcon Glyph(string key) => new() { Width = GlyphSize, Height = GlyphSize, DataKey = key };

    /// <summary>A tree row's chevron, a glyph as Shell.axaml's PathIcon.chevron is, <see cref="Turned"/> down while its row
    /// is open; a tree's row holds it in its column (<see cref="RowExpander"/>).</summary>
    public static RowIcon Chevron() => Glyph(IconKeys.ChevronRight);

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

    /// <summary>Whether its icon is a plain folder's, drawn only while Settings shows plain folders' icons.</summary>
    public bool PlainFolder
    {
        get => GetValue(PlainFolderProperty);
        set => SetValue(PlainFolderProperty, value);
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
        if (data is null || (PlainFolder && !Explorer.FileIcons.ShowsPlainFolders(this)))
        {
            return;
        }
        Draw(context, data, brush, IconLook.Of(this, ActualThemeVariant), Bounds.Size, LayoutHelper.ApplyLayoutConstraints(this, Bounds.Size), Turned, LayoutHelper.GetLayoutScale(this));
    }

    /// <summary>Draws a geometry as a PathIcon arranged at <paramref name="bounds"/>, of size <paramref name="box"/>, draws
    /// it, turned a quarter about its middle as one under a RotateTransform of 90 about its centre is, its colours as the
    /// theme's look of its icons draws them, and an icon a theme gives as an SVG fitted to the same place: the one place
    /// every icon is drawn.</summary>
    internal static void Draw(DrawingContext context, Geometry data, IBrush? brush, IconLook look, Size bounds, Size box, bool turned, double scale)
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
        Matrix turn = turned ? Turn(bounds) : Matrix.Identity;
        // The clip pushed before the turn, so it stands square to the device's pixels.
        using DrawingContext.PushedState clipped = context.PushClip(Whole(new Rect(shown).TransformToAABB(Matrix.CreateTranslation(at.X, at.Y) * turn), scale));
        using DrawingContext.PushedState turning = context.PushTransform(turn);
        if (data is IconPicture picture)
        {
            picture.Draw(context, new Rect(at, shown), brush, look);
            return;
        }
        // The path arranged at its measure, rounded up, its geometry stretched to fill it.
        Size arranged = LayoutHelper.RoundLayoutSizeUp(LayoutHelper.RoundLayoutSizeUp(path, scale), scale);
        double stretch = Math.Min(arranged.Width / extent.Width, arranged.Height / extent.Height);
        Geometry drawn = StretchedOf(data, Matrix.CreateTranslation(-(Vector)extent.Position) * Matrix.CreateScale(stretch, stretch));
        using (context.PushTransform(Matrix.CreateTranslation(at.X, at.Y)))
        using (context.PushTransform(Matrix.CreateScale(fit.X, fit.Y)))
        {
            context.DrawGeometry(look.Apply(brush), null, drawn);
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
