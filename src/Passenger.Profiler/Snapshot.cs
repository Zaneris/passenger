using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Passenger.Profiler;

/// <summary>A node of the call tree: a method called along one path from a thread's first frame, with how many samples
/// found the thread in it (Total) and in it and no deeper (Self). Most nodes have one callee, which is kept without a
/// table by name; the callees are listed once, when first asked for, the tree being whole by then.</summary>
public sealed class CallNode(string name, string module, CallNode? parent, MethodCode? code = null)
{
    /// <summary>The callee while there is one.</summary>
    private CallNode? only;

    /// <summary>The callees by name, once there are two.</summary>
    private Dictionary<string, CallNode>? children;

    /// <summary>The callees as <see cref="Ordered"/> lists them, once asked for.</summary>
    private CallNode[]? ordered;

    public string Name { get; internal set; } = name;
    public string Module { get; } = module;

    /// <summary>Where its method is, as <see cref="HotSpot.Code"/> says it; none for a thread, a type or a native frame.</summary>
    public MethodCode? Code { get; } = code;
    public CallNode? Parent { get; } = parent;
    public int Total { get; internal set; }
    public int Self { get; internal set; }
    public IReadOnlyCollection<CallNode> Children => children is not null ? children.Values : Ordered;

    /// <summary>The children, the heaviest first, as the call tree lists them.</summary>
    public IReadOnlyList<CallNode> Ordered => ordered ??= children is not null ? Sorted(children) : only is not null ? [only] : [];

    internal CallNode Child(string method, string module, MethodCode? code)
    {
        if (children is null && (only is null || only.Name == method))
        {
            return only ??= new CallNode(method, module, this, code);
        }
        if (children is null)
        {
            children = new Dictionary<string, CallNode>(StringComparer.Ordinal) { [only!.Name] = only };
            only = null;
        }
        if (!children.TryGetValue(method, out CallNode? child))
        {
            children[method] = child = new CallNode(method, module, this, code);
            ordered = null;
        }
        return child;
    }

    private static CallNode[] Sorted(Dictionary<string, CallNode> children)
    {
        CallNode[] sorted = [.. children.Values];
        Array.Sort(sorted, static (a, b) => a.Total != b.Total ? b.Total.CompareTo(a.Total) : string.CompareOrdinal(a.Name, b.Name));
        return sorted;
    }
}

/// <summary>A method's share of the run, all threads together: samples in it and no deeper (Self), and samples with it
/// anywhere on the stack, counted once however deep it recurses (Total).</summary>
public sealed record HotSpot(string Name, string Module, int Self, int Total)
{
    /// <summary>Where the method is: its module's file and its metadata token, which <see cref="MethodSources"/> finds
    /// its source by; none for a native frame.</summary>
    public MethodCode? Code { get; init; }
}

/// <summary>A managed method by its module's file and metadata token.</summary>
public readonly record struct MethodCode(string ModuleFile, int Token);

/// <summary>A type the program allocated: about how many bytes and objects (the runtime reports an allocation tick each
/// 100 KB or so, naming the type and stack that crossed it), and the stacks that allocated it, the allocating method
/// first and its callers below, weighed in kilobytes.</summary>
public sealed record AllocatedType(string Name, long Bytes, int Ticks, CallNode BackTraces);

/// <summary>A garbage collection: its number, generation, why, when it began and how long the program was paused.</summary>
public sealed record Collection(int Number, int Generation, string Reason, double AtMs, double PauseMs);

/// <summary>A thread's samples in a row that caught it at one stack (a number into the snapshot's table of stacks): when
/// the first and the last were taken, in milliseconds from the trace's start, and how many, evenly spaced between. A
/// thread waiting all run long is one; one busy, a run per change of where it was caught. Sixteen bytes: its times are
/// kept as floats, to a sixteenth of a millisecond ten minutes in and a quarter of one an hour in.</summary>
public readonly struct SampleRun(double fromMs, double toMs, int stack, int count)
{
    private readonly float from = (float)fromMs;
    private readonly float length = (float)(toMs - fromMs);

    public double FromMs => from;

    public double ToMs => (double)from + length;

    public int Stack { get; } = stack;

    public int Count { get; } = count;

    /// <summary>How many of its samples were taken from one time to another.</summary>
    public int Within(double fromMs, double toMs)
    {
        if (ToMs < fromMs || FromMs > toMs)
        {
            return 0;
        }
        if (FromMs >= fromMs && ToMs <= toMs)
        {
            return Count;
        }
        // Partly inside, so at least two samples a step apart: those whose times fall within.
        double step = (ToMs - FromMs) / (Count - 1);
        int first = Math.Max(0, (int)Math.Ceiling(((fromMs - FromMs) / step) - 1e-9));
        int last = Math.Min(Count - 1, (int)Math.Floor(((toMs - FromMs) / step) + 1e-9));
        return Math.Max(0, last - first + 1);
    }
}

/// <summary>An allocation tick: when, the type, the bytes it stands for, and the allocating stack (a number into the
/// snapshot's table of stacks).</summary>
public readonly record struct AllocationTick(double AtMs, string Type, long Bytes, int Stack);

/// <summary>A thread's row of the timeline: its name, how many samples it had, and the runs of them in time.</summary>
public sealed record ThreadLine(int Id, string Name, int Samples, IReadOnlyList<SampleRun> Runs);

/// <summary>What a profiling run found, or a stretch of it: per thread, the call tree of its samples; the hot spots across
/// them all; the allocations and collections; and when each thread was sampled, for the timeline. A sample is a thread
/// caught at one stack, about once a millisecond (<see cref="SampleMs"/> says how often). Each distinct stack is kept
/// once and a thread's samples as runs at one stack, so what a run holds grows with where its threads went rather than
/// with how long they were watched; any interval of it (<see cref="Within"/>) has the same views.</summary>
public sealed class Snapshot
{
    /// <summary>EventPipe's sampler: every managed thread's stack, each millisecond.</summary>
    public const string SampleProvider = "Microsoft-DotNETCore-SampleProfiler";

    /// <summary>The runtime's own events: method loads, which name the frames, and the GC's.</summary>
    public const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>The longest a thread may go unsampled with its samples still one run, past which a stretch would count
    /// samples into the gap: many times the sampler's period, even on a machine that slows it.</summary>
    private const double LongestGap = 50;

    /// <summary>Each thread's runs, in time.</summary>
    private readonly IReadOnlyList<(int Thread, SampleRun[] Runs)> runs;
    private readonly IReadOnlyList<int[]> stacks;
    private readonly IReadOnlyList<(string Method, string Module)> frames;

    /// <summary>Each frame's module file and token, beside <see cref="frames"/>.</summary>
    private readonly IReadOnlyList<MethodCode?> codes;
    private readonly IReadOnlyDictionary<int, string> threadNames;
    private readonly IReadOnlyList<AllocationTick> ticks;
    private readonly IReadOnlyList<Collection> allCollections;
    private readonly IReadOnlyList<UiFrame> allUiFrames;
    private readonly Lazy<IReadOnlyList<ThreadLine>> timeline;

    private Snapshot(IReadOnlyList<(int Thread, SampleRun[] Runs)> runs, IReadOnlyList<int[]> stacks, IReadOnlyList<(string Method, string Module)> frames,
        IReadOnlyList<MethodCode?> codes, IReadOnlyDictionary<int, string> threadNames, IReadOnlyList<AllocationTick> ticks, IReadOnlyList<Collection> collections,
        IReadOnlyList<UiFrame> uiFrames, double startMs, double endMs, string trace, Snapshot? whole)
    {
        this.runs = runs;
        this.stacks = stacks;
        this.frames = frames;
        this.codes = codes;
        this.threadNames = threadNames;
        this.ticks = ticks;
        allCollections = collections;
        allUiFrames = uiFrames;
        StartMs = startMs;
        EndMs = endMs;
        TracePath = trace;
        Whole = whole ?? this;
        // A thread's samples at a stack inside the stretch, counted once a pair however many runs they came in.
        Dictionary<(int Thread, int Stack), int> counted = [];
        int samples = 0;
        foreach ((int thread, SampleRun[] taken) in runs)
        {
            foreach (SampleRun run in taken)
            {
                int inside = run.Within(startMs, endMs);
                if (inside > 0)
                {
                    counted[(thread, run.Stack)] = counted.GetValueOrDefault((thread, run.Stack)) + inside;
                    samples += inside;
                }
            }
        }
        Samples = samples;
        SampleMs = whole?.SampleMs ?? Period(runs);
        (All, HotSpots, IReadOnlyDictionary<int, CallNode> byId) = Build(counted);
        Threads = All.Ordered;
        Collections = [.. collections.Where(c => c.AtMs >= startMs && c.AtMs <= endMs)];
        UiFrames = whole is null ? uiFrames : [.. uiFrames.Where(f => f.StartMs >= startMs && f.StartMs <= endMs)];
        Allocations = Allocated([.. ticks.Where(t => t.AtMs >= startMs && t.AtMs <= endMs)]);
        timeline = new(() => Lines(byId));
    }

    /// <summary>The whole run this is a stretch of (itself when it is the whole).</summary>
    public Snapshot Whole { get; }

    /// <summary>Where the snapshot begins and ends, in milliseconds from the trace's start.</summary>
    public double StartMs { get; }
    public double EndMs { get; }

    /// <summary>The types allocated, the most bytes first.</summary>
    public IReadOnlyList<AllocatedType> Allocations { get; }

    /// <summary>The garbage collections, in order.</summary>
    public IReadOnlyList<Collection> Collections { get; }

    /// <summary>An Avalonia app's frames, in order, as its inspector recorded them (<see cref="Profiler.UiFrames"/>); none for
    /// any other program.</summary>
    public IReadOnlyList<UiFrame> UiFrames { get; }

    /// <summary>Every thread under one root, whose total is every sample: what the flame graph spans.</summary>
    public CallNode All { get; }

    /// <summary>A root per thread (named for it), the heaviest first.</summary>
    public IReadOnlyList<CallNode> Threads { get; }

    /// <summary>The methods by their own samples, the heaviest first.</summary>
    public IReadOnlyList<HotSpot> HotSpots { get; }

    /// <summary>Each thread's samples in time, the busiest thread first.</summary>
    public IReadOnlyList<ThreadLine> Timeline => timeline.Value;

    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, EndMs - StartMs));

    public int Samples { get; }

    /// <summary>The distinct stacks the whole run's samples and allocation ticks caught, each kept once.</summary>
    public int Stacks => stacks.Count;

    /// <summary>The runs the whole run's samples are kept as (<see cref="SampleRun"/>): what it holds grows with these
    /// rather than with its samples.</summary>
    public int Runs => runs.Sum(t => t.Runs.Length);

    /// <summary>How long a sample stands for: the time between the sampler's passes over the busiest thread, the whole run's.
    /// The runtime asks for one a millisecond, but a loaded machine, or a program run at background priority (whose
    /// timers macOS coalesces), gets fewer, so a count is turned into time by this rather than read as milliseconds.</summary>
    public double SampleMs { get; }

    /// <summary>The time some samples of this run stand for, in milliseconds.</summary>
    public double Milliseconds(int samples) => samples * SampleMs;

    /// <summary>The .nettrace file the run was written to.</summary>
    public string TracePath { get; }

    /// <summary>Where the program ran when not on this machine (its container), whose modules' PDBs are read there
    /// (<see cref="MethodSources.FindAsync"/>); null for a program of this machine's, or a snapshot opened from its file.</summary>
    public ProfilePlace? Place
    {
        get => ReferenceEquals(Whole, this) ? place : Whole.Place;
        internal set => place = value;
    }

    private ProfilePlace? place;

    /// <summary>The same views over a stretch of the run. Its work grows with the run's stacks and runs, and it reads
    /// the run without changing it, so any thread may ask.</summary>
    public Snapshot Within(double fromMs, double toMs)
    {
        Snapshot whole = Whole;
        double from = Math.Max(whole.StartMs, Math.Min(fromMs, toMs));
        double to = Math.Min(whole.EndMs, Math.Max(fromMs, toMs));
        return new Snapshot(whole.runs, whole.stacks, whole.frames, whole.codes, whole.threadNames, whole.ticks, whole.allCollections, whole.allUiFrames, from, to, whole.TracePath, whole);
    }

    /// <summary>When a method was on a sampled stack, first to last, or null when it never was: what the timeline shows
    /// to say where a method ran.</summary>
    public (double From, double To)? Span(string method)
    {
        bool[] named = [.. frames.Select(f => f.Method.Contains(method, StringComparison.Ordinal))];
        bool[] holding = [.. stacks.Select(s => s.Any(f => named[f]))];
        double first = double.MaxValue;
        double last = double.MinValue;
        foreach (SampleRun run in runs.SelectMany(t => t.Runs))
        {
            if (holding[run.Stack] && run.Within(StartMs, EndMs) > 0)
            {
                first = Math.Min(first, Math.Max(run.FromMs, StartMs));
                last = Math.Max(last, Math.Min(run.ToMs, EndMs));
            }
        }
        return first <= last ? (first, last) : null;
    }

    /// <summary>The mean time between the busiest thread's samples, which the sampler takes on every pass it makes; a
    /// millisecond, as asked for, when there are too few to measure.</summary>
    private static double Period(IReadOnlyList<(int Thread, SampleRun[] Runs)> taken)
    {
        if (taken.Count == 0)
        {
            return 1;
        }
        SampleRun[] busiest = taken.MaxBy(t => t.Runs.Sum(r => r.Count)).Runs;
        int count = busiest.Sum(r => r.Count);
        double first = busiest.Min(r => r.FromMs);
        double last = busiest.Max(r => r.ToMs);
        return count >= 2 && last > first ? (last - first) / (count - 1) : 1;
    }

    /// <summary>The call trees (a root per thread under one for all) and hot spots of each thread's samples at each stack.</summary>
    private (CallNode All, IReadOnlyList<HotSpot> HotSpots, IReadOnlyDictionary<int, CallNode> Threads) Build(Dictionary<(int Thread, int Stack), int> counted)
    {
        CallNode all = new("All threads", string.Empty, null);
        Dictionary<int, CallNode> threads = [];
        Dictionary<int, (int Self, int Total)> spots = [];
        // The stack a frame was last counted for, so a recursive method's total counts each stack once.
        int[] countedFor = new int[frames.Count];
        int stamp = 0;
        foreach (((int thread, int stack), int count) in counted)
        {
            if (!threads.TryGetValue(thread, out CallNode? node))
            {
                threads[thread] = node = all.Child(threadNames.GetValueOrDefault(thread, $"Thread {thread}"), string.Empty, null);
            }
            int[] caught = stacks[stack];
            all.Total += count;
            node.Total += count;
            // From the thread's first frame to where it was caught.
            for (int i = caught.Length - 1; i >= 0; i--)
            {
                (string method, string module) = frames[caught[i]];
                node = node.Child(method, module, codes[caught[i]]);
                node.Total += count;
            }
            node.Self += count;
            stamp++;
            for (int i = 0; i < caught.Length; i++)
            {
                int frame = caught[i];
                (int Self, int Total) spot = spots.GetValueOrDefault(frame);
                bool first = countedFor[frame] != stamp;
                countedFor[frame] = stamp;
                spots[frame] = (spot.Self + (i == 0 ? count : 0), spot.Total + (first ? count : 0));
            }
        }
        // The thread the program's entry point runs on is named its main thread.
        foreach (CallNode thread in all.Children)
        {
            if (RunsEntryPoint(thread, 4))
            {
                thread.Name = $"Main thread ({thread.Name})";
            }
        }
        IReadOnlyList<HotSpot> hot = [.. spots.Where(s => s.Value.Self > 0)
            .Select(s => new HotSpot(frames[s.Key].Method, frames[s.Key].Module, s.Value.Self, s.Value.Total) { Code = codes[s.Key] })
            .OrderByDescending(s => s.Self)
            .ThenBy(s => s.Name, StringComparer.Ordinal)];
        return (all, hot, threads);
    }

    /// <summary>The timeline's rows: each thread sampled in the stretch, with its runs there, the busiest first.</summary>
    private IReadOnlyList<ThreadLine> Lines(IReadOnlyDictionary<int, CallNode> byId)
    {
        List<ThreadLine> lines = [];
        foreach ((int thread, SampleRun[] taken) in runs)
        {
            int samples = taken.Sum(r => r.Within(StartMs, EndMs));
            if (samples > 0)
            {
                // The whole run's rows are its own runs; a stretch's, those it holds.
                IReadOnlyList<SampleRun> held = ReferenceEquals(Whole, this) ? taken : [.. taken.Where(r => r.Within(StartMs, EndMs) > 0)];
                lines.Add(new ThreadLine(thread, byId.TryGetValue(thread, out CallNode? named) ? named.Name : $"Thread {thread}", samples, held));
            }
        }
        return [.. lines.OrderByDescending(t => t.Samples)];
    }

    /// <summary>The types some allocation ticks name, with their back traces.</summary>
    private IReadOnlyList<AllocatedType> Allocated(IReadOnlyList<AllocationTick> taken)
    {
        Dictionary<string, (long Bytes, int Ticks, CallNode Root)> allocated = new(StringComparer.Ordinal);
        foreach (AllocationTick tick in taken)
        {
            if (!allocated.TryGetValue(tick.Type, out (long Bytes, int Ticks, CallNode Root) so))
            {
                so = (0, 0, new CallNode(tick.Type, string.Empty, null));
            }
            int kilobytes = (int)Math.Max(1, tick.Bytes / 1024);
            so.Root.Total += kilobytes;
            CallNode at = so.Root;
            foreach (int frame in stacks[tick.Stack])
            {
                at = at.Child(frames[frame].Method, frames[frame].Module, codes[frame]);
                at.Total += kilobytes;
            }
            at.Self += kilobytes;
            allocated[tick.Type] = (so.Bytes + tick.Bytes, so.Ticks + 1, so.Root);
        }
        return [.. allocated.Select(a => new AllocatedType(a.Key, a.Value.Bytes, a.Value.Ticks, a.Value.Root)).OrderByDescending(a => a.Bytes)];
    }

    /// <summary>Whether a program's entry point is among a thread's first frames (below native ones, the runtime's
    /// CallEntryPoint, or a Main, top-level statements' &lt;Main&gt;$ included).</summary>
    private static bool RunsEntryPoint(CallNode node, int depth) =>
        depth > 0 && node.Children.Any(c =>
            c.Name.StartsWith("System.Environment.CallEntryPoint", StringComparison.Ordinal)
            || c.Name.Contains(".Main(", StringComparison.Ordinal)
            || c.Name.Contains(".<Main>$(", StringComparison.Ordinal)
            || RunsEntryPoint(c, depth - 1));

    /// <summary>A method written with its parameters by their types' own names ("Main(String[])"), not IL's
    /// ("Main(class System.String[])"), in one string made from a buffer on the stack (or the pool's, for a long one).</summary>
    public static string Pretty(string method)
    {
        int open = method.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !method.EndsWith(')'))
        {
            return method;
        }
        // Each parameter only loses characters, and its ", " is a character more than its comma: twice the name holds it.
        int most = (method.Length * 2) + 2;
        char[]? rented = most > 512 ? ArrayPool<char>.Shared.Rent(most) : null;
        Span<char> written = rented is null ? stackalloc char[most] : rented;
        method.AsSpan(0, open + 1).CopyTo(written);
        int at = open + 1;
        bool first = true;
        ReadOnlySpan<char> parameters = method.AsSpan(open + 1, method.Length - open - 2);
        foreach (Range range in parameters.Split(','))
        {
            ReadOnlySpan<char> parameter = parameters[range].Trim();
            if (parameter.IsEmpty)
            {
                continue;
            }
            if (!first)
            {
                written[at++] = ',';
                written[at++] = ' ';
            }
            first = false;
            Span<char> type = written[at..];
            parameter.CopyTo(type);
            int length = Removed(type[..parameter.Length], "value class ");
            length = Removed(type[..length], "class ");
            ReadOnlySpan<char> bare = type[..length];
            int generic = bare.IndexOfAny(TypeSuffixes);
            int dot = (generic < 0 ? bare : bare[..generic]).LastIndexOf('.');
            bare[(dot + 1)..].CopyTo(type);
            at += length - dot - 1;
        }
        written[at++] = ')';
        string pretty = new(written[..at]);
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
        return pretty;
    }

    /// <summary>What starts a type's generic arguments, arity, array, reference or pointer, which keep their namespaces.</summary>
    private static readonly SearchValues<char> TypeSuffixes = SearchValues.Create("<`[&*");

    /// <summary>Takes every occurrence of a word out of some text in place, found left to right and never overlapping, and
    /// says how long the text is now.</summary>
    private static int Removed(Span<char> text, ReadOnlySpan<char> word)
    {
        int kept = 0;
        int read = 0;
        while (text[read..].IndexOf(word) is var found and >= 0)
        {
            text.Slice(read, found).CopyTo(text[kept..]);
            kept += found;
            read += found + word.Length;
        }
        text[read..].CopyTo(text[kept..]);
        return kept + text.Length - read;
    }

    /// <summary>Reads a .nettrace file EventPipe wrote: the samples' stacks, with the methods named from the run's own
    /// method load events, the allocation ticks, the collections and an Avalonia app's frames. Each stack is named once (the
    /// trace numbers its own), kept once however many samples caught it, and a thread's samples in a row at one stack become
    /// one run.</summary>
    public static Snapshot Read(string nettrace)
    {
        string etlx = TraceLog.CreateFromEventPipeDataFile(nettrace, null, new TraceLogOptions { ContinueOnError = true });
        try
        {
            using TraceLog log = new(etlx);
            FrameNames names = new(log);
            List<int[]> stacks = [];
            Dictionary<int[], int> distinct = new(StackComparer.Instance);
            Dictionary<int[], int>.AlternateLookup<ReadOnlySpan<int>> found = distinct.GetAlternateLookup<ReadOnlySpan<int>>();
            // Per stack the trace numbers, the one kept for it and one more: none until an event caught it.
            int[] numbered = new int[log.CallStacks.Count];
            // Each thread's runs, the last of which its next sample at the same stack lengthens.
            Dictionary<int, List<SampleRun>> runs = [];
            Dictionary<int, string> threads = [];
            Dictionary<string, string> types = new(StringComparer.Ordinal);
            List<AllocationTick> ticks = [];
            List<Collection> collections = [];
            Profiler.UiFrames.Reader uiFrames = new();
            (int Number, int Generation, string Reason, double At)? collecting = null;
            double? suspended = null;
            foreach (TraceEvent data in log.Events)
            {
                if (data.ProviderName == RuntimeProvider)
                {
                    switch (data.EventName)
                    {
                        case "GC/AllocationTick":
                            string type = data.PayloadByName("TypeName") as string is { Length: > 0 } typed ? typed : "(unknown type)";
                            if (!types.TryGetValue(type, out string? known))
                            {
                                types[type] = known = type;
                            }
                            long bytes = Convert.ToInt64(data.PayloadByName("AllocationAmount64") ?? data.PayloadByName("AllocationAmount") ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
                            ticks.Add(new AllocationTick(data.TimeStampRelativeMSec, known, bytes, Stack(data)));
                            break;
                        case "GC/Start":
                            collecting = (Convert.ToInt32(data.PayloadByName("Count") ?? 0, System.Globalization.CultureInfo.InvariantCulture),
                                Convert.ToInt32(data.PayloadByName("Depth") ?? 0, System.Globalization.CultureInfo.InvariantCulture),
                                data.PayloadByName("Reason")?.ToString() ?? string.Empty,
                                data.TimeStampRelativeMSec);
                            break;
                        case "GC/SuspendEEStart":
                            suspended = data.TimeStampRelativeMSec;
                            break;
                        case "GC/RestartEEStop" when collecting is { } gc:
                            collections.Add(new Collection(gc.Number, gc.Generation, gc.Reason, gc.At, suspended is { } from ? data.TimeStampRelativeMSec - from : 0));
                            collecting = null;
                            suspended = null;
                            break;
                    }
                    continue;
                }
                if (uiFrames.Take(data) || data.ProviderName != SampleProvider || data.CallStackIndex() == CallStackIndex.Invalid)
                {
                    continue;
                }
                int threadId = data.ThreadID;
                if (!runs.TryGetValue(threadId, out List<SampleRun>? taken))
                {
                    runs[threadId] = taken = [];
                    threads[threadId] = data.Thread()?.ThreadInfo is { Length: > 0 } info ? $"Thread {threadId} ({info})" : $"Thread {threadId}";
                }
                int stack = Stack(data);
                double at = data.TimeStampRelativeMSec;
                if (taken.Count > 0 && taken[^1] is { } run && run.Stack == stack && at >= run.FromMs && at - run.ToMs <= LongestGap)
                {
                    taken[^1] = new SampleRun(run.FromMs, Math.Max(run.ToMs, at), stack, run.Count + 1);
                }
                else
                {
                    taken.Add(new SampleRun(at, at, stack, 1));
                }
            }
            (int Thread, SampleRun[] Runs)[] kept = [.. runs.Select(t => (t.Key, t.Value.ToArray()))];
            IReadOnlyList<UiFrame> drawn = uiFrames.Frames();
            // A run attached for its frames alone may have few samples: its frames are the run too.
            IEnumerable<double> starts = kept.Select(t => t.Runs.Min(r => r.FromMs)).Concat(drawn.Select(f => f.StartMs));
            IEnumerable<double> ends = kept.Select(t => t.Runs.Max(r => r.ToMs)).Concat(drawn.Select(f => f.EndMs));
            double first = starts.DefaultIfEmpty(0).Min();
            double end = ends.DefaultIfEmpty(0).Max();
            return new Snapshot(kept, stacks, names.Named, names.Codes, threads, ticks, collections, drawn, first, end, nettrace, null);

            // The event's stack as a number into the table, each the trace numbers named once and each distinct one kept once.
            int Stack(TraceEvent data)
            {
                CallStackIndex index = data.CallStackIndex();
                bool numberable = index != CallStackIndex.Invalid && (int)index < numbered.Length;
                if (numberable && numbered[(int)index] > 0)
                {
                    return numbered[(int)index] - 1;
                }
                ReadOnlySpan<int> caught = names.Walk(index);
                if (!found.TryGetValue(caught, out int number))
                {
                    int[] stack = caught.ToArray();
                    distinct[stack] = number = stacks.Count;
                    stacks.Add(stack);
                }
                if (numberable)
                {
                    numbered[(int)index] = number + 1;
                }
                return number;
            }
        }
        finally
        {
            try
            {
                File.Delete(etlx);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Stacks compared by their frames, so one caught again is the one kept, and looked up by a walk's frames
    /// before an array is made of them.</summary>
    private sealed class StackComparer : IEqualityComparer<int[]>, IAlternateEqualityComparer<ReadOnlySpan<int>, int[]>
    {
        public static readonly StackComparer Instance = new();

        public bool Equals(int[]? x, int[]? y) => x is not null && y is not null ? x.AsSpan().SequenceEqual(y) : x == y;

        public int GetHashCode(int[] stack) => Hash(stack);

        public bool Equals(ReadOnlySpan<int> alternate, int[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<int> alternate) => Hash(alternate);

        public int[] Create(ReadOnlySpan<int> alternate) => alternate.ToArray();

        private static int Hash(ReadOnlySpan<int> stack)
        {
            HashCode hash = new();
            hash.AddBytes(MemoryMarshal.AsBytes(stack));
            return hash.ToHashCode();
        }
    }

    /// <summary>The trace's stacks as numbers into a table of frames, walked down the trace's own table of callers: each
    /// code address is numbered once, each method's name made once, and a walk's frames are written into a buffer it keeps.
    /// A frame is a method and its module; runs of unnamed native frames are one, and the runtime's GC poll at the leaf
    /// (where the sampler stops a busy loop) is left to its caller.</summary>
    private sealed class FrameNames(TraceLog log)
    {
        private readonly TraceCallStacks callStacks = log.CallStacks;
        private readonly TraceCodeAddresses addresses = log.CodeAddresses;
        private readonly Dictionary<(string Method, string Module), int> table = [];
        private readonly Dictionary<string, string> natives = new(StringComparer.Ordinal);

        /// <summary>Per code address, its frame's number and one more: none until a walk came to it.</summary>
        private int[] byAddress = new int[log.CodeAddresses.Count];

        /// <summary>Per method, its name as shown.</summary>
        private string?[] byMethod = new string?[log.CodeAddresses.Methods.Count];

        private int[] walked = new int[128];

        public List<(string Method, string Module)> Named { get; } = [];

        /// <summary>Each frame's module file and token, beside <see cref="Named"/>.</summary>
        public List<MethodCode?> Codes { get; } = [];

        /// <summary>A stack's frames from where it was caught to its thread's first, good until the next walk.</summary>
        public ReadOnlySpan<int> Walk(CallStackIndex stack)
        {
            int count = 0;
            string? previous = null;
            for (CallStackIndex at = stack; at != CallStackIndex.Invalid; at = callStacks.Caller(at))
            {
                int frame = Frame(callStacks.CodeAddressIndex(at));
                string method = Named[frame].Method;
                if ((previous == method && method.StartsWith('[')) || (count == 0 && method.Contains("<PollGC>", StringComparison.Ordinal)))
                {
                    continue;
                }
                if (count == walked.Length)
                {
                    Array.Resize(ref walked, walked.Length * 2);
                }
                walked[count++] = frame;
                previous = method;
            }
            return walked.AsSpan(0, count);
        }

        /// <summary>The number of a code address's frame, named and numbered into the table the first time.</summary>
        private int Frame(CodeAddressIndex address)
        {
            int index = (int)address;
            if (index >= byAddress.Length)
            {
                Array.Resize(ref byAddress, Math.Max(index + 1, byAddress.Length * 2));
            }
            if (byAddress[index] > 0)
            {
                return byAddress[index] - 1;
            }
            MethodIndex methodIndex = addresses.MethodIndex(address);
            TraceModuleFile? moduleFile = addresses.ModuleFile(address);
            string module = moduleFile?.Name ?? string.Empty;
            string method = addresses.Methods.FullMethodName(methodIndex) is { Length: > 0 } full ? Method(methodIndex, full) : Native(module);
            if (!table.TryGetValue((method, module), out int number))
            {
                table[(method, module)] = number = Named.Count;
                Named.Add((method, module));
                int token = addresses.Methods.MethodToken(methodIndex);
                Codes.Add(methodIndex != MethodIndex.Invalid && token != 0
                    && (addresses.ModuleFiles[addresses.Methods.MethodModuleFileIndex(methodIndex)]?.FilePath ?? (moduleFile is null ? string.Empty : moduleFile.FilePath)) is { Length: > 0 } file
                    ? new MethodCode(file, token)
                    : null);
            }
            byAddress[index] = number + 1;
            return number;
        }

        private string Method(MethodIndex method, string full)
        {
            int index = (int)method;
            if (index >= byMethod.Length)
            {
                Array.Resize(ref byMethod, Math.Max(index + 1, byMethod.Length * 2));
            }
            return byMethod[index] ??= Pretty(full);
        }

        private string Native(string module)
        {
            if (!natives.TryGetValue(module, out string? name))
            {
                natives[module] = name = $"[{(module.Length > 0 ? module : "native")}]";
            }
            return name;
        }
    }
}
