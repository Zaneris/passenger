using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using Passenger.Core;

namespace Passenger.App.ToolWindows;

/// <summary>Where text written to a console came from: a program's output or errors, or the debugger's own words. Each
/// has its colour-code state of its own, so one stream's unfinished sequence never swallows another's text.</summary>
public enum ConsoleStream
{
    Output,
    Error,
    System,
}

/// <summary>A stretch of a batch of console text and the stream it came from.</summary>
public readonly record struct ConsoleRun(int Start, int Length, ConsoleStream Stream);

/// <summary>A batch of a program's text as its console took it in, and the stretches of it each of the program's streams
/// wrote, in the order they came: a line one stream began may end after another stream's text.</summary>
public sealed record ConsoleWritten(string Text, IReadOnlyList<ConsoleRun> Runs);

/// <summary>What one read of a program's stream brought, as it came: the reader's own buffer, which its next read
/// writes over.</summary>
public delegate void ConsoleRead(ReadOnlySpan<char> text);

/// <summary>What a person types into a console goes to the program through these: a line at a time with its line break,
/// then the end of its input (Ctrl+D). Called on the UI thread; neither may wait on the program.</summary>
public sealed record ConsoleInput(Action<string> Send, Action Close);

/// <summary>The one way output reaches a console (Run, Build and Debug alike), from any thread: terminal control codes are
/// taken out as it comes, each stream's state (a pump's its own) kept from one write to the next, so a sequence split
/// between two reads never shows as text, nor a "\r\n" split so as an extra line. A program's output is read in by
/// <see cref="PumpAsync"/>, the one loop from a reader into a console. The console takes what waits a batch at a time,
/// at most once a frame and below the window's input and drawing, so a program that writes without pause never floods
/// the window; what waits is never more than the console keeps, the oldest dropped first.</summary>
public sealed class ConsoleSink
{
    /// <summary>A frame's time: batches come no closer together than this.</summary>
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>The longest line a pump tells its reader of whole: a longer one is told in pieces of this length.</summary>
    private const int LongestLine = 16 * 1024;

    /// <summary>The most room, in characters, what waits keeps from one batch to the next.</summary>
    private const int KeptRoom = 64 * 1024;

    private readonly object gate = new();
    private readonly Action take;
    private readonly ControlCodes[] streams = [new(), new(), new()];
    private StringBuilder waiting = new();
    private List<ConsoleRun> runs = [];
    private int limit;
    private bool queued;
    private long lastTaken;

    /// <param name="take">The console's taking of a batch (<see cref="Take"/>), posted to the UI thread.</param>
    /// <param name="limit">How much text the console keeps, in characters.</param>
    internal ConsoleSink(Action take, int limit)
    {
        this.take = take;
        this.limit = limit;
    }

    /// <summary>How much text the console keeps, in characters: what waits beyond it could never be shown.</summary>
    internal int Limit
    {
        set
        {
            lock (gate)
            {
                limit = value;
            }
        }
    }

    /// <summary>Adds what a stream wrote; from any thread.</summary>
    public void Write(ReadOnlySpan<char> text, ConsoleStream stream = ConsoleStream.Output) => Write(text, stream, streams[(int)stream]);

    private void Write(ReadOnlySpan<char> text, ConsoleStream stream, ControlCodes codes, CancellationToken letGo = default)
    {
        bool post;
        lock (gate)
        {
            // Read under the gate, so nothing let go of lands after a Drop that follows.
            if (letGo.IsCancellationRequested)
            {
                return;
            }
            int before = waiting.Length;
            codes.Strip(text, waiting);
            post = Added(before, stream);
        }
        if (post)
        {
            Post();
        }
    }

    /// <inheritdoc cref="Write(ReadOnlySpan{char}, ConsoleStream)"/>
    public void Write(string text, ConsoleStream stream = ConsoleStream.Output) => Write(text.AsSpan(), stream);

    /// <summary>A stream has ended (its program has): a '\r' it held back, in case a '\n' came next, is let go, and an
    /// unfinished control sequence is dropped, so the next program's text starts clean.</summary>
    public void End(ConsoleStream stream) => End(stream, streams[(int)stream]);

    private void End(ConsoleStream stream, ControlCodes codes)
    {
        bool post;
        lock (gate)
        {
            int before = waiting.Length;
            codes.End(waiting);
            post = Added(before, stream);
        }
        if (post)
        {
            Post();
        }
    }

    /// <summary>Reads a program's stream into the console as it comes, a read at a time (a prompt with no line end too),
    /// with colour-code state of its own, and ends it there however it ends, a '\r' held back let go. Each line read is
    /// told to <paramref name="lines"/>, each read to <paramref name="chunks"/>. Once <paramref name="letGo"/> is cancelled
    /// what comes is dropped; <paramref name="shown"/> writes its text as shown (secrets masked), whole lines at a time, and
    /// <paramref name="prefix"/> each whole line under it (<see cref="Prefixed"/>). Any thread; never throws.</summary>
    public async Task PumpAsync(TextReader reader, ConsoleStream stream, CancellationToken letGo = default, Action<string>? lines = null,
        ConsoleRead? chunks = null, Func<string, string>? shown = null, string? prefix = null)
    {
        ControlCodes codes = new();
        LineSplitter? split = lines is null ? null : new LineSplitter(lines);
        Held? held = shown is null ? null : new Held();
        Prefixed? prefixed = prefix is null ? null : new Prefixed(prefix);
        char[] buffer = new char[8192];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, letGo).ConfigureAwait(false)) > 0)
            {
                if (letGo.IsCancellationRequested)
                {
                    return;
                }
                ReadOnlySpan<char> came = buffer.AsSpan(0, read);
                if (prefixed is not null)
                {
                    // Whole lines, each under the prefix, through the console's own state for the stream, as a merged log's.
                    if (prefixed.Lines(came) is { Length: > 0 } whole)
                    {
                        Write(whole, stream, streams[(int)stream], letGo);
                    }
                }
                else if (held is null)
                {
                    Write(came, stream, codes, letGo);
                }
                else if (held.Whole(came) is { } whole)
                {
                    Write(shown!(whole), stream, codes, letGo);
                }
                split?.Add(came);
                chunks?.Invoke(came);
            }
        }
        catch (OperationCanceledException) when (letGo.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ended) when (ended is IOException or ObjectDisposedException)
        {
            // Its pipe broken as its program was killed, or its reader put away: the stream has ended all the same.
        }
        if (prefixed is not null)
        {
            // The console's state for the stream is every source's in the merged log: its last line let out, not ended.
            if (prefixed.Rest() is { Length: > 0 } last)
            {
                Write(last, stream, streams[(int)stream], letGo);
            }
            return;
        }
        if (held?.Rest() is { } rest)
        {
            Write(shown!(rest), stream, codes, letGo);
        }
        End(stream, codes);
        split?.End();
    }

    /// <summary>A masked stream's text after its last line break, until a read ends a line or it reaches
    /// <see cref="LongestLine"/>; the lines a read ends are made one string with what waited, for the mask.</summary>
    private sealed class Held
    {
        private char[] text = new char[256];
        private int length;

        /// <summary>What waited and the whole lines a read ends (all it holds past <see cref="LongestLine"/>), or null
        /// when it ends none; what follows the read's last break waits.</summary>
        public string? Whole(ReadOnlySpan<char> came)
        {
            int last = came.LastIndexOfAny('\n', '\r');
            if (last < 0 && length + came.Length < LongestLine)
            {
                Keep(came);
                return null;
            }
            ReadOnlySpan<char> taken = last < 0 ? came : came[..(last + 1)];
            string whole = string.Concat(text.AsSpan(0, length), taken);
            length = 0;
            Keep(came[taken.Length..]);
            return whole;
        }

        /// <summary>What still waits at the stream's end, or null.</summary>
        public string? Rest()
        {
            string? rest = length > 0 ? new string(text, 0, length) : null;
            length = 0;
            return rest;
        }

        private void Keep(ReadOnlySpan<char> more)
        {
            if (length + more.Length > text.Length)
            {
                Array.Resize(ref text, Math.Max(2 * text.Length, length + more.Length));
            }
            more.CopyTo(text.AsSpan(length));
            length += more.Length;
        }
    }

    /// <summary>A stream's lines, each ending at its '\n', written under a prefix with the '\r's before that dropped (a
    /// container's in a merged log): what follows a read's last '\n' waits for the next, a line past <see cref="LongestLine"/>
    /// let out in pieces of that length, and what still waits let out at the end (<see cref="Rest"/>).</summary>
    private sealed class Prefixed(string prefix)
    {
        private char[] line = new char[256];
        private int length;
        private char[] written = new char[1024];
        private int count;

        /// <summary>The lines a read ends, with what waited before the first, and the pieces of a line grown past the
        /// longest, each under the prefix; empty when it lets out none.</summary>
        public ReadOnlySpan<char> Lines(ReadOnlySpan<char> came)
        {
            count = 0;
            for (int end = came.IndexOf('\n'); end >= 0; end = came.IndexOf('\n'))
            {
                Put(line.AsSpan(0, length), came[..end], ended: true);
                length = 0;
                came = came[(end + 1)..];
            }
            if (length + came.Length > line.Length)
            {
                Array.Resize(ref line, Math.Max(2 * line.Length, length + came.Length));
            }
            came.CopyTo(line.AsSpan(length));
            length += came.Length;
            int from = 0;
            for (; length - from >= LongestLine; from += LongestLine)
            {
                Put(line.AsSpan(from, LongestLine), [], ended: false);
            }
            if (from > 0)
            {
                line.AsSpan(from, length - from).CopyTo(line);
                length -= from;
            }
            return written.AsSpan(0, count);
        }

        /// <summary>What waits at the stream's end as its last line under the prefix; empty when nothing does.</summary>
        public ReadOnlySpan<char> Rest()
        {
            count = 0;
            if (length > 0)
            {
                Put(line.AsSpan(0, length), [], ended: true);
                length = 0;
            }
            return written.AsSpan(0, count);
        }

        /// <summary>A line written under the prefix, from what waited and what came, its '\r's at its end dropped where it
        /// <paramref name="ended"/> (a piece of a longer line keeps them).</summary>
        private void Put(ReadOnlySpan<char> waited, ReadOnlySpan<char> rest, bool ended)
        {
            int needed = count + prefix.Length + waited.Length + rest.Length + 1;
            if (needed > written.Length)
            {
                Array.Resize(ref written, Math.Max(2 * written.Length, needed));
            }
            prefix.CopyTo(written.AsSpan(count));
            count += prefix.Length;
            int start = count;
            waited.CopyTo(written.AsSpan(count));
            count += waited.Length;
            rest.CopyTo(written.AsSpan(count));
            count += rest.Length;
            while (ended && count > start && written[count - 1] == '\r')
            {
                count--;
            }
            written[count++] = '\n';
        }
    }

    /// <summary>A stream's text told a line at a time as a reader's ReadLine ends them, at '\r', '\n' or "\r\n" (one split
    /// between two reads too), the last line at the end, and one past <see cref="LongestLine"/> in pieces.</summary>
    private sealed class LineSplitter(Action<string> told)
    {
        private readonly StringBuilder line = new();
        private bool afterReturn;

        public void Add(ReadOnlySpan<char> text)
        {
            while (text.Length > 0)
            {
                if (afterReturn)
                {
                    afterReturn = false;
                    if (text[0] == '\n')
                    {
                        text = text[1..];
                        continue;
                    }
                }
                int end = text.IndexOfAny('\r', '\n');
                int taken = Math.Min(end < 0 ? text.Length : end, LongestLine - line.Length);
                line.Append(text[..taken]);
                if (line.Length == LongestLine || taken == end)
                {
                    Tell();
                }
                if (taken == end)
                {
                    afterReturn = text[end] == '\r';
                    taken++;
                }
                text = text[taken..];
            }
        }

        public void End()
        {
            if (line.Length > 0)
            {
                Tell();
            }
        }

        private void Tell()
        {
            told(line.ToString());
            line.Clear();
        }
    }

    /// <summary>The waiting text and the streams it came from, taken for the console; null when nothing waits. UI thread.</summary>
    internal (string Text, List<ConsoleRun> Runs)? Take()
    {
        lock (gate)
        {
            queued = false;
            lastTaken = Stopwatch.GetTimestamp();
            if (waiting.Length == 0)
            {
                return null;
            }
            string text = waiting.ToString();
            List<ConsoleRun> taken = runs;
            // Its room kept for the next batch, unless a flood made it larger than a batch should need.
            if (waiting.Capacity > KeptRoom)
            {
                waiting = new StringBuilder();
            }
            else
            {
                waiting.Clear();
            }
            runs = [];
            return (text, taken);
        }
    }

    /// <summary>Drops what waits, for a console cleared.</summary>
    internal void Drop()
    {
        lock (gate)
        {
            waiting.Clear();
            runs.Clear();
        }
    }

    /// <summary>Notes the text a write added, keeps what waits within the console's limit, and says whether the console
    /// must be told: once for all that comes before it takes the batch.</summary>
    private bool Added(int before, ConsoleStream stream)
    {
        int added = waiting.Length - before;
        if (added == 0)
        {
            return false;
        }
        if (runs.Count > 0 && runs[^1].Stream == stream && runs[^1].Start + runs[^1].Length == before)
        {
            runs[^1] = runs[^1] with { Length = runs[^1].Length + added };
        }
        else
        {
            runs.Add(new ConsoleRun(before, added, stream));
        }
        // Twice the limit before cutting, so a flood moves the waiting text along now and then rather than at every write.
        if (waiting.Length > 2 * limit)
        {
            Cut(waiting.Length - limit);
        }
        if (queued)
        {
            return false;
        }
        queued = true;
        return true;
    }

    private void Cut(int length)
    {
        waiting.Remove(0, length);
        List<ConsoleRun> kept = [];
        foreach (ConsoleRun run in runs)
        {
            int start = Math.Max(run.Start, length);
            int end = run.Start + run.Length;
            if (end > start)
            {
                kept.Add(run with { Start = start - length, Length = end - start });
            }
        }
        runs = kept;
    }

    /// <summary>Has the console take the batch on the UI thread a frame after it took the last, below input and drawing.
    /// The wait is background work, so a screenshot or a test settles on the output it brings.</summary>
    private void Post()
    {
        TimeSpan wait;
        lock (gate)
        {
            wait = Frame - Stopwatch.GetElapsedTime(lastTaken);
        }
        if (wait <= TimeSpan.Zero)
        {
            UiThread.Post(take, DispatcherPriority.Background);
            return;
        }
        IDisposable held = BackgroundWork.Scope();
        IdeErrors.Observe(Task.Delay(wait).ContinueWith(_ =>
        {
            UiThread.Post(take, DispatcherPriority.Background);
            held.Dispose();
        }, TaskScheduler.Default));
    }

    /// <summary>One stream's terminal control sequences taken out of its text, in the shapes ECMA-48 gives them and xterm
    /// reads them: CSI (colours, cursor moves) to its final byte, OSC (titles, links) to BEL or ST, DCS, SOS, PM and APC
    /// strings to ST, and the other escapes. A sequence broken by a line break, or longer than any real one, is given up
    /// and the text after it kept.</summary>
    private sealed class ControlCodes
    {
        private const char Escape = '\u001b';
        private const char Bell = '\u0007';
        private const int Longest = 4096;

        private State state;
        private int length;
        private bool carriageReturn;

        private enum State
        {
            Text,
            Escape,
            EscapeIntermediate,
            Csi,
            String,
            StringEscape,
        }

        public void Strip(ReadOnlySpan<char> text, StringBuilder into)
        {
            int i = 0;
            while (i < text.Length)
            {
                if (state == State.Text)
                {
                    if (carriageReturn)
                    {
                        carriageReturn = false;
                        if (text[i] != '\n')
                        {
                            into.Append('\r');
                        }
                    }
                    int found = text[i..].IndexOfAny(Escape, '\r', Bell);
                    int end = found < 0 ? text.Length : i + found;
                    into.Append(text[i..end]);
                    if (found < 0)
                    {
                        return;
                    }
                    i = end + 1;
                    if (text[end] == '\r')
                    {
                        // Held back until the next character says whether it begins a line break.
                        carriageReturn = true;
                    }
                    else if (text[end] == Escape)
                    {
                        state = State.Escape;
                        length = 0;
                    }
                    continue;
                }
                if (Take(text[i]))
                {
                    i++;
                }
                else
                {
                    // Not part of a sequence: the sequence is given up, and the character is text again.
                    state = State.Text;
                }
            }
        }

        public void End(StringBuilder into)
        {
            if (carriageReturn)
            {
                into.Append('\r');
            }
            carriageReturn = false;
            state = State.Text;
        }

        /// <summary>Takes one character of a sequence under way; false when it cannot belong to it.</summary>
        private bool Take(char c)
        {
            if (++length > Longest || c is '\n' or '\r')
            {
                return false;
            }
            switch (state)
            {
                case State.Escape:
                    state = c switch
                    {
                        '[' => State.Csi,
                        ']' or 'P' or 'X' or '^' or '_' => State.String,
                        >= ' ' and <= '/' => State.EscapeIntermediate,
                        >= '0' and <= '~' => State.Text,
                        Escape => State.Escape,
                        _ => State.Text,
                    };
                    return c is >= ' ' and <= '~' or Escape;
                case State.EscapeIntermediate:
                    if (c is >= ' ' and <= '/')
                    {
                        return true;
                    }
                    return Final(c, '0');
                case State.Csi:
                    if (c is >= '0' and <= '?' or >= ' ' and <= '/')
                    {
                        return true;
                    }
                    return Final(c, '@');
                case State.String:
                    if (c == Bell)
                    {
                        state = State.Text;
                    }
                    else if (c == Escape)
                    {
                        state = State.StringEscape;
                    }
                    return true;
                case State.StringEscape:
                    if (c == '\\')
                    {
                        state = State.Text;
                        return true;
                    }
                    // Another escape begins: the string ended with this one.
                    state = State.Escape;
                    return Take(c);
                default:
                    return false;
            }
        }

        /// <summary>A sequence's last character, from <paramref name="first"/> to '~'; ESC begins another instead.</summary>
        private bool Final(char c, char first)
        {
            if (c >= first && c <= '~')
            {
                state = State.Text;
                return true;
            }
            if (c == Escape)
            {
                state = State.Escape;
                length = 0;
                return true;
            }
            return false;
        }
    }
}
