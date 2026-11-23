using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A tree row's chevron and icon as one control, drawn pixel for pixel as RowIcons in their places: the chevron
/// a glyph in the middle of a <see cref="RowExpander"/>'s column, turned down while the row is open, a press on that column
/// being <see cref="ChevronPressed"/>; then the icon, a row's icon's size midway down, a small step before what follows.</summary>
public sealed class RowGlyphs : Control
{
    private const double Column = RowExpander.Column;
    private const double ChevronSize = RowIcon.GlyphSize;
    private const double IconSize = RowIcon.Size;
    private const double Gap = Gaps.Small;

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
        DoubleTapped += (_, e) => e.Handled |= OnChevron(e.GetPosition(this));
    }

    /// <summary>A press on the chevron's column of a row that has one. A double press there is two such presses alone, never
    /// the list's double click, which acts on the row chosen.</summary>
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
        if (OnChevron(e.GetPosition(this)))
        {
            ChevronPressed?.Invoke(this, e);
        }
    }

    private bool OnChevron(Point at) => HasChevron && at.X < Column;

    public override void Render(DrawingContext context)
    {
        // As hit as the chevron's column and the icon were, both with a transparent ground.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        double scale = LayoutHelper.GetLayoutScale(this);
        IBrush? themed = Resource(TokenKeys.Icon) as IBrush;
        IconLook look = IconLook.Of(this, ActualThemeVariant);
        if (HasChevron && Resource(IconKeys.ChevronRight) is Geometry chevron)
        {
            Glyph(context, chevron, themed, look, new Point((Column - ChevronSize) / 2, (Bounds.Height - ChevronSize) / 2), ChevronSize, Turned, scale);
        }
        if (Icon is { } icon)
        {
            Glyph(context, icon, IconBrush ?? themed, look, new Point(Column, (Bounds.Height - IconSize) / 2), IconSize, turned: false, scale);
        }
    }

    /// <summary>A glyph of a size where layout would put a RowIcon of it, its place rounded to whole device pixels.</summary>
    private static void Glyph(DrawingContext context, Geometry geometry, IBrush? brush, IconLook look, Point place, double size, bool turned, double scale)
    {
        Point at = LayoutHelper.RoundLayoutPoint(place, scale);
        using (context.PushTransform(Matrix.CreateTranslation(at.X, at.Y)))
        {
            RowIcon.Draw(context, geometry, brush, look, new Size(size, size), new Size(size, size), turned, scale);
        }
    }

    private object? Resource(string key) => this.TryFindResource(key, ActualThemeVariant, out object? value) ? value : null;
}
