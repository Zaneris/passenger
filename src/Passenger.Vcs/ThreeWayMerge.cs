using System.Text;
using Passenger.Text;

namespace Passenger.Vcs;

/// <summary>One change of a three-way merge: a stretch of the base's lines and what each side has there, lines from
/// zero, ends exclusive. A side that left the stretch as the base has it has no change there; a stretch both sides changed
/// alike is no conflict, and one they changed otherwise is.</summary>
public sealed record MergeChunk(int BaseStart, int BaseEnd, int LeftStart, int LeftEnd, int RightStart, int RightEnd, bool LeftChanged, bool RightChanged, bool IsConflict)
{
    public bool Changed(MergeSide side) => side == MergeSide.Left ? LeftChanged : RightChanged;

    public int Start(MergeSide side) => side == MergeSide.Left ? LeftStart : RightStart;

    public int End(MergeSide side) => side == MergeSide.Left ? LeftEnd : RightEnd;
}

/// <summary>A side of the merge window: yours on the left, theirs on the right.</summary>
public enum MergeSide
{
    Left,
    Right,
}

/// <summary>Three texts compared: each side's changes against the base they both came from, a change of one side
/// touching a change of the other taken with it as one, a conflict unless they agree; and a conflict tried again word
/// by word, which resolves it when the two sides changed different words (Resolve Simple Conflicts). Any thread may
/// compare.</summary>
public static class ThreeWayMerge
{
    /// <summary>A text's lines as the merge compares them: a last line with no break after it differs from the same line
    /// with one, so a side that adds or takes away the text's last break changes that line, as git's merge has it.</summary>
    public static string[] Compared(string text)
    {
        string[] lines = LineDiff.Lines(text);
        if (lines.Length > 0 && text[^1] != '\n')
        {
            // No line holds a '\n' of its own, so this one is never the same as a line a break ends.
            lines[^1] += "\n";
        }
        return lines;
    }

    /// <summary>The changes between the base's lines and each side's.</summary>
    public static IReadOnlyList<MergeChunk> Chunks(string[] common, string[] left, string[] right)
    {
        IReadOnlyList<DiffHunk> lefts = LineDiff.Hunks(common, left);
        IReadOnlyList<DiffHunk> rights = LineDiff.Hunks(common, right);
        List<MergeChunk> chunks = [];
        int i = 0;
        int j = 0;
        int leftShift = 0;
        int rightShift = 0;
        while (i < lefts.Count || j < rights.Count)
        {
            int start = Math.Min(i < lefts.Count ? lefts[i].OldStart : int.MaxValue, j < rights.Count ? rights[j].OldStart : int.MaxValue);
            int end = start;
            int firstLeft = i;
            int firstRight = j;
            int leftGrowth = 0;
            int rightGrowth = 0;
            bool empty = true;
            // A hunk of either side starting inside the stretch, or where it ends, joins it, as git joins touching changes.
            while (true)
            {
                if (i < lefts.Count && (empty ? lefts[i].OldStart == start : lefts[i].OldStart <= end))
                {
                    end = Math.Max(end, lefts[i].OldStart + lefts[i].OldCount);
                    leftGrowth += lefts[i].NewCount - lefts[i].OldCount;
                    i++;
                }
                else if (j < rights.Count && (empty ? rights[j].OldStart == start : rights[j].OldStart <= end))
                {
                    end = Math.Max(end, rights[j].OldStart + rights[j].OldCount);
                    rightGrowth += rights[j].NewCount - rights[j].OldCount;
                    j++;
                }
                else
                {
                    break;
                }
                empty = false;
            }
            int leftStart = start + leftShift;
            int leftEnd = end + leftShift + leftGrowth;
            int rightStart = start + rightShift;
            int rightEnd = end + rightShift + rightGrowth;
            bool leftChanged = i > firstLeft;
            bool rightChanged = j > firstRight;
            bool conflict = leftChanged && rightChanged
                && !left.AsSpan(leftStart, leftEnd - leftStart).SequenceEqual(right.AsSpan(rightStart, rightEnd - rightStart));
            chunks.Add(new MergeChunk(start, end, leftStart, leftEnd, rightStart, rightEnd, leftChanged, rightChanged, conflict));
            leftShift += leftGrowth;
            rightShift += rightGrowth;
        }
        return chunks;
    }

    /// <summary>A conflict's lines merged word by word (Resolve Simple Conflicts): each side's changes to the base's words
    /// where none of them touches a change of the other side; null where they do.</summary>
    public static string[]? ResolveSimply(string[] common, string[] left, string[] right)
    {
        string[] words = Words(string.Join('\n', common));
        string[] lefts = Words(string.Join('\n', left));
        string[] rights = Words(string.Join('\n', right));
        StringBuilder merged = new();
        int at = 0;
        foreach (MergeChunk chunk in Chunks(words, lefts, rights))
        {
            if (chunk.IsConflict)
            {
                return null;
            }
            merged.AppendJoin(string.Empty, words[at..chunk.BaseStart]);
            merged.AppendJoin(string.Empty, chunk.LeftChanged ? lefts[chunk.LeftStart..chunk.LeftEnd] : rights[chunk.RightStart..chunk.RightEnd]);
            at = chunk.BaseEnd;
        }
        merged.AppendJoin(string.Empty, words[at..]);
        string text = merged.ToString();
        return text.Length == 0 ? [] : text.Split('\n');
    }

    /// <summary>A text as the word-by-word comparison sees it: a run of letters, digits and underscores is a word, a run of
    /// spaces and tabs is one, and each line break and each other character stands alone.</summary>
    private static string[] Words(string text)
    {
        List<string> words = [];
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            char c = text[i];
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }
            }
            else if (c is ' ' or '\t')
            {
                while (i < text.Length && text[i] is ' ' or '\t')
                {
                    i++;
                }
            }
            else
            {
                i++;
            }
            words.Add(text[start..i]);
        }
        return [.. words];
    }

    /// <summary>How many lines a text has as the comparison counts them: a break at the very end ends the last line rather
    /// than starting another.</summary>
    public static int LineCount(TextSnapshot text) =>
        text.Length == 0 ? 0 : text.LineCount > 1 && text.GetLine(text.LineCount - 1).Length == 0 ? text.LineCount - 1 : text.LineCount;

    /// <summary>The edit that puts <paramref name="lines"/> in place of a text's lines from <paramref name="start"/> to
    /// <paramref name="end"/>, each ending with <paramref name="lineBreak"/> as the text's lines do, the text ending with a
    /// break or not as it did, or where the stretch reaches its end and <paramref name="breakAtEnd"/> says, as that says.</summary>
    public static TextEdit ReplaceLines(TextSnapshot text, int start, int end, IReadOnlyList<string> lines, string lineBreak, bool? breakAtEnd = null)
    {
        int count = LineCount(text);
        bool endsWithBreak = count > 0 && text.LineCount > count;
        TextEdit edit = Replaced(text, start, end, lines, lineBreak, count, endsWithBreak);
        if (end < count || breakAtEnd is not { } wanted || wanted == endsWithBreak)
        {
            return edit;
        }
        // The stretch reaches the text's end, so the edit runs to it too: its break is added after what is put there, or taken off.
        if (wanted)
        {
            return edit.Start > 0 || edit.Text.Length > 0 ? edit with { Text = edit.Text + lineBreak } : edit;
        }
        if (lines.Count > 0)
        {
            return edit with { Text = edit.Text[..^lineBreak.Length] };
        }
        int after = start > 0 ? text.GetLine(start - 1).End : 0;
        return new TextEdit(after, text.Length - after, string.Empty);
    }

    /// <summary>The edit of <see cref="ReplaceLines"/> that keeps the text's last break as it is.</summary>
    private static TextEdit Replaced(TextSnapshot text, int start, int end, IReadOnlyList<string> lines, string lineBreak, int count, bool endsWithBreak)
    {
        string joined = string.Join(lineBreak, lines);
        if (end < count || endsWithBreak)
        {
            // Every line of the stretch ends with a break, which each line put there takes too.
            int from = text.GetLineStart(start);
            int to = end < count ? text.GetLineStart(end) : text.Length;
            return new TextEdit(from, to - from, lines.Count > 0 ? joined + lineBreak : string.Empty);
        }
        if (start < end)
        {
            if (lines.Count > 0)
            {
                int from = text.GetLineStart(start);
                return new TextEdit(from, text.Length - from, joined);
            }
            // The last lines go with the break before them.
            int after = start > 0 ? text.GetLine(start - 1).End : 0;
            return new TextEdit(after, text.Length - after, string.Empty);
        }
        if (lines.Count == 0)
        {
            return new TextEdit(text.Length, 0, string.Empty);
        }
        return new TextEdit(text.Length, 0, count == 0 ? joined : lineBreak + joined);
    }
}
