using Passenger.Text;

namespace Passenger.App.Workspace;

/// <summary>A line (from zero) of a file, or a place on it given its column, carried with the file's text while it is open
/// (<see cref="Attach"/>) by an anchor there. A line's anchor is at its first character: text inserted before it or at its
/// very start pushes it along, so a line broken at its start goes down with its code, and a line removed lands where the
/// removal did. An edit that leaves the anchor inside its line puts it back at the line's start, so the line keeps its own
/// first character. A place's anchor stays before text inserted at it and moves with what is around it, on its line or
/// another. Closed, it stays where it was.</summary>
public sealed class AnchoredLine(int line, int? column = null)
{
    private Anchor? anchor;
    private TextDocument? text;

    /// <summary>The line as last read from its anchor (<see cref="Follow"/>), or as given.</summary>
    public int Line { get; private set; } = line;

    /// <summary>The place's column (from zero) on its line as last read from its anchor, or as given; null for the line.</summary>
    public int? Column { get; private set; } = column;

    /// <summary>Carries the line with the text of its file, opened.</summary>
    public void Attach(TextDocument document)
    {
        Detach();
        text = document;
        TextSnapshot now = document.Snapshot;
        LineSpan span = now.GetLine(Math.Clamp(Line, 0, now.LineCount - 1));
        anchor = Column is int at
            ? document.AnchorAt(span.Start + Math.Clamp(at, 0, span.Length), AnchorGravity.Before)
            : document.AnchorAt(span.Start, AnchorGravity.After);
    }

    /// <summary>Its file closed: the line stays where it was last read.</summary>
    public void Detach()
    {
        anchor?.Remove();
        anchor = null;
        text = null;
    }

    /// <summary>Reads the line, and the place's column, from its anchor after a change, a line's anchor put back at the
    /// line's start; true when either moved.</summary>
    public bool Follow()
    {
        if (anchor is null || text is null)
        {
            return false;
        }
        TextSnapshot now = text.Snapshot;
        int offset = anchor.Offset;
        int line = now.GetLineFromOffset(offset);
        int start = now.GetLineStart(line);
        int? column = Column is null ? null : offset - start;
        if (Column is null && start != offset)
        {
            anchor.MoveTo(start);
        }
        if (line == Line && column == Column)
        {
            return false;
        }
        (Line, Column) = (line, column);
        return true;
    }
}
