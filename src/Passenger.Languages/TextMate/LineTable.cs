using Passenger.Core;
using Passenger.Editor;
using Tint;

namespace Passenger.Languages.TextMate;

/// <summary>A line's colours, each span from the line's start, and the grammar's state where it ends.</summary>
internal readonly record struct TokenLine(SyntaxSpan[] Spans, StateStack? End);

/// <summary>The colours of every line of one version of a text, as the grammar left them, never changed once made. Lines
/// from <see cref="StaleFrom"/> on hold colours found from an earlier state (their text is the same, or they are blank),
/// which the next pass finds again. Kept in runs that later tables share, so a key that changed one line makes a table
/// of a few small arrays, whatever the length of the file.</summary>
internal sealed class LineTable
{
    private readonly ArraySegment<TokenLine>[] runs;
    private readonly int[] starts;

    public LineTable(ArraySegment<TokenLine>[] runs, int staleFrom)
    {
        this.runs = runs;
        starts = new int[runs.Length];
        int at = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            starts[i] = at;
            at += runs[i].Count;
        }
        Count = at;
        StaleFrom = Math.Min(staleFrom, at);
    }

    public int Count { get; }

    /// <summary>The first line whose state is not known to follow from the lines above it; <see cref="Count"/> when every
    /// line's is.</summary>
    public int StaleFrom { get; }

    public bool Complete => StaleFrom == Count;

    public TokenLine this[int line]
    {
        get
        {
            int run = Sorted.FirstAfter(starts, line) - 1;
            return runs[run][line - starts[run]];
        }
    }

    /// <summary>The runs that cover lines [start, start + count), the first and last cut to fit.</summary>
    public IEnumerable<ArraySegment<TokenLine>> Slice(int start, int count)
    {
        if (count <= 0)
        {
            yield break;
        }
        int run = Sorted.FirstAfter(starts, start) - 1;
        int end = start + count;
        for (; run < runs.Length && starts[run] < end; run++)
        {
            int from = Math.Max(start, starts[run]) - starts[run];
            int to = Math.Min(end, starts[run] + runs[run].Count) - starts[run];
            yield return runs[run].Slice(from, to - from);
        }
    }
}

/// <summary>Makes a <see cref="LineTable"/> line by line, taking runs of an older table whole where they are long enough
/// to share and copying the short ones, so the runs stay few however many edits a file has had.</summary>
internal sealed class LineTableBuilder
{
    private const int RunLength = 512;
    private const int SharedAtLeast = 128;

    /// <summary>Blank lines every table shares runs of, never written.</summary>
    private static readonly TokenLine[] Blanks = [.. Enumerable.Repeat(new TokenLine([], null), 4096)];

    private readonly List<ArraySegment<TokenLine>> runs = [];
    private readonly TokenLine[] pending = new TokenLine[RunLength];
    private int filled;

    public int Count { get; private set; }

    public void Add(TokenLine line)
    {
        if (filled == pending.Length)
        {
            Flush();
        }
        pending[filled++] = line;
        Count++;
    }

    /// <summary>Lines [start, start + count) of an older table, as they are.</summary>
    public void AddRange(LineTable table, int start, int count)
    {
        foreach (ArraySegment<TokenLine> run in table.Slice(start, count))
        {
            if (run.Count >= SharedAtLeast)
            {
                Flush();
                runs.Add(run);
                Count += run.Count;
            }
            else
            {
                foreach (TokenLine line in run)
                {
                    Add(line);
                }
            }
        }
    }

    /// <summary>Lines with no colours and no state yet: changed lines a pass stopped before, or has not read yet.</summary>
    public void AddBlank(int count)
    {
        while (count > 0)
        {
            int run = Math.Min(count, Blanks.Length);
            if (run >= SharedAtLeast)
            {
                Flush();
                runs.Add(new ArraySegment<TokenLine>(Blanks, 0, run));
                Count += run;
            }
            else
            {
                for (int i = 0; i < run; i++)
                {
                    Add(Blanks[0]);
                }
            }
            count -= run;
        }
    }

    public LineTable Build(int staleFrom)
    {
        Flush();
        return new LineTable([.. runs], staleFrom);
    }

    private void Flush()
    {
        if (filled > 0)
        {
            runs.Add(new ArraySegment<TokenLine>(pending[..filled]));
            filled = 0;
        }
    }
}
