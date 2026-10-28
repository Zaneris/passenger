using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace Passenger.Editor;

/// <summary>The context switcher: beside the problem counts over the stripe, the context the file is analysed in when
/// it has several (a target framework of a multi-targeted project); a click asks the host to offer the others.</summary>
public sealed partial class CodeEditor
{
    private ContextSwitcher? switcher;
    private string? contextLabel;

    /// <summary>The switcher was clicked: the host offers the file's other contexts.</summary>
    public event EventHandler? ContextSwitchRequested;

    /// <summary>The context the file is analysed in, shown beside the counts; null to show none (a file in one only).</summary>
    public string? ContextLabel
    {
        get => contextLabel;
        set
        {
            if (contextLabel == value)
            {
                return;
            }
            contextLabel = value;
            if (switcher is null)
            {
                switcher = new ContextSwitcher(this);
                VisualChildren.Add(switcher);
                LogicalChildren.Add(switcher);
            }
            switcher.IsVisible = value is not null && showErrorStripe;
            switcher.InvalidateMeasure();
            switcher.InvalidateVisual();
            InvalidateArrange();
        }
    }

    /// <summary>Where the switcher is in the editor, for the host's menu to open under it; empty while it is hidden.</summary>
    public Rect ContextSwitcherBounds => switcher is { IsVisible: true } shown ? shown.Bounds : default;

    /// <summary>The switcher placed to the left of the counts, at their height.</summary>
    private void ArrangeContext(Rect counted)
    {
        if (switcher is null)
        {
            return;
        }
        switcher.IsVisible = contextLabel is not null && showErrorStripe;
        switcher.Measure(Size.Infinity);
        Size size = switcher.DesiredSize;
        switcher.Arrange(new Rect(counted.X - size.Width - 4, counted.Y, size.Width, counted.Height));
    }

    private sealed class ContextSwitcher : Control
    {
        private readonly CodeEditor editor;

        static ContextSwitcher()
        {
            AffectsMeasure<ContextSwitcher>(TextElement.FontFamilyProperty);
            AffectsRender<ContextSwitcher>(TextElement.FontFamilyProperty);
        }

        public ContextSwitcher(CodeEditor editor)
        {
            this.editor = editor;
            Cursor = HandCursor;
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(6 + Label(editor.contextLabel ?? string.Empty, Brushes.Transparent).Width + 4 + 8 + 6, 18);

        public override void Render(DrawingContext context)
        {
            Palette palette = editor.palette;
            context.FillRectangle(palette.Background, new Rect(Bounds.Size), 4);
            FormattedText label = Label(editor.contextLabel ?? string.Empty, palette.Text);
            context.DrawText(label, new Point(6, (Bounds.Height - label.Height) / 2));
            // The chevron that says a click offers more.
            double x = 6 + label.Width + 4;
            double y = Bounds.Height / 2 - 1.5;
            StreamGeometry chevron = new();
            using (StreamGeometryContext path = chevron.Open())
            {
                path.BeginFigure(new Point(x, y), false);
                path.LineTo(new Point(x + 4, y + 4));
                path.LineTo(new Point(x + 8, y));
                path.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(palette.Text, 1.2), chevron);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            editor.ContextSwitchRequested?.Invoke(editor, EventArgs.Empty);
            e.Handled = true;
        }

        /// <summary>The context's name in the window's own font, as the counts' are.</summary>
        private FormattedText Label(string text, IBrush brush) =>
            new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this)), 11.5, brush);
    }
}
