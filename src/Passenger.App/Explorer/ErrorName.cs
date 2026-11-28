using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Explorer;

/// <summary>A row's name, with the wave the editor draws under an error drawn under it in the error's colour while what the
/// row stands for has errors (<see cref="ErrorFiles"/>).</summary>
public sealed class ErrorName : TextBlock
{
    public static readonly StyledProperty<bool> HasErrorsProperty = AvaloniaProperty.Register<ErrorName, bool>(nameof(HasErrors));

    private static readonly StyledProperty<IBrush?> WaveBrushProperty = AvaloniaProperty.Register<ErrorName, IBrush?>("WaveBrush");

    /// <summary>The wave made for the width it was last drawn under, and the pen for the brush it was last drawn in.</summary>
    private (double Width, StreamGeometry Shape)? wave;
    private (IBrush Brush, Pen Pen)? pen;

    static ErrorName() => AffectsRender<ErrorName>(HasErrorsProperty, WaveBrushProperty);

    public ErrorName() => Bind(WaveBrushProperty, this.GetResourceObservable(TokenKeys.Error));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public bool HasErrors
    {
        get => GetValue(HasErrorsProperty);
        set => SetValue(HasErrorsProperty, value);
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        base.RenderTextLayout(context, origin);
        if (!HasErrors || GetValue(WaveBrushProperty) is not { } brush || TextLayout.Width <= 0)
        {
            return;
        }
        double width = TextLayout.Width;
        if (wave is not { } made || made.Width != width)
        {
            StreamGeometry shape = new();
            using (StreamGeometryContext path = shape.Open())
            {
                Underlines.Trace(path, Underline.Wave, width);
            }
            wave = made = (width, shape);
        }
        if (pen is not { } drawn || !ReferenceEquals(drawn.Brush, brush))
        {
            pen = drawn = (brush, new Pen(brush, 1));
        }
        using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y + TextLayout.Baseline + 3)))
        {
            context.DrawGeometry(null, drawn.Pen, made.Shape);
        }
    }
}
