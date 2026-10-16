using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Passenger.Tests;

/// <summary>Whole-window audit evidence: optional scale-two compositor captures and controls that extend past the frame.</summary>
public static class UiAudit
{
    /// <summary>The whole rectangle a control paints in another control's coordinates, including press transforms.</summary>
    public static Rect BoundsIn(Control child, Control parent) => new Rect(child.Bounds.Size)
        .TransformToAABB(child.TransformToVisual(parent) ?? throw new InvalidOperationException("The controls have no common visual ancestor"));

    public static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("PASSENGER_UI_AUDIT") is not { Length: > 0 } folder)
        {
            return;
        }
        Directory.CreateDirectory(folder);
        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window has drawn no frame");
        frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    public static IEnumerable<string> HorizontalOverflow(Window window, string surface, Control? viewport = null)
    {
        Control frame = viewport ?? window;
        foreach (Control control in frame.GetVisualDescendants().OfType<Control>().Where(c => c.TemplatedParent is null
            && c.IsEffectivelyVisible && c is Button or TextBox or ComboBox or NumericUpDown or TextBlock))
        {
            if (control.TranslatePoint(default, frame) is not { } at)
            {
                continue;
            }
            // A code preview or a list wider than its viewport deliberately offers horizontal scrolling.
            if (control.GetVisualAncestors().TakeWhile(v => v != frame).OfType<ScrollViewer>()
                .Any(s => s.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled && s.Extent.Width > s.Viewport.Width))
            {
                continue;
            }
            if (at.X < -0.5 || at.X + control.Bounds.Width > frame.Bounds.Width + 0.5)
            {
                yield return $"{surface}: {control.GetType().Name} '{control.Name ?? control.ToString()}' spans {at.X:0.0}..{at.X + control.Bounds.Width:0.0} in page viewport {frame.Bounds.Width:0}px";
            }
            if (control is TextBlock text && text.TextTrimming == TextTrimming.None
                && (text.TextLayout.Width > text.Bounds.Width + 1 || text.TextLayout.Height > text.Bounds.Height + 1))
            {
                yield return $"{surface}: label '{text.Text}' clips {text.TextLayout.Width:0.0}x{text.TextLayout.Height:0.0} into {text.Bounds.Size}";
            }
        }
    }
}
