using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A column of the gutter the host fills, which the editor sets beside its line numbers without knowing what it
/// shows: it asks the column's width, has it draw the lines on screen and tint the text behind them, and offers it the
/// presses on the gutter and the drag of one it took (<see cref="CodeEditor.SetGutterColumns"/>). The numbers and the
/// folds' chevrons are the editor's own.</summary>
public interface IGutterColumn
{
    /// <summary>Raised when what it shows changed, its width perhaps with it: the editor draws it again.</summary>
    event EventHandler? Changed;

    /// <summary>Its width at the editor's font, zero while it has nothing to show.</summary>
    double Width(GutterFont font);

    /// <summary>Draws what it shows beside the lines on screen, in its strip of the gutter.</summary>
    void Draw(GutterFrame frame);

    /// <summary>A press on the gutter at a line, offered to each column in turn until one takes it: what this one does
    /// with it.</summary>
    GutterAnswer Press(GutterPress press);

    /// <summary>The press it took to drag is over a line (from zero).</summary>
    void Drag(int line)
    {
    }

    /// <summary>The press it took to drag let go: whether that did something, or was a click after all, which the columns
    /// after it and the host's <see cref="CodeEditor.GutterClicked"/> then hear.</summary>
    bool Drop(GutterPress press) => false;

    /// <summary>The tints it lays behind the text of the lines on screen.</summary>
    void Tint(LineTints tints)
    {
    }
}

/// <summary>What a column did with a press on the gutter.</summary>
public enum GutterAnswer
{
    /// <summary>Not its own: the press goes on to the next column, and past the last to the host.</summary>
    Passed,

    /// <summary>Taken, and done with.</summary>
    Taken,

    /// <summary>Taken to drag: the pointer's moves until it lets go are the column's.</summary>
    Dragging,
}

/// <summary>The editor's font, as a column measures itself by it.</summary>
public readonly record struct GutterFont(double CharWidth, double LineHeight);

/// <summary>A press on a line's number (from zero), with the keys held and where it was (in the editor's coordinates);
/// <see cref="Context"/> for a right-click.</summary>
public readonly record struct GutterClick(int Line, KeyModifiers Modifiers, Point Position, bool Context);

/// <summary>A press as a column hears it: the click, and how far right of the column's left edge it fell, below zero or past
/// <see cref="Width"/> when it fell on another column.</summary>
public readonly record struct GutterPress(GutterClick Click, double X, double Width)
{
    /// <summary>Whether the press fell on this column's own strip.</summary>
    public bool OnStrip => X >= 0 && X < Width;
}

/// <summary>A line on screen as a column draws beside it: the line (from zero) and the top of its first row in the editor.</summary>
public readonly record struct GutterLine(int Line, double Top);

/// <summary>What a column draws with, one frame at a time: the drawing context, its strip of the gutter, the lines on
/// screen, where any line is, and the look's brushes and labels in the editor's font. Kept by the editor from frame to
/// frame, so a frame makes none.</summary>
public sealed class GutterFrame
{
    private readonly CodeEditor editor;
    private readonly List<GutterLine> lines = [];
    private readonly HashSet<int> covered = [];
    private DrawingContext? context;

    internal GutterFrame(CodeEditor editor)
    {
        this.editor = editor;
    }

    public DrawingContext Context => context ?? throw new InvalidOperationException("A gutter frame is read only while it draws.");

    /// <summary>Its strip's left edge and width, in the editor's coordinates.</summary>
    public double Left { get; private set; }

    public double Width { get; private set; }

    /// <summary>Where the line numbers end, in the editor's coordinates.</summary>
    public double NumbersRight { get; private set; }

    /// <summary>The gutter's height.</summary>
    public double Height { get; private set; }

    public double LineHeight { get; private set; }

    public double CharWidth { get; private set; }

    /// <summary>The first and last lines (from zero) with a row on screen.</summary>
    public int First { get; private set; }

    public int Last { get; private set; }

    /// <summary>How many lines the text has.</summary>
    public int LineCount { get; private set; }

    /// <summary>The lines whose first row is on screen, in order: a wrapped line's other rows have none.</summary>
    public ReadOnlySpan<GutterLine> Lines => CollectionsMarshal.AsSpan(lines);

    /// <summary>A line's top and the bottom of its last row, in the editor's coordinates; a folded line's are its fold's.</summary>
    public double TopOf(int line) => editor.GutterTopOf(line);

    public double BottomOf(int line) => editor.GutterBottomOf(line);

    /// <summary>Whether a fold hides a line.</summary>
    public bool IsHidden(int line) => editor.GutterHides(line);

    /// <summary>The look's brush for a token (<see cref="TokenKeys"/>), read once for each look; none for a key it lacks.</summary>
    public IBrush Brush(string key) => editor.GutterBrush(key);

    /// <summary>Text in the editor's font, a scale of its size, in a token's colour, kept from frame to frame.</summary>
    public TextLine Label(string text, string brushKey, double scale = 1, bool bold = false) => editor.GutterLabel(text, brushKey, scale, bold);

    /// <summary>Has a line's number give way to what the column draws over it.</summary>
    public void Cover(int line) => covered.Add(line);

    internal bool Covers(int line) => covered.Contains(line);

    internal List<GutterLine> Shown => lines;

    internal void Begin(DrawingContext drawing, double numbersRight, double height, double lineHeight, double charWidth, int first, int last, int lineCount)
    {
        LineCount = lineCount;
        context = drawing;
        covered.Clear();
        NumbersRight = numbersRight;
        Height = height;
        LineHeight = lineHeight;
        CharWidth = charWidth;
        First = first;
        Last = last;
    }

    internal void Strip(double left, double width)
    {
        Left = left;
        Width = width;
    }

    internal void End()
    {
        context = null;
        lines.Clear();
    }
}

/// <summary>The tints the columns lay behind the text of the lines on screen, one frame at a time: a whole line's, or a
/// stretch of it, drawn only on a line with no tint of its own.</summary>
public sealed class LineTints
{
    private readonly CodeEditor editor;
    private readonly Dictionary<int, IBrush> whole = [];
    private readonly List<(int Line, TextRange Range, IBrush Brush)> parts = [];

    internal LineTints(CodeEditor editor)
    {
        this.editor = editor;
    }

    /// <summary>The first and last lines (from zero) with a row on screen.</summary>
    public int First { get; private set; }

    public int Last { get; private set; }

    /// <summary>The look's brush for a token, as <see cref="GutterFrame.Brush"/> gives it.</summary>
    public IBrush Brush(string key) => editor.GutterBrush(key);

    /// <summary>Tints a whole line, over a tint given it before.</summary>
    public void Line(int line, IBrush brush) => whole[line] = brush;

    /// <summary>Tints a stretch of a line.</summary>
    public void Part(int line, TextRange range, IBrush brush) => parts.Add((line, range, brush));

    internal IBrush? Of(int line) => whole.Count > 0 && whole.TryGetValue(line, out IBrush? brush) ? brush : null;

    internal List<(int Line, TextRange Range, IBrush Brush)> Parts => parts;

    internal void Begin(int first, int last)
    {
        First = first;
        Last = last;
        whole.Clear();
        parts.Clear();
    }
}
