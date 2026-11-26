using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A stretch of words a click follows: where it begins and ends among them, and what it leads to (a page's
/// address, a symbol's key), which the words hand to <see cref="SelectableWords.Follow"/>.</summary>
public readonly record struct WordsLink(int Start, int End, object Target);

/// <summary>Words to read and copy (code, a Markdown page's, a value in Details), laid out a rounding error wider than
/// the width they measured where they are given that width: the text's formatter sums a line's advances with an error of
/// its own, so given its own measured width (as a box sized to them gives it wherever that width falls on a pixel at the
/// scale in force, code's at 1.25 and 2) it wraps the line's last word to a line there is no height for, cutting the words
/// short. Their links (<see cref="WordLinks"/>) are one way everywhere: the hand and an underline over the one the pointer
/// is on, and a click on it that selected nothing following it.</summary>
public class SelectableWords : SelectableTextBlock
{
    /// <summary>A millionth of a unit: far above a line's error in summing its advances, far below a pixel at any scale.</summary>
    private const double RoundingError = 1e-6;

    /// <summary>The pointer over a link, made once for every block: a move over one makes none.</summary>
    private static Cursor? hand;

    /// <summary>The width the words last measured, before the layout rounded it.</summary>
    private double measured;

    private IReadOnlyList<WordsLink> links = [];

    /// <summary>The link the pointer is on, underlined; null for none.</summary>
    private WordsLink? pointed;

    /// <summary>The link a press began on, followed where the press ends as a click on it.</summary>
    private WordsLink? pressed;

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>The stretches a click follows, by their place in the words as drawn.</summary>
    public IReadOnlyList<WordsLink> WordLinks
    {
        get => links;
        set
        {
            links = value;
            if (pointed is { } link && !value.Contains(link))
            {
                Hover(null);
            }
        }
    }

    /// <summary>Follows a link clicked; nothing without.</summary>
    public Action<WordsLink>? Follow { get; set; }

    /// <summary>The link drawn at a point of the words, or null: the one whose words are drawn there, on any of its lines.</summary>
    public WordsLink? WordLinkAt(Point point)
    {
        if (links.Count == 0)
        {
            return null;
        }
        Point inText = point - new Point(Padding.Left, Padding.Top);
        // Not the hit's IsInside, which is false below a block's first line.
        int at = TextLayout.HitTestPoint(inText).TextPosition;
        foreach (WordsLink link in links)
        {
            if (at >= link.Start && at < link.End && TextLayout.HitTestTextRange(link.Start, link.End - link.Start).Any(drawn => drawn.Contains(inText)))
            {
                return link;
            }
        }
        return null;
    }

    /// <summary>Where a link's first word is drawn among the words, for a click on it.</summary>
    public Point PointOf(WordsLink link) =>
        TextLayout.HitTestTextRange(link.Start, link.End - link.Start).First().Center + new Point(Padding.Left, Padding.Top);

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
        if (pointed is not { } link || Foreground is not { } ink)
        {
            return;
        }
        Pen pen = new(ink);
        foreach (Rect run in TextLayout.HitTestTextRange(link.Start, link.End - link.Start))
        {
            double y = origin.Y + run.Bottom - 1;
            context.DrawLine(pen, new Point(origin.X + run.Left, y), new Point(origin.X + run.Right, y));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        pressed = e.ClickCount == 1 && e.KeyModifiers == KeyModifiers.None && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            ? WordLinkAt(e.GetPosition(this)) : null;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (links.Count == 0)
        {
            return;
        }
        WordsLink? link = WordLinkAt(e.GetPosition(this));
        if (pressed is not null && (pressed != link || SelectionStart != SelectionEnd))
        {
            pressed = null;
        }
        Hover(link);
    }

    protected override void OnTapped(TappedEventArgs e)
    {
        base.OnTapped(e);
        WordsLink? link = pressed;
        pressed = null;
        if (link is { } chosen && SelectionStart == SelectionEnd && chosen == WordLinkAt(e.GetPosition(this)))
        {
            e.Handled = true;
            Follow?.Invoke(chosen);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        pressed = null;
        Hover(null);
        base.OnPointerExited(e);
    }

    /// <summary>The pointer now on <paramref name="link"/>, or on none: its hand and underline drawn again only where that
    /// changed.</summary>
    private void Hover(WordsLink? link)
    {
        if (pointed == link)
        {
            return;
        }
        pointed = link;
        if (link is null)
        {
            ClearValue(CursorProperty);
        }
        else
        {
            Cursor = hand ??= new Cursor(StandardCursorType.Hand);
        }
        InvalidateVisual();
    }
}
