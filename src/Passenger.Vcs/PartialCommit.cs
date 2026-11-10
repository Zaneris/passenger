using System.Text;
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

    private static int Offset(LineStarts starts, int length, int line) => line < starts.Count ? starts[line] : length;
}
