using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Which line shown H, M and L go to: counted from the top, the middle, or counted from the bottom.</summary>
public enum VimScreenLine
{
    Top,
    Middle,
    Bottom,
}

/// <summary>What Vim mode asks of the editor it edits in: how it indents and breaks lines, and what is on screen.</summary>
public interface IVimView
{
    IndentOptions Indent { get; }

    string LineEnding { get; }

    MarkupKind Markup { get; }

    /// <summary>gj and gk: the offset <paramref name="rows"/> rows of the screen below (above when negative), wrapped
    /// lines' rows counted, at the x the offset is drawn at; the offset itself where there is no such row.</summary>
    int RowsAway(TextSnapshot text, int offset, int rows);

    /// <summary>H, M and L: the line of the row <paramref name="count"/> - 1 rows below the top shown whole (above the
    /// bottom one), or of the middle row shown.</summary>
    int LineShown(TextSnapshot text, VimScreenLine at, int count);

    /// <summary>Where the mark <paramref name="name"/> stands in the text now, carried through the edits since it was
    /// set; null where it was never set.</summary>
    int? Mark(char name, TextSnapshot text);

    /// <summary>Sets the mark <paramref name="name"/> at an offset of the text shown.</summary>
    void SetMark(char name, int offset);
}

/// <summary>A view of the text with no screen: every line one row and every line shown, for Vim mode's tests and any
/// host without an editor.</summary>
public sealed class VimTextView(IndentOptions indent, string lineEnding = "\n", MarkupKind markup = MarkupKind.None) : IVimView
{
    private readonly Dictionary<char, int> marks = [];

    public IndentOptions Indent { get; } = indent;

    public string LineEnding { get; } = lineEnding;

    public MarkupKind Markup { get; } = markup;

    public int RowsAway(TextSnapshot text, int offset, int rows)
    {
        if (VimMotions.LinesAway(text, offset, rows) is not { } line)
        {
            return offset;
        }
        return VimMotions.OffsetAtColumn(text, line, VimMotions.ColumnOf(text, offset, Indent.TabWidth), Indent.TabWidth);
    }

    public int LineShown(TextSnapshot text, VimScreenLine at, int count)
    {
        int last = VimMotions.LastLine(text);
        return at switch
        {
            VimScreenLine.Top => Math.Min(count - 1, last),
            VimScreenLine.Bottom => Math.Max(last - count + 1, 0),
            _ => last / 2,
        };
    }

    public int? Mark(char name, TextSnapshot text) => marks.TryGetValue(name, out int at) ? Math.Min(at, text.Length) : null;

    public void SetMark(char name, int offset) => marks[name] = offset;
}

/// <summary>Words Vim mode has for the status bar, and whether they tell of a failure.</summary>
public sealed record VimMessage(string Text, bool Error);
