using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>The code editor: draws a <see cref="TextDocument"/> with a line-number gutter, carets and
/// selections, and turns keys, typing and the mouse into edits. Lines are formatted only when they
/// are on screen and kept while their text and colours stay the same, so a keystroke costs the one
/// line it changed.</summary>
public sealed partial class CodeEditor : Control
{
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<CodeEditor, double>(nameof(FontSize), 13);

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<CodeEditor, double>(nameof(LineSpacing), 1.2);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<CodeEditor, FontFamily>(nameof(FontFamily), new FontFamily("avares://Passenger.Editor/Fonts#JetBrains Mono"));

    /// <summary>Space between the gutter's edge and the line numbers, and after them.</summary>
    private const double GutterPadLeft = 14;
    private const double GutterPadRight = 22;
    private const double TextPadLeft = 6;

    /// <summary>Drawn by the host's ScrollBar theme; "stripe" marks the bar that carries the error stripe.</summary>
    private readonly ScrollBar vertical = new() { Orientation = Orientation.Vertical, AllowAutoHide = false, Classes = { "stripe" } };
    private readonly ScrollBar horizontal = new() { Orientation = Orientation.Horizontal, AllowAutoHide = true };
    private readonly DispatcherTimer blink;
    private TextDocument document = new();
    private SelectionSet selections = SelectionSet.Single(0);

    /// <summary>The text the carets are offsets in: the document's, but while a change is told to those who heard of it
    /// before this editor carried its carets through it (mid-edit, or someone else's edit).</summary>
    private TextSnapshot caretsText;
    private IHighlighter? highlighter;
    private Vector scroll;
    private double widestLine;

    /// <summary>Whether the text changed since the last frame, whose lines then set the horizontal extent afresh.</summary>
    private bool widestStale;
    private bool caretOn = true;
    private bool updatingBars;
    private bool editing;
    private int? pendingGoTo;
    private bool pendingEnd;
    private bool ligatures;
    private Metrics metrics;
    private Palette palette;
    private bool rainbowBraces = true;
    private IndentOptions indent = new();
    private (TextRunProperties Value, TextRunProperties Exception) annotationRuns;
    private TextRunProperties? questionRun;

    public CodeEditor()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        stripe = new ErrorStripe(this);
        counts = new ProblemCounts(this);
        VisualChildren.Add(vertical);
        VisualChildren.Add(horizontal);
        VisualChildren.Add(stripe);
        VisualChildren.Add(counts);
        LogicalChildren.Add(vertical);
        LogicalChildren.Add(horizontal);
        LogicalChildren.Add(stripe);
        LogicalChildren.Add(counts);
        vertical.PropertyChanged += OnScrollBarChanged;
        horizontal.PropertyChanged += OnScrollBarChanged;
        blink = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) =>
        {
            caretOn = !caretOn;
            Blinks++;
            InvalidateVisual();
        });
        document.ChangedFirst += OnChangedFirst;
        document.Changed += OnDocumentChanged;
        caretsText = document.Snapshot;
        ActualThemeVariantChanged += (_, _) => RefreshStyle();
        // A theme or a colour scheme laid over the application's resources (Settings) changes the brushes, not the variant.
        ResourcesChanged += (_, _) =>
        {
            if (palette is not { } shown || !Palette.From(this).SameAs(shown))
            {
                RefreshStyle();
            }
        };
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = InputMethod);
        metrics = Metrics.Measure(Typeface, FontSize, LineSpacing);
        palette = Palette.Default;
        numberRuns = (
            RunWith(palette.LineNumberCurrent),
            RunWith(palette.LineNumber));
        annotationRuns = (ItalicRunWith(palette.InlineValue), ItalicRunWith(palette.Error));
    }

    /// <summary>Raised when the view scrolls.</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>Raised when any caret or selection changes, by an edit or a move.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Cmd+click (Ctrl+click on Windows) at an offset: the host's go-to-definition.</summary>
    public event EventHandler<int>? NavigateRequested;

    /// <summary>The pointer has rested on the text: the offset under it and where it is, for a hover
    /// card. Raised once per rest; moving off clears it (<see cref="HoverEnded"/>).</summary>
    public event EventHandler<HoverEventArgs>? Hovered;

    public event EventHandler? HoverEnded;

    /// <summary>The context menu is asked for: by a right-click on the text, at its point, or from the keyboard (the menu
    /// key, Shift+F10), with no point, at the caret.</summary>
    public event EventHandler<Point?>? ContextMenuRequested;

    /// <summary>Asks for the context menu at the caret, as the keyboard's menu key does.</summary>
    public void RequestContextMenu() => ContextMenuRequested?.Invoke(this, null);

    /// <summary>Text arrived from the keyboard and was typed into the document, so completion and
    /// signature help can decide whether to open.</summary>
    public event EventHandler<string>? Typed;

    /// <summary>The markers drawn over the text (sorted), from the language service.</summary>
    public IReadOnlyList<Marker> Markers { get; private set; } = [];

    /// <summary>The furthest any marker up to each reaches, so a line finds its first marker by halving.</summary>
    private int[] markerReach = [];

    /// <summary>Sets the markers, taken as given when they are in order already, as a host carrying them through a key gives
    /// them: thousands sorted again at every key cost it.</summary>
    public void SetMarkers(IReadOnlyList<Marker> markers)
    {
        Markers = MarkersInOrder(markers) ? markers : [.. markers.OrderBy(m => m.Range.Start)];
        if (markerReach.Length != Markers.Count)
        {
            markerReach = new int[Markers.Count];
        }
        for (int i = 0; i < Markers.Count; i++)
        {
            markerReach[i] = Math.Max(i > 0 ? markerReach[i - 1] : 0, Markers[i].Range.End);
        }
        ProblemCount = CountProblems(Markers);
        counts.InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    private static bool MarkersInOrder(IReadOnlyList<Marker> markers)
    {
        for (int i = 1; i < markers.Count; i++)
        {
            if (markers[i].Range.Start < markers[i - 1].Range.Start)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The marks in the gutter (breakpoints, the execution point), from the host.</summary>
    public IReadOnlyList<LineMark> LineMarks { get; private set; } = [];

    public void SetLineMarks(IReadOnlyList<LineMark> marks)
    {
        LineMarks = marks;
        InvalidateVisual();
    }

    /// <summary>The lines changed since the last commit, as bars against the code, from the host.</summary>
    public IReadOnlyList<ChangeMark> ChangeMarks { get; private set; } = [];

    public void SetChangeMarks(IReadOnlyList<ChangeMark> marks)
    {
        ChangeMarks = marks;
        InvalidateVisual();
    }

    private readonly AnchoredList<InlineChange> inlineChanges = new(c => c.Range, (c, range) => c with { Range = range });

    /// <summary>The words that changed inside changed lines (sorted), drawn over the lines' tint in a stronger colour; they
    /// move with edits until the host sets them again.</summary>
    public IReadOnlyList<InlineChange> InlineChanges => inlineChanges.Items;

    public void SetInlineChanges(IReadOnlyList<InlineChange> changes)
    {
        if (changes.Count == 0 && inlineChanges.Count == 0)
        {
            return;
        }
        inlineChanges.Set(document, [.. changes.OrderBy(c => c.Range.Start)]);
        InvalidateVisual();
    }

    /// <summary>The bookmarked lines, drawn as ribbons at the line numbers' left.</summary>
    public IReadOnlyList<int> Bookmarks { get; private set; } = [];

    public void SetBookmarks(IReadOnlyList<int> lines)
    {
        Bookmarks = lines;
        InvalidateVisual();
    }

    /// <summary>Text per line in a column at the gutter's left (Git blame's author and date), null for a line with none;
    /// empty for no column.</summary>
    public IReadOnlyList<string?> GutterAnnotations { get; private set; } = [];

    private double annotationWidth;

    public void SetGutterAnnotations(IReadOnlyList<string?> perLine)
    {
        GutterAnnotations = perLine;
        int longest = perLine.Count == 0 ? 0 : perLine.Max(l => l?.Length ?? 0);
        annotationWidth = longest == 0 ? 0 : (longest * metrics.CharWidth) + 18;
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>Whether a point (in this control) is on the annotation column.</summary>
    public bool IsInAnnotationColumn(Point at) => annotationWidth > 0 && at.X < annotationWidth;

    /// <summary>Whether the change marks tint whole lines rather than draw bars in the gutter; a mark with no lines is then
    /// a rule between two.</summary>
    public bool ChangeBackgrounds { get; set; }

    /// <summary>Whether a point (in this control) is on the change bars' strip at the gutter's edge.</summary>
    public bool IsInChangeColumn(Point at) => ShowLineNumbers && at.X >= GutterWidth - FoldWidth - 8 && at.X < GutterWidth - FoldWidth;

    /// <summary>The tests' run icons in the gutter, from the host.</summary>
    public IReadOnlyList<TestMark> TestMarks { get; private set; } = [];

    public void SetTestMarks(IReadOnlyList<TestMark> marks)
    {
        TestMarks = marks;
        InvalidateVisual();
    }

    /// <summary>The run icons in the gutter of what run configurations run (a compose file's services), from the host.</summary>
    public IReadOnlyList<RunMark> RunMarks { get; private set; } = [];

    public void SetRunMarks(IReadOnlyList<RunMark> marks)
    {
        RunMarks = marks;
        InvalidateVisual();
    }

    /// <summary>The colours lines write, each a swatch in the gutter, from the host.</summary>
    public IReadOnlyList<ColourMark> ColourMarks { get; private set; } = [];

    public void SetColourMarks(IReadOnlyList<ColourMark> marks)
    {
        ColourMarks = marks;
        InvalidateVisual();
    }

    /// <summary>Whether a point (in the editor's coordinates) is in the gutter's column of run icons and the
    /// execution arrow, right of the line numbers.</summary>
    public bool IsInMarkColumn(Point at) => ShowLineNumbers && at.X >= GutterWidth - FoldWidth - GutterPadRight && at.X < GutterWidth - FoldWidth;

    /// <summary>Text after lines' code (the debugger's inline values, an exception), from the host.</summary>
    public IReadOnlyList<LineAnnotation> LineAnnotations { get; private set; } = [];

    public void SetLineAnnotations(IReadOnlyList<LineAnnotation> annotations)
    {
        LineAnnotations = annotations;
        InvalidateVisual();
    }

    /// <summary>A click on a line's number (from zero): the host's breakpoint toggle. With a handler, a click there no
    /// longer selects the line.</summary>
    public event EventHandler<GutterClick>? GutterClicked;

    /// <summary>The execution arrow was dragged to another line (from zero), to Set Next Statement there. With a
    /// handler, a press on the arrow drags it; let go where it was, it is the gutter click it began as.</summary>
    public event EventHandler<int>? ExecutionPointDragged;

    /// <summary>Where a line's number is drawn, in the editor's coordinates: what a popup about its breakpoint
    /// is placed beside.</summary>
    public Point GutterPoint(int line) => new(GutterWidth, TopOf(line) + metrics.LineHeight - scroll.Y);

    /// <summary>The middle of one of the gutter's columns at a line, in the editor's coordinates: where a click on the
    /// execution arrow or a test's icon, a change bar or a fold's chevron lands.</summary>
    public Point GutterColumnPoint(GutterColumn column, int line)
    {
        double right = GutterWidth - FoldWidth;
        double x = column switch
        {
            GutterColumn.Folding => GutterWidth - (FoldWidth / 2),
            GutterColumn.Changes => right - 4,
            _ => right - (GutterPadRight / 2),
        };
        return new Point(x, TopOf(line) - scroll.Y + (metrics.LineHeight / 2));
    }

    /// <summary>The markers at an offset, for a tooltip.</summary>
    public IEnumerable<Marker> MarkersAt(int offset) =>
        Markers.Where(m => m.Range.Start <= offset && (offset < m.Range.End || (m.Range.Length == 0 && offset == m.Range.Start)));

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>How Enter, Tab, Shift+Tab, Backspace and a paste indent, and how wide a tab is drawn.</summary>
    public IndentOptions Indent
    {
        get => indent;
        set
        {
            bool redraw = value.TabWidth != indent.TabWidth;
            indent = value;
            if (redraw)
            {
                RefreshStyle();
            }
        }
    }

    /// <summary>Whether a pasted block takes the document's indentation; off where indentation is the text's own (Markdown's
    /// code blocks, plain text).</summary>
    public bool ReindentsPaste { get; set; } = true;

    /// <summary>Whether brackets take a colour by their depth (rainbow braces), or the text's.</summary>
    public bool RainbowBraces
    {
        get => rainbowBraces;
        set
        {
            if (rainbowBraces != value)
            {
                rainbowBraces = value;
                RefreshStyle();
            }
        }
    }

    /// <summary>Whether the font may join characters into ligatures (=> drawn as one arrow); off by default, so every
    /// character stays the one typed.</summary>
    public bool Ligatures
    {
        get => ligatures;
        set
        {
            ligatures = value;
            RefreshStyle();
        }
    }

    public WordStop WordStop { get; set; } = OperatingSystem.IsMacOS() ? WordStop.End : WordStop.Start;

    /// <summary>What a line comment starts with in this document's language, or null for none.</summary>
    public string? LineCommentPrefix { get; set; } = "//";

    /// <summary>What a block comment starts and ends with in this document's language, or null for none. A language with
    /// no line comment comments a line with it.</summary>
    public (string Start, string End)? BlockComment { get; set; } = ("/*", "*/");

    /// <summary>Which markup this document's language is, whose tags Enter's indentation follows; none for code.</summary>
    public MarkupKind Markup { get; set; }

    public bool IsReadOnly { get; set; }

    /// <summary>Whether the gutter shows line numbers; a console's output goes without.</summary>
    public bool ShowLineNumbers { get; set; } = true;

    /// <summary>Whether the scroll bars show when there is more to see; a few lines shown as a picture of a place go
    /// without, a long line cut at the edge.</summary>
    public bool ShowScrollBars { get; set; } = true;

    /// <summary>Scrolls so the last line sits at the bottom of the view, as output follows its end; not yet laid out (a
    /// console written to while its window is hidden), once it is.</summary>
    public void ScrollToEnd()
    {
        if (Bounds.Height <= 0)
        {
            pendingEnd = true;
            return;
        }
        // Output that fits stays at its top; longer output ends clear of the sideways bar drawn over the view's foot.
        double height = RowCount * metrics.LineHeight;
        double bottom = height <= Viewport.Height ? 0 : height - Viewport.Height + horizontal.DesiredSize.Height;
        ScrollTo(new Vector(0, bottom));
    }

    /// <summary>When set, the editor resolves its own key presses; an IDE leaves it null and sends
    /// actions in through <see cref="TryExecute"/> from one window-wide keymap.</summary>
    public Keymap? Keymap
    {
        get => ownKeys?.Keymap;
        set => ownKeys = value is null ? null : new KeymapDispatcher(value);
    }

    public TextDocument Document
    {
        get => document;
        set
        {
            document.ChangedFirst -= OnChangedFirst;
            document.Changed -= OnDocumentChanged;
            document = value;
            document.ChangedFirst += OnChangedFirst;
            document.Changed += OnDocumentChanged;
            lineShape++;
            // A template's places, a composition, a GoTo waiting for layout and the folds are offsets in the text left behind.
            EndSnippet();
            CancelChoices();
            pendingGoTo = null;
            usages.Clear();
            UsagesKept = false;
            inlineChanges.Clear();
            highlights.Clear();
            hovered.Release();
            pressed.Release();
            HideFoldPreview();
            folds = [];
            EndComposition();
            selections = SelectionSet.Single(0);
            caretsText = value.Snapshot;
            leaning = [];
            wrapLeaning = [];
            nextWrapLeaning = null;
            wraps = null;
            rowMap = null;
            scroll = default;
            widestLine = 0;
            measuredWidth = 0;
            ForgetLines();
            InvalidateArrange();
            InvalidateVisual();
            FollowInputMethod();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IHighlighter? Highlighter
    {
        get => highlighter;
        set
        {
            if (highlighter is not null)
            {
                highlighter.Changed -= OnHighlighterChanged;
            }
            highlighter = value;
            if (highlighter is not null)
            {
                highlighter.Changed += OnHighlighterChanged;
            }
            colouring++;
            InvalidateVisual();
        }
    }

    public SelectionSet Selections
    {
        get => selections;
        set => SetSelections(value, reveal: true);
    }

    /// <summary>The primary caret's line and column, both from zero.</summary>
    public TextPosition CaretPosition => document.Snapshot.GetPosition(selections.Primary.Caret);

    public Vector ScrollOffset => scroll;

    public double LineHeight => metrics.LineHeight;

    private Typeface Typeface => new(FontFamily);

    private double GutterWidth => ShowLineNumbers
        ? annotationWidth + GutterPadLeft + Math.Max(2, Digits(document.Snapshot.LineCount)) * metrics.CharWidth + GutterPadRight + FoldWidth
        : 4;

    private double TextLeft => GutterWidth + TextPadLeft;

    private Size Viewport => new(Math.Max(0, Bounds.Width - GutterWidth), Bounds.Height);

    /// <summary>Puts the primary caret at a line and column (from zero) and scrolls it into the middle
    /// of the view, as opening a file at a position does; never past where the last line stands at the view's foot, so a
    /// file that fits the view shows from its first line.</summary>
    public void GoTo(TextPosition position)
    {
        int offset = document.Snapshot.GetOffset(position);
        SetSelections(SelectionSet.Single(offset), reveal: false);
        if (Bounds.Height <= 0)
        {
            // Not laid out yet, so there is no view to centre in; the first arrange does it.
            pendingGoTo = offset;
            return;
        }
        double y = RowOfOffset(document.Snapshot, offset, LeansBack(offset)).Row * metrics.LineHeight;
        // A text that fits stays at its top, its first line whole under what stands above it (a diff's header).
        bool fits = RowCount * metrics.LineHeight <= Viewport.Height - horizontal.DesiredSize.Height;
        double lastAtFoot = Math.Max(0, RowCount * metrics.LineHeight - Viewport.Height);
        ScrollTo(new Vector(scroll.X, fits ? 0 : Math.Min(y - Viewport.Height / 3, lastAtFoot)));
        Reveal(offset);
    }

    /// <summary>Shows a document with the carets and scroll it was left at, as switching back to a
    /// tab does, its lines wrapped or not as they were (the scroll is in its rows as they were).</summary>
    public void Show(TextDocument shown, SelectionSet carets, Vector offset, bool wrapped = false)
    {
        Document = shown;
        softWrap = wrapped;
        selections = carets.Clamp(shown.Snapshot.Length);
        caretsText = shown.Snapshot;
        leaning = [];
        wrapLeaning = [];
        // Kept for rows the text may no longer have (its file reloaded shorter while its tab was hidden or closed).
        scroll = new Vector(offset.X, WithinRows(offset.Y));
        UpdateScrollBars();
        InvalidateVisual();
        FollowInputMethod();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets go of the text and colours shown, whose changes reach this editor no more, and of all drawn from them,
    /// leaving an empty text until another is shown: a split or a diff that closed is then held by nothing it showed, and
    /// an edit to the file no longer maps its carets or redraws it.</summary>
    public void Detach()
    {
        blink.Stop();
        hover?.Stop();
        Highlighter = null;
        Document = new TextDocument();
        SetMarkers([]);
        SetLineMarks([]);
        SetChangeMarks([]);
        SetInlineChanges([]);
        SetBookmarks([]);
        SetGutterAnnotations([]);
        SetTestMarks([]);
        SetRunMarks([]);
        SetColourMarks([]);
        SetLineAnnotations([]);
        SetInlayHints([]);
        SetFoldRegions([]);
        SetHighlights([], -1);
        SetUsageHighlights([]);
        foldLines = null;
        stripeBuild = null;
        stripeDrawn = default;
    }

    private readonly AnchoredList<TextRange> highlights = new(r => r, (_, range) => range);

    /// <summary>Ranges a search found (sorted), drawn behind the text, the current one stronger; they move with edits until
    /// the search sets them again.</summary>
    public IReadOnlyList<TextRange> Highlights => highlights.Items;

    public int CurrentHighlight { get; private set; } = -1;

    public void SetHighlights(IReadOnlyList<TextRange> ranges, int current)
    {
        highlights.Set(document, ranges);
        CurrentHighlight = current;
        InvalidateVisual();
    }

    /// <summary>Selects a range and scrolls it into view, as finding it does.</summary>
    public void Select(TextRange range)
    {
        SetSelections(SelectionSet.Single(new Selection(range.Start, range.End)), reveal: true);
    }

    /// <summary>Where an offset's caret would stand, as the top of its row, in this control's
    /// coordinates: what a popup anchors to, and where a click lands on that offset.</summary>
    public Point PointOf(int offset)
    {
        (int row, double x) = Place(document.Snapshot, offset, LeansBack(offset));
        return new Point(TextLeft - scroll.X + x, (row * metrics.LineHeight) - scroll.Y);
    }

    /// <summary>How many rows the text takes on screen: its lines less the folded ones, each wrapped line counted for every
    /// row soft wrap makes of it.</summary>
    public int RowsShown => RowCount;

    /// <summary>The first and last lines (from zero) the rows on screen show, for a host that works only on what is seen.</summary>
    public (int First, int Last) LinesShown
    {
        get
        {
            int count = document.Snapshot.LineCount;
            int firstRow = Math.Max(0, (int)(scroll.Y / metrics.LineHeight));
            int lastRow = Math.Max(firstRow, (int)((scroll.Y + Math.Max(Bounds.Height, metrics.LineHeight)) / metrics.LineHeight));
            RowMap rows = Rows;
            return (Math.Clamp(rows.LineOfRow(firstRow).Line, 0, Math.Max(0, count - 1)), Math.Clamp(rows.LineOfRow(lastRow).Line, 0, Math.Max(0, count - 1)));
        }
    }

    public void ScrollTo(Vector offset)
    {
        double maxY = MaxScrollY;
        double maxX = MaxScrollX;
        Vector clamped = new(Math.Clamp(offset.X, 0, maxX), Math.Clamp(offset.Y, 0, maxY));
        if (clamped == scroll)
        {
            return;
        }
        scroll = clamped;
        HideFoldPreview();
        UpdateScrollBars();
        InvalidateVisual();
        FollowInputMethod();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        vertical.Measure(availableSize);
        horizontal.Measure(availableSize);
        double width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double barWidth = vertical.DesiredSize.Width;
        double barHeight = horizontal.DesiredSize.Height;
        Rect bar = new(finalSize.Width - barWidth, 0, barWidth, Math.Max(0, finalSize.Height - barHeight));
        vertical.Arrange(bar);
        ArrangeStripe(bar);
        horizontal.Arrange(new Rect(GutterWidth, finalSize.Height - barHeight, Math.Max(0, finalSize.Width - GutterWidth - barWidth), barHeight));
        UpdateScrollBars();
        if (pendingEnd && finalSize.Height > 0)
        {
            pendingEnd = false;
            Dispatcher.UIThread.Post(ScrollToEnd);
        }
        if (pendingGoTo is { } offset)
        {
            pendingGoTo = null;
            Dispatcher.UIThread.Post(() => GoTo(document.Snapshot.GetPosition(Math.Min(offset, document.Snapshot.Length))));
        }
        // The gutter's width, and with it the caret's x, follows the text's line count and the font.
        FollowInputMethod();
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty || change.Property == LineSpacingProperty || change.Property == FontFamilyProperty)
        {
            // A zoom keeps the line at the top of the view there.
            KeepTopLine(() =>
            {
                metrics = Metrics.Measure(Typeface, FontSize, LineSpacing);
                RefreshStyle();
            });
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshStyle();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        RestartBlink();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        blink.Stop();
        ownKeys?.Reset();
        document.BreakUndoGroup();
        InvalidateVisual();
    }

    /// <summary>A command would have made more carets than <see cref="SelectionSet.MaxCount"/> and made that many at most,
    /// or none: the host says so.</summary>
    public event EventHandler? CaretLimitReached;

    private void SetSelections(SelectionSet value, bool reveal)
    {
        if (value.Count > SelectionSet.MaxCount)
        {
            // From anywhere else (a host's set, a column drag): the most the editor keeps, around the primary.
            int from = Math.Clamp(value.PrimaryIndex - (SelectionSet.MaxCount / 2), 0, value.Count - SelectionSet.MaxCount);
            value = SelectionSet.Create([.. value.Items.Skip(from).Take(SelectionSet.MaxCount)], value.PrimaryIndex - from);
            CaretLimitReached?.Invoke(this, EventArgs.Empty);
        }
        SelectionSet clamped = value.Clamp(document.Snapshot.Length);
        leaning = nextLeaning ?? [];
        nextLeaning = null;
        wrapLeaning = nextWrapLeaning ?? [];
        nextWrapLeaning = null;
        UnfoldAroundCarets(clamped);
        bool changed = !clamped.SameAs(selections) || clamped.PrimaryIndex != selections.PrimaryIndex;
        selections = clamped;
        caretsText = document.Snapshot;
        CheckSnippet();
        if (reveal)
        {
            Reveal(selections.Primary.Caret);
        }
        RestartBlink();
        InvalidateVisual();
        FollowInputMethod();
        if (changed)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Scrolls just enough to show <paramref name="offset"/>, keeping a few columns of room
    /// beside it.</summary>
    private void Reveal(int offset)
    {
        if (Bounds.Height <= 0)
        {
            return;
        }
        TextSnapshot text = document.Snapshot;
        (int row, double caretX) = Place(text, offset, LeansBack(offset));
        double top = row * metrics.LineHeight;
        double y = scroll.Y;
        Size viewport = Viewport;
        double usable = viewport.Height - horizontal.DesiredSize.Height;
        if (top < y)
        {
            y = top;
        }
        else if (top + metrics.LineHeight > y + usable)
        {
            y = top + metrics.LineHeight - usable;
        }
        double x = scroll.X;
        double room = metrics.CharWidth * 4;
        if (caretX - room < x)
        {
            x = Math.Max(0, caretX - room);
        }
        else if (caretX + room > x + viewport.Width - TextPadLeft - vertical.DesiredSize.Width)
        {
            x = caretX + room - (viewport.Width - TextPadLeft - vertical.DesiredSize.Width);
        }
        widestLine = Math.Max(widestLine, caretX);
        ScrollTo(new Vector(x, y));
    }

    private void RestartBlink()
    {
        caretOn = true;
        if (IsFocused)
        {
            blink.Stop();
            blink.Start();
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        FollowLineShape(e);
        // What was offered to choose among is the text left behind.
        CancelChoices();
        FollowUsages();
        FollowLinkEdits();
        HideFoldPreview();
        if (!editing)
        {
            leaning = [];
            wrapLeaning = [];
            selections = TextEditing.MapThrough(selections, e.Edits).Clamp(e.After.Length);
            caretsText = e.After;
            FollowInputMethod();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            // Fewer rows may leave the view past the last (another split deleting, the file reloaded shorter).
            ScrollWithinRows();
        }
        widestStale = true;
        InvalidateArrange();
        InvalidateVisual();
    }

    private void OnHighlighterChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Recolour);

    /// <summary>New colours for the same text: the lines are formatted again, each found again if its colours stayed. When
    /// the lines drawn keep every colour they have (a parse that finds what a key carried them to), nothing is drawn again
    /// but a bracket pair the parse told anew.</summary>
    private void Recolour()
    {
        if (BracesChanged())
        {
            InvalidateVisual();
        }
        if (views.Keep(document.Snapshot, Colours))
        {
            return;
        }
        colouring++;
        InvalidateVisual();
    }

    private void OnScrollBarChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (updatingBars || e.Property != RangeBase.ValueProperty)
        {
            return;
        }
        ScrollTo(sender == vertical ? new Vector(scroll.X, vertical.Value) : new Vector(horizontal.Value, scroll.Y));
    }

    private void UpdateScrollBars()
    {
        updatingBars = true;
        Size viewport = Viewport;
        double maxY = MaxScrollY;
        vertical.Maximum = maxY;
        vertical.ViewportSize = viewport.Height;
        vertical.LargeChange = viewport.Height;
        vertical.SmallChange = metrics.LineHeight;
        vertical.Value = scroll.Y;
        // The stripe is its track, so it stays with nothing to scroll; with soft wrap too, so the wrap width never shifts.
        vertical.IsVisible = ShowScrollBars && (showErrorStripe || softWrap || RowCount * metrics.LineHeight > viewport.Height);
        double maxX = MaxScrollX;
        horizontal.Maximum = maxX;
        horizontal.ViewportSize = viewport.Width;
        horizontal.LargeChange = viewport.Width;
        horizontal.SmallChange = metrics.CharWidth * 4;
        horizontal.Value = scroll.X;
        horizontal.IsVisible = ShowScrollBars && maxX > 0;
        updatingBars = false;
    }

    /// <summary>How far the view scrolls down: to where the last row stands at its top.</summary>
    private double MaxScrollY => Math.Max(0, (RowCount - 1) * metrics.LineHeight);

    /// <summary>A scroll's height brought within the rows: from past the last, to where the last stands at the view's foot,
    /// so as many show as fit; as it is while soft wrap waits for the width that makes them.</summary>
    private double WithinRows(double y)
    {
        double max = softWrap && Wraps is null ? y : MaxScrollY;
        return y <= max ? y : Math.Min(max, Math.Max(0, (RowCount * metrics.LineHeight) - Viewport.Height));
    }

    /// <summary>Brings the view back within the rows once they are fewer (a shorter text, lines folded): past the last row
    /// it would draw nothing, not even the gutter, until scrolled.</summary>
    private void ScrollWithinRows()
    {
        double y = WithinRows(scroll.Y);
        if (y < scroll.Y)
        {
            ScrollTo(new Vector(scroll.X, y));
        }
    }

    /// <summary>How far the view scrolls sideways: to the widest line drawn and a little more, or nowhere while the lines
    /// wrap to the view.</summary>
    private double MaxScrollX => softWrap && Wraps is not null ? 0 : Math.Max(0, widestLine + TextPadLeft + metrics.CharWidth * 4 - Viewport.Width);

    private void RefreshStyle()
    {
        // The font and the tab are what the wraps measured with.
        look++;
        palette = Palette.From(this);
        questionRun = null;
        hintRun = null;
        runProperties.Clear();
        numberRuns = (
            RunWith(palette.LineNumberCurrent),
            RunWith(palette.LineNumber));
        annotationRuns = (ItalicRunWith(palette.InlineValue), ItalicRunWith(palette.Error));
        ForgetLines();
        // The stripe and the counts paint with the palette too, so they are drawn again with the next frame.
        stripeDrawn = default;
        InvalidateArrange();
        InvalidateVisual();
    }

    private static int Digits(int value) => value < 10 ? 1 : (int)Math.Floor(Math.Log10(value)) + 1;

    /// <summary>The font's measures at a size: the height of a line and the width of a character,
    /// both whole pixels so lines and columns land on the pixel grid.</summary>
    private readonly record struct Metrics(double LineHeight, double CharWidth, double FontSize)
    {
        public static Metrics Measure(Typeface typeface, double fontSize, double spacing)
        {
            GenericTextRunProperties props = new(typeface, fontSize);
            using TextLine probe = TextFormatter.Current.FormatLine(
                new SingleRun("MMMMMMMMMM", props), 0, double.PositiveInfinity, new LineProperties(props, 4))!;
            return new Metrics(Math.Ceiling(probe.Height * spacing), probe.WidthIncludingTrailingWhitespace / 10, fontSize);
        }
    }
}
