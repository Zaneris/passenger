using Passenger.Text;

namespace Passenger.App.Workspace;

/// <summary>A line (from zero) of a file, carried with the file's text while it is open (<see cref="Attach"/>) by an anchor
/// at the line's first character: text inserted before it or at its very start pushes it along, so a line broken at its
/// start goes down with its code, and a line removed lands where the removal did. An edit that leaves the anchor inside
/// its line puts it back at the line's start, so the line keeps its own first character. Closed, it stays where it was.</summary>
public sealed class AnchoredLine(int line)
{
    private Anchor? anchor;
    private TextDocument? text;

    /// <summary>The line as last read from its anchor (<see cref="Follow"/>), or as given.</summary>
    public int Line { get; private set; } = line;

    /// <summary>Carries the line with the text of its file, opened.</summary>
    public void Attach(TextDocument document)
    {
        Detach();
        text = document;
        TextSnapshot now = document.Snapshot;
        anchor = document.AnchorAt(now.GetLineStart(Math.Clamp(Line, 0, now.LineCount - 1)), AnchorGravity.After);
    }

    /// <summary>Its file closed: the line stays where it was last read.</summary>
    public void Detach()
    {
        anchor?.Remove();
        anchor = null;
        text = null;
    }

    /// <summary>Reads the line from its anchor after a change, the anchor put back at the line's start; true when the line
    /// moved.</summary>
    public bool Follow()
    {
        if (anchor is null || text is null)
        {
            return false;
        }
        TextSnapshot now = text.Snapshot;
        int offset = anchor.Offset;
        int line = now.GetLineFromOffset(offset);
        if (now.GetLineStart(line) != offset)
        {
            anchor.MoveTo(now.GetLineStart(line));
        }
        if (line == Line)
        {
            return false;
        }
        Line = line;
        return true;
    }
}
