using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Passenger.Editor;
using Passenger.Languages.CSharp;

namespace Passenger.Tests;

/// <summary>A fling through a long C# file leaves the collector little to promote: each line laid out in one run for each
/// look, blanks adding none, each run shaped on its own so the formatter splits no buffer it would leave to its
/// finalizer, and the colours and hints of rows scrolled past let go while they are young, a long file keeping what a
/// short one does. Counts, not times; collections forced and finalizers counted, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class FlingTests
{
    [AvaloniaFact]
    public void Blanks_between_tokens_add_no_run_to_a_line_so_it_is_laid_out_in_as_many_runs_as_without_them()
    {
        long Runs(string line)
        {
            EditorRig rig = new(string.Join('\n', Enumerable.Repeat(line, 20)));
            long before = rig.Editor.RunsFormatted;
            using CSharpHighlighter csharp = new(rig.Document);
            rig.Editor.Highlighter = csharp;
            Ui.Settle();
            long runs = rig.Editor.RunsFormatted - before;
            rig.Window.Close();
            return runs;
        }
        long spaced = Runs("        int total = Of(3, 4) + seed;");
        long packed = Runs("int total=Of(3,4)+seed;");
        Assert.True(spaced > 0 && spaced == packed,
            $"Twenty lines with blanks between their tokens were laid out in {spaced} runs, and the same tokens without them in {packed}: each blank between two colours makes a glyph run and a buffer split of its own");
    }

    [AvaloniaFact]
    public void A_fling_through_lines_of_many_colours_leaves_the_finalizer_next_to_nothing_per_line()
    {
        const int Lines = 2400;
        EditorRig rig = new(string.Join('\n', Enumerable.Range(0, Lines).Select(Line)));
        using CSharpHighlighter csharp = new(rig.Document);
        rig.Editor.Highlighter = csharp;
        Ui.Settle();
        CodeEditor editor = rig.Editor;
        Point over = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), rig.Window)!.Value;
        double screen = Math.Floor(editor.Bounds.Height / 50);
        long runs = editor.RunsFormatted;
        int first = editor.LinesShown.First;
        List<int> batches = [];
        using Finalized finalized = new();
        finalized.Begin();
        while (editor.LinesShown.Last < Lines - 1)
        {
            // A few frames between full collections: each batch counts what its frames left, however young.
            for (int frame = 0; frame < 4; frame++)
            {
                rig.Window.MouseWheel(over, new Vector(0, -screen));
                Ui.Draw();
            }
            batches.Add(finalized.End());
        }
        TestContext.Current.TestOutputHelper?.WriteLine(finalized.Told());
        int flung = editor.LinesShown.First - first;
        Assert.True(editor.RunsFormatted - runs > 4L * flung, $"The fling through {flung} lines laid out only {editor.RunsFormatted - runs} runs: its lines were not of many colours");
        int left = batches.Sum();
        Assert.True(left <= flung / 10,
            $"A fling through {flung} lines of several colours left the finalizer {left} objects ({string.Join(", ", batches)} by each batch of frames; {finalized.Named()}), where its lines' layouts leave it none: each buffer the formatter splits waits for it, "
            + "is promoted with what it holds, and takes the pooled arrays it shares out of the pool");
    }

    [AvaloniaFact]
    public void A_fling_through_a_long_file_keeps_the_colours_and_hints_a_short_ones_does()
    {
        (int Lines, int Hints, int Rows) Kept(int lines)
        {
            EditorRig rig = new(string.Join('\n', Enumerable.Range(0, lines).Select(Line)));
            using CSharpHighlighter csharp = new(rig.Document);
            CodeEditor editor = rig.Editor;
            editor.Highlighter = csharp;
            editor.SetInlayHints([.. Enumerable.Range(0, lines).Where(i => i % 10 != 9)
                .Select(i => new InlayHint(rig.Document.Snapshot.GetLineStart(i) + rig.Document.Snapshot.GetLine(i).ToString().IndexOf("3,", StringComparison.Ordinal), "width:", PadRight: true))]);
            Ui.Settle();
            Point over = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), rig.Window)!.Value;
            double screen = Math.Floor(editor.Bounds.Height / 50);
            while (editor.LinesShown.Last < lines - 1)
            {
                rig.Window.MouseWheel(over, new Vector(0, -screen));
                Ui.Draw();
            }
            (int, int, int) kept = (csharp.LinesKept, editor.RowHintsKept, (int)(editor.Bounds.Height / editor.LineHeight));
            rig.Window.Close();
            return kept;
        }
        (int Lines, int Hints, int Rows) few = Kept(500);
        (int Lines, int Hints, int Rows) many = Kept(8000);
        Assert.True(many.Lines <= few.Lines + few.Rows,
            $"Flung through 8,000 lines, the highlighter kept {many.Lines} lines' colours, against {few.Lines} through 500: a line scrolled past is kept until it reaches the collector's oldest generation");
        Assert.True(many.Hints <= few.Hints + few.Rows && many.Hints <= 3 * many.Rows,
            $"Flung through 8,000 lines, the editor kept {many.Hints} rows' hints, against {few.Hints} through 500, with {many.Rows} rows on screen: the hints of rows scrolled past are kept");
    }

    /// <summary>The objects the runtime finalizes, by type, as its own events name each, whichever collection found it, but
    /// for what is no fling's: the callback of the runtime's pools that every full collection finds and that registers itself
    /// again, what a thread leaves as it ends (the pool lets go of threads left idle a while, those the tests run before
    /// in the process kept busy among them, some hundred objects for a dozen), the properties of the surface the headless
    /// platform makes for each frame it draws, which it never disposes, and what the session's own reader leaves. The
    /// events come through an EventPipe session of the test's own over its process's diagnostics port, sized by the test
    /// and asking only for a collection's start and end and an object's finalizing, read as they come by TraceEvent, which
    /// counts what the session lost: so no pinned handle's event at a full collection crowds the finalizer's out.</summary>
    private sealed class Finalized : IDisposable
    {
        private const string Runtime = "Microsoft-Windows-DotNETRuntime";
        private const long Collector = 0x1;
        private const int Megabytes = 64;
        private const int KindsTold = 8;

        // GCStart, GCEnd and FinalizeObject, by their ids in the runtime's manifest.
        private static readonly uint[] Wanted = [1, 2, 29];

        private static readonly HashSet<string> NoFlings = new(StringComparer.Ordinal)
        {
            "System.Gen2GcCallback",
            "System.Threading.Thread",
            "System.Threading.ThreadPoolWorkQueueThreadLocals",
            "System.Threading.ThreadInt64PersistentCounter+ThreadLocalNodeFinalizationHelper",
            "System.Threading.ThreadLocal`1+FinalizationHelper",
            "System.Threading.LowLevelLifoSemaphore+LifoBlockerNode",
            "System.Threading.WaitSubsystem+ThreadWaitInfo",
            "Microsoft.Win32.SafeHandles.SafeWaitHandle",
            "SkiaSharp.SKSurfaceProperties",
        };

        private readonly EventPipeSession session;
        private readonly Task reading;
        private readonly ConcurrentDictionary<Type, int> found = new();
        private readonly List<string> named = [];
        private volatile TaskCompletionSource heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile EventPipeEventSource? source;
        private int markers;
        private int ended;
        private int laters;
        private long mostPinned;
        private long lastPinned;

        // The tally, under its lock: each kind's count, each collection's starts less its ends (kept while not even), the
        // collections begun, and when the last event was written and heard.
        private readonly Lock tally = new();
        private readonly Dictionary<string, long> kinds = new(StringComparer.Ordinal);
        private readonly Dictionary<int, int> uneven = [];
        private long events;
        private long begun;
        private DateTime lastWritten;
        private DateTime lastHeard;

        public Finalized()
        {
            EventPipeProvider runtime = new(Runtime, EventLevel.Verbose, Collector, null, new EventPipeProviderEventFilter(true, Wanted));
            session = new DiagnosticsClient(Environment.ProcessId).StartEventPipeSession(
                new EventPipeSessionConfiguration([runtime], Megabytes, requestRundown: false, requestStackwalk: false));
            reading = Waiting.Apart(Hear);
            // What making the reader leaves the finalizer comes before the first batch's collections, which take it.
            Assert.True(Waiting.Until(() => source is not null || reading.IsCompleted, TimeSpan.FromSeconds(30)), "The reader of the session's stream never began");
        }

        /// <summary>Starts the first batch once what came before it is finalized and its word has come.</summary>
        public void Begin()
        {
            Collected();
            Heard();
            found.Clear();
        }

        /// <summary>What the batch since <see cref="Begin"/> or the last <see cref="End"/> left the finalizer; the next batch
        /// starts here, with all before it finalized and heard.</summary>
        public int End()
        {
            Collected();
            Heard();
            List<(string Name, int Count)> batch = [];
            foreach (Type type in found.Keys)
            {
                if (found.TryRemove(type, out int count))
                {
                    batch.Add((type.FullName ?? type.Name, count));
                }
            }
            int left = batch.Sum(b => b.Count);
            if (left > 0)
            {
                named.Add($"batch {ended}: {string.Join(", ", batch.OrderByDescending(b => b.Count).Take(KindsTold).Select(b => $"{b.Count} {b.Name}"))}");
            }
            ended++;
            return left;
        }

        /// <summary>The types each batch that left any left, the most first: a batch's own, apart from another's.</summary>
        public string Named() => named.Count == 0 ? "none named" : string.Join("; ", named);

        /// <summary>What the session brought so far: its events by kind, the most first, what it lost, the collections it
        /// heard, and the pinned objects the runtime found at the test's full collections.</summary>
        public string Told(long? since = null)
        {
            lock (tally)
            {
                string most = events == 0 ? "none"
                    : string.Join(", ", kinds.OrderByDescending(k => k.Value).Take(KindsTold).Select(k => $"{k.Value} {k.Key}"))
                        + (kinds.Count > KindsTold ? $" and {kinds.Count - KindsTold} kinds more" : string.Empty);
                string marked = since is long before ? $", {events - before} of them since the marker was made" : string.Empty;
                string last = events == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture,
                    $"; the last heard {(DateTime.UtcNow - lastHeard).TotalSeconds:0.0} s ago, written {(lastHeard - lastWritten).TotalSeconds:0.0} s before it was heard");
                return string.Create(CultureInfo.InvariantCulture,
                    $"The test's session of the runtime's events brought {events} ({most}){marked} and lost {Lost}{last}; of the {begun} collections it heard begin, {uneven.Count} were heard without their start or without their end; the runtime found up to {mostPinned} pinned objects at the test's full collections, {lastPinned} at the last");
            }
        }

        /// <summary>Stops the session, whose stream then ends its reader.</summary>
        public void Dispose()
        {
            session.Stop();
            session.Dispose();
        }

        private int Lost => source?.EventsLost ?? 0;

        private long Events
        {
            get
            {
                lock (tally)
                {
                    return events;
                }
            }
        }

        /// <summary>Reads the session's stream as it comes, to its end; making the source waits for the runtime's first
        /// words.</summary>
        private void Hear()
        {
            try
            {
                using EventPipeEventSource stream = new(session.EventStream);
                stream.AllEvents += data => Tally(data.EventName, data.TimeStamp.ToUniversalTime());
                stream.Clr.GCStart += data => Collection(data.Count, start: true);
                stream.Clr.GCStop += data => Collection(data.Count, start: false);
                stream.Clr.GCFinalizeObject += data => Finalizing(data.TypeID);
                source = stream;
                stream.Process();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or EndOfStreamException or FormatException)
            {
                // The stream ended with the session.
            }
        }

        private void Tally(string kind, DateTime written)
        {
            lock (tally)
            {
                events++;
                CollectionsMarshal.GetValueRefOrAddDefault(kinds, kind, out _)++;
                lastWritten = written;
                lastHeard = DateTime.UtcNow;
            }
        }

        /// <summary>Counts a collection's start or end by the collection's number, which the two share: one heard without the
        /// other is what a session that drops events leaves.</summary>
        private void Collection(int number, bool start)
        {
            lock (tally)
            {
                begun += start ? 1 : 0;
                int balance = uneven.GetValueOrDefault(number) + (start ? 1 : -1);
                if (balance == 0)
                {
                    uneven.Remove(number);
                }
                else
                {
                    uneven[number] = balance;
                }
            }
        }

        private void Finalizing(ulong typeId)
        {
            if (Type.GetTypeFromHandle(RuntimeTypeHandle.FromIntPtr(unchecked((nint)typeId))) is not { } type)
            {
                return;
            }
            if (type == typeof(Marker))
            {
                Interlocked.Increment(ref markers);
                heard.TrySetResult();
            }
            else if (type == typeof(Later))
            {
                Interlocked.Increment(ref laters);
            }
            else if (type.Assembly != typeof(EventPipeEventSource).Assembly && type.Assembly != typeof(DiagnosticsClient).Assembly
                && !NoFlings.Contains((type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName ?? type.Name))
            {
                found.AddOrUpdate(type, 1, static (_, count) => count + 1);
            }
        }

        /// <summary>Waits for the word of every finalizer run so far: a marker's, finalized after them by the one thread that
        /// runs finalizers, whose words come in the order it gave them. Fails if the session lost any event, as a count it
        /// lost some of would be partial; a wait that gives up says what the session brought and what came after it.</summary>
        private void Heard()
        {
            heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int words = Volatile.Read(ref markers);
            long since = Events;
            int before = Marker.Finalized;
            Marker.Drop();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.True(Marker.Finalized > before, "The marker outlived a full collection, so its word says nothing of the finalizers before it");
            Pinned();
            Push();
            try
            {
                Ui.Idle(heard.Task, "the runtime's word of the finalizers it ran");
            }
            catch (TimeoutException waited)
            {
                throw new TimeoutException($"{waited.Message}. {Unheard(words, since)}", waited);
            }
            Assert.True(Lost == 0, $"The session lost events of the runtime, so what the batches left the finalizer would be counted in part. {Told(since)}");
        }

        /// <summary>What the session brought as the wait for the marker's word gave up, then what a second marker brought: a
        /// first word come only then came late; one still missing while the reader read on past it was lost; nothing read is
        /// a stream stuck.</summary>
        private string Unheard(int words, long since)
        {
            string told = Told(since);
            DateTime checking = DateTime.UtcNow;
            long started = Stopwatch.GetTimestamp();
            long before = Events;
            int finalized = Later.Finalized;
            int later = Volatile.Read(ref laters);
            Later.Drop();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Push();
            bool second = Waiting.Until(() => Volatile.Read(ref laters) > later, TimeSpan.FromSeconds(10));
            double waited = Stopwatch.GetElapsedTime(started).TotalSeconds;
            bool first = Volatile.Read(ref markers) > words;
            long read;
            double written;
            lock (tally)
            {
                read = events - before;
                written = (lastWritten - checking).TotalSeconds;
            }
            string after = read == 0 ? "it brought no event"
                : string.Create(CultureInfo.InvariantCulture, $"it brought {read} events, the last written {Math.Abs(written):0.0} s {(written < 0 ? "before" : "after")} the second marker was made");
            string so = reading.IsCompleted ? $"so the reader had ended: {reading.Exception?.InnerException?.Message ?? "its stream ended"}"
                : first ? "so the word came late, only once more events followed it"
                : second || (read > 0 && written >= 0) ? string.Create(CultureInfo.InvariantCulture, $"so the marker's event never reached the reader, which read on past it, the session telling of {Lost} events lost")
                : read == 0 ? "so the stream is stuck: it brought nothing more"
                : "so the reader is behind, still reading what was written before the second marker";
            string checkedWith = string.Create(CultureInfo.InvariantCulture,
                $"Then a second marker was finalized, and a collection made on a thread of its own: in {waited:0.0} s {after}");
            string markersCame = $"the second marker was {(Later.Finalized > finalized ? "finalized" : "never finalized")} and its word {(second ? "came" : "never came")}, the first's {(first ? "came" : "never came")}";
            return string.Create(CultureInfo.InvariantCulture, $"Marker {words + 1}'s word never came. {told}. {checkedWith}; {markersCame}; {so}");
        }

        /// <summary>Keeps the pinned objects the last full collection found, each an event a session asking for every
        /// collection's events would carry.</summary>
        private void Pinned()
        {
            lastPinned = GC.GetGCMemoryInfo(GCKind.FullBlocking).PinnedObjectsCount;
            mostPinned = Math.Max(mostPinned, lastPinned);
        }

        /// <summary>A collection on a new thread, whose start the stream marks as the oldest event of its flush: its reader
        /// holds the last events of a thread's run until such a mark comes, and lets go of all before it then.</summary>
        private static void Push() => new Thread(() => GC.Collect(0)) { IsBackground = true, Name = "Pushes the runtime's events to the test" }.Start();

        private static void Collected()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>An object made only to be finalized after the rest.</summary>
    private sealed class Marker
    {
        private static int finalized;

        ~Marker() => Interlocked.Increment(ref finalized);

        public static int Finalized => Volatile.Read(ref finalized);

        /// <summary>Makes one and lets it go, outside the caller's frame, which could keep it.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Drop() => GC.KeepAlive(new Marker());
    }

    /// <summary>An object made only to be finalized after a <see cref="Marker"/> whose word never came.</summary>
    private sealed class Later
    {
        private static int finalized;

        ~Later() => Interlocked.Increment(ref finalized);

        public static int Finalized => Volatile.Read(ref finalized);

        /// <inheritdoc cref="Marker.Drop"/>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Drop() => GC.KeepAlive(new Later());
    }

    private static string Line(int i) => (i % 10) switch
    {
        9 => "        // " + string.Concat(Enumerable.Repeat("a note of one colour, ", 3)),
        _ => string.Create(CultureInfo.InvariantCulture, $"        int unused{i} = Of(3, {i}) + total * \"text {i}\".Length;"),
    };
}
