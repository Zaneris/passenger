using System.Text;

namespace Passenger.Core;

/// <summary>A child's text cut into lines as it comes, a piece at a time: a line ends at "\n", "\r\n" or a lone "\r" (a
/// "\r\n" cut between two pieces one end), its end left off, and the last is told once the text ends, unless the text ended
/// with its end. With a longest, each line is cut there and the rest of it passed over, so a child that writes without end
/// costs no more than that.</summary>
internal sealed class ChildLines
{
    private readonly Action<string> heard;
    private readonly int longest;
    private readonly StringBuilder line = new();
    private bool afterReturn;

    public ChildLines(Action<string> heard, int? longest = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(longest ?? 1, 1, nameof(longest));
        this.heard = heard;
        this.longest = longest ?? int.MaxValue;
    }

    /// <summary>Takes the next piece of the text, telling each line it ends.</summary>
    public void Read(ReadOnlySpan<char> piece)
    {
        if (afterReturn && piece.Length > 0)
        {
            afterReturn = false;
            if (piece[0] == '\n')
            {
                piece = piece[1..];
            }
        }
        int end;
        while ((end = piece.IndexOfAny('\r', '\n')) >= 0)
        {
            Keep(piece[..end]);
            Tell();
            if (piece[end] == '\r')
            {
                if (end + 1 == piece.Length)
                {
                    afterReturn = true;
                }
                else if (piece[end + 1] == '\n')
                {
                    end++;
                }
            }
            piece = piece[(end + 1)..];
        }
        Keep(piece);
    }

    /// <summary>The text has ended: the line it was in, if any, told.</summary>
    public void End()
    {
        if (line.Length > 0)
        {
            Tell();
        }
    }

    private void Keep(ReadOnlySpan<char> part)
    {
        int room = Math.Min(part.Length, longest - line.Length);
        if (room > 0)
        {
            line.Append(part[..room]);
        }
    }

    private void Tell()
    {
        string told = line.ToString();
        line.Clear();
        heard(told);
    }
}
