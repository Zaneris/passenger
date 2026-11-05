using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>What one or more changes of a text did, as the one stretch they span: where it starts, how long it was, how long
/// it is now. A document's change gives its edits sorted in the text before it (<see cref="Of"/>); changes one after another
/// join into one (<see cref="Then"/>), so a tree parses again once for all the keys typed since it was made.</summary>
public readonly record struct GDScriptTextChange(int Start, int OldLength, int NewLength)
{
    /// <summary>The stretch a change's edits span, given sorted and apart in the text before it.</summary>
    public static GDScriptTextChange Of(IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return new GDScriptTextChange(0, 0, 0);
        }
        int start = edits[0].Start;
        int end = edits[^1].End;
        int grown = 0;
        foreach (TextEdit edit in edits)
        {
            grown += edit.Text.Length - edit.Length;
        }
        return new GDScriptTextChange(start, end - start, end - start + grown);
    }

    /// <summary>This change and a later one (in the text this one left) as one stretch of the text before this one.</summary>
    public GDScriptTextChange Then(GDScriptTextChange next)
    {
        int start = Math.Min(Start, next.Start);
        int endNow = Math.Max(Start + NewLength, next.Start + next.OldLength);
        int endBefore = endNow >= Start + NewLength ? endNow - NewLength + OldLength : Start + OldLength;
        return new GDScriptTextChange(start, endBefore - start, endNow + next.NewLength - next.OldLength - start);
    }

    /// <summary>Whether it can be the change from a text of one length to one of another.</summary>
    public bool Fits(int before, int after) => Start >= 0 && Start + OldLength <= before && Start + NewLength <= after && after - before == NewLength - OldLength;

    /// <summary>It as one edit of the text before it, written from the text after it.</summary>
    public TextEdit Edit(string after) => new(Start, OldLength, after.Substring(Start, NewLength));
}
