using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>A Markdown file's top-level blocks down a page, a control only for those in the preview's sight and a sight
/// below it: every other block stands as its height, as last measured or estimated from its words, so a long file costs
/// the window what is on screen. A block keeps its control while it stays within two sights of the one shown, and the
/// page keeps its controls while it is put aside, so a file shown again makes none. The blocks in sight anchor the scroll,
/// so a block measured above them moves nothing a person sees; a scroll the preview sets (<see cref="Jump"/>) is its own.</summary>
public sealed class MarkdownBlocks : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<MarkdownBlocks, double>(nameof(Spacing));

    /// <summary>The width a block is estimated at before the page is first measured.</summary>
    private const double FirstWidth = 600;

    private readonly Func<Markdown.Block, Control> make;
    private readonly Func<bool> arrive;
    private readonly List<Entry> shown = [];
    private Entry[] entries = [];
    private double[] tops = [0];
    private bool topsStale = true;
    private double width = double.NaN;
    private Rect sight;
    private bool sighted;
    private bool sightMoved;
    private bool arriving;
    private bool jumped;
    private bool sightSinceJump;
    private IScrollAnchorProvider? anchors;

    static MarkdownBlocks() => AffectsMeasure<MarkdownBlocks>(SpacingProperty);

    /// <param name="make">A block's control.</param>
    /// <param name="arrive">Scrolls the preview as the page arrives in it, once its height is known; whether it moved.</param>
    internal MarkdownBlocks(Func<Markdown.Block, Control> make, Func<bool> arrive)
    {
        this.make = make;
        this.arrive = arrive;
        EffectiveViewportChanged += OnSight;
    }

    /// <summary>A top-level block of the page: the lines it was read from, its heading's anchor, its control while it has
    /// one, and its height as measured at <see cref="MeasuredAt"/> (NaN until it is) or else estimated.</summary>
    internal sealed class Entry(int index, Markdown.Placed placed, string? anchor, Entry? kept)
    {
        /// <summary>Its place among the page's blocks.</summary>
        public int Index { get; } = index;

        public Markdown.Placed Placed { get; } = placed;

        public string? Anchor { get; } = anchor;

        /// <summary>The block drawn before that this one is, whose control and height it takes as the page takes it.</summary>
        public Entry? Kept { get; set; } = kept;

        public Control? Control { get; set; }

        public double Height { get; set; } = double.NaN;

        public double MeasuredAt { get; set; } = double.NaN;
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Raised when a block drawn changes its height (a picture read, a narrower page), which moves those under it.</summary>
    public event EventHandler? Moved;

    /// <summary>The text the blocks were read from and its version, null until they are.</summary>
    internal (TextDocument Text, int Version)? From { get; private set; }

    internal IReadOnlyList<Entry> Entries => entries;

    /// <summary>The blocks' controls drawn now, in order.</summary>
    internal IReadOnlyList<Entry> Shown => shown;

    /// <summary>Takes a text's blocks: a kept block's control and height its own, the controls of blocks gone let go.</summary>
    internal void Take(Entry[] next, TextDocument text, int version)
    {
        foreach (Entry entry in next)
        {
            if (entry.Kept is { } old)
            {
                entry.Control = old.Control;
                entry.Height = old.Height;
                entry.MeasuredAt = old.MeasuredAt;
                old.Control = null;
                entry.Kept = null;
            }
        }
        foreach (Entry gone in shown)
        {
            if (gone.Control is { } control)
            {
                gone.Control = null;
                LetGo(control);
            }
        }
        shown.Clear();
        foreach (Entry entry in next)
        {
            if (entry.Control is { } control)
            {
                // A block moved past another kept one goes to its place among the page's controls.
                if (Children[shown.Count] != control)
                {
                    Children.Remove(control);
                    Children.Insert(shown.Count, control);
                }
                shown.Add(entry);
            }
        }
        entries = next;
        From = (text, version);
        topsStale = true;
        InvalidateMeasure();
    }

    /// <summary>Has the page's next sight wait for the preview to scroll to its place, its height known by then.</summary>
    internal void Arrive() => arriving = true;

    /// <summary>A scroll the preview sets next: nothing drawn anchors it, so the block it puts at the top stays there.</summary>
    internal void Jump()
    {
        jumped = true;
        sightSinceJump = false;
        foreach (Entry entry in shown)
        {
            anchors?.UnregisterAnchorCandidate(entry.Control!);
        }
    }

    /// <summary>The index of the block at a line of the file (from zero) or the last before it, -1 for none.</summary>
    internal int IndexAtLine(int line)
    {
        int low = 0;
        int high = entries.Length - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (entries[middle].Placed.Line <= line)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }

    /// <summary>Where a block's words are drawn down the page and how tall, its margins aside: the block is drawn now if it
    /// was not, so a scroll to it lands on its words.</summary>
    internal (double Top, double Height) Drawn(int index)
    {
        Entry entry = entries[index];
        if (entry.Control is null && !double.IsNaN(width))
        {
            Make(entry, new Size(width, double.PositiveInfinity));
        }
        Tops();
        Thickness margin = entry.Control?.Margin ?? default;
        return (tops[index] + margin.Top, Math.Max(0, entry.Height - margin.Top - margin.Bottom));
    }

    private void OnSight(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (arriving)
        {
            arriving = false;
            if (arrive())
            {
                // The preview moved: the sight it moved to comes next.
                return;
            }
        }
        sight = e.EffectiveViewport;
        sighted = true;
        sightMoved = true;
        sightSinceJump = jumped;
        InvalidateMeasure();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        anchors = this.FindAncestorOfType<IScrollAnchorProvider>();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (Entry entry in shown)
        {
            anchors?.UnregisterAnchorCandidate(entry.Control!);
        }
        anchors = null;
        sighted = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double across = double.IsFinite(availableSize.Width) ? availableSize.Width : FirstWidth;
        bool moved = false;
        if (across != width)
        {
            moved = shown.Count > 0;
            width = across;
            foreach (Entry entry in entries)
            {
                // A block measured at another width keeps that height as its estimate.
                if (double.IsNaN(entry.MeasuredAt))
                {
                    entry.Height = double.NaN;
                }
            }
            topsStale = true;
        }
        if (jumped && sightSinceJump)
        {
            jumped = false;
        }
        Size each = new(width, double.PositiveInfinity);
        foreach (Entry entry in shown)
        {
            entry.Control!.Measure(each);
            double height = entry.Control.DesiredSize.Height;
            if (height != entry.Height)
            {
                moved |= entry.MeasuredAt == width;
                entry.Height = height;
                topsStale = true;
            }
            entry.MeasuredAt = width;
        }
        Tops();
        if (sighted && entries.Length > 0)
        {
            Realize(sight.Top, sight.Bottom + sight.Height, each);
        }
        if (sighted && sightMoved)
        {
            // Only a scroll lets blocks go: one an edit pushed past the reach stays, so typing remakes none.
            sightMoved = false;
            double reach = 2 * sight.Height;
            for (int at = shown.Count - 1; at >= 0; at--)
            {
                Entry entry = shown[at];
                bool far = tops[entry.Index] + entry.Height < sight.Top - reach || tops[entry.Index] > sight.Bottom + reach;
                if (far && !entry.Control!.IsKeyboardFocusWithin)
                {
                    shown.RemoveAt(at);
                    LetGo(entry.Control);
                    entry.Control = null;
                }
            }
        }
        if (moved)
        {
            Moved?.Invoke(this, EventArgs.Empty);
        }
        return new Size(width, PageHeight());
    }

    /// <summary>Draws the blocks from the one at <paramref name="from"/> down to <paramref name="to"/>, each measured as it
    /// comes, so a block taller than its estimate pushes those after it on.</summary>
    private void Realize(double from, double to, Size each)
    {
        double spacing = Spacing;
        int index = IndexAt(from);
        bool shifted = false;
        while (index < entries.Length && tops[index] < to)
        {
            Entry entry = entries[index];
            if (entry.Control is null)
            {
                shifted |= Make(entry, each);
            }
            tops[index + 1] = tops[index] + entry.Height + spacing;
            index++;
        }
        if (shifted)
        {
            for (; index < entries.Length; index++)
            {
                tops[index + 1] = tops[index] + entries[index].Height + spacing;
            }
        }
        topsStale = false;
    }

    /// <summary>Draws a block, its control among the page's in the blocks' order; whether its height was not as thought.</summary>
    private bool Make(Entry entry, Size each)
    {
        Control control = make(entry.Placed.Block);
        entry.Control = control;
        int at = shown.Count;
        while (at > 0 && shown[at - 1].Index > entry.Index)
        {
            at--;
        }
        shown.Insert(at, entry);
        Children.Insert(at, control);
        control.Measure(each);
        entry.MeasuredAt = width;
        if (control.DesiredSize.Height == entry.Height)
        {
            return false;
        }
        entry.Height = control.DesiredSize.Height;
        topsStale = true;
        return true;
    }

    private void LetGo(Control control)
    {
        anchors?.UnregisterAnchorCandidate(control);
        Children.Remove(control);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (Entry entry in shown)
        {
            Rect place = new(0, tops[entry.Index], finalSize.Width, entry.Height);
            entry.Control!.Arrange(place);
            if (!jumped && sighted && place.Intersects(sight))
            {
                anchors?.RegisterAnchorCandidate(entry.Control);
            }
        }
        return finalSize;
    }

    /// <summary>The page's height: its blocks' and the spaces between them.</summary>
    private double PageHeight()
    {
        Tops();
        return entries.Length == 0 ? 0 : tops[entries.Length] - Spacing;
    }

    /// <summary>The index of the block at a place down the page: the last whose top is at or above it.</summary>
    private int IndexAt(double y)
    {
        int low = 0;
        int high = entries.Length - 1;
        int found = 0;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (tops[middle] <= y)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }

    /// <summary>Each block's top again where a height changed, a block not yet estimated estimated at the page's width.</summary>
    private void Tops()
    {
        if (!topsStale && tops.Length == entries.Length + 1)
        {
            return;
        }
        double across = double.IsNaN(width) ? FirstWidth : width;
        if (tops.Length < entries.Length + 1)
        {
            tops = new double[entries.Length + 1];
        }
        double spacing = Spacing;
        for (int i = 0; i < entries.Length; i++)
        {
            Entry entry = entries[i];
            if (double.IsNaN(entry.Height))
            {
                entry.Height = Estimate(entry.Placed.Block, across);
            }
            tops[i + 1] = tops[i] + entry.Height + spacing;
        }
        topsStale = false;
    }

    /// <summary>A block's height at a width, from its words and lines as the page's styles draw them.</summary>
    private static double Estimate(Markdown.Block block, double width) => block switch
    {
        Markdown.Heading heading => heading.Level switch { 1 => 39, 2 => 33, 3 or 4 => 25, _ => 22 },
        Markdown.Paragraph paragraph => Wrapped(paragraph.Text.Length, width),
        Markdown.Code code => 14 + (16 * (code.Text.AsSpan().Count('\n') + 1)),
        Markdown.Listing listing => Items(listing, width),
        Markdown.Quote quote => 4 + Stacked(quote.Blocks, width - 15),
        Markdown.Table table => 2 + (32 * (table.Rows.Count + 1)),
        _ => 17,
    };

    /// <summary>A list's items, each its blocks beside its mark, at least a line.</summary>
    private static double Items(Markdown.Listing listing, double width)
    {
        double height = 6 * Math.Max(0, listing.Items.Count - 1);
        for (int i = 0; i < listing.Items.Count; i++)
        {
            height += Math.Max(21, Stacked(listing.Items[i], width - 28));
        }
        return height;
    }

    private static double Stacked(IReadOnlyList<Markdown.Block> blocks, double width)
    {
        double height = 8 * Math.Max(0, blocks.Count - 1);
        for (int i = 0; i < blocks.Count; i++)
        {
            height += Estimate(blocks[i], width);
        }
        return height;
    }

    /// <summary>Words wrapped at a width, a line of 21 for each width's worth of their characters.</summary>
    private static double Wrapped(int characters, double width) => 21 * Math.Max(1, Math.Ceiling(characters * 7.0 / Math.Max(width, 100)));
}
