using Passenger.Text;

namespace Passenger.Vcs;

/// <summary>How a diff marks the differences inside changed lines (Highlighting Differences): none (the lines alone),
/// the words or the characters that changed, or no highlighting at all.</summary>
public enum DiffHighlighting
{
    Lines,
    Words,
    Characters,
    None,
}

/// <summary>Each side's changed words, as offsets into its text.</summary>
public sealed record InlineChanges(IReadOnlyList<InlineChange> Left, IReadOnlyList<InlineChange> Right)
{
    public static InlineChanges Empty { get; } = new([], []);
}

/// <summary>A file's text compared with the last commit's: the hunks between them and the change bars they make, kept
/// with both texts, so a popup, a rollback or a diff of that same text acts on them without comparing again. Immutable,
/// and worked out off the window's thread.</summary>
public sealed class FileDiff
{
    /// <summary>After this many changes too big to compare word by word, the rest are shown by their lines alone.</summary>
    private const int MostTooBig = 3;

    private readonly string[] headLines;
    private readonly int[] headStarts;

    private FileDiff(string head, string[] headLines, int[] headStarts, TextSnapshot text, IReadOnlyList<DiffHunk> hunks)
    {
        Head = head;
        this.headLines = headLines;
        this.headStarts = headStarts;
        Text = text;
        Hunks = hunks;
        Marks = [.. hunks.Select(h => h.OldCount == 0 ? new ChangeMark(h.NewStart, h.NewCount, ChangeMarkKind.Added)
            : h.NewCount == 0 ? new ChangeMark(h.NewStart, 0, ChangeMarkKind.Deleted)
            : new ChangeMark(h.NewStart, h.NewCount, ChangeMarkKind.Modified))];
    }

    /// <summary>The last commit's text, empty for a file it does not have.</summary>
    public string Head { get; }

    /// <summary>The file's text the hunks are of.</summary>
    public TextSnapshot Text { get; }

    public IReadOnlyList<DiffHunk> Hunks { get; }

    /// <summary>The change bars for the file's side: lines added, lines modified, and a mark where lines went.</summary>
    public IReadOnlyList<ChangeMark> Marks { get; }

    public static FileDiff Compare(string head, TextSnapshot text) => Compare(head, LineDiff.Lines(head), Starts(head), text);

    /// <summary>Another text of the file compared with the same last commit, whose lines are split already.</summary>
    public FileDiff Compare(TextSnapshot text) => Compare(Head, headLines, headStarts, text);

    private static FileDiff Compare(string head, string[] headLines, int[] headStarts, TextSnapshot text) =>
        new(head, headLines, headStarts, text, LineDiff.Hunks(headLines, LineDiff.Lines(text.ToString())));

    /// <summary>Where each line of a text starts, as a document of it numbers them.</summary>
    private static int[] Starts(string text)
    {
        List<int> starts = [0];
        for (int at = text.IndexOf('\n'); at >= 0; at = text.IndexOf('\n', at + 1))
        {
            starts.Add(at + 1);
        }
        return [.. starts];
    }

    /// <summary>The words (or characters) that differ inside every change with lines on both sides: on the last commit's
    /// side as offsets into <see cref="Head"/>, on the file's into <see cref="Text"/>. None for
    /// <see cref="DiffHighlighting.Lines"/> and <see cref="DiffHighlighting.None"/>. Worked out off the window's thread: its
    /// time is bounded by the words compared, never by how much of them differs.</summary>
    public InlineChanges InnerChanges(DiffHighlighting highlighting)
    {
        List<InlineChange> left = [];
        List<InlineChange> right = [];
        int tooBig = 0;
        foreach (DiffHunk hunk in Hunks)
        {
            if (tooBig >= MostTooBig)
            {
                break;
            }
            if (!Inner(hunk, highlighting, left, right))
            {
                tooBig++;
            }
        }
        return new InlineChanges(left, right);
    }

    /// <summary>The words that differ inside one change: on the last commit's side as offsets into <see cref="Before"/> of
    /// it, on the file's into <see cref="Text"/>.</summary>
    public InlineChanges InnerChanges(DiffHunk hunk, DiffHighlighting highlighting)
    {
        List<InlineChange> left = [];
        List<InlineChange> right = [];
        Inner(hunk, highlighting, left, right, inBefore: true);
        return new InlineChanges(left, right);
    }

    /// <summary>A change's differences inside its lines, added to each side's; false when it was too big to compare.</summary>
    private bool Inner(DiffHunk hunk, DiffHighlighting highlighting, List<InlineChange> left, List<InlineChange> right, bool inBefore = false)
    {
        if (hunk.OldCount == 0 || hunk.NewCount == 0 || highlighting is not (DiffHighlighting.Words or DiffHighlighting.Characters))
        {
            return true;
        }
        string[] before = headLines[hunk.OldStart..(hunk.OldStart + hunk.OldCount)];
        string[] after = [.. Enumerable.Range(hunk.NewStart, hunk.NewCount).Select(Text.GetLineText)];
        string oldText = string.Join('\n', before);
        string newText = string.Join('\n', after);
        IReadOnlyList<InlineDiff>? found = highlighting == DiffHighlighting.Words ? WordDiff.Words(oldText, newText) : WordDiff.Characters(oldText, newText);
        if (found is null)
        {
            return false;
        }
        foreach (InlineDiff change in found)
        {
            ChangeMarkKind kind = change.IsInsertion ? ChangeMarkKind.Added : change.IsDeletion ? ChangeMarkKind.Deleted : ChangeMarkKind.Modified;
            if (!change.IsInsertion)
            {
                left.Add(new InlineChange(inBefore ? TextRange.FromBounds(change.OldStart, change.OldEnd)
                    : TextRange.FromBounds(OldOffset(before, hunk.OldStart, change.OldStart), OldOffset(before, hunk.OldStart, change.OldEnd)), kind));
            }
            if (!change.IsDeletion)
            {
                right.Add(new InlineChange(TextRange.FromBounds(NewOffset(after, hunk.NewStart, change.NewStart), NewOffset(after, hunk.NewStart, change.NewEnd)), kind));
            }
        }
        return true;
    }

    /// <summary>An offset into a change's lines joined by line feeds, as an offset into the last commit's text.</summary>
    private int OldOffset(string[] lines, int first, int offset)
    {
        (int line, int column) = Place(lines, offset);
        return headStarts[first + line] + column;
    }

    /// <summary>An offset into a change's lines joined by line feeds, as an offset into the file's text.</summary>
    private int NewOffset(string[] lines, int first, int offset)
    {
        (int line, int column) = Place(lines, offset);
        return Text.GetLineStart(first + line) + column;
    }

    internal static (int Line, int Column) Place(string[] lines, int offset)
    {
        int line = 0;
        while (line < lines.Length - 1 && offset > lines[line].Length)
        {
            offset -= lines[line].Length + 1;
            line++;
        }
        return (line, offset);
    }

    /// <summary>The change a line of the file is in, where lines went being the lines either side of the place; null
    /// for a line as the last commit has it.</summary>
    public DiffHunk? HunkAt(int line) => Hunks.Cast<DiffHunk?>().FirstOrDefault(h => h!.Value.NewCount == 0
        ? line == h.Value.NewStart || line == h.Value.NewStart - 1
        : line >= h.Value.NewStart && line < h.Value.NewStart + h.Value.NewCount);

    /// <summary>Lines of <see cref="Text"/> (from 0) as the last commit numbers them, for Show History for Selection: each
    /// moved by the changes above it, and a changed one taken as the whole change's lines there. Null when none of them is
    /// in the last commit.</summary>
    public (int First, int Last)? HeadLines(int first, int last)
    {
        int from = Math.Max(0, HeadLine(first, forward: true));
        int to = Math.Min(headLines.Length - 1, HeadLine(last, forward: false));
        return from <= to ? (from, to) : null;
    }

    /// <summary>The last commit's line for a line of the file; for one added since, the committed line after it (or before
    /// it, going back).</summary>
    private int HeadLine(int line, bool forward)
    {
        int shift = 0;
        foreach (DiffHunk hunk in Hunks.OrderBy(h => h.NewStart))
        {
            if (line < hunk.NewStart)
            {
                break;
            }
            if (line < hunk.NewStart + hunk.NewCount)
            {
                return hunk.OldCount == 0
                    ? (forward ? hunk.OldStart : hunk.OldStart - 1)
                    : (forward ? hunk.OldStart : hunk.OldStart + hunk.OldCount - 1);
            }
            shift = hunk.OldStart + hunk.OldCount - (hunk.NewStart + hunk.NewCount);
        }
        return line + shift;
    }

    /// <summary>The lines a change had in the last commit, each but the last ending with <paramref name="lineBreak"/>.</summary>
    public string Before(DiffHunk hunk, string lineBreak = "\n") => string.Join(lineBreak, headLines, hunk.OldStart, hunk.OldCount);

    /// <summary>The edit to <see cref="Text"/> that puts a change's lines back as the last commit has them, keeping the
    /// file's line ending and whether it ends with one.</summary>
    public TextEdit Restore(DiffHunk hunk)
    {
        TextSnapshot text = Text;
        string eol = text.LineCount > 1 && text.GetLine(0).BreakLength == 2 ? "\r\n" : "\n";
        string old = Before(hunk, eol);
        int end = hunk.NewStart + hunk.NewCount;
        if (end < text.LineCount)
        {
            // A line follows the hunk, so every line of it ends with a break.
            int from = text.GetLineStart(hunk.NewStart);
            return new TextEdit(from, text.GetLineStart(end) - from, hunk.OldCount > 0 ? old + eol : string.Empty);
        }
        if (hunk.NewCount == 0)
        {
            return new TextEdit(text.Length, 0, eol + old);
        }
        if (hunk.OldCount == 0 && hunk.NewStart > 0)
        {
            // The last lines were added: they go with the break before them.
            LineSpan before = text.GetLine(hunk.NewStart - 1);
            return new TextEdit(before.Start + before.Length, text.Length - before.Start - before.Length, string.Empty);
        }
        int start = text.GetLineStart(hunk.NewStart);
        return new TextEdit(start, text.Length - start, old);
    }
}
