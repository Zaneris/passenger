using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Testing;

/// <summary>An open file's coverage marks: each line a run with coverage measured, carried with the file's text as it is
/// edited by a mark on the line's text, so its marks stay on their code, a line typed between them has none and a line
/// deleted takes its own with it. Read by line from the lines on screen alone, so a key costs what it moved: the marks
/// keep their order through every edit, so a line's is found by halving. Read on the window's thread.</summary>
public sealed class CoveredLines
{
    private readonly TextDocument text;
    private readonly AnchoredRange[] marks;
    private readonly CoveredLine[] lines;

    /// <summary>Whether a line was empty when measured, so its mark stands though it holds no text.</summary>
    private readonly bool[] empty;

    /// <summary>Marks a file's text with its lines as a run measured them, those past its end left out.</summary>
    public CoveredLines(TextDocument text, IReadOnlyList<CoveredLine> measured)
    {
        this.text = text;
        TextSnapshot now = text.Snapshot;
        List<CoveredLine> kept = new(measured.Count);
        foreach (CoveredLine line in measured)
        {
            if (line.Line <= now.LineCount)
            {
                kept.Add(line);
            }
        }
        lines = [.. kept];
        marks = new AnchoredRange[lines.Length];
        empty = new bool[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            LineSpan span = now.GetLine(lines[i].Line - 1);
            marks[i] = text.MarkAt(new TextRange(span.Start, span.Length));
            empty[i] = span.Length == 0;
        }
    }

    public int Count => lines.Length;

    /// <summary>The place of the first mark on a line (from zero) or after it.</summary>
    public int From(int line) => Sorted.FirstAtOrAfter(lines.Length, this, static (of, at) => of.LineOf(at), line);

    /// <summary>The line (from zero) a mark stands on now.</summary>
    public int LineOf(int at) => text.Snapshot.GetLineFromOffset(marks[at].Start);

    /// <summary>Whether a mark still has its line: an edit that took the line's text took its mark.</summary>
    public bool Stands(int at) => empty[at] || marks[at].Range.Length > 0;

    /// <summary>The line as the run measured it.</summary>
    public CoveredLine this[int at] => lines[at];

    /// <summary>The line's coverage where a mark stands on a line (from zero), null where none does.</summary>
    public CoveredLine? At(int line)
    {
        for (int at = From(line); at < lines.Length && LineOf(at) == line; at++)
        {
            if (Stands(at))
            {
                return lines[at];
            }
        }
        return null;
    }

    /// <summary>Lets the text go of every mark: the file shows coverage no more.</summary>
    public void Clear()
    {
        foreach (AnchoredRange mark in marks)
        {
            mark.Remove();
        }
    }
}
