using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A change between two panes: its top and bottom on each, in the strip's coordinates, and its colour.</summary>
public readonly record struct DiffBand(int Index, double LeftTop, double LeftBottom, double RightTop, double RightBottom, ChangeMarkKind Kind);

/// <summary>The strip between two panes of a diff or a merge: each change a band from its lines on the left to its lines
/// on the right, tinted by its kind, with the button whose arrow takes it (pointing away from the icons' side) and the one
/// whose X ignores it at that side's end, while what made the strip allows them, each named by its tip. Only the changes in
/// view have buttons, so a diff of thousands scrolls as lightly as one of a few; <see cref="Refresh"/> follows a scroll or
/// a change of the bands.</summary>
public sealed class DiffStrip : Control
{
    private const double IconSize = 9;

    /// <summary>How far round its icon a button takes the pointer.</summary>
    private const double Reach = 3;

    /// <summary>A band's two buttons, the arrow's and the X's.</summary>
    private static readonly bool[] Ignoring = [false, true];

    private readonly Func<IEnumerable<DiffBand>> bands;
    private readonly MergeSide iconSide;
    private readonly CodeEditor pane;
    private readonly Func<bool> takes;
    private readonly bool ignores;
    private readonly Func<DiffBand, bool, (string Words, string? Keys)> tip;
    private readonly Action<DiffBand, bool, KeyModifiers> pressed;
    private readonly List<BandButton> buttons = [];

    /// <param name="iconSide">The side whose end has the icons, the arrow pointing away from it.</param>
    /// <param name="pane">The pane at <paramref name="iconSide"/>, whose line the icons stand beside.</param>
    /// <param name="tip">What a change's arrow (false) or X (true) does, and the keys that do it to the change at the caret.</param>
    /// <param name="pressed">A click on a change's arrow (false) or X (true), with the keys held.</param>
    public DiffStrip(Func<IEnumerable<DiffBand>> bands, MergeSide iconSide, CodeEditor pane, Func<bool> takes, bool ignores,
        Func<DiffBand, bool, (string Words, string? Keys)> tip, Action<DiffBand, bool, KeyModifiers> pressed)
    {
        this.bands = bands;
        this.iconSide = iconSide;
        this.pane = pane;
        this.takes = takes;
        this.ignores = ignores;
        this.tip = tip;
        this.pressed = pressed;
        ClipToBounds = true;
        SizeChanged += (_, _) => Place();
    }

    /// <summary>The bands drawn again and their buttons put beside them, after a scroll or a change of the bands.</summary>
    public void Refresh()
    {
        InvalidateVisual();
        Place();
    }

    /// <summary>Its colours are the theme's as it draws, so a change of theme draws it again.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateVisual();
        }
    }

    /// <summary>Each band in view, tinted and edged in its change's colour: closed at the panes' edges only while its arrow
    /// is drawn in it, as a band one line high each side is otherwise an empty box.</summary>
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Tokens.Brush(this, TokenKeys.Editor_Background), new Rect(Bounds.Size));
        double width = Bounds.Width;
        bool boxed = takes();
        foreach (DiffBand band in bands())
        {
            if (Math.Max(band.LeftBottom, band.RightBottom) < -IconSize || Math.Min(band.LeftTop, band.RightTop) > Bounds.Height + IconSize)
            {
                continue;
            }
            (string tint, string edge) = Colours(band.Kind);
            Pen pen = new(Tokens.Brush(this, edge), 1);
            StreamGeometry shape = new();
            using (StreamGeometryContext path = shape.Open())
            {
                path.BeginFigure(new Point(0, band.LeftTop), true);
                path.CubicBezierTo(new Point(width / 2, band.LeftTop), new Point(width / 2, band.RightTop), new Point(width, band.RightTop));
                path.LineTo(new Point(width, band.RightBottom));
                path.CubicBezierTo(new Point(width / 2, band.RightBottom), new Point(width / 2, band.LeftBottom), new Point(0, band.LeftBottom));
                path.EndFigure(true);
            }
            context.DrawGeometry(Tokens.Brush(this, tint), boxed ? pen : null, shape);
            if (boxed)
            {
                continue;
            }
            StreamGeometry edges = new();
            using (StreamGeometryContext path = edges.Open())
            {
                path.BeginFigure(new Point(0, band.LeftTop), false);
                path.CubicBezierTo(new Point(width / 2, band.LeftTop), new Point(width / 2, band.RightTop), new Point(width, band.RightTop));
                path.EndFigure(false);
                path.BeginFigure(new Point(0, band.LeftBottom), false);
                path.CubicBezierTo(new Point(width / 2, band.LeftBottom), new Point(width / 2, band.RightBottom), new Point(width, band.RightBottom));
                path.EndFigure(false);
            }
            context.DrawGeometry(null, pen, edges);
        }
    }

    /// <summary>A change's tint and its edge's colour, which its icons are drawn in.</summary>
    private static (string Tint, string Edge) Colours(ChangeMarkKind kind) => kind switch
    {
        ChangeMarkKind.Conflict => (TokenKeys.Diff_Conflict, TokenKeys.Vcs_Conflict),
        ChangeMarkKind.Added => (TokenKeys.Diff_Added, TokenKeys.Vcs_Added),
        ChangeMarkKind.Deleted => (TokenKeys.Diff_Deleted, TokenKeys.Vcs_Deleted),
        _ => (TokenKeys.Diff_Modified, TokenKeys.Vcs_Modified),
    };

    /// <summary>Where the change of <paramref name="index"/> has its arrow or X, or null while it has none.</summary>
    public Rect? Icon(int index, bool ignore) =>
        bands().Where(b => b.Index == index).Select(b => Icon(b, ignore)).FirstOrDefault();

    /// <summary>A band's arrow or X beside its first line at the icons' end, the arrow nearest the pane and the X after it.</summary>
    private Rect? Icon(DiffBand band, bool ignore)
    {
        if (ignore ? !ignores : !takes())
        {
            return null;
        }
        (double top, double bottom) = iconSide == MergeSide.Left ? (band.LeftTop, band.LeftBottom) : (band.RightTop, band.RightBottom);
        double y = top + Math.Min(pane.LineHeight / 2, Math.Max((IconSize / 2) + 1, (bottom - top) / 2));
        double offset = ignore ? 18 : 4;
        double x = iconSide == MergeSide.Left ? offset : Bounds.Width - offset - IconSize;
        return new Rect(x, y - (IconSize / 2), IconSize, IconSize);
    }

    /// <summary>A button beside each icon in view, the buttons made once and taken again by whichever change stands where
    /// each goes.</summary>
    private void Place()
    {
        int used = 0;
        double height = Bounds.Height;
        foreach (DiffBand band in bands())
        {
            foreach (bool ignore in Ignoring)
            {
                if (Icon(band, ignore) is not { } icon || icon.Bottom + Reach < 0 || icon.Top - Reach > height)
                {
                    continue;
                }
                BandButton button = used < buttons.Count ? buttons[used] : Made();
                used++;
                button.Show(band, ignore, iconSide, Colours(band.Kind).Edge, icon.Inflate(Reach), tip);
            }
        }
        for (int i = used; i < buttons.Count; i++)
        {
            buttons[i].Button.IsVisible = false;
        }
        InvalidateArrange();
    }

    private BandButton Made()
    {
        BandButton made = new(IconSize, Reach, pressed);
        LogicalChildren.Add(made.Button);
        VisualChildren.Add(made.Button);
        buttons.Add(made);
        return made;
    }

    /// <summary>Its size is what holds it gives it, whatever its buttons'.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (BandButton button in buttons)
        {
            button.Button.Measure(button.At.Size);
        }
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (BandButton button in buttons)
        {
            button.Button.Arrange(button.At);
        }
        return finalSize;
    }

    /// <summary>A button of the changes in view: the arrow or X of whichever change it stands beside, which a click takes or
    /// ignores with the keys held as it is let go.</summary>
    private sealed class BandButton
    {
        private readonly BandGlyph glyph;
        private DiffBand band;
        private KeyModifiers held;

        public BandButton(double size, double reach, Action<DiffBand, bool, KeyModifiers> pressed)
        {
            glyph = new BandGlyph { Width = size, Height = size };
            Button = new Button { Classes = { StyleClasses.Tool }, Content = glyph, Focusable = false, Width = size + (2 * reach), Height = size + (2 * reach), MinHeight = 0 };
            Button.AddHandler(InputElement.PointerReleasedEvent, (_, e) => held = e.KeyModifiers, RoutingStrategies.Tunnel);
            Button.Click += (_, _) =>
            {
                KeyModifiers keys = held;
                held = KeyModifiers.None;
                pressed(band, Ignore, keys);
            };
        }

        public Button Button { get; }

        /// <summary>The change it stands beside, by its place among the changes; -1 before it stands beside one.</summary>
        public int Index { get; private set; } = -1;

        public bool Ignore { get; private set; }

        /// <summary>Where it stands in the strip.</summary>
        public Rect At { get; private set; }

        /// <summary>Stands beside a change's icon at <paramref name="at"/>, named for that change and drawn in its colour.</summary>
        public void Show(DiffBand shown, bool ignore, MergeSide iconSide, string edge, Rect at, Func<DiffBand, bool, (string Words, string? Keys)> tip)
        {
            band = shown;
            if (Index != shown.Index || Ignore != ignore)
            {
                Index = shown.Index;
                Ignore = ignore;
                (string words, string? keys) = tip(shown, ignore);
                ToolButtons.Tip(Button, words, keys);
            }
            glyph.Show(ignore ? 0 : iconSide == MergeSide.Left ? 1 : -1, edge);
            At = at;
            Button.IsVisible = true;
        }
    }

    /// <summary>A band button's icon in its change's colour: ">>" from the left, "<<" from the right (the lines toward the
    /// other pane), or the X.</summary>
    private sealed class BandGlyph : Control
    {
        /// <summary>Which way the arrow points, 1 rightward and -1 leftward; 0 for the X.</summary>
        private int pointing;
        private string edge = TokenKeys.Vcs_Modified;

        public void Show(int toward, string colour)
        {
            if (toward != pointing || colour != edge)
            {
                pointing = toward;
                edge = colour;
                InvalidateVisual();
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
            {
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            Pen pen = new(Tokens.Brush(this, edge), 1.6, lineCap: PenLineCap.Round);
            Rect icon = new(Bounds.Size);
            if (pointing == 0)
            {
                Rect x = icon.Deflate(1);
                context.DrawLine(pen, x.TopLeft, x.BottomRight);
                context.DrawLine(pen, x.TopRight, x.BottomLeft);
                return;
            }
            for (int k = 0; k < 2; k++)
            {
                double x = pointing > 0 ? icon.X + (k * 4.5) : icon.Right - (k * 4.5);
                context.DrawLine(pen, new Point(x, icon.Y), new Point(x + (pointing * 4), icon.Center.Y));
                context.DrawLine(pen, new Point(x + (pointing * 4), icon.Center.Y), new Point(x, icon.Bottom));
            }
        }
    }
}
