using System.Buffers;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Terminal;

namespace Passenger.App.ToolWindows;

/// <summary>Where text written to a console came from: a program's output or errors, or the debugger's own words. Each
/// has its control-code state of its own, so one stream's unfinished sequence never swallows another's text, nor its
/// colours another's.</summary>
public enum ConsoleStream
{
    Output,
    Error,
    System,
}

/// <summary>A stretch of a batch of console text, the stream it came from and the colours and style its program gave it
/// (SGR), plain where it gave none.</summary>
public readonly record struct ConsoleRun(int Start, int Length, ConsoleStream Stream)
{
    public CellStyle Style { get; init; } = CellStyle.Plain;
}

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
/// read as they come by the terminal's own reader (<see cref="SequenceReader"/>), the colours and styles a program sets
/// (SGR) kept with its text as runs and every other sequence left out, each stream's state (a pump's its own) kept from one
/// write to the next, so a sequence split between two reads never shows as text, nor a "\r\n" split so as an extra line.
/// What it costs is a run for each change of colour, never anything for each character. A program's output is read in by
/// <see cref="PumpAsync"/>, the one loop from a reader into a console. The console takes what waits a batch at a time,
/// at most once a frame and below the window's input and drawing, so a program that writes without pause never floods
/// the window; what waits is never more than the console keeps, the oldest dropped first.</summary>
public sealed class ConsoleSink
{
    /// <summary>A frame's time: batches come no closer together than this.</summary>
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>The longest line a pump tells its reader of whole: a longer one is told in pieces of this length.</summary>
    private const int LongestLine = 16 * 1024;

    /// <summary>The most room, in characters, what waits keeps from one batch to the next once a flood is over.</summary>
    private const int KeptRoom = 64 * 1024;

    private readonly object gate = new();
    private readonly Action take;
    private readonly ControlCodes[] streams = [new(), new(), new()];

    /// <summary>Each stream's way into what waits.</summary>
    private readonly Waiting[] into;
    private Pending waiting = new();
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
        into = [new(this, ConsoleStream.Output), new(this, ConsoleStream.Error), new(this, ConsoleStream.System)];
    }

    /// <summary>Tells a program whose output a console reads that .NET's console may write its colours there
    /// (<see cref="RuntimeVariables.AnsiColorRedirection"/>), unless the environment it starts with names that already.</summary>
    public static void TellColours(IDictionary<string, string?> environment) => environment.TryAdd(RuntimeVariables.AnsiColorRedirection, "1");

    /// <summary><see cref="TellColours(IDictionary{string, string?})"/> for the variables a program is given over Passenger's
    /// own environment, which it inherits.</summary>
    public static IReadOnlyDictionary<string, string> TellColours(IReadOnlyDictionary<string, string> given) =>
        Environment.GetEnvironmentVariable(RuntimeVariables.AnsiColorRedirection) is not null
            || given.Keys.Any(k => k.Equals(RuntimeVariables.AnsiColorRedirection, StringComparison.OrdinalIgnoreCase))
            ? given
            : new Dictionary<string, string>(given) { [RuntimeVariables.AnsiColorRedirection] = "1" };

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
            codes.Read(text, into[(int)stream]);
            post = Added(before);
        }
        if (post)
        {
            Post();
        }
    }

    /// <summary>Text read already, with its runs (a masked stream's), added as it is.</summary>
    private void Write(Styled text, ConsoleStream stream, CancellationToken letGo)
    {
        bool post;
        lock (gate)
        {
            if (letGo.IsCancellationRequested)
            {
                return;
            }
            int before = waiting.Length;
            text.CopyTo(into[(int)stream]);
            post = Added(before);
        }
        if (post)
        {
            Post();
        }
    }

    /// <summary>A merged log's whole lines (<see cref="Prefixed"/>), each under its source's name in plain text, read
    /// through the source's own state, so one source's colours never reach another's lines or a name.</summary>
    private void Write(Prefixed lines, ConsoleStream stream, ControlCodes codes, CancellationToken letGo)
    {
        bool post;
        lock (gate)
        {
            if (letGo.IsCancellationRequested)
            {
                return;
            }
            int before = waiting.Length;
            lines.ReadInto(codes, into[(int)stream]);
            post = Added(before);
        }
        if (post)
        {
            Post();
        }
    }

    /// <inheritdoc cref="Write(ReadOnlySpan{char}, ConsoleStream)"/>
    public void Write(string text, ConsoleStream stream = ConsoleStream.Output) => Write(text.AsSpan(), stream);

    /// <summary>A stream has ended (its program has): a '\r' it held back, in case a '\n' came next, is let go, and an
    /// unfinished control sequence and the colours in force are dropped, so the next program's text starts clean.</summary>
    public void End(ConsoleStream stream) => End(stream, streams[(int)stream]);

    private void End(ConsoleStream stream, ControlCodes codes)
    {
        bool post;
        lock (gate)
        {
            int before = waiting.Length;
            codes.End(into[(int)stream]);
            post = Added(before);
        }
        if (post)
        {
            Post();
        }
    }

    /// <summary>Reads a program's stream into the console as it comes, a read at a time (a prompt with no line end too),
    /// with control-code state of its own, and ends it there however it ends. Each line read is told to <paramref name="lines"/>,
    /// each read to <paramref name="chunks"/>. Once <paramref name="letGo"/> is cancelled what comes is dropped; <paramref name="shown"/>
    /// makes whole lines as shown (secrets masked) from their text read out of its codes, so a code splits no secret from
    /// its mask, and <paramref name="prefix"/> heads each whole line (<see cref="Prefixed"/>). Any thread; never throws.</summary>
    public async Task PumpAsync(TextReader reader, ConsoleStream stream, CancellationToken letGo = default, Action<string>? lines = null,
        ConsoleRead? chunks = null, Func<string, string>? shown = null, string? prefix = null)
    {
        ControlCodes codes = new();
        LineSplitter? split = lines is null ? null : new LineSplitter(lines);
        Held? held = shown is null ? null : new Held();
        Styled? styled = shown is null ? null : new Styled();
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
                    if (prefixed.Lines(came))
                    {
                        Write(prefixed, stream, codes, letGo);
                    }
                }
                else if (held is null)
                {
                    Write(came, stream, codes, letGo);
                }
                else if (held.Whole(came) is { IsEmpty: false } whole)
                {
                    Write(styled!.Read(whole, codes).Shown(shown!), stream, letGo);
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
            // Its last line let out whole, under the prefix, with nothing held back for an end to let go.
            if (prefixed.Rest())
            {
                Write(prefixed, stream, codes, letGo);
            }
            return;
        }
        if (held is not null && held.Rest() is { IsEmpty: false } rest)
        {
            Write(styled!.Read(rest, codes).Shown(shown!), stream, letGo);
        }
        End(stream, codes);
        split?.End();
    }

    /// <summary>A masked stream's text after its last line break, until a read ends a line or it reaches
    /// <see cref="LongestLine"/>; the lines a read ends are let out with what waited, read from where they wait, so the
    /// mask's string is the one copy of them made.</summary>
    private sealed class Held
    {
        private char[] text = new char[256];
        private int length;

        /// <summary>How much of <see cref="text"/> was let out at the last read, dropped at the next.</summary>
        private int letOut;

        /// <summary>What waited and the whole lines a read ends (all it holds past <see cref="LongestLine"/>), or nothing
        /// when it ends none: what follows the read's last break waits. Read before the next read, which writes over it.</summary>
        public ReadOnlySpan<char> Whole(ReadOnlySpan<char> came)
        {
            Drop();
            int last = came.LastIndexOfAny('\n', '\r');
            Keep(came);
            if (last < 0 && length < LongestLine)
            {
                return [];
            }
            letOut = last < 0 ? length : length - (came.Length - (last + 1));
            return text.AsSpan(0, letOut);
        }

        /// <summary>What still waits at the stream's end, or nothing.</summary>
        public ReadOnlySpan<char> Rest()
        {
            Drop();
            letOut = length;
            return text.AsSpan(0, length);
        }

        private void Drop()
        {
            if (letOut == 0)
            {
                return;
            }
            text.AsSpan(letOut, length - letOut).CopyTo(text);
            length -= letOut;
            letOut = 0;
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
    /// let out in pieces of that length, and what still waits let out at the end (<see cref="Rest"/>). The lines a read lets
    /// out are kept until they are read into the console (<see cref="ReadInto"/>).</summary>
    private sealed class Prefixed(string prefix)
    {
        private readonly List<int> ends = [];
        private char[] line = new char[256];
        private int length;
        private char[] written = new char[1024];
        private int count;

        /// <summary>Takes a read: whether it lets out a line, with what waited before the first, or a piece of a line grown
        /// past the longest.</summary>
        public bool Lines(ReadOnlySpan<char> came)
        {
            Clear();
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
            return ends.Count > 0;
        }

        /// <summary>Takes the stream's end: whether what waits makes a last line.</summary>
        public bool Rest()
        {
            Clear();
            if (length > 0)
            {
                Put(line.AsSpan(0, length), [], ended: true);
                length = 0;
            }
            return ends.Count > 0;
        }

        /// <summary>The lines let out, each under the prefix in plain text and read through <paramref name="codes"/>.</summary>
        public void ReadInto(ControlCodes codes, IStyledInto into)
        {
            int start = 0;
            foreach (int end in ends)
            {
                into.Put(prefix, CellStyle.Plain);
                codes.Read(written.AsSpan(start, end - start), into);
                start = end;
            }
        }

        private void Clear()
        {
            count = 0;
            ends.Clear();
        }

        /// <summary>A line from what waited and what came, its '\r's at its end dropped where it <paramref name="ended"/> (a
        /// piece of a longer line keeps them).</summary>
        private void Put(ReadOnlySpan<char> waited, ReadOnlySpan<char> rest, bool ended)
        {
            int needed = count + waited.Length + rest.Length + 1;
            if (needed > written.Length)
            {
                Array.Resize(ref written, Math.Max(2 * written.Length, needed));
            }
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
            ends.Add(count);
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
            waiting.Clear();
            // Its room kept for the next batch while batches fill it; past a flood, a small batch lets the rest go.
            if (text.Length < KeptRoom / 4)
            {
                waiting.LetGoPast(KeptRoom);
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

    /// <summary>Keeps what waits within the console's limit once a write added to it, and says whether the console must
    /// be told: once for all that comes before it takes the batch.</summary>
    private bool Added(int before)
    {
        if (waiting.Length == before)
        {
            return false;
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

    /// <summary>Text in a stream's colours put after what waits, the last run lengthened where it is the same stream's in
    /// the same colours; under the gate.</summary>
    private void Append(ReadOnlySpan<char> text, ConsoleStream stream, CellStyle style)
    {
        int before = waiting.Length;
        waiting.Append(text);
        if (runs.Count > 0 && runs[^1].Stream == stream && runs[^1].Style == style && runs[^1].Start + runs[^1].Length == before)
        {
            runs[^1] = runs[^1] with { Length = runs[^1].Length + text.Length };
        }
        else
        {
            runs.Add(new ConsoleRun(before, text.Length, stream) { Style = style });
        }
    }

    private void Cut(int length)
    {
        waiting.Remove(length);
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

    /// <summary>The text waiting for the console, in chunks kept from batch to batch: what a stream writes is copied in once,
    /// with nothing made for it but a chunk where it outgrows those kept, never a larger copy of all that waits, and a batch
    /// is taken as one string. A chunk is short of the large object heap, which a flood would otherwise fill and empty.</summary>
    private sealed class Pending
    {
        private const int Chunk = 8 * 1024;

        private readonly List<char[]> chunks = [];

        /// <summary>Where the text begins in the first chunk, past what <see cref="Remove"/> dropped.</summary>
        private int start;

        public int Length { get; private set; }

        public void Append(ReadOnlySpan<char> text)
        {
            while (!text.IsEmpty)
            {
                int end = start + Length;
                if (end / Chunk == chunks.Count)
                {
                    chunks.Add(new char[Chunk]);
                }
                Span<char> room = chunks[end / Chunk].AsSpan(end % Chunk);
                int taken = Math.Min(room.Length, text.Length);
                text[..taken].CopyTo(room);
                Length += taken;
                text = text[taken..];
            }
        }

        /// <summary>Drops the first characters, each chunk they empty moved to the end as room for what comes.</summary>
        public void Remove(int count)
        {
            start += count;
            Length -= count;
            while (start >= Chunk)
            {
                char[] emptied = chunks[0];
                chunks.RemoveAt(0);
                chunks.Add(emptied);
                start -= Chunk;
            }
        }

        public void Clear()
        {
            Length = 0;
            start = 0;
        }

        /// <summary>Lets go of the chunks past the first <paramref name="room"/> characters', nothing waiting.</summary>
        public void LetGoPast(int room)
        {
            int kept = Math.Max(1, room / Chunk);
            if (chunks.Count > kept)
            {
                chunks.RemoveRange(kept, chunks.Count - kept);
            }
        }

        public override string ToString() => string.Create(Length, this, static (into, pending) =>
        {
            int at = pending.start;
            for (int chunk = 0; !into.IsEmpty; chunk++, at = 0)
            {
                ReadOnlySpan<char> part = pending.chunks[chunk].AsSpan(at, Math.Min(Chunk - at, into.Length));
                part.CopyTo(into);
                into = into[part.Length..];
            }
        });
    }

    /// <summary>Where a stream's text goes as it is read, a stretch of one style at a time.</summary>
    private interface IStyledInto
    {
        void Put(ReadOnlySpan<char> text, CellStyle style);
    }

    /// <summary>A stream's way into what waits for the console; under the gate.</summary>
    private sealed class Waiting(ConsoleSink sink, ConsoleStream stream) : IStyledInto
    {
        public void Put(ReadOnlySpan<char> text, CellStyle style)
        {
            if (!text.IsEmpty)
            {
                sink.Append(text, stream, style);
            }
        }
    }

    /// <summary>Text read and held apart with its runs, for what is shown of it (secrets masked) to be made from the text
    /// as the console shows it; kept for the next read, nothing made for one but its string.</summary>
    private sealed class Styled : IStyledInto
    {
        private readonly List<(int Start, int Length, CellStyle Style)> runs = [];
        private readonly StringBuilder text = new();

        /// <summary>The text as shown, once <see cref="Shown"/> made it.</summary>
        private string? shown;

        /// <summary>A stream's text read through its state, what was held before let go.</summary>
        public Styled Read(ReadOnlySpan<char> written, ControlCodes codes)
        {
            text.Clear();
            runs.Clear();
            shown = null;
            codes.Read(written, this);
            return this;
        }

        public void Put(ReadOnlySpan<char> put, CellStyle style)
        {
            if (put.IsEmpty)
            {
                return;
            }
            if (runs.Count > 0 && runs[^1].Style == style)
            {
                runs[^1] = runs[^1] with { Length = runs[^1].Length + put.Length };
            }
            else
            {
                runs.Add((text.Length, put.Length, style));
            }
            text.Append(put);
        }

        /// <summary>The text as <paramref name="show"/> makes it: each stretch it changed (a secret masked), found line by
        /// line, takes the colours where it begins, and the rest keep theirs.</summary>
        public Styled Shown(Func<string, string> show)
        {
            string read = text.ToString();
            shown = show(read);
            if (string.Equals(shown, read, StringComparison.Ordinal))
            {
                return this;
            }
            List<(int From, int To, int ShownFrom, int ShownTo)> changes = [];
            if (read.Count('\n') == shown.Count('\n'))
            {
                for (int from = 0, shownFrom = 0; from <= read.Length;)
                {
                    int to = read.IndexOf('\n', from) is >= 0 and int end ? end : read.Length;
                    int shownTo = shown.IndexOf('\n', shownFrom) is >= 0 and int shownEnd ? shownEnd : shown.Length;
                    Changed(from, to, shownFrom, shownTo);
                    from = to + 1;
                    shownFrom = shownTo + 1;
                }
            }
            else
            {
                Changed(0, read.Length, 0, shown.Length);
            }
            List<(int Start, int Length, CellStyle Style)> kept = [];
            int change = 0;
            int moved = 0;
            int changedTo = 0;
            CellStyle last = CellStyle.Plain;
            foreach ((int start, int length, CellStyle style) in runs)
            {
                last = style;
                int end = start + length;
                for (int at = Math.Max(start, changedTo); at < end;)
                {
                    if (change < changes.Count && changes[change].From == at)
                    {
                        (int from, int to, int shownFrom, int shownTo) = changes[change++];
                        kept.Add((shownFrom, shownTo - shownFrom, style));
                        moved += (shownTo - shownFrom) - (to - from);
                        at = changedTo = to;
                        continue;
                    }
                    int stop = change < changes.Count ? Math.Min(end, changes[change].From) : end;
                    kept.Add((at + moved, stop - at, style));
                    at = stop;
                }
            }
            for (; change < changes.Count; change++)
            {
                kept.Add((changes[change].ShownFrom, changes[change].ShownTo - changes[change].ShownFrom, last));
            }
            runs.Clear();
            foreach ((int start, int length, CellStyle style) in kept)
            {
                if (length > 0)
                {
                    runs.Add((start, length, style));
                }
            }
            return this;

            void Changed(int from, int to, int shownFrom, int shownTo)
            {
                ReadOnlySpan<char> line = read.AsSpan(from, to - from);
                ReadOnlySpan<char> made = shown.AsSpan(shownFrom, shownTo - shownFrom);
                if (line.SequenceEqual(made))
                {
                    return;
                }
                int same = line.CommonPrefixLength(made);
                int after = 0;
                while (after < Math.Min(line.Length, made.Length) - same && line[^(after + 1)] == made[^(after + 1)])
                {
                    after++;
                }
                changes.Add((from + same, to - after, shownFrom + same, shownTo - after));
            }
        }

        /// <summary>The text as shown, in its runs.</summary>
        public void CopyTo(IStyledInto into)
        {
            ReadOnlySpan<char> all = shown ?? text.ToString();
            foreach ((int start, int length, CellStyle style) in runs)
            {
                into.Put(all.Slice(start, length), style);
            }
        }
    }

    /// <summary>One stream's control codes read out of its text by the terminal's reader (<see cref="SequenceReader"/>): the
    /// colours and style an SGR sets kept in force for the text after it, until the next or a reset; every other sequence
    /// (a cursor move, a title, a link) and the bell left out. An OSC or other string a line break comes into is given up,
    /// and the text after it kept, so one never ended swallows no more than its line.</summary>
    private sealed class ControlCodes
    {
        private static readonly SearchValues<char> Stops = SearchValues.Create("\u001b\r\u0007");

        private readonly SequenceReader reader = new();
        private CellStyle style = CellStyle.Plain;
        private bool carriageReturn;
        private char highSurrogate;

        public void Read(ReadOnlySpan<char> text, IStyledInto into)
        {
            Acting acting = new(this, into);
            int i = 0;
            while (i < text.Length)
            {
                if (reader.InText && highSurrogate == '\0')
                {
                    ReadOnlySpan<char> rest = text[i..];
                    int stop = rest.IndexOfAny(Stops);
                    int take = stop < 0 ? rest.Length : stop;
                    if (take > 0)
                    {
                        Put(rest[..take], into);
                        i += take;
                    }
                    if (stop >= 0)
                    {
                        reader.Take(text[i++], acting);
                    }
                    continue;
                }
                char c = text[i++];
                if (c is '\n' or '\r' && reader.InString)
                {
                    reader.Abandon();
                    i--;
                    continue;
                }
                if (highSurrogate != '\0')
                {
                    char high = highSurrogate;
                    highSurrogate = '\0';
                    if (char.IsLowSurrogate(c))
                    {
                        reader.Take(char.ConvertToUtf32(high, c), acting);
                        continue;
                    }
                    reader.Take(0xFFFD, acting);
                }
                if (char.IsHighSurrogate(c))
                {
                    highSurrogate = c;
                    continue;
                }
                reader.Take(char.IsLowSurrogate(c) ? 0xFFFD : c, acting);
            }
        }

        /// <summary>The stream's end: a '\r' held back let go, and the sequence under way and the colours in force dropped.</summary>
        public void End(IStyledInto into)
        {
            if (carriageReturn)
            {
                into.Put("\r", style);
            }
            carriageReturn = false;
            highSurrogate = '\0';
            reader.Abandon();
            style = CellStyle.Plain;
        }

        /// <summary>Text in the colours in force, after a '\r' held back unless the text begins the line break it starts.</summary>
        private void Put(ReadOnlySpan<char> text, IStyledInto into)
        {
            if (carriageReturn)
            {
                carriageReturn = false;
                if (text[0] != '\n')
                {
                    into.Put("\r", style);
                }
            }
            into.Put(text, style);
        }

        /// <summary>What the reader finds, acted on as a console shows it.</summary>
        private readonly struct Acting(ControlCodes codes, IStyledInto into) : ISequenceActions
        {
            public void Print(int rune)
            {
                Span<char> chars = stackalloc char[2];
                codes.Put(chars[..new System.Text.Rune(rune).EncodeToUtf16(chars)], into);
            }

            public void Control(int rune)
            {
                switch (rune)
                {
                    case '\r':
                        // Held back until the next character says whether it begins a line break.
                        if (codes.carriageReturn)
                        {
                            into.Put("\r", codes.style);
                        }
                        codes.carriageReturn = true;
                        break;
                    case 0x07:
                        break;
                    default:
                        codes.Put([(char)rune], into);
                        break;
                }
            }

            public void Escape(SequenceReader sequence, char final)
            {
                if (final == 'c' && sequence.Intermediates == 0)
                {
                    codes.style = CellStyle.Plain;
                }
            }

            public void Csi(SequenceReader sequence, char final)
            {
                if (final == 'm' && sequence.PrivateMarker == '\0' && sequence.Intermediates == 0)
                {
                    codes.style = GraphicRendition.Apply(codes.style, sequence.Parameters);
                }
            }

            public void Osc(ReadOnlySpan<char> text)
            {
            }
        }
    }
}
