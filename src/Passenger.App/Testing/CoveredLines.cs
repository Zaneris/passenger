using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Testing;

/// <summary>An open file's marks of one kind per line (a run's coverage, which tests run a line), each carried with the file's
/// text as it is edited by a mark on the line's text, so its marks stay on their code, a line typed between them has none and
/// a line deleted takes its own with it. Read by line from the lines on screen alone, so a key costs what it moved: the marks
/// keep their order through every edit, so a line's is found by halving. Read on the window's thread.</summary>
public class MarkedLines<T> where T : struct
{
    private readonly TextDocument text;
    private readonly AnchoredRange[] marks;
    private readonly T[] lines;

    /// <summary>Whether a line was empty when marked, so its mark stands though it holds no text.</summary>
    private readonly bool[] empty;

    /// <summary>Marks a file's text with each line's value (lines from one, in order), those past its end left out.</summary>
    public MarkedLines(TextDocument text, IEnumerable<(int Line, T Value)> marked)
    {
        this.text = text;
        TextSnapshot now = text.Snapshot;
        List<(int Line, T Value)> kept = [.. marked.Where(m => m.Line >= 1 && m.Line <= now.LineCount)];
        lines = [.. kept.Select(m => m.Value)];
        marks = new AnchoredRange[kept.Count];
        empty = new bool[kept.Count];
        for (int i = 0; i < kept.Count; i++)
        {
            LineSpan span = now.GetLine(kept[i].Line - 1);
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

    /// <summary>The line's value as marked.</summary>
    public T this[int at] => lines[at];

    /// <summary>The line's value where a mark stands on a line (from zero), null where none does.</summary>
    public T? At(int line)
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

    /// <summary>Lets the text go of every mark: the file shows these marks no more.</summary>
    public void Clear()
    {
        foreach (AnchoredRange mark in marks)
        {
            mark.Remove();
        }
    }
}

/// <summary>An open file's coverage marks: each line as a run with coverage measured it.</summary>
public sealed class CoveredLines(TextDocument text, IReadOnlyList<CoveredLine> measured) : MarkedLines<CoveredLine>(text, measured.Select(l => (l.Line, l)));

/// <summary>An open file's marks of which tests run its lines, as continuous testing mapped them (<see cref="TestReachState.Marks"/>).</summary>
public sealed class ReachedLines(TextDocument text, IReadOnlyList<ReachedLine> mapped) : MarkedLines<ReachedLine>(text, mapped.Select(l => (l.Line, l)));
