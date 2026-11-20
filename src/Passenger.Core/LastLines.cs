namespace Passenger.Core;

/// <summary>The last lines a program wrote, and the exception named above the last stack trace among them, kept however many
/// frames follow it: what a .NET program's end says when an exception ends it, its report's first lines naming the exception
/// and each inner one, the rest frames. Read by one thread at a time.</summary>
/// <param name="kept">How many of the last lines are kept.</param>
public sealed class LastLines(int kept)
{
    private readonly Queue<string> lines = new();

    /// <summary>The lines naming an exception and its inner ones before a trace's first frame: those coming now, and those
    /// of the last trace to have come.</summary>
    private readonly List<string> naming = [];
    private List<string> thrown = [];

    /// <summary>Whether the last line written was a stack trace's.</summary>
    private bool inTrace;

    public void Heard(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        lines.Enqueue(line);
        while (lines.Count > kept)
        {
            lines.Dequeue();
        }
        inTrace = IsFrame(line);
        if (inTrace)
        {
            if (naming.Count > 0)
            {
                thrown = [.. naming];
                naming.Clear();
            }
        }
        else if (line.TrimStart().StartsWith("--->", StringComparison.Ordinal) && naming.Count is > 0 and < 5)
        {
            naming.Add(line);
        }
        else
        {
            naming.Clear();
            naming.Add(line);
        }
    }

    /// <summary>The exception the program's writing ended on, its inner ones after it, where a stack trace was the last it
    /// wrote; null where it ended on anything else.</summary>
    public string? Thrown => inTrace && thrown.Count > 0 ? string.Join(" ", thrown.Select(t => t.Trim())) : null;

    /// <summary>What the program last said: <see cref="Thrown"/>, else its last line; null where it wrote nothing.</summary>
    public string? Last => Thrown ?? lines.LastOrDefault();

    /// <summary>The last lines, after the lines naming <see cref="Thrown"/> where its frames left them out.</summary>
    public IReadOnlyList<string> Kept => [.. (Thrown is null ? [] : thrown.Where(t => !lines.Contains(t))), .. lines];

    /// <summary>Whether a line is a stack trace's: a frame, or the end of an inner exception's frames.</summary>
    private static bool IsFrame(string line)
    {
        ReadOnlySpan<char> trimmed = line.AsSpan().TrimStart();
        return trimmed.StartsWith("at ", StringComparison.Ordinal) || trimmed.StartsWith("--- ", StringComparison.Ordinal);
    }
}
