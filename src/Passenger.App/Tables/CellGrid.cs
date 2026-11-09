using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Tables;

/// <summary>A table's cells as a spreadsheet shows them: the columns' headers along the top and the rows' numbers down the
/// side, both held in place while the cells scroll, numbers against their cell's right edge. Only the cells on screen are
/// drawn, each one's text laid out once and kept while it stays there, so a sheet of a million rows scrolls as one of ten.
/// A click chooses a cell, a drag or Shift a range, the header a column and the side a row; a header's edge is dragged to
/// size its column, or double-clicked to fit it, and the arrow at a title's end (shown on the column sorted by, and on the
/// title the pointer is over) sorts by its column, as the header's menu does. Copy writes the chosen cells as
/// tab-separated lines, from the keys or the cells' menu, which a right-click opens on the cell under it (the chosen ones,
/// when it falls among them). A table whose cells may be written (<see cref="Table.IsEditable"/>) is written in place: a
/// cell opens for writing on Enter, F2, a double-click or a character typed, and Enter, Tab or a click away writes it.</summary>
public sealed class CellGrid : Control, IOwnKeys
{
    private const double RowHeight = ListRow.Height;
    private const double Pad = 8;
    private const double FontSize = 12;

    /// <summary>The narrowest and widest a column is made to fit its cells, in characters.</summary>
    private const int FewestCharacters = 4;
    private const int MostCharacters = 48;

    /// <summary>How near a header's edge the pointer sizes its column.</summary>
    private const double EdgeReach = 4;

    /// <summary>The sort's arrow at a title's end, and the room the title leaves it inside the column's padding.</summary>
    private const double ArrowSize = 12;
    private const double ArrowRoom = ArrowSize + 4;

    /// <summary>How many cells are read to fit the columns: enough to see what a column holds, never a whole large file.</summary>
    private const int SampledCells = 200_000;

    /// <summary>How many laid-out texts are kept: some screens' worth, after which they are laid out afresh.</summary>
    private const int KeptTexts = 8192;

    private static readonly Cursor SizeColumn = new(StandardCursorType.SizeWestEast);

    private readonly ScrollBar vertical = new() { Orientation = Orientation.Vertical, AllowAutoHide = true };
    private readonly ScrollBar horizontal = new() { Orientation = Orientation.Horizontal, AllowAutoHide = true };
    private readonly Dictionary<long, FormattedText?> texts = [];
    private Table? table;
    private double[] widths = [];

    /// <summary>Each column's left edge from the first column's, and one past the last column's right.</summary>
    private double[] lefts = [0];

    private double gutter;
    private double charWidth;
    private Typeface plain = new(CodeEditor.CodeFont);
    private Typeface bold = new(CodeEditor.CodeFont, FontStyle.Normal, FontWeight.SemiBold);
    private Vector scroll;
    private bool updatingBars;
    private Palette? palette;
    private (int Row, int Column) caret;
    private (int Row, int Column) anchor;

    /// <summary>A column being sized by its header's edge, or -1.</summary>
    private int sizing = -1;

    private bool choosing;

    /// <summary>The column whose title the pointer is over, which shows its sort's arrow; -1 for none.</summary>
    private int pointed = -1;

    /// <summary>The cells Find matched, by <see cref="Spot"/>, and the one it stands on.</summary>
    private HashSet<long> matched = [];
    private long currentMatch = -1;

    /// <summary>The cell being written, its box over it and the table it was in as the writing began; null while none is.</summary>
    private TextBox? box;
    private Border? boxFrame;
    private (int Row, int Column) written;
    private Table? writtenIn;

    /// <summary>The most rows a cell's box grows over as its text takes more lines.</summary>
    private const int BoxRows = 8;

    public CellGrid()
    {
        Focusable = true;
        ClipToBounds = true;
        VisualChildren.Add(vertical);
        VisualChildren.Add(horizontal);
        LogicalChildren.Add(vertical);
        LogicalChildren.Add(horizontal);
        vertical.PropertyChanged += OnScrollBarChanged;
        horizontal.PropertyChanged += OnScrollBarChanged;
        ActualThemeVariantChanged += (_, _) => Restyle();
        ResourcesChanged += (_, _) => Restyle();
        ContextRequested += (_, e) =>
        {
            ShowMenu(e.TryGetPosition(this, out Point at) ? at : null);
            e.Handled = true;
        };
        charWidth = CodeEditor.MeasureFont(plain, FontSize).CharWidth;
    }

    /// <summary>The cells' menu while it is open.</summary>
    public ContextMenu? Menu { get; private set; }

    /// <summary>A sort asked for, by a column's arrow or the header's menu, for the file to sort its rows so: a column and
    /// which way, or null for the file's order.</summary>
    public event EventHandler<(int Column, bool Descending)?>? SortRequested;

    /// <summary>A cell written, for the file to take: the table shown as its writing began, the cell's row in the file's
    /// order, its column and its new text.</summary>
    public event EventHandler<CellWrite>? CellWritten;

    /// <summary>Rows asked to be taken out of the file (the cells' menu), by their places in the file's order.</summary>
    public event EventHandler<CellWrite>? RowsDeleted;

    /// <summary>The box a cell is being written in, null while none is.</summary>
    public TextBox? Writing => box;

    /// <summary>The cell under the caret opened for writing, holding its text or the character typed in place of it;
    /// false where the cell may not be written.</summary>
    public bool BeginWriting(string? typed = null)
    {
        if (table is null || box is not null || table.RowCount == 0 || !table.IsEditable(caret.Row, caret.Column))
        {
            return false;
        }
        anchor = caret;
        Reveal(caret);
        written = caret;
        writtenIn = table;
        string text = typed ?? table.Cell(caret.Row, caret.Column);
        TextBox writing = new()
        {
            Classes = { StyleClasses.Cell },
            Text = text,
            AcceptsReturn = true,
            FontFamily = plain.FontFamily,
            FontSize = FontSize,
            MinHeight = RowHeight - 1,
            Padding = new Thickness(Pad - 1, 2),
            Name = "CellBox",
        };
        writing.AddHandler(KeyDownEvent, OnWritingKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        writing.LostFocus += (_, _) => EndWriting(keep: true, focus: false);
        Border frame = new() { Child = writing };
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable(TokenKeys.Editor_Background));
        box = writing;
        boxFrame = frame;
        VisualChildren.Add(frame);
        LogicalChildren.Add(frame);
        InvalidateMeasure();
        InvalidateVisual();
        writing.Focus();
        writing.CaretIndex = text.Length;
        return true;
    }

    /// <summary>Enter writes the cell and goes down (across, from a new row's first cell), Tab across, Shift+Enter breaks
    /// its line, and Escape leaves it as it was.</summary>
    private void OnWritingKey(object? sender, KeyEventArgs e)
    {
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        (int Row, int Column) at = written;
        switch (e.Key)
        {
            case Key.Enter when !shift && e.KeyModifiers == KeyModifiers.None:
                bool added = writtenIn?.IsNewRow(at.Row) == true;
                EndWriting(keep: true, focus: true);
                Move(added ? (at.Row, at.Column + 1) : (at.Row + 1, at.Column));
                e.Handled = true;
                break;
            case Key.Tab:
                EndWriting(keep: true, focus: true);
                Move((at.Row, at.Column + (shift ? -1 : 1)));
                e.Handled = true;
                break;
            case Key.Escape:
                EndWriting(keep: false, focus: true);
                e.Handled = true;
                break;
        }
    }

    private void Move((int Row, int Column) to)
    {
        caret = anchor = Clamp(to);
        Reveal(caret);
        InvalidateVisual();
    }

    /// <summary>The cell's writing ended: its text handed on where it changed, unless left; the keys back to the cells when
    /// the writing ended by them.</summary>
    private void EndWriting(bool keep, bool focus)
    {
        if (box is not { } writing)
        {
            return;
        }
        box = null;
        string text = writing.Text ?? string.Empty;
        if (focus)
        {
            Focus();
        }
        VisualChildren.Remove(boxFrame!);
        LogicalChildren.Remove(boxFrame!);
        boxFrame = null;
        InvalidateVisual();
        if (keep && writtenIn is { } at && written.Row < at.RowCount && text != at.Cell(written.Row, written.Column))
        {
            CellWritten?.Invoke(this, new CellWrite(at, [at.FileRow(written.Row)], written.Column, text));
        }
    }

    /// <summary>While a cell is written its box takes Enter, Tab and Escape; else Enter and F2 open a cell for writing.</summary>
    public bool TakesKey(KeyEventArgs e) => box is not null
        ? e.Key is Key.Enter or Key.Tab or Key.Escape
        : e.Key is Key.Enter or Key.F2 && e.KeyModifiers == KeyModifiers.None && table is { RowCount: > 0 } shown && shown.IsEditable(caret.Row, caret.Column);

    /// <summary>The cells' menu, Copy and Select All with their keys, at the pointer, or from the keys (the menu key,
    /// Shift+F10) under the caret's cell; first the sorts of the column it opened on (the pointer's, or the caret's from the
    /// keys), the one in force checked.</summary>
    private void ShowMenu(Point? at)
    {
        if (table is null)
        {
            return;
        }
        bool cells = table.RowCount > 0 && table.ColumnCount > 0;
        ContextMenu menu = new()
        {
            PlacementTarget = this,
            Items =
            {
                ListMenus.Keyed("Copy", EditorActions.Copy, () => Execute(EditorActions.Copy), cells),
                ListMenus.Keyed("Select All", EditorActions.SelectAll, () => Execute(EditorActions.SelectAll), cells),
            },
        };
        if (cells && (at is not { } place || place.X >= gutter))
        {
            int column = at is { } pointer ? ColumnAt(pointer.X - gutter + scroll.X) : caret.Column;
            (string Title, (int Column, bool Descending)? Sort)[] sorts = [("Sort Ascending", (column, false)), ("Sort Descending", (column, true)), ("File Order", null)];
            for (int i = 0; i < sorts.Length; i++)
            {
                (int Column, bool Descending)? sort = sorts[i].Sort;
                menu.Items.Insert(i, MenuChoice.Of(sorts[i].Title, sort == table.Sort, () => SortRequested?.Invoke(this, sort)));
            }
            menu.Items.Insert(sorts.Length, new Separator());
        }
        (int top, _, int bottom, _) = Chosen;
        int[] rows = cells ? [.. Enumerable.Range(top, bottom - top + 1).Where(row => !table.IsNewRow(row)).Select(table.FileRow)] : [];
        if (rows.Length > 0 && table.DeleteTitle(rows.Length) is { } deleting)
        {
            Table from = table;
            menu.Items.Add(new Separator());
            menu.Items.Add(ListMenus.Item(deleting, null, () => RowsDeleted?.Invoke(this, new CellWrite(from, rows, 0, string.Empty))));
        }
        if (at is null)
        {
            Rect body = Body;
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            menu.PlacementRect = new Rect(gutter + lefts[Math.Min(caret.Column, lefts.Length - 1)] - scroll.X, body.Top + (caret.Row * RowHeight) - scroll.Y, 1, RowHeight);
        }
        Menu = menu;
        menu.Closed += (_, _) => Menu = Menu == menu ? null : Menu;
        menu.Open(this);
    }

    /// <summary>The table shown, its columns fitted to their cells; the chosen cell and scroll are kept where they still are.</summary>
    public Table? Table
    {
        get => table;
        set
        {
            // The same rows in another order keep the columns as they were sized.
            bool resorted = value is not null && table is not null && ReferenceEquals(value.Unsorted, table.Unsorted);
            table = value;
            texts.Clear();
            if (!resorted)
            {
                Fit();
            }
            caret = Clamp(caret);
            anchor = Clamp(anchor);
            scroll = ClampScroll(scroll);
            InvalidateArrange();
            InvalidateVisual();
            TableChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the table shown is replaced (read again, another sheet, none).</summary>
    public event EventHandler? TableChanged;

    /// <summary>The cells Find matched, the one at <paramref name="current"/> marked stronger.</summary>
    public void SetMatches(IReadOnlyList<(int Row, int Column)> matches, int current)
    {
        matched = [.. matches.Select(Spot)];
        currentMatch = current >= 0 && current < matches.Count ? Spot(matches[current]) : -1;
        InvalidateVisual();
    }

    private static long Spot((int Row, int Column) cell) => ((long)cell.Row << 32) | (uint)cell.Column;

    /// <summary>The cell chosen, which the keys move from.</summary>
    public (int Row, int Column) Caret => caret;

    /// <summary>Where the chosen range started, its corner opposite the caret.</summary>
    public (int Row, int Column) Anchor => anchor;

    /// <summary>The chosen range's corners: the first row and column and the last.</summary>
    public (int Top, int Left, int Bottom, int Right) Chosen =>
        (Math.Min(anchor.Row, caret.Row), Math.Min(anchor.Column, caret.Column), Math.Max(anchor.Row, caret.Row), Math.Max(anchor.Column, caret.Column));

    public Vector ScrollOffset => scroll;

    /// <summary>Chooses a range, from <paramref name="from"/> to <paramref name="to"/>, and scrolls to its end.</summary>
    public void Choose((int Row, int Column) from, (int Row, int Column) to)
    {
        anchor = Clamp(from);
        caret = Clamp(to);
        Reveal(caret);
        InvalidateVisual();
    }

    public void ScrollTo(Vector offset)
    {
        Vector clamped = ClampScroll(offset);
        if (clamped == scroll)
        {
            return;
        }
        // A cell's box stays over its cell: scrolled away, it is written.
        EndWriting(keep: true, focus: box?.IsKeyboardFocusWithin == true);
        scroll = clamped;
        UpdateScrollBars();
        InvalidateVisual();
    }

    /// <summary>The chosen cells as Excel copies them: a line per row, tabs between cells, a cell with a tab, a line break
    /// or a quote in quotes.</summary>
    public string ChosenText()
    {
        if (table is null || table.RowCount == 0 || table.ColumnCount == 0)
        {
            return string.Empty;
        }
        (int top, int left, int bottom, int right) = Chosen;
        StringBuilder text = new();
        for (int row = top; row <= bottom; row++)
        {
            for (int column = left; column <= right; column++)
            {
                if (column > left)
                {
                    text.Append('\t');
                }
                string cell = table.Cell(row, column);
                if (cell.AsSpan().IndexOfAny("\t\r\n\"") >= 0)
                {
                    text.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                }
                else
                {
                    text.Append(cell);
                }
            }
            text.Append(Environment.NewLine);
        }
        return text.ToString();
    }

    /// <summary>An editing action sent to the focused table: true when it acts.</summary>
    public bool Execute(string action)
    {
        switch (action)
        {
            case EditorActions.Copy:
                IdeErrors.Observe(Copy());
                return true;
            case EditorActions.SelectAll when table is not null:
                Choose((0, 0), (table.RowCount - 1, table.ColumnCount - 1));
                return true;
            case EditorActions.Delete or EditorActions.Backspace:
                // A cell, never the text under the table, is what the keys clear.
                if (table is { RowCount: > 0 } shown && shown.IsEditable(caret.Row, caret.Column) && !shown.IsNewRow(caret.Row) && shown.Cell(caret.Row, caret.Column).Length > 0)
                {
                    CellWritten?.Invoke(this, new CellWrite(shown, [shown.FileRow(caret.Row)], caret.Column, string.Empty));
                }
                return true;
        }
        return false;
    }

    private async Task Copy()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, ChosenText());
        }
    }

    /// <summary>The cells' area: the control less its header, side and scroll bars.</summary>
    private Rect Body
    {
        get
        {
            double right = Bounds.Width - (vertical.IsVisible ? vertical.DesiredSize.Width : 0);
            double bottom = Bounds.Height - (horizontal.IsVisible ? horizontal.DesiredSize.Height : 0);
            return new Rect(gutter, RowHeight, Math.Max(0, right - gutter), Math.Max(0, bottom - RowHeight));
        }
    }

    private double ContentWidth => lefts[^1];

    private double ContentHeight => (table?.RowCount ?? 0) * RowHeight;

    private Vector ClampScroll(Vector offset)
    {
        Rect body = Body;
        return new Vector(
            Math.Clamp(offset.X, 0, Math.Max(0, ContentWidth - body.Width)),
            Math.Clamp(offset.Y, 0, Math.Max(0, ContentHeight - body.Height)));
    }

    private (int Row, int Column) Clamp((int Row, int Column) cell) =>
        table is null ? (0, 0) : (Math.Clamp(cell.Row, 0, Math.Max(0, table.RowCount - 1)), Math.Clamp(cell.Column, 0, Math.Max(0, table.ColumnCount - 1)));

    /// <summary>Each column as wide as the longest of its first cells, within bounds, and the side as wide as the last
    /// row's number.</summary>
    private void Fit()
    {
        int columns = table?.ColumnCount ?? 0;
        widths = new double[columns];
        for (int column = 0; column < columns; column++)
        {
            widths[column] = FittedWidth(column);
        }
        Place();
        int last = table is { RowCount: > 0 } ? table.Unsorted.RowNumber(table.RowCount - 1) : 1;
        gutter = Math.Max(3, last.ToString(CultureInfo.InvariantCulture).Length) * charWidth + 2 * Pad;
    }

    private double FittedWidth(int column)
    {
        if (table is null)
        {
            return 0;
        }
        // The title leaves its sort's arrow room.
        double title = Math.Min(Shown(table.Header(column)).Length, MostCharacters) * charWidth + ArrowRoom;
        int longest = 0;
        int rows = Math.Min(table.RowCount, Math.Max(50, SampledCells / Math.Max(1, table.ColumnCount)));
        for (int row = 0; row < rows && longest < MostCharacters; row++)
        {
            longest = Math.Max(longest, Shown(table.Cell(row, column)).Length);
        }
        return Math.Max(Math.Clamp(longest, FewestCharacters, MostCharacters) * charWidth, title) + 2 * Pad;
    }

    private void Place()
    {
        lefts = new double[widths.Length + 1];
        for (int column = 0; column < widths.Length; column++)
        {
            lefts[column + 1] = lefts[column] + widths[column];
        }
    }

    /// <summary>A cell's text as one line: its first, the rest marked as there.</summary>
    private static string Shown(string text)
    {
        int end = text.AsSpan().IndexOfAny('\r', '\n');
        return end < 0 ? text : text[..end] + " …";
    }

    /// <summary>The column whose span holds an x from the first column's left edge, or the last when past it.</summary>
    private int ColumnAt(double x)
    {
        int column = Sorted.FirstAfter(lefts, x) - 1;
        return Math.Clamp(column, 0, Math.Max(0, widths.Length - 1));
    }

    /// <summary>A header's right edge near a point on the header, the column it sizes; -1 for none, as a table of no
    /// columns has.</summary>
    private int EdgeAt(Point at)
    {
        if (table is null || widths.Length == 0 || at.Y >= RowHeight || at.X < gutter)
        {
            return -1;
        }
        double x = at.X - gutter + scroll.X;
        int column = ColumnAt(x);
        if (Math.Abs(lefts[column + 1] - x) <= EdgeReach)
        {
            return column;
        }
        return column > 0 && Math.Abs(lefts[column] - x) <= EdgeReach ? column - 1 : -1;
    }

    private (int Row, int Column) CellAt(Point at)
    {
        int row = (int)Math.Floor((at.Y - RowHeight + scroll.Y) / RowHeight);
        return Clamp((row, ColumnAt(at.X - gutter + scroll.X)));
    }

    /// <summary>Scrolls as little as shows a whole cell.</summary>
    private void Reveal((int Row, int Column) cell)
    {
        if (table is null || widths.Length == 0)
        {
            return;
        }
        Rect body = Body;
        double top = cell.Row * RowHeight;
        double y = top < scroll.Y ? top : top + RowHeight > scroll.Y + body.Height ? top + RowHeight - body.Height : scroll.Y;
        double left = lefts[cell.Column];
        double right = lefts[cell.Column + 1];
        double x = left < scroll.X || right - left > body.Width ? left : right > scroll.X + body.Width ? right - body.Width : scroll.X;
        ScrollTo(new Vector(x, y));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (table is not null && point.Properties.IsRightButtonPressed)
        {
            ChooseUnder(point.Position);
            return;
        }
        if (table is null || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        Focus();
        e.Handled = true;
        Point at = point.Position;
        if (EdgeAt(at) is int edge and >= 0)
        {
            if (e.ClickCount == 2)
            {
                Resize(edge, FittedWidth(edge));
                return;
            }
            sizing = edge;
            e.Pointer.Capture(this);
            return;
        }
        if (table.RowCount == 0 || table.ColumnCount == 0)
        {
            return;
        }
        (int row, int column) = CellAt(at);
        bool extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (at.Y < RowHeight && at.X >= gutter && OnArrow(at, column))
        {
            SortRequested?.Invoke(this, TableSort.Next(table.Sort, column));
            return;
        }
        if (at.Y < RowHeight && at.X >= gutter)
        {
            anchor = extend ? (0, anchor.Column) : (0, column);
            caret = (table.RowCount - 1, column);
        }
        else if (at.X < gutter && at.Y >= RowHeight)
        {
            anchor = extend ? (anchor.Row, 0) : (row, 0);
            caret = (row, table.ColumnCount - 1);
        }
        else if (at.Y < RowHeight)
        {
            Execute(EditorActions.SelectAll);
            return;
        }
        else
        {
            caret = (row, column);
            if (!extend)
            {
                anchor = caret;
            }
            if (e.ClickCount == 2 && !extend && BeginWriting())
            {
                return;
            }
            choosing = true;
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
    }

    /// <summary>Where a column's sort arrow is drawn on the header, as it is scrolled now.</summary>
    public Rect SortArrow(int column) =>
        new(gutter + lefts[column + 1] - scroll.X - Pad - ArrowSize, Math.Round((RowHeight - ArrowSize) / 2), ArrowSize, ArrowSize);

    /// <summary>Whether a point on the header falls on a column's sort arrow, at its title's end.</summary>
    private bool OnArrow(Point at, int column)
    {
        double right = gutter + lefts[column + 1] - scroll.X;
        return at.X < right - EdgeReach && at.X >= right - Pad - ArrowRoom;
    }

    /// <summary>A right-click's cell chosen, unless it falls among the cells chosen already, which its menu then acts on.</summary>
    private void ChooseUnder(Point at)
    {
        Focus();
        if (table is null || table.RowCount == 0 || table.ColumnCount == 0 || at.X < gutter || at.Y < RowHeight)
        {
            return;
        }
        (int row, int column) = CellAt(at);
        (int top, int left, int bottom, int right) = Chosen;
        if (row < top || row > bottom || column < left || column > right)
        {
            caret = anchor = (row, column);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point at = e.GetPosition(this);
        if (sizing >= 0)
        {
            Resize(sizing, at.X - gutter + scroll.X - lefts[sizing]);
            return;
        }
        if (choosing)
        {
            caret = CellAt(at);
            Reveal(caret);
            InvalidateVisual();
            return;
        }
        Cursor = EdgeAt(at) >= 0 ? SizeColumn : null;
        Point(table is { ColumnCount: > 0 } && at.Y < RowHeight && at.X >= gutter ? ColumnAt(at.X - gutter + scroll.X) : -1);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Point(-1);
    }

    /// <summary>The title the pointer is over, whose arrow then shows.</summary>
    private void Point(int column)
    {
        if (column != pointed)
        {
            pointed = column;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        sizing = -1;
        choosing = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Vector delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Delta.X == 0 ? new Vector(e.Delta.Y, 0) : e.Delta;
        ScrollTo(scroll - new Vector(delta.X * charWidth * 8, delta.Y * RowHeight * 3));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (table is null || table.RowCount == 0 || table.ColumnCount == 0 || box is not null)
        {
            return;
        }
        if (e.Key is Key.Enter or Key.F2 && e.KeyModifiers == KeyModifiers.None && BeginWriting())
        {
            e.Handled = true;
            return;
        }
        KeyModifiers command = Keystroke.Command;
        bool far = e.KeyModifiers.HasFlag(command);
        bool extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        int page = Math.Max(1, (int)(Body.Height / RowHeight) - 1);
        int lastRow = table.RowCount - 1;
        int lastColumn = table.ColumnCount - 1;
        (int Row, int Column)? to = e.Key switch
        {
            Key.C when e.KeyModifiers == command => null,
            Key.A when e.KeyModifiers == command => null,
            Key.Up => (far ? 0 : caret.Row - 1, caret.Column),
            Key.Down => (far ? lastRow : caret.Row + 1, caret.Column),
            Key.Left => (caret.Row, far ? 0 : caret.Column - 1),
            Key.Right => (caret.Row, far ? lastColumn : caret.Column + 1),
            Key.Tab => (caret.Row, caret.Column + (extend ? -1 : 1)),
            Key.PageUp => (caret.Row - page, caret.Column),
            Key.PageDown => (caret.Row + page, caret.Column),
            Key.Home => far ? (0, 0) : (caret.Row, 0),
            Key.End => far ? (lastRow, lastColumn) : (caret.Row, lastColumn),
            _ => null,
        };
        if (e.Key is Key.C or Key.A && e.KeyModifiers == command)
        {
            e.Handled = Execute(e.Key == Key.C ? EditorActions.Copy : EditorActions.SelectAll);
            return;
        }
        if (to is not { } cell)
        {
            return;
        }
        caret = Clamp(cell);
        if (!extend || e.Key == Key.Tab)
        {
            anchor = caret;
        }
        Reveal(caret);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>A character typed on a cell that may be written opens it for writing, the character in place of its text.</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (box is null && !e.Handled && e.Text is { Length: > 0 } typed && !char.IsControl(typed[0]) && BeginWriting(typed))
        {
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    private void Resize(int column, double width)
    {
        widths[column] = Math.Max(charWidth * 2 + Pad, width);
        Place();
        // Texts are laid out to their column's width.
        texts.Clear();
        scroll = ClampScroll(scroll);
        UpdateScrollBars();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        vertical.Measure(availableSize);
        horizontal.Measure(availableSize);
        if (boxFrame is not null && written.Column < widths.Length)
        {
            boxFrame.Measure(new Size(Math.Max(charWidth * 8, widths[written.Column] - 1), RowHeight * BoxRows));
        }
        return new Size(
            double.IsInfinity(availableSize.Width) ? gutter + ContentWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? RowHeight + ContentHeight : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double barWidth = vertical.DesiredSize.Width;
        double barHeight = horizontal.DesiredSize.Height;
        // Whether each bar is needed, given the room the other takes.
        bool tall = RowHeight + ContentHeight > finalSize.Height;
        bool wide = gutter + ContentWidth > finalSize.Width - (tall ? barWidth : 0);
        tall |= RowHeight + ContentHeight > finalSize.Height - (wide ? barHeight : 0);
        vertical.IsVisible = tall;
        horizontal.IsVisible = wide;
        vertical.Arrange(new Rect(finalSize.Width - barWidth, RowHeight, barWidth, Math.Max(0, finalSize.Height - RowHeight - (wide ? barHeight : 0))));
        horizontal.Arrange(new Rect(gutter, finalSize.Height - barHeight, Math.Max(0, finalSize.Width - gutter - (tall ? barWidth : 0)), barHeight));
        scroll = ClampScroll(scroll);
        UpdateScrollBars();
        if (boxFrame is not null && written.Column < widths.Length)
        {
            double width = Math.Max(charWidth * 8, widths[written.Column] - 1);
            boxFrame.Arrange(new Rect(gutter + lefts[written.Column] - scroll.X, RowHeight + (written.Row * RowHeight) - scroll.Y, width,
                Math.Clamp(boxFrame.DesiredSize.Height, RowHeight - 1, RowHeight * BoxRows)));
        }
        return finalSize;
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
        Rect body = Body;
        vertical.Maximum = Math.Max(0, ContentHeight - body.Height);
        vertical.ViewportSize = body.Height;
        vertical.LargeChange = body.Height;
        vertical.SmallChange = RowHeight;
        vertical.Value = scroll.Y;
        horizontal.Maximum = Math.Max(0, ContentWidth - body.Width);
        horizontal.ViewportSize = body.Width;
        horizontal.LargeChange = body.Width;
        horizontal.SmallChange = charWidth * 8;
        horizontal.Value = scroll.X;
        updatingBars = false;
    }

    private void Restyle()
    {
        palette = null;
        texts.Clear();
        FollowFont();
        InvalidateVisual();
    }

    /// <summary>The code font in force taken for the cells, each column fitted again to its width when it changed.</summary>
    private void FollowFont()
    {
        FontFamily font = this.TryFindResource(CodeFonts.Key, out object? found) && found is FontFamily family ? family : CodeEditor.CodeFont;
        if (font.Equals(plain.FontFamily))
        {
            return;
        }
        plain = new Typeface(font);
        bold = new Typeface(font, FontStyle.Normal, FontWeight.SemiBold);
        charWidth = CodeEditor.MeasureFont(plain, FontSize).CharWidth;
        Fit();
        scroll = ClampScroll(scroll);
        InvalidateArrange();
    }

    public override void Render(DrawingContext context)
    {
        Palette colours = palette ??= Palette.From(this);
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(colours.Background, bounds);
        if (table is null)
        {
            return;
        }
        Rect body = Body;
        int rows = table.RowCount;
        int columns = widths.Length;
        int firstRow = Math.Min(rows, (int)(scroll.Y / RowHeight));
        int lastRow = Math.Min(rows - 1, (int)((scroll.Y + body.Height) / RowHeight));
        int firstColumn = ColumnAt(scroll.X);
        int lastColumn = columns == 0 ? -1 : ColumnAt(scroll.X + body.Width);
        (int top, int left, int bottom, int right) = Chosen;
        bool hasCells = rows > 0 && columns > 0;
        IBrush chosen = IsFocused ? colours.Selection : colours.SelectionInactive;
        double X(int column) => body.X + lefts[column] - scroll.X;
        double Y(int row) => body.Y + row * RowHeight - scroll.Y;

        using (context.PushClip(body))
        {
            if (hasCells && bottom >= firstRow && top <= lastRow && right >= firstColumn && left <= lastColumn)
            {
                double y0 = Math.Max(body.Y, Y(top));
                double y1 = Math.Min(body.Bottom, Y(bottom + 1));
                double x0 = Math.Max(body.X, X(left));
                double x1 = Math.Min(body.Right, X(right + 1));
                context.FillRectangle(chosen, new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0)));
            }
            for (int row = firstRow; row <= lastRow; row++)
            {
                double y = Y(row);
                for (int column = firstColumn; column <= lastColumn; column++)
                {
                    DrawCell(context, row, column, new Rect(X(column), y, widths[column], RowHeight), colours);
                }
            }
            // The grid's lines, a pixel each, after the cells so a long text never covers one.
            double gridRight = Math.Min(body.Right, columns == 0 ? body.X : X(columns));
            double gridBottom = Math.Min(body.Bottom, Y(rows));
            for (int row = firstRow; row <= lastRow; row++)
            {
                context.FillRectangle(colours.Grid, new Rect(body.X, Y(row + 1) - 1, gridRight - body.X, 1));
            }
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                context.FillRectangle(colours.Grid, new Rect(X(column + 1) - 1, body.Y, 1, gridBottom - body.Y));
            }
            if (hasCells && caret.Row >= firstRow && caret.Row <= lastRow && caret.Column >= firstColumn && caret.Column <= lastColumn)
            {
                context.DrawRectangle(colours.CaretPen, new Rect(X(caret.Column) + 0.5, Y(caret.Row) + 0.5, widths[caret.Column] - 2, RowHeight - 2));
            }
        }

        Rect header = new(body.X, 0, body.Width, RowHeight);
        context.FillRectangle(colours.Header, new Rect(0, 0, bounds.Width, RowHeight));
        using (context.PushClip(header))
        {
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                Rect place = new(X(column), 0, widths[column], RowHeight);
                if (hasCells && column >= left && column <= right)
                {
                    context.FillRectangle(colours.HeaderChosen, place);
                }
                if (Text(-1, column, place.Width - ArrowRoom, bold, colours.HeaderText) is { } name)
                {
                    DrawText(context, name, place, number: false);
                }
                (int Column, bool Descending)? sort = table.Sort;
                if (hasCells && (sort?.Column == column || pointed == column))
                {
                    bool sorted = sort?.Column == column;
                    DrawArrow(context, sorted && sort!.Value.Descending ? colours.Down : colours.Up, sorted ? colours.HeaderText : colours.RowNumber,
                        new Point(place.Right - Pad - ArrowSize, Math.Round((RowHeight - ArrowSize) / 2)));
                }
                context.FillRectangle(colours.Grid, new Rect(place.Right - 1, 0, 1, RowHeight));
            }
        }

        Rect side = new(0, body.Y, gutter, body.Height);
        context.FillRectangle(colours.Header, new Rect(0, RowHeight, gutter, bounds.Height - RowHeight));
        using (context.PushClip(side))
        {
            for (int row = firstRow; row <= lastRow; row++)
            {
                Rect place = new(0, Y(row), gutter, RowHeight);
                if (hasCells && row >= top && row <= bottom)
                {
                    context.FillRectangle(colours.HeaderChosen, place);
                }
                DrawText(context, Text(row, Gutter, gutter, plain, colours.RowNumber)!, place, number: true);
            }
        }
        context.FillRectangle(colours.Edge, new Rect(0, RowHeight - 1, bounds.Width, 1));
        context.FillRectangle(colours.Edge, new Rect(gutter - 1, 0, 1, bounds.Height));
    }

    private void DrawCell(DrawingContext context, int row, int column, Rect place, Palette colours)
    {
        if (box is not null && written == (row, column))
        {
            return;
        }
        if (matched.Count > 0 && matched.Contains(Spot((row, column))))
        {
            context.FillRectangle(Spot((row, column)) == currentMatch ? colours.SearchCurrent : colours.SearchMatch, place);
        }
        bool number = table!.IsNumber(row, column);
        if (Text(row, column, place.Width, plain, number ? colours.Number : colours.Text) is { } text)
        {
            DrawText(context, text, place, number);
        }
    }

    /// <summary>A sort's arrow (an icon of 16 units) drawn <see cref="ArrowSize"/> wide from a point.</summary>
    private static void DrawArrow(DrawingContext context, Geometry? arrow, IBrush brush, Point at)
    {
        if (arrow is null)
        {
            return;
        }
        using (context.PushTransform(Matrix.CreateScale(ArrowSize / 16, ArrowSize / 16) * Matrix.CreateTranslation(at.X, at.Y)))
        {
            context.DrawGeometry(brush, null, arrow);
        }
    }

    private static void DrawText(DrawingContext context, FormattedText text, Rect place, bool number)
    {
        double x = number ? place.Right - Pad - text.WidthIncludingTrailingWhitespace : place.X + Pad;
        context.DrawText(text, new Point(Math.Max(place.X + Pad, x), place.Y + Math.Round((RowHeight - text.Height) / 2)));
    }

    /// <summary>The column a row's number is kept under among the laid-out texts, past any sheet's last.</summary>
    private const int Gutter = 0xFFFF;

    /// <summary>A cell's text (the header's at row -1, a row's number in <see cref="Gutter"/>) laid out to its column's width, cut short with an ellipsis; kept
    /// while it is drawn, so a frame lays out only the cells newly scrolled in.</summary>
    private FormattedText? Text(int row, int column, double width, Typeface typeface, IBrush brush)
    {
        long key = ((long)(row + 1) << 16) | (uint)column;
        if (texts.TryGetValue(key, out FormattedText? kept))
        {
            return kept;
        }
        if (texts.Count >= KeptTexts)
        {
            texts.Clear();
        }
        string shown = column == Gutter ? table!.RowLabel(row)
            : Shown(row < 0 ? table!.Header(column) : table!.Cell(row, column));
        if (shown.Length == 0)
        {
            // An empty cell lays nothing out, and is kept as nothing.
            texts[key] = null;
            return null;
        }
        FormattedText text = new(shown, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, brush);
        text.MaxTextWidth = Math.Max(1, width - 2 * Pad);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        texts[key] = text;
        return text;
    }

    /// <summary>The theme's brushes the table draws with, read once per look.</summary>
    private sealed record Palette(IBrush Background, IBrush Text, IBrush Number, IBrush Header, IBrush HeaderText, IBrush HeaderChosen,
        IBrush RowNumber, IBrush Grid, IBrush Edge, IBrush Selection, IBrush SelectionInactive, IBrush SearchMatch, IBrush SearchCurrent, IPen CaretPen,
        Geometry? Up, Geometry? Down)
    {
        public static Palette From(Control control)
        {
            IBrush Brush(string key) => Tokens.Brush(control, key);
            return new Palette(
                Brush(TokenKeys.Editor_Background),
                Brush(TokenKeys.Editor_Text),
                Brush(TokenKeys.Syntax_Number),
                Brush(TokenKeys.Panel),
                Brush(TokenKeys.Editor_Text),
                Brush(TokenKeys.Hover),
                Brush(TokenKeys.Editor_LineNumber),
                Brush(TokenKeys.Separator),
                Brush(TokenKeys.Divider),
                Brush(TokenKeys.Editor_Selection),
                Brush(TokenKeys.Editor_SelectionInactive),
                Brush(TokenKeys.Editor_SearchMatch),
                Brush(TokenKeys.Editor_SearchCurrent),
                new Pen(Brush(TokenKeys.Accent), 1),
                control.TryFindResource(IconKeys.ArrowUp, out object? up) ? up as Geometry : null,
                control.TryFindResource(IconKeys.ArrowDown, out object? down) ? down as Geometry : null);
        }
    }
}

/// <summary>Cells written, or rows taken out, in a table as it was shown: the rows by their places in the file's order, the
/// column and the text written (empty for rows taken out).</summary>
public sealed record CellWrite(Table Table, IReadOnlyList<int> Rows, int Column, string Text);
