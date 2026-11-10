using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
        while (editor.LinesShown.Last < Lines - 1)
        {
            // A few frames between full collections: each batch counts what its frames left, however young.
            finalized.Begin();
            for (int frame = 0; frame < 4; frame++)
            {
                rig.Window.MouseWheel(over, new Vector(0, -screen));
                Ui.Draw();
            }
            batches.Add(finalized.End());
        }
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
    /// in the process kept busy among them, some hundred objects for a dozen), and the properties of the surface the
    /// headless platform makes for each frame it draws, which it never disposes. It tallies every event it hears, for a
    /// wait for its word that gives up to say what came.</summary>
    private sealed class Finalized : EventListener
    {
        private const EventKeywords Collector = (EventKeywords)0x1;
        private const int KindsTold = 8;

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

        private readonly ConcurrentDictionary<Type, int> found = new();
        private readonly Dictionary<string, int> named = new(StringComparer.Ordinal);
        private volatile TaskCompletionSource heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int markers;
        private int laters;

        // The tally, under its lock: each kind's count, each collection's starts less its ends (kept while not even), the
        // collections begun, the threads that read the session, and when the last event was written and heard.
        private readonly Lock tally = new();
        private readonly Dictionary<string, long> kinds = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, int> uneven = [];
        private readonly HashSet<int> readers = [];
        private long events;
        private long begun;
        private DateTime lastWritten;
        private DateTime lastHeard;

        /// <summary>Starts a batch once what came before it is finalized and its word has come.</summary>
        public void Begin()
        {
            Collected();
            Heard();
            found.Clear();
        }

        /// <summary>What the batch since <see cref="Begin"/> left the finalizer.</summary>
        public int End()
        {
            Collected();
            Heard();
            int left = 0;
            foreach ((Type type, int count) in found)
            {
                named[type.FullName ?? type.Name] = named.GetValueOrDefault(type.FullName ?? type.Name) + count;
                left += count;
            }
            return left;
        }

        /// <summary>The types the batches left, the most first.</summary>
        public string Named() => named.Count == 0 ? "none named" : string.Join(", ", named.OrderByDescending(n => n.Value).Select(n => $"{n.Value} {n.Key}"));

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Verbose, Collector);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            Tally(eventData);
            if (eventData.EventName != "FinalizeObject" || eventData.Payload is not [nint handle, ..] || Type.GetTypeFromHandle(RuntimeTypeHandle.FromIntPtr(handle)) is not { } type)
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
            else if (!NoFlings.Contains((type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName ?? type.Name))
            {
                found.AddOrUpdate(type, 1, static (_, count) => count + 1);
            }
        }

        /// <summary>Counts the event by its kind, and a collection's start or end by the collection's number, which the two
        /// share: one heard without the other is what a session that drops events leaves.</summary>
        private void Tally(EventWrittenEventArgs eventData)
        {
            string kind = eventData.EventName ?? "unnamed";
            bool start = kind.StartsWith("GCStart", StringComparison.Ordinal);
            uint? collection = (start || kind.StartsWith("GCEnd", StringComparison.Ordinal))
                && eventData.PayloadNames?.IndexOf("Count") is int at and >= 0 && eventData.Payload?[at] is uint count ? count : null;
            lock (tally)
            {
                events++;
                CollectionsMarshal.GetValueRefOrAddDefault(kinds, kind, out _)++;
                readers.Add(Environment.CurrentManagedThreadId);
                lastWritten = eventData.TimeStamp;
                lastHeard = DateTime.UtcNow;
                if (collection is uint number)
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
        }

        /// <summary>Waits for the word of every finalizer run so far: a marker's, finalized after them by the one thread that
        /// runs finalizers, whose words come in the order it gave them. A wait that gives up says what the listener heard
        /// and what one wake of the reader brought.</summary>
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
            try
            {
                Ui.Idle(heard.Task, "the runtime's word of the finalizers it ran");
            }
            catch (TimeoutException waited)
            {
                throw new TimeoutException($"{waited.Message}. {Unheard(words, since)}", waited);
            }
        }

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

        /// <summary>What the listener heard as the wait for the marker's word gave up, then what one wake of the reader
        /// brought (<see cref="Later.Wake"/>): a word come only then had come unread; one still missing while the reader read
        /// on past it never reached the reader; nothing read at all is a reader stuck.</summary>
        private string Unheard(int words, long since)
        {
            string told = Told(since);
            string lost;
            lock (tally)
            {
                lost = readers.Count > 1 ? "left in a session restarted under it"
                    : uneven.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"dropped as the session's buffers filled, as the {uneven.Count} collections heard in part were")
                    : "dropped by the session, though it heard no collection in part";
            }
            DateTime woken = DateTime.UtcNow;
            long waking = Stopwatch.GetTimestamp();
            long before = Events;
            int finalized = Later.Finalized;
            int later = Volatile.Read(ref laters);
            Later.Wake();
            bool second = Waiting.Until(() => Volatile.Read(ref laters) > later, TimeSpan.FromSeconds(10));
            double waited = Stopwatch.GetElapsedTime(waking).TotalSeconds;
            bool first = Volatile.Read(ref markers) > words;
            long read;
            double written;
            lock (tally)
            {
                read = events - before;
                written = (lastWritten - woken).TotalSeconds;
            }
            string after = read == 0 ? "it read no event"
                : string.Create(CultureInfo.InvariantCulture, $"it read {read} events, the last written {Math.Abs(written):0.0} s {(written < 0 ? "before" : "after")} the wake");
            string so = first ? "so the word had come unread: the reader slept past it"
                : second || (read > 0 && written >= 0) ? $"so the marker's event never reached the reader, which read on past it: {lost}"
                : read == 0 ? "so the reader is stuck: woken, it read nothing"
                : "so the reader is behind, still reading what was written before the wake";
            string woke = string.Create(CultureInfo.InvariantCulture,
                $"Woken once (a collection on a thread of the test's own, whose first event wakes the reader, finalizing a second marker after the first): in {waited:0.0} s {after}");
            string markersCame = $"the second marker was {(Later.Finalized > finalized ? "finalized" : "never finalized")} and its word {(second ? "came" : "never came")}, the first's {(first ? "came" : "never came")}";
            return string.Create(CultureInfo.InvariantCulture, $"Marker {words + 1}'s word never came: {told}. {woke}; {markersCame}; {so}");
        }

        /// <summary>The events heard so far, by kind, the most first, and how the session read them.</summary>
        private string Told(long since)
        {
            lock (tally)
            {
                if (events == 0)
                {
                    return "the listener heard no event of the runtime at all";
                }
                string most = string.Join(", ", kinds.OrderByDescending(k => k.Value).Take(KindsTold).Select(k => $"{k.Value} {k.Key}"))
                    + (kinds.Count > KindsTold ? $" and {kinds.Count - KindsTold} kinds more" : string.Empty);
                string threads = readers.Count == 1 ? "1 thread" : $"{readers.Count} threads, as a restarted session reads on a new one";
                string last = string.Create(CultureInfo.InvariantCulture,
                    $"the last heard {(DateTime.UtcNow - lastHeard).TotalSeconds:0.0} s before the wait gave up, written {(lastHeard - lastWritten).TotalSeconds:0.0} s before it was heard");
                return string.Create(CultureInfo.InvariantCulture,
                    $"the listener heard {events} events of the runtime ({most}), {events - since} of them since the marker was made, read on {threads}; {last}; of the {begun} collections it heard begin, {uneven.Count} were heard without their start or without their end");
            }
        }

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

        /// <summary>Makes one and collects it on a new thread, whose first event of the session takes a buffer of its own,
        /// which signals the session's reader: so the reader, unless stuck, reads on past both markers' finalizing.</summary>
        public static void Wake() => new Thread(() =>
        {
            Drop();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }) { IsBackground = true, Name = "Wakes the runtime's event reader" }.Start();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Drop() => GC.KeepAlive(new Later());
    }

    private static string Line(int i) => (i % 10) switch
    {
        9 => "        // " + string.Concat(Enumerable.Repeat("a note of one colour, ", 3)),
        _ => string.Create(CultureInfo.InvariantCulture, $"        int unused{i} = Of(3, {i}) + total * \"text {i}\".Length;"),
    };
}
