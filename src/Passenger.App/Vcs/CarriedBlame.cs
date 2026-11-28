using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A file's one blame carried through its edits: the lines as git blamed the text it read, and the changes made
/// since, taken in only as it is read (<see cref="Now"/>), so a key costs a change kept. A line an edit touched or made is
/// one not committed yet, as git blames a working tree's; the lines it did not touch keep their commits.</summary>
internal sealed class CarriedBlame(IReadOnlyList<BlameLine> lines, TextSnapshot text)
{
    /// <summary>What git says of a line not committed yet.</summary>
    private static readonly BlameLine Uncommitted = new(new string('0', 40), "Not Committed Yet", default, string.Empty);

    private readonly List<DocumentChangedEventArgs> since = [];
    private IReadOnlyList<BlameLine> lines = lines;
    private TextSnapshot text = text;

    /// <summary>Keeps a change of the text to take in as the blame is next read.</summary>
    public void Follow(DocumentChangedEventArgs change) => since.Add(change);

    /// <summary>The blame of the text as it stands, line by line: the changes kept since taken in, in order, from the text
    /// blamed; null where they no longer lead to it.</summary>
    public IReadOnlyList<BlameLine>? Now(TextSnapshot now)
    {
        foreach (DocumentChangedEventArgs change in since)
        {
            if (ReferenceEquals(change.Before, text))
            {
                lines = Carry(lines, change);
                text = change.After;
            }
        }
        since.Clear();
        return ReferenceEquals(text, now) ? lines : null;
    }

    /// <summary>A text's blame carried through one change: the lines each run of edits touched, as they stand after it, not
    /// committed yet; the rest as they were, a line that lines were put in above or below (a paste at its start, Enter at
    /// its end) among them.</summary>
    private static List<BlameLine> Carry(IReadOnlyList<BlameLine> lines, DocumentChangedEventArgs change)
    {
        TextSnapshot before = change.Before;
        IReadOnlyList<TextEdit> edits = change.Edits;
        List<BlameLine> made = new(lines.Count + 4);
        int copied = 0;
        int i = 0;
        while (i < edits.Count)
        {
            int first = before.GetLineFromOffset(edits[i].Start);
            int last = before.GetLineFromOffset(edits[i].End);
            int added = Breaks(edits[i].Text) - (last - first);
            int j = i + 1;
            while (j < edits.Count && before.GetLineFromOffset(edits[j].Start) <= last)
            {
                int end = before.GetLineFromOffset(edits[j].End);
                added += Breaks(edits[j].Text) - (end - before.GetLineFromOffset(edits[j].Start));
                last = Math.Max(last, end);
                j++;
            }
            LineSpan at = before.GetLine(first);
            bool lone = j == i + 1 && edits[i].Length == 0 && added > 0;
            // Whole lines put in before a line's start, or after its end, leave the line as it was.
            int kept = lone && edits[i].Start == at.Start && edits[i].Text.EndsWith('\n') ? 0
                : lone && edits[i].Start == at.End && edits[i].Text.StartsWith('\n') ? 1
                : -1;
            for (int line = copied; line < (kept == 1 ? first + 1 : first) && line < lines.Count; line++)
            {
                made.Add(lines[line]);
            }
            for (int line = first; line <= last + added - (kept >= 0 ? 1 : 0); line++)
            {
                made.Add(Uncommitted);
            }
            copied = kept == 0 ? first : last + 1;
            i = j;
        }
        for (int line = copied; line < lines.Count; line++)
        {
            made.Add(lines[line]);
        }
        return made;
    }

    private static int Breaks(string text) => text.AsSpan().Count('\n');
}
