using System.Text;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Vcs;

/// <summary>A file's chosen changes, tied to the texts reviewed so a later edit cannot silently enter the commit.</summary>
public sealed record PartialCommit(FileDiff Diff, IReadOnlyList<DiffHunk> Hunks, string? From = null)
{
    /// <summary>The last commit's text with just the chosen changes, retaining each side's line separators.</summary>
    public string Content()
    {
        string after = Diff.Text.ToString();
        LineStarts oldStarts = new(Diff.Head);
        LineStarts newStarts = new(after);
        StringBuilder content = new();
        int copied = 0;
        foreach (DiffHunk hunk in Hunks.OrderBy(h => h.OldStart))
        {
            int start = Offset(oldStarts, Diff.Head.Length, hunk.OldStart);
            int end = Offset(oldStarts, Diff.Head.Length, hunk.OldStart + hunk.OldCount);
            int added = Offset(newStarts, after.Length, hunk.NewStart);
            int addedEnd = Offset(newStarts, after.Length, hunk.NewStart + hunk.NewCount);
            if (hunk.NewCount == 0 && added == after.Length && after.Length > 0 && after[^1] != '\n' && start > 0)
            {
                start -= Diff.Head[start - 1] == '\n' ? start > 1 && Diff.Head[start - 2] == '\r' ? 2 : 1 : 0;
            }
            content.Append(Diff.Head, copied, start - copied);
            if (hunk.OldCount == 0 && start == Diff.Head.Length && start > 0 && Diff.Head[^1] != '\n' && added > 0)
            {
                content.Append(after[added - 1] == '\n' && added > 1 && after[added - 2] == '\r' ? "\r\n" : "\n");
            }
            content.Append(after, added, addedEnd - added);
            copied = end;
        }
        content.Append(Diff.Head, copied, Diff.Head.Length - copied);
        return content.ToString();
    }

    /// <summary>The changes of <paramref name="next"/>, another text of the file against the same last commit, that the
    /// chosen ones still make: each touching a chosen change's lines of the last commit and no change left out. None
    /// where the last commit's text is another.</summary>
    public IReadOnlyList<DiffHunk> Following(FileDiff next)
    {
        if (!ReferenceEquals(next.Head, Diff.Head) && next.Head != Diff.Head)
        {
            return [];
        }
        HashSet<DiffHunk> chosen = [.. Hunks];
        List<DiffHunk> kept = [];
        int at = 0;
        foreach (DiffHunk hunk in next.Hunks)
        {
            while (at < Diff.Hunks.Count && End(Diff.Hunks[at]) < hunk.OldStart)
            {
                at++;
            }
            bool taken = false;
            bool left = false;
            for (int i = at; i < Diff.Hunks.Count && Diff.Hunks[i].OldStart <= End(hunk); i++)
            {
                taken |= chosen.Contains(Diff.Hunks[i]);
                left |= !chosen.Contains(Diff.Hunks[i]);
            }
            if (taken && !left)
            {
                kept.Add(hunk);
            }
        }
        return kept;
    }

    /// <summary>The changes of <paramref name="diff"/> that lines of the file as it is now touch, named as "3,12-14" (lines
    /// counted from 1): each change a range overlaps goes in whole, one that only deletes lines by either line beside where
    /// they were. Why not, saying where the changes are, where the ranges do not read or one touches no change.</summary>
    public static (IReadOnlyList<DiffHunk> Hunks, string? Why) Touched(FileDiff diff, string ranges)
    {
        HashSet<DiffHunk> taken = [];
        foreach (string given in ranges.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Range(given) is not (int first, int last))
            {
                return ([], $"{given} is not a line or a range of lines, as 3 or 12-14");
            }
            DiffHunk[] touched = [.. diff.Hunks.Where(h => Lines(h).First <= last && Lines(h).Last >= first)];
            if (touched.Length == 0)
            {
                string where = diff.Hunks.Count == 0 ? "it has none" : $"{(diff.Hunks.Count == 1 ? "its one change is" : "its changes are")} at {Plural.Listed([.. diff.Hunks.Select(Said)])}";
                return ([], $"no change is at line{(first == last ? $" {first}" : $"s {first}-{last}")}: {where}");
            }
            taken.UnionWith(touched);
        }
        return taken.Count == 0 ? ([], "no lines are named") : ([.. diff.Hunks.Where(taken.Contains)], null);

        static (int, int)? Range(string given)
        {
            string[] ends = given.Split('-', StringSplitOptions.TrimEntries);
            return ends.Length <= 2 && Line(ends[0], out int first) && Line(ends[^1], out int last) && first <= last ? (first, last) : null;
        }

        static bool Line(string text, out int line) =>
            int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out line) && line > 0;

        // A deletion lies between the line before it and the line after, which is where a unified diff's "+N,0" names it.
        static (int First, int Last) Lines(DiffHunk hunk) => hunk.NewCount == 0 ? (Math.Max(1, hunk.NewStart), hunk.NewStart + 1) : (hunk.NewStart + 1, hunk.NewStart + hunk.NewCount);

        static string Said(DiffHunk hunk) => hunk.NewCount switch
        {
            0 => hunk.NewStart == 0 ? "before line 1" : $"between lines {hunk.NewStart} and {hunk.NewStart + 1}",
            1 => $"line {hunk.NewStart + 1}",
            _ => $"lines {hunk.NewStart + 1}-{hunk.NewStart + hunk.NewCount}",
        };
    }

    private static int End(DiffHunk hunk) => hunk.OldStart + hunk.OldCount;

    private static int Offset(LineStarts starts, int length, int line) => line < starts.Count ? starts[line] : length;
}
