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

    /// <summary>What the index is to hold of a file once a commit took some of its changes, each text as git keeps it:
    /// <paramref name="staged"/>, what it held, with the changes <paramref name="committed"/> made to the last commit's
    /// <paramref name="head"/> taken in, so a staged change the commit left out stays staged. Where both changed the same
    /// lines of the last commit, the committed lines are taken. Null where the three can't be read alike: UTF-16 or UTF-32
    /// (by a byte order mark) in one but not all, or a wide text that does not read back byte for byte.</summary>
    public static byte[]? Restaged(byte[] head, byte[] staged, byte[] committed)
    {
        Encoding? wide = Wide(head);
        if (Wide(staged)?.CodePage != wide?.CodePage || Wide(committed)?.CodePage != wide?.CodePage)
        {
            return null;
        }
        // Latin-1 gives each byte a character of its own, so a UTF-8 or 8-bit text splits at its line feeds and comes back whole.
        Encoding reading = wide ?? Encoding.Latin1;
        return Read(head) is { } before && Read(staged) is { } kept && Read(committed) is { } taken
            ? reading.GetBytes(string.Concat(Restaged(before, kept, taken))) : null;

        string[]? Read(byte[] bytes) => reading.GetString(bytes) is var text && reading.GetBytes(text).AsSpan().SequenceEqual(bytes) ? Lines(text) : null;
    }

    /// <summary><see cref="Restaged(byte[], byte[], byte[])"/> over lines that keep their breaks. Changes of the two
    /// sides that only meet, one ending where the other begins, are both taken; those that share a line of the last
    /// commit, or both add lines at one place, make one stretch, which is the committed side's where it changed any of it.</summary>
    private static List<string> Restaged(string[] head, string[] staged, string[] committed)
    {
        IReadOnlyList<DiffHunk> kept = LineDiff.Hunks(head, staged);
        IReadOnlyList<DiffHunk> taken = LineDiff.Hunks(head, committed);
        List<string> lines = [];
        int at = 0;
        int i = 0;
        int j = 0;
        int keptShift = 0;
        int takenShift = 0;
        while (i < kept.Count || j < taken.Count)
        {
            // The stretch begins at the first change left, one adding lines before one replacing lines at the same place.
            bool keptFirst = j == taken.Count || i < kept.Count && (kept[i].OldStart, kept[i].OldCount > 0).CompareTo((taken[j].OldStart, taken[j].OldCount > 0)) <= 0;
            DiffHunk first = keptFirst ? kept[i++] : taken[j++];
            int start = first.OldStart;
            int end = End(first);
            int keptGrowth = keptFirst ? first.NewCount - first.OldCount : 0;
            int takenGrowth = keptFirst ? 0 : first.NewCount - first.OldCount;
            bool anyTaken = !keptFirst;
            while (true)
            {
                if (i < kept.Count && Overlaps(kept[i], start, end))
                {
                    end = Math.Max(end, End(kept[i]));
                    keptGrowth += kept[i].NewCount - kept[i].OldCount;
                    i++;
                }
                else if (j < taken.Count && Overlaps(taken[j], start, end))
                {
                    end = Math.Max(end, End(taken[j]));
                    takenGrowth += taken[j].NewCount - taken[j].OldCount;
                    anyTaken = true;
                    j++;
                }
                else
                {
                    break;
                }
            }
            lines.AddRange(head.AsSpan(at, start - at));
            lines.AddRange(anyTaken ? committed.AsSpan(start + takenShift, end - start + takenGrowth) : staged.AsSpan(start + keptShift, end - start + keptGrowth));
            at = end;
            keptShift += keptGrowth;
            takenShift += takenGrowth;
        }
        lines.AddRange(head.AsSpan(at));
        return lines;
    }

    /// <summary>What the index is to hold of a file once some of its changes are taken out of it, each text as git keeps it:
    /// <paramref name="staged"/>, what it held, with each of its changes from the last commit's <paramref name="head"/> that
    /// shares lines of the last commit with one of <paramref name="changes"/> (the file's changes, by their lines there)
    /// put back as the last commit has them. Null where the two can't be read alike, as <see cref="Restaged(byte[], byte[], byte[])"/>.</summary>
    public static byte[]? Unstaged(byte[] head, byte[] staged, IReadOnlyList<DiffHunk> changes)
    {
        Encoding? wide = Wide(head);
        if (Wide(staged)?.CodePage != wide?.CodePage)
        {
            return null;
        }
        Encoding reading = wide ?? Encoding.Latin1;
        if (Read(head) is not { } before || Read(staged) is not { } held)
        {
            return null;
        }
        List<string> lines = [];
        int at = 0;
        int shift = 0;
        foreach (DiffHunk hunk in LineDiff.Hunks(before, held))
        {
            bool taken = changes.Any(c => Overlaps(hunk, c.OldStart, End(c)) || Overlaps(c, hunk.OldStart, End(hunk)));
            lines.AddRange(before.AsSpan(at, hunk.OldStart - at));
            lines.AddRange(taken ? before.AsSpan(hunk.OldStart, hunk.OldCount) : held.AsSpan(hunk.OldStart + shift, hunk.NewCount));
            at = End(hunk);
            shift += hunk.NewCount - hunk.OldCount;
        }
        lines.AddRange(before.AsSpan(at));
        return reading.GetBytes(string.Concat(lines));

        string[]? Read(byte[] bytes) => reading.GetString(bytes) is var text && reading.GetBytes(text).AsSpan().SequenceEqual(bytes) ? Lines(text) : null;
    }

    /// <summary>A text's encoding where it is UTF-16 or UTF-32, by its byte order mark; null for any other.</summary>
    private static Encoding? Wide(byte[] bytes) => FileText.Bom(bytes) is { CodePage: not 65001 } encoding ? encoding : null;

    /// <summary>Whether a change of the last commit's lines meets a stretch of them: lines added at a place meet a stretch
    /// only inside it, and lines added at that same place.</summary>
    private static bool Overlaps(DiffHunk hunk, int start, int end) => (hunk.OldCount > 0, start < end) switch
    {
        (true, true) => hunk.OldStart < end && start < End(hunk),
        (true, false) => hunk.OldStart < start && start < End(hunk),
        (false, true) => start < hunk.OldStart && hunk.OldStart < end,
        (false, false) => hunk.OldStart == start,
    };

    /// <summary>A text's lines, each with the break that ends it.</summary>
    private static string[] Lines(string text)
    {
        List<string> lines = [];
        for (int start = 0; start < text.Length;)
        {
            int feed = text.IndexOf('\n', start);
            int end = feed < 0 ? text.Length : feed + 1;
            lines.Add(text[start..end]);
            start = end;
        }
        return [.. lines];
    }

    private static int End(DiffHunk hunk) => hunk.OldStart + hunk.OldCount;

    private static int Offset(LineStarts starts, int length, int line) => line < starts.Count ? starts[line] : length;
}
