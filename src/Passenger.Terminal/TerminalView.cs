using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Fonts.Tables.Cmap;
using Avalonia.Threading;

namespace Passenger.Terminal;

/// <summary>Draws a <see cref="TerminalSession"/>'s screen in the editor's font and sends it the keys: xterm's
/// encodings for every key (the arrows' application forms when a program asks), the clipboard on Cmd+C and Cmd+V
/// (Ctrl+Shift+C and V, or Ctrl+C with a selection, elsewhere), a drag to select and a double-click for a word, the
/// wheel through the scrollback (or to the program, when it asked for the mouse). The size follows the control's, in
/// whole cells. Web addresses and stack frames are drawn as links, opened by a click (one that drags selects instead);
/// file paths are drawn as text, underlined faintly under the pointer and as links while Cmd (Ctrl elsewhere) is held.
/// Cmd+click (Ctrl+click) opens either.</summary>
public sealed class TerminalView : Control
{
    public static readonly FontFamily DefaultFontFamily = new("avares://Passenger.Editor/Fonts#JetBrains Mono");

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalView, double>(nameof(FontSize), 13);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalView, FontFamily>(nameof(FontFamily), DefaultFontFamily);

    public static readonly StyledProperty<bool> CursorBlinkingProperty =
        AvaloniaProperty.Register<TerminalView, bool>(nameof(CursorBlinking));

    public static readonly StyledProperty<CursorShape> CursorStyleProperty =
        AvaloniaProperty.Register<TerminalView, CursorShape>(nameof(CursorStyle), CursorShape.Block);

    private const double Pad = 6;

    private readonly Dictionary<int, IBrush> palette = [];
    private TerminalSession? session;
    private double cellWidth = 8;
    private double cellHeight = 16;
    private CharacterToGlyphMap? glyphs;
    private long? heldTop;
    private (long Line, int Column)? anchor;
    private (long Line, int Column)? active;
    private bool selecting;
    private bool selectedWord;
    private bool reportedPress;
    private bool attached;
    private bool cursorOn = true;
    private readonly DispatcherTimer blink;
    private IBrush foreground = Brushes.Transparent;
    private IBrush background = Brushes.Transparent;
    private IBrush cursor = Brushes.Transparent;
    private IBrush selection = Brushes.Transparent;
    private IBrush link = Brushes.Transparent;
    private IBrush linkUnderline = Brushes.Transparent;

    /// <summary>Where the pointer is over the view, for a link to underline as Cmd or Ctrl goes down; null when it is not.</summary>
    private Point? pointer;

    /// <summary>The keys held at the pointer's last move or key, for the link under it found again as output moves it.</summary>
    private KeyModifiers held;

    /// <summary>The link a plain press began on, which its release opens if nothing was dragged.</summary>
    private TerminalLink? pressedLink;

    public TerminalView()
    {
        // Blinking is continuous visual work, so it must not hold screenshots or background-work waits.
        blink = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) =>
        {
            if (!ShouldBlink)
            {
                RestartBlink();
                return;
            }
            cursorOn = !cursorOn;
            InvalidateVisual();
        });
        blink.Stop();
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ActualThemeVariantChanged += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            Refresh();
            RestartBlink();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            RestartBlink();
        };
        // A theme laid over the application's resources (Settings) changes the brushes, not the variant.
        ResourcesChanged += (_, _) =>
        {
            if (Find("Passenger.Terminal.Background") != background || Find("Passenger.Terminal.Foreground") != foreground)
            {
                Refresh();
            }
        };
        ContextMenu = Menu();
    }

    /// <summary>The terminal's menu: Copy, Paste and Clear Buffer, with their keys.</summary>
    private ContextMenu Menu()
    {
        bool mac = OperatingSystem.IsMacOS();
        KeyModifiers clipboard = mac ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift;
        MenuItem copy = new() { Header = "Copy", InputGesture = new KeyGesture(Key.C, clipboard) };
        copy.Click += (_, _) => _ = CopyAsync();
        MenuItem paste = new() { Header = "Paste", InputGesture = new KeyGesture(Key.V, clipboard) };
        paste.Click += (_, _) => _ = PasteAsync();
        MenuItem clear = new() { Header = "Clear Buffer", InputGesture = mac ? new KeyGesture(Key.K, KeyModifiers.Meta) : null };
        clear.Click += (_, _) => ClearBuffer();
        ContextMenu menu = new() { Items = { copy, paste, new Separator(), clear } };
        menu.Opening += (_, _) => copy.IsEnabled = SelectedText.Length > 0;
        return menu;
    }

    /// <summary>Finds the links in a line's text (the rows a wrapped line takes, joined): where each starts in it, how
    /// long it is, what it opens (which only the host reads), and whether it is hidden until Cmd or Ctrl is held. Null for
    /// a terminal with none.</summary>
    public Func<string, IReadOnlyList<(int Start, int Length, object Target, bool Hidden)>>? Links { get; set; }

    /// <summary>The link under the pointer; null when there is none.</summary>
    public TerminalLink? HoveredLink { get; private set; }

    /// <summary>A link was clicked, or Cmd-clicked (Ctrl-clicked elsewhere): the host opens what it names.</summary>
    public event EventHandler<object>? LinkClicked;

    /// <summary>The key that opens a link with a click: Cmd on the Mac, Ctrl elsewhere.</summary>
    private static KeyModifiers LinkKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Clear Buffer: the scrollback and the screen cleared but for the prompt.</summary>
    public void ClearBuffer()
    {
        if (session is null)
        {
            return;
        }
        session.Screen.ClearBuffer();
        ClearSelection();
        heldTop = null;
        RestartBlink();
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public bool CopyOnSelection { get; set; }

    public bool CursorBlinking
    {
        get => GetValue(CursorBlinkingProperty);
        set => SetValue(CursorBlinkingProperty, value);
    }

    public CursorShape CursorStyle
    {
        get => GetValue(CursorStyleProperty);
        set => SetValue(CursorStyleProperty, value);
    }

    /// <summary>The shell drawn and typed to.</summary>
    public TerminalSession? Session
    {
        get => session;
        set
        {
            if (session is not null)
            {
                session.Output -= OnOutput;
            }
            session = value;
            reportedPress = false;
            if (session is not null)
            {
                session.Output += OnOutput;
                FitSize();
            }
            heldTop = null;
            RestartBlink();
        }
    }

    /// <summary>How many lines the view is scrolled back from the bottom.</summary>
    public int ScrolledBack => session is null ? 0 : (int)(session.Screen.ScreenTop - TopLine);

    /// <summary>The number of the line drawn at the top (<see cref="TerminalScreen.LineAt"/>). Scrolled back, it is the
    /// line the person scrolled to, which holds its place as output arrives; else the screen's top row.</summary>
    public long TopLine
    {
        get
        {
            if (session is null)
            {
                return 0;
            }
            TerminalScreen screen = session.Screen;
            return heldTop is { } held ? Math.Clamp(held, screen.FirstLine, screen.ScreenTop) : screen.ScreenTop;
        }
    }

    /// <summary>The middle of a line's cell, in this control's coordinates: where a click lands on it.</summary>
    public Point PointOf(long line, int column) => new(Pad + ((column + 0.5) * cellWidth), Pad + ((line - TopLine + 0.5) * cellHeight));

    /// <summary>The text chosen by a drag, rows joined where the text wrapped; empty when nothing is chosen.</summary>
    public string SelectedText
    {
        get
        {
            if (session is null || Ordered() is not var ((startLine, startColumn), (endLine, endColumn)))
            {
                return string.Empty;
            }
            TerminalScreen screen = session.Screen;
            StringBuilder text = new();
            // Lines gone from the scrollback's front take what they chose with them.
            for (long line = Math.Max(startLine, screen.FirstLine); line <= Math.Min(endLine, screen.ScreenTop + screen.Rows - 1); line++)
            {
                TerminalLine row = screen.LineAt(line);
                int from = line == startLine ? startColumn : 0;
                int to = line == endLine ? endColumn + 1 : row.Cells.Length;
                text.Append(row.Text(from, to));
                if (line < endLine && !row.Wrapped)
                {
                    text.Append('\n');
                }
            }
            return text.ToString();
        }
    }

    /// <summary>Chooses lines of the scrollback and screen together, by their numbers, as a drag over them does.</summary>
    public void Select(long fromLine, int fromColumn, long toLine, int toColumn)
    {
        anchor = (fromLine, fromColumn);
        active = (toLine, toColumn);
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        anchor = null;
        active = null;
        InvalidateVisual();
    }

    /// <summary>Scrolls the view through the scrollback: up a positive number of lines, down a negative one.</summary>
    public void ScrollLines(int lines)
    {
        if (session is null)
        {
            return;
        }
        TerminalScreen screen = session.Screen;
        long to = Math.Clamp(TopLine - lines, screen.FirstLine, screen.ScreenTop);
        heldTop = to < screen.ScreenTop ? to : null;
        RestartBlink();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty || change.Property == FontFamilyProperty)
        {
            Measure();
            FitSize();
            InvalidateVisual();
        }
        else if (change.Property == CursorBlinkingProperty || change.Property == CursorStyleProperty || change.Property == IsVisibleProperty)
        {
            RestartBlink();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        FitSize();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        RestartBlink();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        RestartBlink();
    }

    private bool ShouldBlink => attached && IsEffectivelyVisible && IsFocused
        && session is { IsRunning: true, Screen.CursorVisible: true }
        && TopLine == session.Screen.ScreenTop && (session.Screen.RequestedCursorBlinking ?? CursorBlinking);

    private void RestartBlink()
    {
        cursorOn = true;
        blink.Stop();
        if (ShouldBlink)
        {
            blink.Start();
        }
        InvalidateVisual();
    }

    private void Refresh()
    {
        palette.Clear();
        foreground = Find("Passenger.Terminal.Foreground") ?? Brushes.Transparent;
        background = Find("Passenger.Terminal.Background") ?? Brushes.Transparent;
        cursor = Find("Passenger.Terminal.Cursor") ?? foreground;
        selection = Find("Passenger.Terminal.Selection") ?? Brushes.Transparent;
        link = Find("Passenger.Syntax.Link") ?? Brushes.Transparent;
        linkUnderline = Find("Passenger.Editor.LinkUnderline") ?? link;
        Measure();
        FitSize();
        InvalidateVisual();
    }

    private IBrush? Find(string key) => this.TryFindResource(key, ActualThemeVariant, out object? found) && found is IBrush brush ? brush : null;

    private void Measure()
    {
        FormattedText probe = new("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily), FontSize, Brushes.Transparent);
        cellWidth = probe.WidthIncludingTrailingWhitespace / 10;
        cellHeight = Math.Ceiling(FontSize * 1.25);
        glyphs = new Typeface(FontFamily).GlyphTypeface.CharacterToGlyphMap;
    }

    /// <summary>The screen as many whole cells as fit, the shell told of a change.</summary>
    private void FitSize()
    {
        if (session is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }
        int columns = Math.Max(2, (int)((Bounds.Width - (Pad * 2)) / cellWidth));
        int rows = Math.Max(1, (int)((Bounds.Height - (Pad * 2)) / cellHeight));
        if (columns != session.Screen.Columns)
        {
            // A reflow numbers the lines again: what was chosen, and the place scrolled back to, are other lines now.
            anchor = null;
            active = null;
            heldTop = null;
        }
        session.Resize(columns, rows);
        InvalidateVisual();
    }

    /// <summary>New output shows at the bottom, but for a person reading back through the scrollback, whose top line
    /// is held by its number, so the place holds as lines arrive below it; a program that clears the scrollback leaves
    /// nothing to read back through, and the view follows the output again.</summary>
    private void OnOutput(object? sender, EventArgs e)
    {
        if (session is { Screen.Scrollback.Count: 0 })
        {
            heldTop = null;
        }
        if (HoveredLink is not null)
        {
            HoverLink(held);
        }
        RestartBlink();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(background, new Rect(Bounds.Size));
        if (session is null)
        {
            return;
        }
        TerminalScreen screen = session.Screen;
        Typeface plain = new(FontFamily);
        long first = TopLine;
        ((long Line, int Column) Start, (long Line, int Column) End)? range = Ordered();
        List<TerminalLink> shown = ShownLinks(screen, first);
        for (int row = 0; row < screen.Rows; row++)
        {
            long line = first + row;
            if (line >= screen.ScreenTop + screen.Rows)
            {
                break;
            }
            DrawLine(context, screen.LineAt(line), line, Pad + (row * cellHeight), plain, range, LinkedOn(shown, line, screen.Columns));
            DrawHiddenLink(context, line, Pad + (row * cellHeight), screen.Columns);
        }
        if (first == screen.ScreenTop && screen.CursorVisible)
        {
            DrawCursor(context, screen, plain);
        }
    }

    private void DrawLine(DrawingContext context, TerminalLine line, long index, double y, Typeface plain, ((long Line, int Column), (long Line, int Column))? range,
        List<(int From, int To)>? linked)
    {
        Cell[] cells = line.Cells;
        int column = 0;
        while (column < cells.Length)
        {
            Cell head = cells[column];
            bool chosen = Within(range, index, column);
            bool isLink = Linked(linked, column);
            int end = column + 1;
            bool alone = !InRun(head);
            if (!alone)
            {
                // A run is cells drawn alike in the terminal's font, every glyph of which is a column wide, so it stays on the grid.
                while (end < cells.Length && InRun(cells[end]) && cells[end].Style == head.Style && Within(range, index, end) == chosen && Linked(linked, end) == isLink)
                {
                    end++;
                }
            }
            else
            {
                if (head.Width == 2 && end < cells.Length)
                {
                    end++;
                }
                // A spacing mark (a Devanagari vowel sign) has a column of its own, but is shaped with the letter before it.
                while (end < cells.Length && cells[end].Width == 1 && CharUnicodeInfo.GetUnicodeCategory(cells[end].Rune) == UnicodeCategory.SpacingCombiningMark
                    && cells[end].Style == head.Style && Within(range, index, end) == chosen)
                {
                    end++;
                }
            }
            DrawRun(context, line, column, end, y, plain, chosen, alone, isLink);
            column = end;
        }
    }

    private static bool Linked(List<(int From, int To)>? linked, int column)
    {
        if (linked is null)
        {
            return false;
        }
        foreach ((int from, int to) in linked)
        {
            if (from <= column && column <= to)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a cell is drawn in a run with its neighbours: one code point a column wide that the terminal's
    /// font has. A wide character, a letter with its marks or one another font draws is drawn alone, fitted to its cells.</summary>
    private bool InRun(Cell cell) => cell.Width == 1 && !cell.IsJoined && (cell.Rune < 0x7f || glyphs?.ContainsGlyph(cell.Rune) == true);

    private void DrawRun(DrawingContext context, TerminalLine line, int from, int to, double y, Typeface plain, bool chosen, bool alone, bool isLink)
    {
        Cell[] cells = line.Cells;
        CellStyle style = cells[from].Style;
        bool inverse = style.Flags.HasFlag(CellFlags.Inverse);
        IBrush fore = isLink && !inverse ? link : Colour(style.Fore, foreground);
        IBrush back = Colour(style.Back, background);
        if (inverse)
        {
            (fore, back) = (back, fore);
        }
        double x = Pad + (from * cellWidth);
        Rect area = new(x, y, (to - from) * cellWidth, cellHeight);
        if (chosen)
        {
            context.FillRectangle(selection, area);
        }
        else if (style.Back != CellStyle.Default || inverse)
        {
            context.FillRectangle(back, area);
        }
        StringBuilder text = new();
        for (int i = from; i < to; i++)
        {
            if (cells[i].Width == 0)
            {
                continue;
            }
            text.Append(line.TextOf(i));
        }
        string shown = text.ToString();
        if (style.Flags.HasFlag(CellFlags.Hidden) || shown.Trim().Length == 0)
        {
            return;
        }
        Typeface face = new(plain.FontFamily, style.Flags.HasFlag(CellFlags.Italic) ? FontStyle.Italic : FontStyle.Normal,
            style.Flags.HasFlag(CellFlags.Bold) ? FontWeight.Bold : FontWeight.Normal);
        using (context.PushOpacity(style.Flags.HasFlag(CellFlags.Dim) ? 0.6 : 1))
        {
            if (alone)
            {
                DrawFitted(context, shown, area, face, fore);
            }
            else
            {
                FormattedText formatted = new(shown, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSize, fore);
                context.DrawText(formatted, new Point(x, y + ((cellHeight - formatted.Height) / 2)));
            }
            if (isLink)
            {
                context.DrawLine(new Pen(linkUnderline, 1), new Point(x, y + cellHeight - 2.5), new Point(area.Right, y + cellHeight - 2.5));
            }
            else if (style.Flags.HasFlag(CellFlags.Underline))
            {
                context.DrawLine(new Pen(fore, 1), new Point(x, y + cellHeight - 2), new Point(area.Right, y + cellHeight - 2));
            }
            if (style.Flags.HasFlag(CellFlags.Strike))
            {
                context.DrawLine(new Pen(fore, 1), new Point(x, y + (cellHeight / 2)), new Point(area.Right, y + (cellHeight / 2)));
            }
        }
    }

    /// <summary>The links drawn as links on the rows shown: every one not hidden, and the hidden one under the pointer while
    /// Cmd or Ctrl is held.</summary>
    private List<TerminalLink> ShownLinks(TerminalScreen screen, long first)
    {
        List<TerminalLink> shown = [];
        if (Links is null)
        {
            return shown;
        }
        long last = Math.Min(first + screen.Rows, screen.ScreenTop + screen.Rows) - 1;
        for (long line = first; line <= last; line++)
        {
            List<TerminalLink> around = LinksAround(screen, line, out long end);
            foreach (TerminalLink found in around)
            {
                if (!found.Hidden || (found == HoveredLink && held.HasFlag(LinkKey)))
                {
                    shown.Add(found);
                }
            }
            line = end;
        }
        return shown;
    }

    /// <summary>The columns of a row that links cover.</summary>
    private static List<(int From, int To)>? LinkedOn(List<TerminalLink> links, long line, int columns)
    {
        List<(int From, int To)>? linked = null;
        foreach (TerminalLink each in links)
        {
            if (line >= each.FromLine && line <= each.ToLine)
            {
                (linked ??= []).Add((line == each.FromLine ? each.FromColumn : 0, line == each.ToLine ? each.ToColumn : columns - 1));
            }
        }
        return linked;
    }

    /// <summary>A hidden link under the pointer with no key held: underlined faintly in the text's colour, so a file
    /// path shows that a Cmd+click opens it.</summary>
    private void DrawHiddenLink(DrawingContext context, long line, double y, int columns)
    {
        if (HoveredLink is not { Hidden: true } hovered || held.HasFlag(LinkKey) || line < hovered.FromLine || line > hovered.ToLine)
        {
            return;
        }
        int from = line == hovered.FromLine ? hovered.FromColumn : 0;
        int to = line == hovered.ToLine ? hovered.ToColumn : columns - 1;
        double under = y + cellHeight - 2.5;
        using (context.PushOpacity(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? 0.4 : 0.5))
        {
            context.DrawLine(new Pen(foreground, 1), new Point(Pad + (from * cellWidth), under), new Point(Pad + ((to + 1) * cellWidth), under));
        }
    }

    /// <summary>The link holding the cell under a point, if any.</summary>
    private TerminalLink? LinkAt(Point at)
    {
        if (Links is null || session is null)
        {
            return null;
        }
        TerminalScreen screen = session.Screen;
        int row = (int)Math.Floor((at.Y - Pad) / cellHeight);
        int column = (int)Math.Floor((at.X - Pad) / cellWidth);
        long line = TopLine + row;
        if (row < 0 || row >= screen.Rows || column < 0 || column >= screen.Columns || line >= screen.ScreenTop + screen.Rows)
        {
            return null;
        }
        foreach (TerminalLink found in LinksAround(screen, line, out _))
        {
            bool afterStart = line > found.FromLine || (line == found.FromLine && column >= found.FromColumn);
            bool beforeEnd = line < found.ToLine || (line == found.ToLine && column <= found.ToColumn);
            if (afterStart && beforeEnd)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>The links on the line a row is part of: its text is read with the rows it wrapped from and onto (sixteen
    /// at most each way), so a long address wrapped across them is one link. <paramref name="end"/> is that line's last row.</summary>
    private List<TerminalLink> LinksAround(TerminalScreen screen, long line, out long end)
    {
        const int Rows = 16;
        long last = screen.ScreenTop + screen.Rows - 1;
        long first = line;
        while (first > screen.FirstLine && line - first < Rows && screen.LineAt(first - 1).Wrapped)
        {
            first--;
        }
        end = line;
        while (end < last && end - line < Rows && screen.LineAt(end).Wrapped)
        {
            end++;
        }
        StringBuilder text = new();
        List<(long Line, int From, int To)> cells = [];
        for (long each = first; each <= end; each++)
        {
            TerminalLine shown = screen.LineAt(each);
            int length = each == end ? shown.Length : shown.Cells.Length;
            for (int c = 0; c < length; c++)
            {
                Cell cell = shown.Cells[c];
                if (cell.Width == 0)
                {
                    continue;
                }
                int to = Math.Min(c + cell.Width - 1, shown.Cells.Length - 1);
                foreach (char unit in shown.TextOf(c))
                {
                    text.Append(unit);
                    cells.Add((each, c, to));
                }
            }
        }
        List<TerminalLink> links = [];
        if (text.Length == 0)
        {
            return links;
        }
        foreach ((int start, int count, object target, bool hidden) in Links!(text.ToString()))
        {
            if (count > 0 && start + count <= cells.Count)
            {
                (long fromLine, int fromColumn, _) = cells[start];
                (long toLine, _, int toColumn) = cells[start + count - 1];
                links.Add(new TerminalLink(fromLine, fromColumn, toLine, toColumn, target, hidden));
            }
        }
        return links;
    }

    /// <summary>The link under the pointer takes the hand when a click opens it: always for one drawn as a link, for a
    /// hidden one while Cmd or Ctrl is held.</summary>
    private void HoverLink(KeyModifiers modifiers)
    {
        bool keyed = modifiers.HasFlag(LinkKey) != held.HasFlag(LinkKey);
        held = modifiers;
        TerminalLink? over = pointer is { } at && !selecting ? LinkAt(at) : null;
        if (over == HoveredLink && !(keyed && over is { Hidden: true }))
        {
            return;
        }
        bool hand = over is not null && (!over.Hidden || modifiers.HasFlag(LinkKey));
        Cursor = new Cursor(hand ? StandardCursorType.Hand : StandardCursorType.Ibeam);
        HoveredLink = over;
        InvalidateVisual();
    }

    /// <summary>A grapheme drawn within its cells: another font's glyph (an emoji's, a Devanagari letter's) is made smaller
    /// when it is wider than they are, so it never covers the next, and centred in them.</summary>
    private void DrawFitted(DrawingContext context, string text, Rect cells, Typeface face, IBrush brush)
    {
        FormattedText formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSize, brush);
        double width = formatted.WidthIncludingTrailingWhitespace;
        if (width > cells.Width + 0.5 || formatted.Height > cells.Height * 1.4)
        {
            double scale = Math.Min(cells.Width / width, cells.Height * 1.25 / formatted.Height);
            formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSize * scale, brush);
            width = formatted.WidthIncludingTrailingWhitespace;
        }
        using (context.PushClip(cells))
        {
            context.DrawText(formatted, new Point(cells.X + Math.Max(0, (cells.Width - width) / 2), cells.Y + ((cells.Height - formatted.Height) / 2)));
        }
    }

    private void DrawCursor(DrawingContext context, TerminalScreen screen, Typeface plain)
    {
        int column = Math.Min(screen.CursorColumn, screen.Columns - 1);
        Cell cell = screen.Lines[screen.CursorRow].Cells[column];
        double x = Pad + (column * cellWidth);
        double y = Pad + (screen.CursorRow * cellHeight);
        double width = cellWidth * Math.Max(1, (int)cell.Width);
        if (!IsFocused)
        {
            context.DrawRectangle(null, new Pen(cursor, 1), new Rect(x + 0.5, y + 0.5, width - 1, cellHeight - 1));
            return;
        }
        if (!cursorOn)
        {
            return;
        }
        switch (screen.RequestedCursorShape ?? CursorStyle)
        {
            case CursorShape.Bar:
                context.FillRectangle(cursor, new Rect(x, y, 2, cellHeight));
                break;
            case CursorShape.Underline:
                context.FillRectangle(cursor, new Rect(x, y + cellHeight - 2, width, 2));
                break;
            default:
                context.FillRectangle(cursor, new Rect(x, y, width, cellHeight));
                if (cell.Rune != 0)
                {
                    DrawFitted(context, screen.Lines[screen.CursorRow].TextOf(column), new Rect(x, y, width, cellHeight), plain, background);
                }
                break;
        }
    }

    /// <summary>A cell's colour: the theme's for the sixteen named ones, xterm's 6x6x6 cube and grey ramp for the rest
    /// of the 256, or the 24-bit colour given.</summary>
    private IBrush Colour(int colour, IBrush fallback)
    {
        if (colour == CellStyle.Default)
        {
            return fallback;
        }
        if (palette.TryGetValue(colour, out IBrush? known))
        {
            return known;
        }
        IBrush brush;
        if ((colour & CellStyle.Rgb) != 0)
        {
            brush = new SolidColorBrush(Color.FromRgb((byte)(colour >> 16), (byte)(colour >> 8), (byte)colour));
        }
        else if (colour < 16)
        {
            brush = Find($"Passenger.Terminal.Ansi{colour}") ?? fallback;
        }
        else if (colour < 232)
        {
            int index = colour - 16;
            static byte Level(int step) => (byte)(step == 0 ? 0 : 55 + (step * 40));
            brush = new SolidColorBrush(Color.FromRgb(Level(index / 36), Level(index / 6 % 6), Level(index % 6)));
        }
        else
        {
            byte grey = (byte)(8 + ((colour - 232) * 10));
            brush = new SolidColorBrush(Color.FromRgb(grey, grey, grey));
        }
        palette[colour] = brush;
        return brush;
    }

    private ((long Line, int Column) Start, (long Line, int Column) End)? Ordered()
    {
        if (anchor is not { } a || active is not { } b || a == b)
        {
            return null;
        }
        return (a.Line, a.Column).CompareTo((b.Line, b.Column)) <= 0 ? (a, b) : (b, a);
    }

    private static bool Within(((long Line, int Column) Start, (long Line, int Column) End)? range, long line, int column) =>
        range is { } r && (line, column).CompareTo(r.Start) >= 0 && (line, column).CompareTo(r.End) <= 0;

    /// <summary>The line (by its number) and column under a point.</summary>
    private (long Line, int Column) CellAt(Point at)
    {
        TerminalScreen screen = session!.Screen;
        int row = Math.Clamp((int)((at.Y - Pad) / cellHeight), 0, screen.Rows - 1);
        int column = Math.Clamp((int)((at.X - Pad) / cellWidth), 0, screen.Columns - 1);
        return (TopLine + row, column);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        selectedWord = false;
        reportedPress = false;
        Focus();
        if (session is null)
        {
            return;
        }
        PointerPointProperties pressed = e.GetCurrentPoint(this).Properties;
        if (pressed.IsLeftButtonPressed && e.KeyModifiers.HasFlag(LinkKey) && LinkAt(e.GetPosition(this)) is { } clicked)
        {
            LinkClicked?.Invoke(this, clicked.Target);
            e.Handled = true;
            return;
        }
        pressedLink = pressed.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1 && session.Screen.MouseTracking == MouseTracking.None
            ? LinkAt(e.GetPosition(this)) is { Hidden: false } plain ? plain : null
            : null;
        if (session.Screen.MouseTracking != MouseTracking.None && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            SendMouse(e.GetPosition(this), pressed.IsRightButtonPressed ? 2 : pressed.IsMiddleButtonPressed ? 1 : 0, release: false);
            reportedPress = true;
            e.Handled = true;
            return;
        }
        if (!pressed.IsLeftButtonPressed)
        {
            return;
        }
        (long line, int column) = CellAt(e.GetPosition(this));
        if (e.ClickCount == 2)
        {
            ClearSelection();
            if (e.KeyModifiers == KeyModifiers.None && session.Screen.MouseTracking == MouseTracking.None
                && LinkAt(e.GetPosition(this)) is { Hidden: false })
            {
                e.Handled = true;
                return;
            }
            SelectWord(line, column);
            selectedWord = SelectedText.Length > 0;
            e.Handled = true;
            return;
        }
        anchor = (line, column);
        active = (line, column);
        selecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        pointer = e.GetPosition(this);
        if (selecting && session is not null)
        {
            active = CellAt(e.GetPosition(this));
            InvalidateVisual();
        }
        HoverLink(e.KeyModifiers);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        pointer = null;
        HoverLink(KeyModifiers.None);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        HoverLink(e.KeyModifiers);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        bool copy = e.InitialPressMouseButton == MouseButton.Left && (selectedWord || (selecting && anchor != active));
        if (session is not null && reportedPress)
        {
            SendMouse(e.GetPosition(this), 0, release: true);
        }
        reportedPress = false;
        selecting = false;
        e.Pointer.Capture(null);
        if (pressedLink is { } link && anchor == active && LinkAt(e.GetPosition(this)) == link)
        {
            ClearSelection();
            LinkClicked?.Invoke(this, link.Target);
        }
        pressedLink = null;
        selectedWord = false;
        if (copy && CopyOnSelection)
        {
            _ = CopyAsync();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (session is null)
        {
            return;
        }
        int lines = (int)Math.Round(e.Delta.Y * 3);
        if (lines == 0)
        {
            return;
        }
        TerminalScreen screen = session.Screen;
        if (screen.MouseTracking != MouseTracking.None)
        {
            for (int i = 0; i < Math.Abs(lines); i++)
            {
                SendMouse(e.GetPosition(this), lines > 0 ? 64 : 65, release: false);
            }
        }
        else if (screen.IsAlternate)
        {
            // A full-screen program with no mouse of its own (less, man) scrolls by its arrow keys.
            string arrow = lines > 0 ? "\x1b[A" : "\x1b[B";
            session.Write(string.Concat(Enumerable.Repeat(arrow, Math.Abs(lines))));
        }
        else
        {
            ScrollLines(lines);
        }
        e.Handled = true;
    }

    /// <summary>A mouse report in the form the program asked for: SGR's (1006), else the original's.</summary>
    private void SendMouse(Point at, int button, bool release)
    {
        TerminalScreen screen = session!.Screen;
        int column = Math.Clamp((int)((at.X - Pad) / cellWidth), 0, screen.Columns - 1) + 1;
        int row = Math.Clamp((int)((at.Y - Pad) / cellHeight), 0, screen.Rows - 1) + 1;
        if (screen.SgrMouse)
        {
            session.Write($"\x1b[<{button};{column};{row}{(release ? 'm' : 'M')}");
        }
        else if (column < 224 && row < 224)
        {
            int code = release ? 3 : button;
            session.Write(Encoding.Latin1.GetString([0x1b, (byte)'[', (byte)'M', (byte)(32 + code), (byte)(32 + column), (byte)(32 + row)]));
        }
    }

    private void SelectWord(long line, int column)
    {
        Cell[] cells = session!.Screen.LineAt(line).Cells;
        static bool Word(Cell cell) => cell.Rune != 0 && (char.IsLetterOrDigit(char.ConvertFromUtf32(cell.Rune), 0) || cell.Rune is '_' or '-' or '.' or '/' or '~');
        if (!Word(cells[column]))
        {
            return;
        }
        int from = column;
        int to = column;
        while (from > 0 && Word(cells[from - 1]))
        {
            from--;
        }
        while (to + 1 < cells.Length && Word(cells[to + 1]))
        {
            to++;
        }
        Select(line, from, line, to);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (session is null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        Typed();
        session.Write(e.Text);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (session is null)
        {
            return;
        }
        if (e.Key is Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl)
        {
            // Cmd or Ctrl going down over a link underlines it, as moving the pointer with it held does.
            HoverLink(e.KeyModifiers);
        }
        KeyModifiers mods = e.KeyModifiers;
        bool mac = OperatingSystem.IsMacOS();
        if ((mac && mods == KeyModifiers.Meta && e.Key == Key.C) || (!mac && mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.C)
            || (!mac && mods == KeyModifiers.Control && e.Key == Key.C && SelectedText.Length > 0))
        {
            _ = CopyAsync();
            e.Handled = true;
            return;
        }
        if ((mac && mods == KeyModifiers.Meta && e.Key == Key.V) || (!mac && mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.V)
            || (mods == KeyModifiers.Shift && e.Key == Key.Insert))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }
        if (mac && mods == KeyModifiers.Meta && e.Key == Key.K)
        {
            ClearBuffer();
            e.Handled = true;
            return;
        }
        if (mods == KeyModifiers.Shift && e.Key is Key.PageUp or Key.PageDown)
        {
            ScrollLines((e.Key == Key.PageUp ? 1 : -1) * Math.Max(1, session.Screen.Rows - 1));
            e.Handled = true;
            return;
        }
        if (mac && mods.HasFlag(KeyModifiers.Meta))
        {
            // Cmd's keys are the IDE's and the Mac's, never the shell's.
            return;
        }
        if (IsAltGrTyping(e))
        {
            // Its character comes as text input, which handling the key here would keep from coming.
            return;
        }
        if (Encode(e.Key, mods, session.Screen.ApplicationCursorKeys) is { } sequence)
        {
            Typed();
            session.Write(sequence);
            e.Handled = true;
        }
    }

    private void Typed()
    {
        heldTop = null;
        if (anchor is not null)
        {
            ClearSelection();
        }
        RestartBlink();
    }

    /// <summary>Windows sends AltGr as Ctrl+Alt: a key the layout gives a character to with them (AltGr+Q's @ on a German
    /// layout) is typing, not Ctrl and Alt for the shell.</summary>
    private static bool IsAltGrTyping(KeyEventArgs e) => OperatingSystem.IsWindows()
        && (e.KeyModifiers & ~KeyModifiers.Shift) == (KeyModifiers.Control | KeyModifiers.Alt)
        && e.KeySymbol is { Length: > 0 } symbol && !char.IsControl(symbol[0]) && !char.IsWhiteSpace(symbol[0]);

    /// <summary>xterm's bytes for a key that is not plain text: Enter, the arrows and the keypad's keys, the
    /// function keys, and Ctrl (or Alt, as a leading ESC) with a letter. Null for keys whose text input carries them.</summary>
    private static string? Encode(Key key, KeyModifiers mods, bool applicationCursor)
    {
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool alt = mods.HasFlag(KeyModifiers.Alt) && !OperatingSystem.IsMacOS();
        string? plain = key switch
        {
            Key.Enter => "\r",
            Key.Back => ctrl ? "\x08" : "\x7f",
            Key.Tab => mods.HasFlag(KeyModifiers.Shift) ? "\x1b[Z" : "\t",
            Key.Escape => "\x1b",
            Key.Up => Cursor('A'),
            Key.Down => Cursor('B'),
            Key.Right => Cursor('C'),
            Key.Left => Cursor('D'),
            Key.Home => Cursor('H'),
            Key.End => Cursor('F'),
            Key.Insert => Tilde(2),
            Key.Delete => Tilde(3),
            Key.PageUp => Tilde(5),
            Key.PageDown => Tilde(6),
            Key.F1 => Function('P'),
            Key.F2 => Function('Q'),
            Key.F3 => Function('R'),
            Key.F4 => Function('S'),
            Key.F5 => Tilde(15),
            Key.F6 => Tilde(17),
            Key.F7 => Tilde(18),
            Key.F8 => Tilde(19),
            Key.F9 => Tilde(20),
            Key.F10 => Tilde(21),
            Key.F11 => Tilde(23),
            Key.F12 => Tilde(24),
            _ => null,
        };
        if (plain is not null)
        {
            return alt && key is Key.Enter or Key.Back ? "\x1b" + plain : plain;
        }
        if (ctrl && Control(key) is { } control)
        {
            return alt ? "\x1b" + control : control;
        }
        if (alt && key is >= Key.A and <= Key.Z)
        {
            char letter = (char)('a' + (key - Key.A));
            return "\x1b" + (mods.HasFlag(KeyModifiers.Shift) ? char.ToUpperInvariant(letter) : letter);
        }
        return null;

        int Modifier() => 1 + (mods.HasFlag(KeyModifiers.Shift) ? 1 : 0) + (mods.HasFlag(KeyModifiers.Alt) ? 2 : 0) + (ctrl ? 4 : 0);
        string Cursor(char final) => Modifier() > 1 ? $"\x1b[1;{Modifier()}{final}" : applicationCursor ? $"\x1bO{final}" : $"\x1b[{final}";
        string Tilde(int code) => Modifier() > 1 ? $"\x1b[{code};{Modifier()}~" : $"\x1b[{code}~";
        string Function(char final) => Modifier() > 1 ? $"\x1b[1;{Modifier()}{final}" : $"\x1bO{final}";
    }

    /// <summary>The control character Ctrl makes of a key: Ctrl+A is 1 through Ctrl+Z 26, and the few punctuation keys
    /// terminals give the rest of C0 to.</summary>
    private static string? Control(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)(key - Key.A + 1)).ToString(),
        Key.Space or Key.D2 => "\0",
        Key.OemOpenBrackets or Key.D3 => "\x1b",
        Key.OemPipe or Key.D4 => "\x1c",
        Key.OemCloseBrackets or Key.D5 => "\x1d",
        Key.D6 => "\x1e",
        Key.OemMinus or Key.D7 or Key.OemQuestion => "\x1f",
        Key.D8 => "\x7f",
        _ => null,
    };

    /// <summary>Copies the selection to the clipboard.</summary>
    public async Task CopyAsync()
    {
        if (SelectedText is { Length: > 0 } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>Types the clipboard's text into the shell.</summary>
    public async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text)
        {
            Typed();
            session?.Paste(text);
        }
    }
}

/// <summary>A link on the terminal's lines, from its first cell to its last by line number (<see cref="TerminalScreen.LineAt"/>)
/// and column, what it opens, and whether it is drawn as text until Cmd or Ctrl is held (a file path's).</summary>
public sealed record TerminalLink(long FromLine, int FromColumn, long ToLine, int ToColumn, object Target, bool Hidden);
