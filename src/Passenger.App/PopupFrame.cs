using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>What every popup over the window shares (Find Action, Go to File, Find in Files, a Git choice, a
/// breakpoint's properties, a card): the frame (Shell.axaml's <c>Border.popup</c>), the room inside it, a width from one
/// scale, the title's look (<c>TextBlock.paneTitle</c>), its options' check boxes, and its list's rows of
/// <see cref="ListRow.Height"/>. A popup takes its width and room from here, never a number of its own (UiRulesTests).</summary>
public static class PopupFrame
{
    /// <summary>A name to type (New Branch, Stash Changes, Rename, Find Shortcut).</summary>
    public const double Narrow = 360;

    /// <summary>A short list to choose from or a few fields (a Git choice, Unstash, a breakpoint's properties).</summary>
    public const double Medium = 560;

    /// <summary>A search over the solution (Search Everywhere's Find Action, Go to File and Symbol, Recent Locations), and
    /// the most a card or a list sized by what it holds is wide.</summary>
    public const double Wide = 720;

    /// <summary>A search with a preview or a list beside its details (Find in Files, Breakpoints).</summary>
    public const double Widest = 860;

    /// <summary>The room round what a popup holds inside its frame, every one's the same (a list popup's rows reach its
    /// sides: <see cref="ListRow.PopupList"/>).</summary>
    public static readonly Thickness Inset = new(Gaps.Wide);

    /// <summary>A card's room (hover, Parameter Info): a popup's at its sides, less above and below, so a card over the
    /// code covers as little of it as it can.</summary>
    public static readonly Thickness CardInset = new(Gaps.Wide, Gaps.Normal);

    /// <summary>Every width a popup may have.</summary>
    public static IReadOnlyList<double> Widths { get; } = [Narrow, Medium, Wide, Widest];

    /// <summary>The narrowest of the widths up to <paramref name="widest"/> that holds <paramref name="natural"/>, else
    /// <paramref name="widest"/>: the width of a popup sized by what it holds.</summary>
    public static double Fitting(double natural, double widest = Wide) =>
        Widths.Where(w => w <= widest).FirstOrDefault(w => w >= natural, widest);

    /// <summary>An option of a popup beside its field (Match case, Show edited only, Also rename files): a check box at the
    /// caption's step, as tall as its words; it leaves the keys with the field unless it <paramref name="takesKeys"/>
    /// (one that turns its own field on, as File mask does).</summary>
    public static CheckBox Option(string words, bool on, bool takesKeys = false) => new()
    {
        Classes = { StyleClasses.Caption },
        Content = words,
        IsChecked = on,
        Focusable = takesKeys,
        MinHeight = 0,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    /// <summary>A popup as it shows, in the frame: itself where it is a frame or holds one as its content, else in a frame
    /// made round it (the one made when it was shown before).</summary>
    public static Control Framed(Control popup) =>
        IsFrame(popup) || popup is ContentControl { Content: Control held } && IsFrame(held) ? popup
        : popup.Parent is Border made && IsFrame(made) ? made
        : new Border { Classes = { StyleClasses.Popup }, Padding = Inset, Child = popup };

    /// <summary>The frame a popup shows in, as <see cref="Framed"/> has it shown: itself, its content, or what holds it.</summary>
    public static Border? FrameOf(Control popup) =>
        popup as Border is { } frame && IsFrame(frame) ? frame
        : popup is ContentControl { Content: Border held } && IsFrame(held) ? held
        : popup.Parent as Border is { } round && IsFrame(round) ? round
        : null;

    private static bool IsFrame(Control control) => control is Border && control.Classes.Contains(StyleClasses.Popup);

    /// <summary>A popup's content with a grip at its frame's lower right corner (<see cref="ResizeGrip"/>), from which a drag
    /// resizes the popup as a window is resized: <paramref name="popup"/> wider, and <paramref name="grows"/>, the part that
    /// takes the height it gains (its details, its list), taller.</summary>
    public static Control Resizable(Control content, Control popup, Control grows) => new Panel { Children = { content, new ResizeGrip(popup, grows) } };
}

/// <summary>A popup's lower right corner, in its frame's room there (<see cref="PopupFrame.Resizable"/>): a drag from it
/// moves the popup's width and the height of the part that grows with the pointer, never below what they first had nor
/// past the room its host fits it to in the window. Drawn as three short strokes in the icons' colour.</summary>
public sealed class ResizeGrip : Control
{
    private readonly Control popup;
    private readonly Control grows;

    /// <summary>Where the drag began, the sizes then and the most the part that grows may take, while one goes on.</summary>
    private (Point At, double Width, double Height, double Tallest)? from;

    /// <summary>The sizes the popup first had, below which a drag never takes it.</summary>
    private (double Width, double Height)? least;

    public ResizeGrip(Control popup, Control grows)
    {
        this.popup = popup;
        this.grows = grows;
        Width = Gaps.Wide;
        Height = Gaps.Wide;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, -PopupFrame.Inset.Right, -PopupFrame.Inset.Bottom);
        Cursor = new Cursor(StandardCursorType.BottomRightCorner);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        least ??= (popup.Bounds.Width, grows.Bounds.Height);
        // The height the popup's host leaves it, past which its foot (Close, this corner) would leave the window.
        double spare = double.IsFinite(popup.MaxHeight) ? Math.Max(0, popup.MaxHeight - popup.Bounds.Height) : double.PositiveInfinity;
        from = (e.GetPosition(top), popup.Bounds.Width, grows.Bounds.Height, grows.Bounds.Height + spare);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (from is not { } start || least is not { } smallest || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        Point at = e.GetPosition(top);
        // A popup centred over the window grows on both sides, so its corner keeps up with the pointer at twice the move.
        double across = popup.Parent is Layoutable { HorizontalAlignment: HorizontalAlignment.Center } ? 2 : 1;
        popup.Width = Math.Max(smallest.Width, start.Width + ((at.X - start.At.X) * across));
        grows.Height = Math.Max(smallest.Height, Math.Min(start.Tallest, start.Height + at.Y - start.At.Y));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (from is not null)
        {
            from = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        from = null;
    }

    public override void Render(DrawingContext context)
    {
        // As hit as it is drawn, its whole square taking the press.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (this.TryFindResource(TokenKeys.Icon, ActualThemeVariant, out object? found) && found is IBrush brush)
        {
            Pen pen = new(brush, 1);
            double size = Bounds.Width;
            for (double inset = Gaps.Hair; inset < size; inset += Gaps.Tight)
            {
                context.DrawLine(pen, new Point(inset, size), new Point(size, inset));
            }
        }
    }
}

/// <summary>A popup's frame sized by what it holds (a list of choices, a change's lines): the narrowest of the popups'
/// widths that holds it, up to <see cref="Widest"/> (<see cref="PopupFrame.Fitting"/>), or the room where that is less.</summary>
public class FittingFrame : Border
{
    public FittingFrame()
    {
        Classes.Add(StyleClasses.Popup);
    }

    /// <summary>The most it is wide.</summary>
    public double Widest { get; init; } = PopupFrame.Wide;

    protected override Size MeasureOverride(Size availableSize)
    {
        double natural = base.MeasureOverride(new Size(double.PositiveInfinity, availableSize.Height)).Width;
        double width = Math.Min(PopupFrame.Fitting(natural, Widest), availableSize.Width);
        return new Size(width, base.MeasureOverride(new Size(width, availableSize.Height)).Height);
    }
}
