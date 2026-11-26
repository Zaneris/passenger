using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>Words to read and copy (code, a Markdown page's, a value in Details), laid out a rounding error wider than
/// the width they measured where they are given that width: the text's formatter sums a line's advances with an error of
/// its own, so given its own measured width (as a box sized to them gives it wherever that width falls on a pixel at the
/// scale in force, code's at 1.25 and 2) it wraps the line's last word to a line there is no height for, cutting the words
/// short.</summary>
public class SelectableWords : SelectableTextBlock
{
    /// <summary>A millionth of a unit: far above a line's error in summing its advances, far below a pixel at any scale.</summary>
    private const double RoundingError = 1e-6;

    /// <summary>The width the words last measured, before the layout rounded it.</summary>
    private double measured;

    private static Cursor? hand;
    private Link? pressedLink;
    private Point? linkPointer;

    internal readonly record struct Link(int Start, int End, string Text, string Target);

    internal Action<string, string>? OpenLink { get; init; }

    internal IReadOnlyList<Link>? InlineLinks { get; set; }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = base.MeasureOverride(availableSize);
        measured = size.Width;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize) =>
        base.ArrangeOverride(Math.Abs(finalSize.Width - measured) < RoundingError ? finalSize.WithWidth(measured + RoundingError) : finalSize);

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        base.RenderTextLayout(context, origin);
        if (linkPointer is not { } point || LinkAt(point) is not { } link || Foreground is not { } ink)
        {
            return;
        }
        // Draw over the current layout so pointer movement never reformats the signature or its selection.
        Pen pen = new(ink);
        foreach (Rect run in TextLayout.HitTestTextRange(link.Start, link.End - link.Start))
        {
            double y = origin.Y + run.Bottom - 1;
            context.DrawLine(pen, new Point(origin.X + run.Left, y), new Point(origin.X + run.Right, y));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        pressedLink = e.ClickCount == 1 && e.KeyModifiers == KeyModifiers.None && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            ? LinkAt(InText(e)) : null;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (InlineLinks is not { Count: > 0 })
        {
            return;
        }
        Point point = InText(e);
        Link? link = LinkAt(point);
        if (pressedLink is not null && (pressedLink != link || SelectionStart != SelectionEnd))
        {
            pressedLink = null;
        }
        HoverLink(link is not null && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? point : null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        Link? link = pressedLink;
        pressedLink = null;
        Point point = InText(e);
        Link? underPointer = LinkAt(point);
        bool open = link is not null && e.Pointer.Captured == this && e.InitialPressMouseButton == MouseButton.Left
            && SelectionStart == SelectionEnd && link == underPointer;
        base.OnPointerReleased(e);
        HoverLink(underPointer is not null && !open ? point : null);
        if (open && link is { } chosen)
        {
            e.Handled = true;
            OpenLink?.Invoke(chosen.Text, chosen.Target);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        pressedLink = null;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        HoverLink(null);
        base.OnPointerExited(e);
    }

    private void HoverLink(Point? point)
    {
        if (linkPointer == point)
        {
            return;
        }
        linkPointer = point;
        if (point is not null)
        {
            Cursor = hand ??= new Cursor(StandardCursorType.Hand);
        }
        else
        {
            ClearValue(CursorProperty);
        }
        InvalidateVisual();
    }

    private Point InText(PointerEventArgs e) => e.GetPosition(this) - new Point(Padding.Left, Padding.Top);

    private Link? LinkAt(Point point)
    {
        if (InlineLinks is not { Count: > 0 } links)
        {
            return null;
        }
        int at = TextLayout.HitTestPoint(point).CharacterHit.FirstCharacterIndex;
        foreach (Link link in links)
        {
            // Hit testing also finds a nearby character in empty space.
            if (link.Start <= at && at < link.End && TextLayout.HitTestTextRange(link.Start, link.End - link.Start).Any(rect => rect.Contains(point)))
            {
                return link;
            }
        }
        return null;
    }
}
