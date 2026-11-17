using Avalonia;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What a key changed in the text a screen reader reads: <paramref name="Removed"/> characters at
/// <paramref name="Start"/> gave way to <paramref name="Inserted"/>, in the text as the change before it left it.</summary>
public readonly record struct AccessibleTextChange(int Start, int Removed, int Inserted);

/// <summary>The text of a control drawing its own (the code editor, the terminal) as each system's screen-reader bridge
/// reads it, one model for both: the text by range, read as asked and never copied whole; its lines, which end at a line
/// break and include it; the caret and what is selected, and a reader's own choice of them; a character and a word around
/// an offset (<see cref="ReadingUnits"/>); where a range is drawn and what is in view; and what a key changed and where the
/// caret went, as it happens. Offsets count UTF-16 code units from the text's start. A UI-thread object of its
/// control.</summary>
public abstract class AccessibleText
{
    /// <summary>The most of the caret's line <see cref="CaretLine"/> gives, from its start, so a line of a megabyte costs a
    /// reader no more than a long one.</summary>
    public const int MostCaretLine = 4096;

    /// <summary>How far either side of an offset its line is read for a character or a word, so a line of a megabyte
    /// costs a reader's question no more than a short one.</summary>
    private const int Reach = 512;

    /// <summary>The caret's line as last told by <see cref="CaretLineChanged"/>, so a move along the line tells
    /// nothing.</summary>
    private int caretLine;

    private EventHandler? caretLineChanged;

    /// <summary>How many characters the text holds.</summary>
    public abstract int Length { get; }

    /// <summary>How many lines it holds, at least one.</summary>
    public abstract int LineCount { get; }

    /// <summary>A line's start, length and line break, by its number from zero.</summary>
    public abstract LineSpan Line(int line);

    /// <summary>The line an offset is on; the last for the text's end.</summary>
    public abstract int LineOf(int offset);

    /// <summary>The characters of a range, read from the control's own text as asked.</summary>
    public abstract string Read(int start, int length);

    /// <summary>The characters of a range.</summary>
    public string Read(TextRange range) => Read(range.Start, range.Length);

    /// <summary>Where the caret stands: the offset typing goes in at.</summary>
    public abstract int Caret { get; }

    /// <summary>What is selected, a range for each, empty where nothing is; in the text's order.</summary>
    public abstract IReadOnlyList<TextRange> Selected { get; }

    /// <summary>Whether the text refuses every change made in it.</summary>
    public abstract bool IsReadOnly { get; }

    /// <summary>Puts text in a range's place as one change the person can undo, as an assistive tool editing the text
    /// does; refused where the text <see cref="IsReadOnly"/>.</summary>
    public abstract void Replace(TextRange range, string text);

    /// <summary>Where a range is drawn, in the control's coordinates: a rectangle for each row it is shown on, none for
    /// what is out of sight or folded away.</summary>
    public abstract IReadOnlyList<Rect> BoundsOf(TextRange range);

    /// <summary>The offset nearest a point in the control's coordinates.</summary>
    public abstract int OffsetAt(Point point);

    /// <summary>The text in the control's view: from the start of the first line drawn to the end of the last, with any
    /// folded away between.</summary>
    public abstract TextRange Shown { get; }

    /// <summary>Chooses a range as a reader moving the caret does: an empty range takes the caret there where the control
    /// has a caret of its own to move (the editor's), a longer one is selected, and either is scrolled into view.</summary>
    public abstract void Select(TextRange range);

    /// <summary>Scrolls a range into the control's view, its start first, leaving the caret and what is selected as they
    /// are.</summary>
    public abstract void Show(TextRange range);

    /// <summary>Raised as the text changes, once for each piece changed.</summary>
    public event EventHandler<AccessibleTextChange>? Changed;

    /// <summary>The first <paramref name="most"/> characters a change removed, for a reader to echo, asked while
    /// <see cref="Changed"/> is raised for it; null where the control keeps no text it showed before (another document
    /// shown in its place, the terminal's oldest lines let go of).</summary>
    public virtual string? ReadRemoved(AccessibleTextChange change, int most) => null;

    /// <summary>Raised as the caret moves or what is selected changes.</summary>
    public event EventHandler? CaretMoved;

    /// <summary>Raised as the caret comes to another line or a change reaches its line, for a reader that reads only
    /// <see cref="CaretLine"/>; followed only while something listens.</summary>
    public event EventHandler? CaretLineChanged
    {
        add
        {
            if (caretLineChanged is null)
            {
                caretLine = LineOf(Caret);
            }
            caretLineChanged += value;
        }
        remove => caretLineChanged -= value;
    }

    /// <summary>The caret's line without its line break, its first <see cref="MostCaretLine"/> characters.</summary>
    public string CaretLine
    {
        get
        {
            TextRange line = LineAt(Caret);
            return Read(line.Start, Math.Min(line.Length, MostCaretLine));
        }
    }

    /// <summary>The line an offset is on, without its line break.</summary>
    public TextRange LineAt(int offset)
    {
        LineSpan line = Line(LineOf(offset));
        return new TextRange(line.Start, line.Length);
    }

    /// <summary>The character holding an offset (a letter with its marks, an emoji with its joiners); a line break is one
    /// character, and the text's end none.</summary>
    public TextRange CharacterAt(int offset) => Around(offset, ReadingUnits.CharacterAt);

    /// <summary>The word holding an offset: the run of one kind of character (a word's, or other marks) there on its line
    /// with the white space after it, a line's indent a word of its own; a line break is one word, and the text's end
    /// none.</summary>
    public TextRange WordAt(int offset) => Around(offset, ReadingUnits.WordAt);

    protected void OnChanged(int start, int removed, int inserted)
    {
        AccessibleTextChange change = new(start, removed, inserted);
        Changed?.Invoke(this, change);
        if (caretLineChanged is not null)
        {
            TextRange line = LineAt(Caret);
            FollowCaretLine(changed: change.Start <= line.End && change.Start + change.Inserted >= line.Start);
        }
    }

    protected void OnCaretMoved()
    {
        CaretMoved?.Invoke(this, EventArgs.Empty);
        if (caretLineChanged is not null)
        {
            FollowCaretLine(changed: false);
        }
    }

    /// <summary>Tells the caret's line changed as the caret comes to another line, or its line changes.</summary>
    private void FollowCaretLine(bool changed)
    {
        int line = LineOf(Caret);
        if (line == caretLine && !changed)
        {
            return;
        }
        caretLine = line;
        caretLineChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The unit holding an offset as <paramref name="unit"/> finds it in the piece of its line around it.</summary>
    private TextRange Around(int offset, Func<ReadOnlySpan<char>, int, (int Start, int End)> unit)
    {
        int at = Math.Clamp(offset, 0, Length);
        if (at == Length)
        {
            return new TextRange(at, 0);
        }
        LineSpan line = Line(LineOf(at));
        if (at >= line.End)
        {
            return TextRange.FromBounds(line.End, line.EndIncludingBreak);
        }
        int from = Math.Max(line.Start, at - Reach);
        int to = Math.Min(line.End, at + Reach);
        (int start, int end) = unit(Read(from, to - from), at - from);
        return TextRange.FromBounds(from + start, from + end);
    }
}
