using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A run's memory snapshots, and what follows their objects from one to the next: a GC moves objects, so an
/// address alone says nothing across a collection. From the first snapshot an EventPipe session of its own hears every
/// collection (the GCHeapSurvivalAndMovement keyword): which generations it condemned, their ranges as it began, and
/// every stretch of objects it kept, moved or not. Each earlier snapshot's objects are carried through those, those in
/// a condemned generation and in no kept stretch dying. A dump is placed among the collections by when createdump held
/// the program, the one long stretch with no sample; a full collection induced right after it says when every
/// collection before has been heard. Only the snapshots kept (<see cref="KeptSnapshots"/>) are followed.</summary>
internal sealed class HeapHistory : IAsyncDisposable
{
    private const string Runtime = "Microsoft-Windows-DotNETRuntime";
    private const long GcKeyword = 0x1;
    private const long MovementKeyword = 0x400000;
    private const long CollectKeyword = 0x800000;

    private readonly int processId;
    private readonly KeptSnapshots kept;
    private readonly object gate = new();
    private readonly SemaphoreSlim taking = new(1, 1);
    private readonly List<GcRecord> open = [];
    private readonly List<GcRecord> held = [];
    private readonly List<Tracker> trackers = [];
    private readonly List<(int Count, TaskCompletionSource<bool> Heard)> waiting = [];
    private bool holding;
    private int lastEnded = -1;
    private bool streamEnded;
    private int lostEvents;
    private int taken;
    private EventPipeSession? session;
    private EventPipeEventSource? source;
    private Task? listening;

    /// <summary>How Passenger writes the program's dump itself (<see cref="DumpTool.Find"/>), or why it cannot, found with
    /// the first snapshot.</summary>
    private (string? Tool, string? WhyNot)? dumpTool;

    public HeapHistory(int processId)
    {
        this.processId = processId;
        kept = new KeptSnapshots(Unfollow);
    }

    /// <summary>Takes the next snapshot, and finds each earlier one's objects in it. Where only the program's runtime can
    /// write its dump, which puts createdump's lines into the program's output, that is done only
    /// <paramref name="intoOutput"/>, else a <see cref="DumpIntoOutputException"/> says so before anything starts.</summary>
    public async Task<MemorySnapshot> TakeAsync(bool intoOutput, CancellationToken cancel)
    {
        await taking.WaitAsync(cancel);
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            dumpTool ??= await BackgroundWork.Run(() =>
            {
                DumpTool.Sweep();
                return DumpTool.Find(processId);
            }, cancel);
            if (dumpTool is (_, string why) && !intoOutput)
            {
                throw new DumpIntoOutputException($"Only the program's runtime can write its memory snapshot, as {why}, and it writes "
                    + "createdump's progress into the program's output, which a program speaking over its output (a language or MCP server) takes for its own");
            }
            await ListenAsync(cancel);
            lock (gate)
            {
                holding = true;
            }
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    string dump = DumpTool.Named(processId);
                    Task? writing = null;
                    Task<HeapGraph>? reading = null;
                    using CancellationTokenSource unread = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    try
                    {
                        Dumped written = await DumpAsync(dump, started => writing = started, cancel);
                        TimeSpan dumped = clock.Elapsed;
                        reading = BackgroundWork.Run(() => HeapGraph.Read(dump, unread.Token), unread.Token);
                        int induced = await InduceAsync(cancel);
                        bool heard = induced >= 0 && await HeardAsync(induced, TimeSpan.FromSeconds(30));
                        HeapGraph graph;
                        try
                        {
                            graph = await reading;
                        }
                        catch (HeapUnreadableException) when (attempt < 3)
                        {
                            continue;
                        }
                        string phases = $"dump {dumped.TotalSeconds:0.0} s, reading {graph.Walked.TotalSeconds:0.0} s, retention {graph.Analysed.TotalSeconds:0.0} s";
                        return Place(graph, heard ? written : null, clock.Elapsed, phases);
                    }
                    finally
                    {
                        // A read the snapshot no longer waits for stops; the dump goes once its writer and its reader end.
                        unread.Cancel();
                        UnawaitedWork.Observe(DumpTool.DeleteAfterAsync(dump, writing, reading));
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    Release();
                }
            }
        }
        finally
        {
            taking.Release();
        }
    }

    /// <summary>Has a dump of the program's heap written (by Passenger where it can, else by the runtime's createdump),
    /// which holds every thread of the program while it writes, and gives what shows when it held them: when
    /// it was asked for and answered, and the samples the runtime's sampler took meanwhile, every thread each
    /// millisecond, its own thread held with the rest. The writing is handed to <paramref name="started"/>: given up, it
    /// writes on.</summary>
    private async Task<Dumped> DumpAsync(string dump, Action<Task> started, CancellationToken cancel)
    {
        DiagnosticsClient client = new(processId);
        List<DateTime> samples = [];
        EventPipeSession sampling = await client.StartEventPipeSessionAsync(
            [new EventPipeProvider(Snapshot.SampleProvider, EventLevel.Informational)], requestRundown: false, circularBufferMB: 64, cancel);
        // Not held: read until its session stops.
        Task reading = BackgroundWork.Unheld(() =>
        {
            using EventPipeEventSource events = new(sampling.EventStream);
            events.AllEvents += e =>
            {
                lock (samples)
                {
                    samples.Add(e.TimeStamp.ToUniversalTime());
                }
            };
            events.Process();
        }, CancellationToken.None);
        DateTime sent = DateTime.UtcNow;
        try
        {
            Task writing = DumpTool.WriteAsync(dumpTool?.Tool, dumpTool is { WhyNot: null } ? null : client, processId, dump);
            started(writing);
            await writing.WaitAsync(cancel);
        }
        finally
        {
            // A stop not taken (the program gone) leaves no samples to wait for: the dump is placed by the collections alone.
            if (await EventPipeStops.StopAsync(sampling, TimeSpan.FromSeconds(30)))
            {
                try
                {
                    await reading.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                }
                catch (Exception error) when (error is IOException or TimeoutException)
                {
                    // Its samples never came: the dump is placed by the collections alone.
                }
            }
            sampling.Dispose();
        }
        lock (samples)
        {
            return new Dumped(sent, DateTime.UtcNow, [.. samples]);
        }
    }

    /// <summary>When a dump's program was held: the longest silence while it was written, with neither a sample nor a
    /// collection's beginning or end, since nothing of the program runs while createdump holds it and a dump takes far
    /// longer than the sampler's millisecond or a collection's pause.</summary>
    private static (DateTime From, DateTime To) Held(Dumped dump, IEnumerable<GcRecord> collections)
    {
        List<DateTime> marks = [dump.Sent, dump.Returned, .. dump.Samples, .. collections.SelectMany(r => new[] { r.Began, r.Ended })];
        DateTime[] within = [.. marks.Where(m => m >= dump.Sent && m <= dump.Returned).Order()];
        (DateTime From, DateTime To) longest = (dump.Sent, dump.Sent);
        for (int i = 1; i < within.Length; i++)
        {
            if (within[i] - within[i - 1] > longest.To - longest.From)
            {
                longest = (within[i - 1], within[i]);
            }
        }
        return longest;
    }

    /// <summary>Places a dump among the collections held while it was taken: earlier snapshots are carried up to it and
    /// matched with it, then this one is followed too, through the collections after. With no placing (the induced
    /// collection never heard), the dump's objects are not followed.</summary>
    private MemorySnapshot Place(HeapGraph graph, Dumped? dumped, TimeSpan took, string phases)
    {
        lock (gate)
        {
            int number = ++taken;
            Dictionary<int, Identity> earlier = [];
            if (source is { EventsLost: > 0 } listened && listened.EventsLost != lostEvents)
            {
                lostEvents = listened.EventsLost;
                Lose("The profiler lost some of the program's events");
            }
            if (dumped is { } dump)
            {
                // A collection can neither run nor end while the program is held: those ending before came before it.
                (DateTime From, DateTime To) hold = Held(dump, held);
                int before = held.FindIndex(r => r.Ended > hold.From);
                before = before < 0 ? held.Count : before;
                for (int i = 0; i < before; i++)
                {
                    Apply(held[i]);
                }
                // Those past the newest kept let go of their objects as this one comes: they are followed no further.
                trackers.RemoveAll(t => t.Number <= number - KeptSnapshots.Count);
                foreach (Tracker tracker in trackers)
                {
                    earlier[tracker.Number] = new Identity(tracker.Match(graph), tracker.Lost);
                }
                trackers.Add(new Tracker(number, graph));
                held.RemoveRange(0, before);
            }
            Release();
            MemorySnapshot snapshot = new(kept, number, DateTime.Now, graph, took, phases, earlier);
            kept.Took(snapshot);
            return snapshot;
        }
    }

    /// <summary>A snapshot removed: its objects are followed no further.</summary>
    private void Unfollow(int number)
    {
        lock (gate)
        {
            trackers.RemoveAll(t => t.Number == number);
        }
    }

    /// <summary>Lets the collections held go to every snapshot followed, in the order they ended.</summary>
    private void Release()
    {
        foreach (GcRecord record in held)
        {
            Apply(record);
        }
        held.Clear();
        holding = false;
    }

    private void Apply(GcRecord record)
    {
        foreach (Tracker tracker in trackers)
        {
            tracker.Apply(record);
        }
    }

    private void Lose(string why)
    {
        foreach (Tracker tracker in trackers)
        {
            tracker.Lost ??= why;
        }
    }

    /// <summary>Starts hearing the program's collections, once.</summary>
    private async Task ListenAsync(CancellationToken cancel)
    {
        if (session is not null)
        {
            return;
        }
        session = await new DiagnosticsClient(processId).StartEventPipeSessionAsync(
            [new EventPipeProvider(Runtime, EventLevel.Informational, GcKeyword | MovementKeyword)], requestRundown: false, circularBufferMB: 256, cancel);
        source = new EventPipeEventSource(session.EventStream);
        source.Clr.GCStart += OnStart;
        source.Clr.GCGenerationRange += e => Current(lose: false)?.Range(e.Generation, e.RangeStart, e.RangeUsedLength);
        source.Clr.GCGlobalHeapHistory += _ => Current(lose: false)?.Planned();
        source.Clr.GCBulkMovedObjectRanges += e =>
        {
            if (Current(lose: true) is { } record)
            {
                for (int i = 0; i < e.Count; i++)
                {
                    GCBulkMovedObjectRangesValues range = e.Values(i);
                    record.Survive(range.OldRangeBase, range.NewRangeBase, range.RangeLength);
                }
            }
        };
        source.Clr.GCBulkSurvivingObjectRanges += e =>
        {
            if (Current(lose: true) is { } record)
            {
                for (int i = 0; i < e.Count; i++)
                {
                    GCBulkSurvivingObjectRangesValues range = e.Values(i);
                    record.Survive(range.RangeBase, range.RangeBase, range.RangeLength);
                }
            }
        };
        source.Clr.GCStop += OnEnd;
        EventPipeEventSource events = source;
        // A thread of its own: the stream is read until the program ends or the run is put away.
        listening = BackgroundWork.UnheldThread(() =>
        {
            try
            {
                events.Process();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or EndOfStreamException or FormatException)
            {
            }
            lock (gate)
            {
                streamEnded = true;
                foreach ((int _, TaskCompletionSource<bool> heard) in waiting)
                {
                    heard.TrySetResult(false);
                }
                waiting.Clear();
            }
        });
    }

    /// <summary>The collection under way that the events in between belong to: the innermost begun, as a background
    /// collection's own come once the foreground ones inside it have ended. Null for one begun before the session, whose
    /// objects kept, when it has any, cannot be followed.</summary>
    private GcRecord? Current(bool lose)
    {
        lock (gate)
        {
            if (open.Count > 0)
            {
                return open[^1];
            }
            if (lose)
            {
                Lose("A collection was under way when the profiler began to follow the objects");
            }
            return null;
        }
    }

    private void OnStart(GCStartTraceData e)
    {
        lock (gate)
        {
            open.Add(new GcRecord(e.Count, e.Depth, e.Type == GCType.BackgroundGC) { Began = e.TimeStamp.ToUniversalTime() });
        }
    }

    private void OnEnd(GCEndTraceData e)
    {
        lock (gate)
        {
            int at = open.FindLastIndex(r => r.Count == e.Count);
            if (at >= 0)
            {
                GcRecord record = open[at];
                open.RemoveAt(at);
                record.Ended = e.TimeStamp.ToUniversalTime();
                if (holding)
                {
                    held.Add(record);
                }
                else
                {
                    Apply(record);
                }
            }
            else
            {
                Lose("A collection began before the profiler began to follow the objects");
            }
            lastEnded = Math.Max(lastEnded, e.Count);
            for (int i = waiting.Count - 1; i >= 0; i--)
            {
                if (waiting[i].Count <= lastEnded)
                {
                    waiting[i].Heard.TrySetResult(true);
                    waiting.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>Waits to hear a collection end; false when it never does in time, or the program ended.</summary>
    private async Task<bool> HeardAsync(int count, TimeSpan limit)
    {
        TaskCompletionSource<bool> heard = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (lastEnded >= count)
            {
                return true;
            }
            if (streamEnded)
            {
                return false;
            }
            waiting.Add((count, heard));
        }
        return await Task.WhenAny(heard.Task, Task.Delay(limit)) == heard.Task && await heard.Task;
    }

    /// <summary>Has the runtime collect in full (a session with the GCHeapCollect keyword), and gives that collection's
    /// number, or -1. The collection is over when the session's start returns.</summary>
    private async Task<int> InduceAsync(CancellationToken cancel)
    {
        int found = -1;
        using EventPipeSession inducing = await new DiagnosticsClient(processId).StartEventPipeSessionAsync(
            [new EventPipeProvider(Runtime, EventLevel.Informational, GcKeyword | CollectKeyword)], requestRundown: false, circularBufferMB: 16, cancel);
        // Not held: read until its session stops.
        Task reading = BackgroundWork.Unheld(() =>
        {
            using EventPipeEventSource events = new(inducing.EventStream);
            events.Clr.GCStart += e =>
            {
                if (found < 0 && e.Reason == GCReason.Induced)
                {
                    found = e.Count;
                }
            };
            events.Process();
        }, CancellationToken.None);
        await inducing.StopAsync(cancel);
        try
        {
            await reading.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        }
        catch (TimeoutException)
        {
            return -1;
        }
        return found;
    }

    public async ValueTask DisposeAsync()
    {
        if (session is { } listened)
        {
            await EventPipeStops.StopAsync(listened, TimeSpan.FromSeconds(5));
            listened.Dispose();
        }
        if (listening is not null)
        {
            await Task.WhenAny(listening, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        source?.Dispose();
        // No snapshot comes after: the objects followed are let go, the snapshots kept keeping their own.
        lock (gate)
        {
            trackers.Clear();
            held.Clear();
        }
    }
}

/// <summary>A dump as it was taken: when it was asked for and answered, and the runtime's samples meanwhile.</summary>
internal sealed record Dumped(DateTime Sent, DateTime Returned, IReadOnlyList<DateTime> Samples);

/// <summary>A generation's stretch of the heap as a collection began.</summary>
internal readonly record struct GenerationRange(int Generation, ulong Start, ulong End);

/// <summary>A collection as its events tell it: what it condemned, the generations as it began, and the stretches of
/// objects it kept, each where it was and where it went.</summary>
internal sealed class GcRecord(int count, int depth, bool background)
{
    private readonly List<GenerationRange> before = [];
    private List<(ulong Old, ulong New, ulong Length)> kept = [];
    private bool working;
    private bool sorted;

    public int Count { get; } = count;

    public DateTime Began { get; init; }

    public DateTime Ended { get; set; }

    /// <summary>A generation's range, kept as the collection began: those it reports as it ends come after its work.</summary>
    public void Range(int generation, ulong start, ulong used)
    {
        if (!working)
        {
            before.Add(new GenerationRange(generation, start, start + used));
        }
    }

    public void Planned() => working = true;

    public void Survive(ulong from, ulong to, ulong length)
    {
        working = true;
        kept.Add((from, to, length));
    }

    /// <summary>The stretches of the heap it condemned: every generation up to its own, the large and pinned object heaps
    /// with the second; a background collection's, the second's and those heaps only.</summary>
    public List<(ulong Start, ulong End)> Condemned() => [.. before
        .Where(r => background ? r.Generation >= 2 : r.Generation <= depth || depth >= 2)
        .Where(r => r.End > r.Start)
        .Select(r => (r.Start, r.End))
        .OrderBy(r => r.Start)];

    /// <summary>The stretches kept, in the order they lay before.</summary>
    public List<(ulong Old, ulong New, ulong Length)> Kept()
    {
        if (!sorted)
        {
            kept = [.. kept.OrderBy(k => k.Old)];
            sorted = true;
        }
        return kept;
    }
}

/// <summary>A snapshot's objects carried through the collections after it: each one's address now, sorted, those that died
/// dropped.</summary>
internal sealed class Tracker
{
    private readonly HeapGraph graph;
    private ulong[] addresses;
    private int[] objects;

    public Tracker(int number, HeapGraph graph)
    {
        Number = number;
        this.graph = graph;
        addresses = (ulong[])graph.Address.Clone();
        objects = [.. Enumerable.Range(0, graph.Count)];
    }

    public int Number { get; }

    /// <summary>Why its objects may not all be followed exactly, or null.</summary>
    public string? Lost { get; set; }

    /// <summary>Carries the objects through a collection: those it condemned go where the stretch they were in went, or
    /// die when they were in none; the rest stay put. Only a collection that condemned some of them costs a pass.</summary>
    public void Apply(GcRecord record)
    {
        List<(ulong Start, ulong End)> condemned = record.Condemned();
        if (condemned.Count == 0 && record.Kept().Count > 0)
        {
            Lost ??= "The runtime did not say which generations a collection condemned";
        }
        if (condemned.Count == 0 || !Touches(condemned))
        {
            return;
        }
        List<(ulong Old, ulong New, ulong Length)> kept = record.Kept();
        ulong[] stayed = new ulong[addresses.Length];
        int[] stayedObjects = new int[addresses.Length];
        int count = 0;
        List<(ulong Address, int Object)> moved = [];
        int c = 0;
        int k = 0;
        for (int i = 0; i < addresses.Length; i++)
        {
            ulong address = addresses[i];
            while (c < condemned.Count && condemned[c].End <= address)
            {
                c++;
            }
            if (c >= condemned.Count || condemned[c].Start > address)
            {
                stayed[count] = address;
                stayedObjects[count++] = objects[i];
                continue;
            }
            while (k < kept.Count && kept[k].Old + kept[k].Length <= address)
            {
                k++;
            }
            if (k < kept.Count && kept[k].Old <= address)
            {
                moved.Add((kept[k].New + (address - kept[k].Old), objects[i]));
            }
        }
        moved.Sort((a, b) => a.Address.CompareTo(b.Address));
        ulong[] merged = new ulong[count + moved.Count];
        int[] mergedObjects = new int[merged.Length];
        int s = 0;
        int m = 0;
        for (int i = 0; i < merged.Length; i++)
        {
            if (m >= moved.Count || (s < count && stayed[s] < moved[m].Address))
            {
                merged[i] = stayed[s];
                mergedObjects[i] = stayedObjects[s++];
            }
            else
            {
                merged[i] = moved[m].Address;
                mergedObjects[i] = moved[m++].Object;
            }
        }
        addresses = merged;
        objects = mergedObjects;
    }

    private bool Touches(List<(ulong Start, ulong End)> condemned)
    {
        foreach ((ulong start, ulong end) in condemned)
        {
            int at = Array.BinarySearch(addresses, start);
            at = at < 0 ? ~at : at;
            if (at < addresses.Length && addresses[at] < end)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Each of its snapshot's objects in a later snapshot: the index of the object of its type and size at the
    /// address it was carried to, or -1 when it died or that object is not it.</summary>
    public int[] Match(HeapGraph later)
    {
        int[] match = new int[graph.Count];
        Array.Fill(match, -1);
        bool[] claimed = new bool[later.Count];
        for (int i = 0; i < addresses.Length; i++)
        {
            int o = objects[i];
            int at = later.IndexOf(addresses[i]);
            if (at >= 0 && !claimed[at] && later.Size[at] == graph.Size[o]
                && later.Types[later.Type[at]].MethodTable == graph.Types[graph.Type[o]].MethodTable)
            {
                match[o] = at;
                claimed[at] = true;
            }
        }
        return match;
    }
}
