using Avalonia;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What a key changed in the text a screen reader reads: <paramref name="Removed"/> characters at
/// <paramref name="Start"/> gave way to <paramref name="Inserted"/>, in the text as the change before it left it.</summary>
public readonly record struct AccessibleTextChange(int Start, int Removed, int Inserted);

/// <summary>The text of a control drawing its own (the code editor, the terminal) as each system's screen-reader bridge
/// reads it, one model for both: the text by range, read as asked and never copied whole; its lines, which end at a line
/// break and include it; the caret and what is selected; a character and a word around an offset
/// (<see cref="ReadingUnits"/>); where a range is drawn; and what a key changed and where the caret went, as it happens.
/// Offsets count UTF-16 code units from the text's start. A UI-thread object of its control.</summary>
public abstract class AccessibleText
{
    /// <summary>How far either side of an offset its line is read for a character or a word, so a line of a megabyte
    /// costs a reader's question no more than a short one.</summary>
    private const int Reach = 512;

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

    /// <summary>Raised as the text changes, once for each piece changed.</summary>
    public event EventHandler<AccessibleTextChange>? Changed;

    /// <summary>Raised as the caret moves or what is selected changes.</summary>
    public event EventHandler? CaretMoved;

    /// <summary>The line an offset is on, without its line break.</summary>
    public TextRange LineAt(int offset)
    {
        LineSpan line = Line(LineOf(offset));
        return new TextRange(line.Start, line.Length);
    }

    /// <summary>The character holding an offset (a letter with its marks, an emoji with its joiners); a line break is one
    /// character, and the text's end none.</summary>
    public TextRange CharacterAt(int offset) => Around(offset, ReadingUnits.CharacterAt);

    /// <summary>The word holding an offset: the run of one kind of character (a word's, white space, or other) there on its
    /// line; a line break is one word, and the text's end none.</summary>
    public TextRange WordAt(int offset) => Around(offset, ReadingUnits.WordAt);

    protected void OnChanged(int start, int removed, int inserted) => Changed?.Invoke(this, new AccessibleTextChange(start, removed, inserted));

    protected void OnCaretMoved() => CaretMoved?.Invoke(this, EventArgs.Empty);

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
