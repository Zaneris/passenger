using Passenger.Core;

namespace Passenger.Text;

/// <summary>A stretch where two texts' lines differ: lines from <see cref="OldStart"/> of the first replaced by
/// lines from <see cref="NewStart"/> of the second (either count may be zero), lines from zero.</summary>
public readonly record struct DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount);

/// <summary>Lines compared by Myers' diff (the shortest edit), which is what Git shows: the hunks between two texts,
/// and the edits that turn one into the other. Any thread may compare; the memory it takes grows with the texts, never
/// with how much of them differs.</summary>
public static class LineDiff
{
    /// <summary>The most edits the comparison looks for before it calls the rest one changed stretch, so a file edited
    /// all over (or a huge one) costs a bounded time.</summary>
    private const int MostEdits = 4000;

    /// <summary>The hunks between two texts' <see cref="Lines"/>, which a caller may keep to compare again.</summary>
    public static IReadOnlyList<DiffHunk> Hunks(string[] before, string[] after)
    {
        // The ends that agree are no work: only the middle is compared.
        int prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
        {
            prefix++;
        }
        int suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }
        int n = before.Length - prefix - suffix;
        int m = after.Length - prefix - suffix;
        if (n == 0 && m == 0)
        {
            return [];
        }
        if (n == 0 || m == 0)
        {
            return [new DiffHunk(prefix, n, prefix, m)];
        }
        return new Comparison(before, after).Run(prefix, prefix + n, prefix, prefix + m, MostEdits) ?? [new DiffHunk(prefix, n, prefix, m)];
    }

    /// <summary>The lines a comparison sees, without their breaks ("\r\n" or "\n"). A break at the very end ends the last
    /// line rather than starting an empty one, so blank lines added before it count, as Git counts them.</summary>
    public static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }
        string[] lines = new string[text.AsSpan().Count('\n') + (text[^1] == '\n' ? 0 : 1)];
        int start = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            int feed = text.IndexOf('\n', start);
            int end = feed < 0 ? text.Length : feed;
            lines[i] = text[start..(feed > start && text[feed - 1] == '\r' ? feed - 1 : end)];
            start = end + 1;
        }
        return lines;
    }

    /// <summary>The edits that turn one text into the other: one per changed stretch of lines, only as wide as the
    /// characters that differ in it, so what lies outside them (carets, breakpoints, folds) keeps its place.</summary>
    public static IReadOnlyList<TextEdit> Edits(string before, string after)
    {
        string[] a = WholeLines(before);
        string[] b = WholeLines(after);
        int[] from = Starts(a);
        int[] to = Starts(b);
        List<TextEdit> edits = [];
        foreach (DiffHunk hunk in Hunks(a, b))
        {
            int oldStart = from[hunk.OldStart];
            int oldEnd = from[hunk.OldStart + hunk.OldCount];
            int newStart = to[hunk.NewStart];
            int newEnd = to[hunk.NewStart + hunk.NewCount];
            while (oldStart < oldEnd && newStart < newEnd && before[oldStart] == after[newStart])
            {
                oldStart++;
                newStart++;
            }
            while (oldEnd > oldStart && newEnd > newStart && before[oldEnd - 1] == after[newEnd - 1])
            {
                oldEnd--;
                newEnd--;
            }
            edits.Add(new TextEdit(oldStart, oldEnd - oldStart, after[newStart..newEnd]));
        }
        return edits;
    }

    /// <summary>The difference as `git diff` prints it: a header, then each hunk with three lines of context around it,
    /// hunks whose context touches merged into one.</summary>
    public static string Unified(string before, string after, string name, int context = 3)
    {
        string[] a = Lines(before);
        string[] b = Lines(after);
        IReadOnlyList<DiffHunk> hunks = Hunks(a, b);
        if (hunks.Count == 0)
        {
            return string.Empty;
        }
        System.Text.StringBuilder text = new();
        text.Append($"--- a/{name}\n+++ b/{name}\n");
        int i = 0;
        while (i < hunks.Count)
        {
            // A run of hunks whose context overlaps is printed as one.
            int j = i;
            while (j + 1 < hunks.Count && hunks[j + 1].OldStart - (hunks[j].OldStart + hunks[j].OldCount) <= context * 2)
            {
                j++;
            }
            int oldFrom = Math.Max(0, hunks[i].OldStart - context);
            int oldTo = Math.Min(a.Length, hunks[j].OldStart + hunks[j].OldCount + context);
            int newFrom = Math.Max(0, hunks[i].NewStart - context);
            int newTo = Math.Min(b.Length, hunks[j].NewStart + hunks[j].NewCount + context);
            text.Append($"@@ -{oldFrom + 1},{oldTo - oldFrom} +{newFrom + 1},{newTo - newFrom} @@\n");
            int at = oldFrom;
            for (int k = i; k <= j; k++)
            {
                DiffHunk hunk = hunks[k];
                for (; at < hunk.OldStart; at++)
                {
                    text.Append(' ').Append(a[at]).Append('\n');
                }
                for (int line = hunk.OldStart; line < hunk.OldStart + hunk.OldCount; line++)
                {
                    text.Append('-').Append(a[line]).Append('\n');
                }
                for (int line = hunk.NewStart; line < hunk.NewStart + hunk.NewCount; line++)
                {
                    text.Append('+').Append(b[line]).Append('\n');
                }
                at = hunk.OldStart + hunk.OldCount;
            }
            for (; at < oldTo; at++)
            {
                text.Append(' ').Append(a[at]).Append('\n');
            }
            i = j + 1;
        }
        return text.ToString();
    }

    /// <summary>How the lines two texts have alike (those no hunk takes in) changed their separators, in words ("Line
    /// separators changed: 3 lines from LF to CRLF; LF added at the end"), which a comparison of their lines alone never
    /// sees; null where none did.</summary>
    public static string? SeparatorChanges(string before, string after)
    {
        string[] a = Lines(before);
        string[] b = Lines(after);
        return SeparatorChanges(Hunks(a, b), a.Length, b.Length, Separators(before, a.Length), Separators(after, b.Length));
    }

    /// <summary><see cref="SeparatorChanges(string, string)"/> over each side's lines' separators as their texts end them:
    /// "LF", "CRLF", or empty for a last line that ends with none.</summary>
    public static string? SeparatorChanges(IReadOnlyList<DiffHunk> hunks, int beforeLines, int afterLines, Func<int, string> before, Func<int, string> after)
    {
        List<(string From, string To, int Lines)> changed = [];
        void Pair(int line, int other)
        {
            (string from, string to) = (before(line), after(other));
            if (from == to)
            {
                return;
            }
            int at = changed.FindIndex(c => c.From == from && c.To == to);
            if (at < 0)
            {
                changed.Add((from, to, 1));
            }
            else
            {
                changed[at] = (from, to, changed[at].Lines + 1);
            }
        }
        int i = 0;
        int j = 0;
        foreach (DiffHunk hunk in hunks)
        {
            for (; i < hunk.OldStart && j < hunk.NewStart; i++, j++)
            {
                Pair(i, j);
            }
            i = hunk.OldStart + hunk.OldCount;
            j = hunk.NewStart + hunk.NewCount;
        }
        for (; i < beforeLines && j < afterLines; i++, j++)
        {
            Pair(i, j);
        }
        if (changed.Count == 0)
        {
            return null;
        }
        // Only a text's last line ends with no separator.
        return "Line separators changed: " + string.Join("; ", changed.Select(c => (c.From, c.To) switch
        {
            ("", _) => $"{c.To} added at the end",
            (_, "") => $"{c.From} removed from the end",
            _ => $"{Plural.Of(c.Lines, "line")} from {c.From} to {c.To}",
        }));
    }

    /// <summary>Each of a text's <see cref="Lines"/>' separator, as <see cref="SeparatorChanges(IReadOnlyList{DiffHunk}, int, int, Func{int, string}, Func{int, string})"/> takes them.</summary>
    private static Func<int, string> Separators(string text, int lines)
    {
        string[] separators = new string[lines];
        int start = 0;
        for (int i = 0; i < lines; i++)
        {
            int feed = text.IndexOf('\n', start);
            separators[i] = feed < 0 ? string.Empty : feed > start && text[feed - 1] == '\r' ? "CRLF" : "LF";
            start = feed + 1;
        }
        return line => separators[line];
    }

    /// <summary>The lines each with its own ending, so that together they are the text exactly.</summary>
    private static string[] WholeLines(string text)
    {
        List<string> lines = [];
        int start = 0;
        while (start < text.Length)
        {
            int feed = text.IndexOf('\n', start);
            int end = feed < 0 ? text.Length : feed + 1;
            lines.Add(text[start..end]);
            start = end;
        }
        return [.. lines];
    }

    /// <summary>Where each line starts, and the text's end after the last.</summary>
    private static int[] Starts(string[] lines)
    {
        int[] starts = new int[lines.Length + 1];
        for (int i = 0; i < lines.Length; i++)
        {
            starts[i + 1] = starts[i] + lines[i].Length;
        }
        return starts;
    }

    /// <summary>Myers' comparison in linear space: a stretch is split where the searches from its two ends first meet,
    /// which is on a shortest edit, and each half compared the same way. It holds one furthest point per diagonal for
    /// each search, where keeping every step's to trace the path back costs the edits squared.</summary>
    private sealed class Comparison
    {
        private readonly string[] a;
        private readonly string[] b;
        private readonly List<DiffHunk> hunks = [];

        /// <summary>The furthest x reached on each diagonal (x - y, from <see cref="offset"/>) by the search from the top
        /// left and by the search from the bottom right.</summary>
        private readonly int[] down;
        private readonly int[] up;
        private readonly int offset;

        public Comparison(string[] a, string[] b)
        {
            this.a = a;
            this.b = b;
            down = new int[a.Length + b.Length + 3];
            up = new int[a.Length + b.Length + 3];
            offset = b.Length + 1;
        }

        /// <summary>The hunks between two stretches whose first lines differ and whose last lines differ, or null when
        /// they differ by more than <paramref name="mostEdits"/>.</summary>
        public List<DiffHunk>? Run(int xoff, int xlim, int yoff, int ylim, int mostEdits)
        {
            if (Split(xoff, xlim, yoff, ylim, (mostEdits + 1) / 2) is not (int x, int y))
            {
                return null;
            }
            Compare(xoff, x, yoff, y);
            Compare(x, xlim, y, ylim);
            return hunks;
        }

        private void Compare(int xoff, int xlim, int yoff, int ylim)
        {
            while (xoff < xlim && yoff < ylim && a[xoff] == b[yoff])
            {
                xoff++;
                yoff++;
            }
            while (xoff < xlim && yoff < ylim && a[xlim - 1] == b[ylim - 1])
            {
                xlim--;
                ylim--;
            }
            if (xoff == xlim || yoff == ylim)
            {
                Add(xoff, xlim - xoff, yoff, ylim - yoff);
                return;
            }
            // Each half's shortest edit is shorter than this one's (two edits at least, the ends trimmed), so the splitting ends.
            (int x, int y) = Split(xoff, xlim, yoff, ylim, int.MaxValue)!.Value;
            Compare(xoff, x, yoff, y);
            Compare(x, xlim, y, ylim);
        }

        /// <summary>Lines deleted or inserted, joined to the last hunk when no matching line lies between them.</summary>
        private void Add(int oldStart, int oldCount, int newStart, int newCount)
        {
            if (oldCount == 0 && newCount == 0)
            {
                return;
            }
            if (hunks.Count > 0 && hunks[^1] is { } last && last.OldStart + last.OldCount == oldStart && last.NewStart + last.NewCount == newStart)
            {
                hunks[^1] = new DiffHunk(last.OldStart, last.OldCount + oldCount, last.NewStart, last.NewCount + newCount);
                return;
            }
            hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount));
        }

        /// <summary>A point on a shortest edit through the stretch, where the searches from its two ends first meet (the
        /// end of Myers' middle snake), or null when they have not within <paramref name="mostSteps"/> edits each. Both
        /// stretches are lines, and their first lines differ, as do their last.</summary>
        private (int X, int Y)? Split(int xoff, int xlim, int yoff, int ylim, int mostSteps)
        {
            int dmin = xoff - ylim;
            int dmax = xlim - yoff;
            int fmid = xoff - yoff;
            int bmid = xlim - ylim;
            int fmin = fmid;
            int fmax = fmid;
            int bmin = bmid;
            int bmax = bmid;
            // On an odd count of diagonals apart, the searches meet on the forward one's step, else on the backward one's.
            bool odd = ((fmid - bmid) & 1) != 0;
            down[offset + fmid] = xoff;
            up[offset + bmid] = xlim;
            for (int step = 1; step <= mostSteps; step++)
            {
                // One more edit on each diagonal: past the stretch's corner the range shrinks instead, keeping its parity.
                if (fmin > dmin)
                {
                    fmin--;
                    down[offset + fmin - 1] = -1;
                }
                else
                {
                    fmin++;
                }
                if (fmax < dmax)
                {
                    fmax++;
                    down[offset + fmax + 1] = -1;
                }
                else
                {
                    fmax--;
                }
                for (int d = fmax; d >= fmin; d -= 2)
                {
                    int low = down[offset + d - 1];
                    int high = down[offset + d + 1];
                    int x = low < high ? high : low + 1;
                    int y = x - d;
                    while (x < xlim && y < ylim && a[x] == b[y])
                    {
                        x++;
                        y++;
                    }
                    down[offset + d] = x;
                    if (odd && bmin <= d && d <= bmax && up[offset + d] <= x)
                    {
                        return (x, y);
                    }
                }
                if (bmin > dmin)
                {
                    bmin--;
                    up[offset + bmin - 1] = int.MaxValue;
                }
                else
                {
                    bmin++;
                }
                if (bmax < dmax)
                {
                    bmax++;
                    up[offset + bmax + 1] = int.MaxValue;
                }
                else
                {
                    bmax--;
                }
                for (int d = bmax; d >= bmin; d -= 2)
                {
                    int low = up[offset + d - 1];
                    int high = up[offset + d + 1];
                    int x = low < high ? low : high - 1;
                    int y = x - d;
                    while (x > xoff && y > yoff && a[x - 1] == b[y - 1])
                    {
                        x--;
                        y--;
                    }
                    up[offset + d] = x;
                    if (!odd && fmin <= d && d <= fmax && x <= down[offset + d])
                    {
                        return (x, y);
                    }
                }
            }
            return null;
        }
    }
}
