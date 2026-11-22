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

    /// <summary>The line comment's mark of the language edited, which gq keeps at the start of every line it wraps a
    /// comment to; null for Vim's own marks.</summary>
    string? LineComment { get; }

    /// <summary>The width gq wraps at where textwidth is not set (a file's max_line_length); 0 for Vim's 79.</summary>
    int LineLength { get; }

    /// <summary>Why the text refuses every change (a file shown read-only), in the host's words, empty where it has none;
    /// null where the text takes changes.</summary>
    string? Refuses { get; }

    /// <summary>Whether the file keeps a final line break, as Vim's 'eol' says: then the text's final line break ends it,
    /// the empty line after it no line; else a final line break begins an empty last line, which a caret stands on.</summary>
    bool KeepsFinalLineBreak { get; }

    /// <summary>gj and gk: the offset <paramref name="rows"/> rows of the screen below (above when negative), wrapped
    /// lines' rows counted, at the x the offset is drawn at; the offset itself where there is no such row.</summary>
    int RowsAway(TextSnapshot text, int offset, int rows);

    /// <summary>H, M and L: the line of the row <paramref name="count"/> - 1 rows below the top shown whole (above the
    /// bottom one), or of the middle row shown.</summary>
    int LineShown(TextSnapshot text, VimScreenLine at, int count);

    /// <summary>Where the mark <paramref name="name"/> stands in the text now, carried through the edits since it was
    /// set; null where it was never set.</summary>
    int? Mark(char name, TextSnapshot text);

    /// <summary>Sets the mark <paramref name="name"/> at an offset of the text shown, <paramref name="text"/>.</summary>
    void SetMark(char name, int offset, TextSnapshot text);

    /// <summary>Takes the mark <paramref name="name"/> away, as a delete of its line does; nothing where it is not set.</summary>
    void EraseMark(char name);
}

/// <summary>A view of the text with no screen: every line one row and every line shown, for Vim mode's tests and any
/// host without an editor.</summary>
public sealed class VimTextView(IndentOptions indent, string lineEnding = "\n", MarkupKind markup = MarkupKind.None) : IVimView
{
    /// <summary>Each mark's character and the start of its line, carried through edits (<see cref="Follow"/>), and the
    /// column it was set in.</summary>
    private readonly Dictionary<char, (int At, int LineStart, int Column)> marks = [];

    public string? LineComment { get; init; }

    public int LineLength { get; init; }

    public string? Refuses { get; init; }

    public bool KeepsFinalLineBreak { get; init; } = true;

    public IndentOptions Indent { get; } = indent;

    public string LineEnding { get; } = lineEnding;

    public MarkupKind Markup { get; } = markup;

    public int RowsAway(TextSnapshot text, int offset, int rows)
    {
        if (VimMotions.LinesAway(text, offset, rows, KeepsFinalLineBreak) is not { } line)
        {
            return offset;
        }
        return VimMotions.OffsetAtColumn(text, line, TextNavigation.VisualColumn(text, offset, Indent.TabWidth), Indent.TabWidth);
    }

    public int LineShown(TextSnapshot text, VimScreenLine at, int count)
    {
        int last = VimMotions.LastLine(text, KeepsFinalLineBreak);
        return at switch
        {
            VimScreenLine.Top => Math.Min(count - 1, last),
            VimScreenLine.Bottom => Math.Max(last - count + 1, 0),
            _ => last / 2,
        };
    }

    public int? Mark(char name, TextSnapshot text) => marks.TryGetValue(name, out (int At, int LineStart, int Column) held) ? VimMarks.Place(text, held.At, held.LineStart, held.Column) : null;

    public void SetMark(char name, int offset, TextSnapshot text)
    {
        int lineStart = VimMotions.LineOf(text, Math.Min(offset, text.Length)).Start;
        marks[name] = (offset, lineStart, offset - lineStart);
    }

    public void EraseMark(char name) => marks.Remove(name);

    /// <summary>The marks carried through edits made to the text (sorted, in the old text's coordinates), as an editor's
    /// anchors carry them: one in text taken out lands where it was taken.</summary>
    public void Follow(IReadOnlyList<TextEdit> edits)
    {
        foreach (char name in marks.Keys.ToList())
        {
            (int at, int lineStart, int column) = marks[name];
            marks[name] = (TextEditing.MapOffset(at, edits, afterInsertions: false), TextEditing.MapOffset(lineStart, edits, afterInsertions: false), column);
        }
    }

    /// <summary>The marks carried through a plan, those on text it moves (<see cref="EditPlan.Moved"/>) going with it.</summary>
    public void Follow(EditPlan plan)
    {
        Dictionary<char, (int, int, int)> moved = [];
        foreach ((char name, (int at, int lineStart, int column)) in marks)
        {
            if (VimMarks.MovedTo(at, plan.Moved) is { } to)
            {
                moved[name] = (to, VimMarks.MovedTo(lineStart, plan.Moved) ?? to, column);
            }
        }
        Follow(plan.Edits);
        foreach ((char name, (int, int, int) held) in moved)
        {
            marks[name] = held;
        }
    }
}

/// <summary>Where Vim keeps a mark: on the line its character goes to, in the column it was set in whatever is put or
/// taken before it on that line, but carried along when that line is joined to the one above.</summary>
internal static class VimMarks
{
    /// <summary>Where a place in text a move takes goes, null where no move takes it.</summary>
    public static int? MovedTo(int offset, IReadOnlyList<TextMove> moves)
    {
        foreach (TextMove move in moves)
        {
            if (offset >= move.From && offset <= move.From + move.Length)
            {
                return move.To + offset - move.From;
            }
        }
        return null;
    }

    /// <summary>A mark's place from its character's offset and its line's start, each carried through the edits since it
    /// was set (a line's start before text put there), and the column it was set in; never past its line's end.</summary>
    public static int Place(TextSnapshot text, int at, int lineStart, int column)
    {
        at = Math.Min(at, text.Length);
        LineSpan line = VimMotions.LineOf(text, at);
        int from = VimMotions.LineOf(text, Math.Min(lineStart, text.Length)).Line == line.Line ? Math.Min(lineStart, text.Length) : line.Start;
        return Math.Min(from + column, line.End);
    }
}

/// <summary>Words Vim mode has for the status bar, and whether they tell of a failure.</summary>
public sealed record VimMessage(string Text, bool Error);
