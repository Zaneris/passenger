using System.Text;

namespace Passenger.App.ToolWindows;

/// <summary>What a console shows of a program told a whole line at a time, each stream's apart, one rule for every reader
/// of a program's lines (Run's console's readers, a part of a run of several waiting for a line of its own): a line ends at
/// '\n' or a lone '\r' (the console has made "\r\n" one '\n'), and one longer than <see cref="LongestLine"/> is told in
/// pieces. UI thread, as the console writes.</summary>
public sealed class ConsoleLines : IDisposable
{
    /// <summary>The longest line told whole; a longer one is told in pieces, so what a program writes with no line end
    /// is never held whole, nor copied again with each batch, while someone listens.</summary>
    public const int LongestLine = 16 * 1024;

    private readonly OutputView console;
    private readonly Action<string> heard;
    private readonly StringBuilder[] partialLines = [new(), new()];
    private readonly bool programs;

    /// <summary>Hears each whole line the console is given from now, in <paramref name="heard"/>, until disposed; with
    /// <paramref name="programs"/>, only those its program wrote, never the IDE's own words about it (what it started, how it ended).</summary>
    public ConsoleLines(OutputView console, Action<string> heard, bool programs = false)
    {
        this.console = console;
        this.heard = heard;
        this.programs = programs;
        console.Written += OnWritten;
    }

    private void OnWritten(object? sender, ConsoleWritten written)
    {
        foreach (ConsoleRun run in written.Runs)
        {
            if (programs && run.Stream == ConsoleStream.System)
            {
                continue;
            }
            Split(written.Text.AsSpan(run.Start, run.Length), partialLines[run.Stream == ConsoleStream.Error ? 1 : 0], heard);
        }
    }

    /// <summary>Tells what each stream began and never ended, as its program's end does: its last line, written with no
    /// line break after it.</summary>
    public void Rest()
    {
        foreach (StringBuilder partialLine in partialLines.Where(p => p.Length > 0))
        {
            heard(partialLine.ToString());
            partialLine.Clear();
        }
    }

    public void Dispose() => console.Written -= OnWritten;

    /// <summary>Tells each line a stream's stretch of a batch ends, after what that stream began before it in
    /// <paramref name="partialLine"/>, which keeps what it begins.</summary>
    public static void Split(ReadOnlySpan<char> text, StringBuilder partialLine, Action<string> line)
    {
        ReadOnlySpan<char> rest = text;
        while (rest.Length > 0)
        {
            int end = rest.IndexOfAny('\n', '\r');
            int taken = Math.Min(end < 0 ? rest.Length : end, LongestLine - partialLine.Length);
            partialLine.Append(rest[..taken]);
            if (taken == end || partialLine.Length == LongestLine)
            {
                line(partialLine.ToString());
                partialLine.Clear();
            }
            rest = rest[(taken == end ? taken + 1 : taken)..];
        }
    }
}
