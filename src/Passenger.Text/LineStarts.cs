using Passenger.Core;

namespace Passenger.Text;

/// <summary>Where each line of a text starts, its lines broken at each '\n' as a document's are ("\r\n" ending at its
/// '\n'), and the line an offset is on. Lines count from zero.</summary>
public sealed class LineStarts
{
    private readonly int[] starts;

    public LineStarts(string text)
    {
        List<int> found = [0];
        for (int at = text.IndexOf('\n'); at >= 0; at = text.IndexOf('\n', at + 1))
        {
            found.Add(at + 1);
        }
        starts = [.. found];
    }

    /// <summary>How many lines the text has: one more than its line breaks.</summary>
    public int Count => starts.Length;

    /// <summary>Where a line starts.</summary>
    public int this[int line] => starts[line];

    /// <summary>The line an offset is on, from zero; the first for an offset before the text.</summary>
    public int LineOf(int offset) => Math.Max(0, Sorted.FirstAfter<int>(starts, offset) - 1);
}
