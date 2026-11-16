using System.Text;
using Passenger.Dap;

namespace Passenger.App.Run;

/// <summary>What a program's output says of its needing a terminal, read from an exception as .NET writes it (unhandled,
/// one the program wrote itself, or an inner one): Console.ReadKey's and Console.KeyAvailable's refusals of input that is
/// no terminal's, an IOException thrown by the console's own calls (Windows' on a handle that is no console's), and
/// Spectre.Console's refusals to prompt or read where its terminal is not interactive. Each of the program's two streams
/// is read apart, a line at a time as it comes in pieces; the first need heard is kept. An exception a debugger stopped at
/// is read the same way, as .NET would write it.</summary>
public sealed class TerminalNeeds
{
    /// <summary>The most of a line read: an exception's line is short, and a longer line's rest is passed over.</summary>
    private const int LongestLine = 1024;

    private const string ReadKey = "System.InvalidOperationException: Cannot read keys when either application does not have a console or when console input has been redirected";
    private const string KeyAvailable = "System.InvalidOperationException: Cannot see if a key has been pressed when either application does not have a console or when console input has been redirected";
    private const string ConsoleFailed = "System.IO.IOException: ";
    private const string SpectreCannotShow = "System.NotSupportedException: Cannot show ";
    private const string SpectreUninteractive = " since the current terminal isn't interactive.";
    private const string SpectreInput = "System.InvalidOperationException: Failed to read input in non-interactive mode.";
    private const string SpectreFrame = "Spectre.Console.";
    private const string ConsoleFrame = "System.Console.";
    private const string ConsolePalFrame = "System.ConsolePal.";

    private readonly Stream[] streams = [new(), new()];

    /// <summary>The call the program needs a terminal for ("Console.ReadKey", "Spectre.Console's selection prompt"), once
    /// heard; null until then.</summary>
    public string? Need { get; private set; }

    /// <summary>Takes in a piece of what the program wrote to its standard error (<paramref name="error"/>) or output; true
    /// once a need is heard.</summary>
    public bool Hear(ReadOnlySpan<char> text, bool error)
    {
        Stream stream = streams[error ? 1 : 0];
        while (Need is null && text.Length > 0)
        {
            int end = text.IndexOfAny('\n', '\r');
            ReadOnlySpan<char> piece = end < 0 ? text : text[..end];
            if (end < 0)
            {
                stream.Partial.Append(piece[..Math.Min(piece.Length, Math.Max(0, LongestLine - stream.Partial.Length))]);
                break;
            }
            if (stream.Partial.Length == 0)
            {
                Line(stream, piece[..Math.Min(piece.Length, LongestLine)]);
            }
            else
            {
                stream.Partial.Append(piece[..Math.Min(piece.Length, Math.Max(0, LongestLine - stream.Partial.Length))]);
                Line(stream, stream.Partial.ToString());
                stream.Partial.Clear();
            }
            text = text[(end + 1)..];
        }
        return Need is not null;
    }

    /// <summary>The call an exception a debugger stopped at says the program needs a terminal for, read as .NET writes the
    /// exception (its type and message, each inner one's, then its frames); null for any other.</summary>
    public static string? Thrown(DapExceptionDetails thrown)
    {
        StringBuilder written = new();
        Write(written, thrown, depth: 0);
        TerminalNeeds needs = new();
        needs.Hear(written.Append('\n').ToString(), error: true);
        return needs.Need;
    }

    /// <summary>An exception as its ToString writes it, its inner exception's within it, as deep as reason allows.</summary>
    private static void Write(StringBuilder written, DapExceptionDetails thrown, int depth)
    {
        written.Append(thrown.FullTypeName ?? thrown.TypeName).Append(": ").Append(thrown.Message);
        if (depth < 10 && thrown.InnerException is [{ } inner, ..])
        {
            written.Append("\n ---> ");
            Write(written, inner, depth + 1);
            written.Append("\n   --- End of inner exception stack trace ---");
        }
        if (thrown.StackTrace is { Length: > 0 } trace)
        {
            written.Append('\n').Append(trace);
        }
    }

    /// <summary>A whole line of one stream: an exception's line names a need, or one its frames decide (<see cref="Waiting"/>);
    /// an empty line, a "\r\n"'s second half among them, says nothing.</summary>
    private void Line(Stream stream, ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
        {
            return;
        }
        if (stream.Waiting is { } waiting)
        {
            ReadOnlySpan<char> frame = Frame(line);
            if (waiting == Waiting.SpectreInput)
            {
                Need = frame.StartsWith(SpectreFrame, StringComparison.Ordinal) ? "Spectre.Console's input" : null;
                stream.Waiting = null;
                return;
            }
            if (ConsoleMember(frame) is { } member)
            {
                // The outermost of the console's own frames is the call the program made.
                stream.Called = member;
                return;
            }
            Need = stream.Called is { } called ? $"Console.{called}" : null;
            stream.Waiting = null;
            stream.Called = null;
            if (Need is not null)
            {
                return;
            }
        }
        if (line.Contains(ReadKey, StringComparison.Ordinal))
        {
            Need = "Console.ReadKey";
        }
        else if (line.Contains(KeyAvailable, StringComparison.Ordinal))
        {
            Need = "Console.KeyAvailable";
        }
        else if (Shown(line) is { } shown)
        {
            Need = $"Spectre.Console's {shown}";
        }
        else if (line.Contains(SpectreInput, StringComparison.Ordinal))
        {
            stream.Waiting = Waiting.SpectreInput;
        }
        else if (line.Contains(ConsoleFailed, StringComparison.Ordinal))
        {
            stream.Waiting = Waiting.ConsoleFailed;
        }
    }

    /// <summary>What Spectre.Console's refusal on a line says it could not show ("selection prompt"), or null.</summary>
    private static string? Shown(ReadOnlySpan<char> line)
    {
        int at = line.IndexOf(SpectreCannotShow, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        ReadOnlySpan<char> rest = line[(at + SpectreCannotShow.Length)..];
        int end = rest.IndexOf(SpectreUninteractive, StringComparison.Ordinal);
        return end > 0 ? rest[..end].ToString() : null;
    }

    /// <summary>The method a line of a stack trace names, with its type ("System.ConsolePal.Clear"), or null for a line
    /// that is no frame.</summary>
    private static ReadOnlySpan<char> Frame(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> trimmed = line.TrimStart();
        if (!trimmed.StartsWith("at ", StringComparison.Ordinal))
        {
            return default;
        }
        ReadOnlySpan<char> named = trimmed[3..];
        int open = named.IndexOf('(');
        return open > 0 ? named[..open] : default;
    }

    /// <summary>The member of the console's own a frame is in, as the program calls it ("KeyAvailable" for
    /// get_KeyAvailable), or null for a frame of anything else, its streams' among them, whose failure is a pipe's.</summary>
    private static string? ConsoleMember(ReadOnlySpan<char> frame)
    {
        ReadOnlySpan<char> member = frame.StartsWith(ConsolePalFrame, StringComparison.Ordinal) ? frame[ConsolePalFrame.Length..]
            : frame.StartsWith(ConsoleFrame, StringComparison.Ordinal) ? frame[ConsoleFrame.Length..]
            : default;
        if (member.IsEmpty || member.Contains('.'))
        {
            return null;
        }
        return (member.StartsWith("get_", StringComparison.Ordinal) || member.StartsWith("set_", StringComparison.Ordinal) ? member[4..] : member).ToString();
    }

    /// <summary>An exception's line whose frames say whether it is a need.</summary>
    private enum Waiting
    {
        /// <summary>An IOException, the console's own where its frames begin in the console's calls.</summary>
        ConsoleFailed,

        /// <summary>A refusal of input in Spectre.Console's words, a need where its first frame is Spectre.Console's.</summary>
        SpectreInput,
    }

    /// <summary>One of the program's streams: the line it has begun, and the exception whose frames are read.</summary>
    private sealed class Stream
    {
        public StringBuilder Partial { get; } = new();

        public Waiting? Waiting { get; set; }

        /// <summary>The outermost console call among the frames read so far of an IOException's.</summary>
        public string? Called { get; set; }
    }
}
