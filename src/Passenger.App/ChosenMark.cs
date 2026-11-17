using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>The mark beside what is chosen, the one way every list, tree, drop-down, tab strip, stripe and toolbar toggle
/// shows its choice: a bar in the chosen mark's colour (Passenger.ChosenMark) along its host's start, or under it in a strip
/// of tabs or on a toolbar, on the host's straight edge clear of its rounded corners, so the choice reads at 3:1 on the page
/// and on its ground whatever that ground does. It spans its host whole; Shell.axaml shows it while the host is chosen and
/// says which side it takes. A row, tab or toggle is given it by its template (<see cref="Rows"/>, <see cref="Choices"/>,
/// <see cref="Template"/>) or, laid out by hand as a Border, by <see cref="Hold"/>.</summary>
public sealed class ChosenMark : Control
{
    /// <summary>How broad the bar is.</summary>
    public const double Breadth = 3;

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<ChosenMark, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<Dock> SideProperty = AvaloniaProperty.Register<ChosenMark, Dock>(nameof(Side), Dock.Left);

    /// <summary>The host's corners, which the bar keeps clear of.</summary>
    public static readonly StyledProperty<CornerRadius> CornersProperty = AvaloniaProperty.Register<ChosenMark, CornerRadius>(nameof(Corners));

    static ChosenMark()
    {
        AffectsRender<ChosenMark>(FillProperty, SideProperty, CornersProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ChosenMark>(false);
        // A screen reader hears the choice from its host's own state, never from a mark.
        AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<ChosenMark>(AccessibilityView.Raw);
    }

    /// <summary>A list's or a tree's row: the theme's template, its one presenter (PART_ContentPresenter, which each state
    /// restyles) beside the mark.</summary>
    public static IControlTemplate Rows { get; } = Template(null, foreground: false);

    /// <summary>A drop-down's choice, a stripe's button, a strip's tab or a toolbar's toggle: as <see cref="Rows"/>, the
    /// presenter taking its host's foreground as the theme's does.</summary>
    public static IControlTemplate Choices { get; } = Template(null, foreground: true);

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Dock Side
    {
        get => GetValue(SideProperty);
        set => SetValue(SideProperty, value);
    }

    public CornerRadius Corners
    {
        get => GetValue(CornersProperty);
        set => SetValue(CornersProperty, value);
    }

    /// <summary>The bar, in the mark's own coordinates (its host's): along its side between the corners there.</summary>
    public Rect Bar
    {
        get
        {
            Size size = Bounds.Size;
            CornerRadius corners = Corners;
            Rect bar = Side switch
            {
                Dock.Top => new Rect(corners.TopLeft, 0, size.Width - corners.TopLeft - corners.TopRight, Breadth),
                Dock.Bottom => new Rect(corners.BottomLeft, size.Height - Breadth, size.Width - corners.BottomLeft - corners.BottomRight, Breadth),
                Dock.Right => new Rect(size.Width - Breadth, corners.TopRight, Breadth, size.Height - corners.TopRight - corners.BottomRight),
                _ => new Rect(0, corners.TopLeft, Breadth, size.Height - corners.TopLeft - corners.BottomLeft),
            };
            return bar.Width >= Breadth && bar.Height >= Breadth ? bar : default;
        }
    }

    /// <summary>A container's template with the mark beside its presenter: the presenter shows its host's content, or holds
    /// a row made once where <paramref name="row"/> makes one (ReusedRows); <paramref name="foreground"/> where the theme's
    /// presenter takes its host's foreground.</summary>
    public static IControlTemplate Template(Func<Control>? row, bool foreground) => new FuncControlTemplate<ContentControl>((_, _) =>
    {
        ContentPresenter presenter = new()
        {
            Name = "PART_ContentPresenter",
            [!ContentPresenter.BackgroundProperty] = new TemplateBinding(TemplatedControl.BackgroundProperty),
            [!ContentPresenter.BorderBrushProperty] = new TemplateBinding(TemplatedControl.BorderBrushProperty),
            [!ContentPresenter.BorderThicknessProperty] = new TemplateBinding(TemplatedControl.BorderThicknessProperty),
            [!ContentPresenter.CornerRadiusProperty] = new TemplateBinding(TemplatedControl.CornerRadiusProperty),
            [!ContentPresenter.PaddingProperty] = new TemplateBinding(TemplatedControl.PaddingProperty),
            [!ContentPresenter.VerticalContentAlignmentProperty] = new TemplateBinding(ContentControl.VerticalContentAlignmentProperty),
            [!ContentPresenter.HorizontalContentAlignmentProperty] = new TemplateBinding(ContentControl.HorizontalContentAlignmentProperty),
        };
        if (row is null)
        {
            presenter[!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty);
            presenter[!ContentPresenter.ContentTemplateProperty] = new TemplateBinding(ContentControl.ContentTemplateProperty);
        }
        else
        {
            presenter.Content = row();
        }
        if (foreground)
        {
            presenter[!ContentPresenter.ForegroundProperty] = new TemplateBinding(TemplatedControl.ForegroundProperty);
        }
        return new Panel { Children = { presenter, new ChosenMark { [!CornersProperty] = new TemplateBinding(TemplatedControl.CornerRadiusProperty) } } };
    });

    /// <summary>Gives a Border laid out by hand (an editor's tab, a table's row) its content beside the mark, which spans the
    /// Border whole, past its padding; Shell.axaml shows it while the Border is an open tab or a chosen row.</summary>
    public static void Hold(Border host, Control content)
    {
        ChosenMark mark = new() { [!CornersProperty] = host[!Border.CornerRadiusProperty] };
        mark.Bind(MarginProperty, host.GetObservable(Decorator.PaddingProperty, padding => new Thickness(-padding.Left, -padding.Top, -padding.Right, -padding.Bottom)));
        host.Child = new Panel { Children = { content, mark } };
    }

    protected override Size MeasureOverride(Size availableSize) => default;

    public override void Render(DrawingContext context)
    {
        if (Fill is { } fill && Bar is { Width: > 0 } bar)
        {
            context.DrawRectangle(fill, null, bar, Breadth / 2, Breadth / 2);
        }
    }
}
