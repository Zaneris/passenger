using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A tree row's chevron and icon as one control, drawn pixel for pixel as RowIcons in their places: the chevron
/// 10 square in the middle of an 18-wide column, turned down while the row is open, a press on that column being
/// <see cref="ChevronPressed"/>; then the icon, 16 square midway down, 6 before what follows.</summary>
public sealed class RowGlyphs : Control
{
    private const double Column = 18;
    private const double ChevronSize = 10;
    private const double IconSize = 16;
    private const double Gap = 6;

    public static readonly StyledProperty<bool> HasChevronProperty = AvaloniaProperty.Register<RowGlyphs, bool>(nameof(HasChevron));
    public static readonly StyledProperty<bool> TurnedProperty = AvaloniaProperty.Register<RowGlyphs, bool>(nameof(Turned));
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<RowGlyphs, Geometry?>(nameof(Icon));
    public static readonly StyledProperty<IBrush?> IconBrushProperty = AvaloniaProperty.Register<RowGlyphs, IBrush?>(nameof(IconBrush));

    static RowGlyphs()
    {
        WidthProperty.OverrideDefaultValue<RowGlyphs>(Column + IconSize + Gap);
        HeightProperty.OverrideDefaultValue<RowGlyphs>(ListRow.Height);
        AffectsRender<RowGlyphs>(HasChevronProperty, TurnedProperty, IconProperty, IconBrushProperty);
    }

    public RowGlyphs()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>A press on the chevron's column of a row that has one.</summary>
    public event EventHandler<PointerPressedEventArgs>? ChevronPressed;

    public bool HasChevron
    {
        get => GetValue(HasChevronProperty);
        set => SetValue(HasChevronProperty, value);
    }

    /// <summary>Whether the chevron is turned down, its row open.</summary>
    public bool Turned
    {
        get => GetValue(TurnedProperty);
        set => SetValue(TurnedProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The icon's brush; the theme's icon colour when none is given.</summary>
    public IBrush? IconBrush
    {
        get => GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HasChevron && e.GetPosition(this).X < Column)
        {
            ChevronPressed?.Invoke(this, e);
        }
    }

    public override void Render(DrawingContext context)
    {
        // As hit as the chevron's column and the icon were, both with a transparent ground.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        double scale = LayoutHelper.GetLayoutScale(this);
        IBrush? themed = Resource(TokenKeys.Icon) as IBrush;
        if (HasChevron && Resource(IconKeys.ChevronRight) is Geometry chevron)
        {
            Glyph(context, chevron, themed, new Point((Column - ChevronSize) / 2, (Bounds.Height - ChevronSize) / 2), ChevronSize, Turned, scale);
        }
        if (Icon is { } icon)
        {
            Glyph(context, icon, IconBrush ?? themed, new Point(Column, (Bounds.Height - IconSize) / 2), IconSize, turned: false, scale);
        }
    }

    /// <summary>A glyph of a size where layout would put a RowIcon of it, its place rounded to whole device pixels.</summary>
    private static void Glyph(DrawingContext context, Geometry geometry, IBrush? brush, Point place, double size, bool turned, double scale)
    {
        Point at = LayoutHelper.RoundLayoutPoint(place, scale);
        using (context.PushTransform(Matrix.CreateTranslation(at.X, at.Y)))
        {
            RowIcon.Draw(context, geometry, brush, new Size(size, size), new Size(size, size), turned, scale);
        }
    }

    private object? Resource(string key) => this.TryFindResource(key, ActualThemeVariant, out object? value) ? value : null;
}
