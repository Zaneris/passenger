using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
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
    /// headless platform makes for each frame it draws, which it never disposes.</summary>
    private sealed class Finalized : EventListener
    {
        private const EventKeywords Collector = (EventKeywords)0x1;

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
            if (eventData.EventName != "FinalizeObject" || eventData.Payload is not [nint handle, ..] || Type.GetTypeFromHandle(RuntimeTypeHandle.FromIntPtr(handle)) is not { } type)
            {
                return;
            }
            if (type == typeof(Marker))
            {
                heard.TrySetResult();
            }
            else if (!NoFlings.Contains((type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName ?? type.Name))
            {
                found.AddOrUpdate(type, 1, static (_, count) => count + 1);
            }
        }

        /// <summary>Waits for the word of every finalizer run so far: a marker's, finalized after them by the one thread that
        /// runs finalizers, whose words come in the order it gave them.</summary>
        private void Heard()
        {
            heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int before = Marker.Finalized;
            Marker.Drop();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.True(Marker.Finalized > before, "The marker outlived a full collection, so its word says nothing of the finalizers before it");
            Ui.Idle(heard.Task, "the runtime's word of the finalizers it ran");
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

    private static string Line(int i) => (i % 10) switch
    {
        9 => "        // " + string.Concat(Enumerable.Repeat("a note of one colour, ", 3)),
        _ => string.Create(CultureInfo.InvariantCulture, $"        int unused{i} = Of(3, {i}) + total * \"text {i}\".Length;"),
    };
}
