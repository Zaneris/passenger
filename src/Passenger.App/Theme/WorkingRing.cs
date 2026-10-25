using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace Passenger.App;

/// <summary>A ring turning, which says work is under way: round a button's icon while its action works
/// (<see cref="ActionButtons"/>), and beside the status bar's words while any does. A three-quarter arc in the accent, one
/// turn a second, turned by the compositor, so the UI thread draws it once and does nothing per frame.</summary>
public sealed class WorkingRing : Control
{
    /// <summary>How broad the arc's stroke is.</summary>
    public const double Stroke = 2;

    public static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<WorkingRing, IBrush?>(nameof(Brush));

    static WorkingRing()
    {
        AffectsRender<WorkingRing>(BrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<WorkingRing>(false);
    }

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!IsSet(BrushProperty))
        {
            this.Bind(BrushProperty, this.GetResourceObservable(TokenKeys.Accent));
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);
        if (finalSize != turning)
        {
            Turn(finalSize);
        }
        return arranged;
    }

    /// <summary>The size the ring turns at, whose centre it turns about.</summary>
    private Size turning;

    /// <summary>Turns the ring about its centre, from its first arrange on; again at a new size, about the new centre.</summary>
    private void Turn(Size size)
    {
        if (ElementComposition.GetElementVisual(this) is not { } visual)
        {
            return;
        }
        turning = size;
        visual.CenterPoint = new Vector3D(size.Width / 2, size.Height / 2, 0);
        ScalarKeyFrameAnimation turn = visual.Compositor.CreateScalarKeyFrameAnimation();
        turn.InsertKeyFrame(0f, 0f);
        turn.InsertKeyFrame(1f, (float)(2 * Math.PI), new LinearEasing());
        turn.Duration = TimeSpan.FromSeconds(1);
        turn.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("RotationAngle", turn);
    }

    public override void Render(DrawingContext context)
    {
        if (Brush is not { } brush)
        {
            return;
        }
        double radius = (Math.Min(Bounds.Width, Bounds.Height) - Stroke) / 2 - 1;
        if (radius <= 0)
        {
            return;
        }
        Point centre = new(Bounds.Width / 2, Bounds.Height / 2);
        double end = -Math.PI / 2 + 1.5 * Math.PI;
        StreamGeometry arc = new();
        using (StreamGeometryContext figure = arc.Open())
        {
            figure.BeginFigure(new Point(centre.X, centre.Y - radius), false);
            figure.ArcTo(new Point(centre.X + radius * Math.Cos(end), centre.Y + radius * Math.Sin(end)), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            figure.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, Stroke, lineCap: PenLineCap.Round), arc);
    }
}
