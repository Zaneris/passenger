using System.Diagnostics.Tracing;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A program's heap read from its runtime's own GC heap dump, through its diagnostics port alone, for a program whose
/// memory no other may read (<see cref="DotnetProcesses.WhyUnreadable"/>): a session with the GC heap snapshot keywords has
/// the runtime collect in full and walk what lived, telling each object's type, size and references and the roots
/// (GCBulkType, GCBulkNode, GCBulkEdge, GCBulkRootEdge, GCBulkRootStaticVar and ConditionalWeakTable's dependent handles).
/// It names no field or element that holds a reference (<see cref="HeapGraph.UnnamedOffset"/>), no value, a static by its
/// field alone (its event's type is the field's, not the one declaring it), and no method whose local is a root.</summary>
internal static class GcHeapDump
{
    private const string Runtime = "Microsoft-Windows-DotNETRuntime";

    /// <summary>GC, Type, GCHeapDump, GCHeapCollect and GCHeapAndTypeNames: what TraceEvent calls GCHeapSnapshot.</summary>
    private const long Keywords = 0x1980001;

    /// <summary>How long the runtime is given to collect and walk its heap.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>What such a snapshot lacks beside one read from a dump, in words for the person and for an agent.</summary>
    public const string Lacks = "Read from the runtime's own GC heap dump, which tells each object's type, size and references but names no field or "
        + "element holding one, shows no field's value or string's text, and names a static field's root without the type declaring it and a "
        + "local variable's without its method";

    /// <summary>The heap as the runtime's heap dump tells it, and the number of the collection that walked it, whose moves
    /// its objects' addresses are after; with how long the dump took.</summary>
    public static async Task<(HeapGraph Graph, int Collection, TimeSpan Took)> ReadAsync(int processId, CancellationToken cancel)
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        EventPipeSession session = await StartAsync(new DiagnosticsClient(processId), cancel);
        Heard heard = new();
        using EventPipeEventSource source = new(session.EventStream);
        heard.Follow(source);
        // Not held: read until its session stops, which the wait below bounds.
        Task reading = BackgroundWork.Unheld(() =>
        {
            try
            {
                source.Process();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or EndOfStreamException or FormatException)
            {
                // The stream ended with the program or the session: what was heard is judged below.
            }
        }, CancellationToken.None);
        bool walked;
        bool ended = false;
        try
        {
            walked = await Task.WhenAny(heard.Walked, Task.Delay(Waits.Of(Limit), cancel)) == heard.Walked;
            cancel.ThrowIfCancellationRequested();
        }
        finally
        {
            await EventPipeStops.StopAsync(session, TimeSpan.FromSeconds(30));
            ended = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None)) == reading;
            session.Dispose();
            if (!ended)
            {
                source.StopProcessing();
            }
        }
        if (!ended)
        {
            throw new InvalidOperationException("The runtime's heap dump did not end once its session stopped");
        }
        if (!walked)
        {
            throw new InvalidOperationException($"The program's runtime gave no heap dump within {Waits.Of(Limit).TotalMinutes:0} minutes");
        }
        if (source.EventsLost > 0)
        {
            throw new InvalidOperationException($"The runtime's heap dump lost {source.EventsLost:N0} of its events, more than its buffer held: try again with the program quieter");
        }
        TimeSpan took = System.Diagnostics.Stopwatch.GetElapsedTime(began);
        HeapGraph graph = await BackgroundWork.Run(() => heard.Graph(cancel), cancel);
        return (graph, heard.Collection, took);
    }

    /// <summary>The session that has the runtime walk its heap: one that blocks the runtime rather than drop its events where
    /// the runtime takes that (.NET 11), else one with a buffer a large heap's dump fits.</summary>
    private static async Task<EventPipeSession> StartAsync(DiagnosticsClient client, CancellationToken cancel)
    {
        EventPipeProvider[] providers = [new(Runtime, EventLevel.Verbose, Keywords)];
        try
        {
            return await client.StartEventPipeSessionAsync(new EventPipeSessionConfiguration(providers, 1024, 0, false, EventPipeBufferingMode.Block), cancel);
        }
        catch (UnsupportedCommandException)
        {
            return await client.StartEventPipeSessionAsync(new EventPipeSessionConfiguration(providers, 1024, 0, false, EventPipeBufferingMode.Drop), cancel);
        }
    }

    /// <summary>A runtime's type name as a dump's reader writes it: a generic's arity taken out and its arguments in angle
    /// brackets, separated by a comma and a space (<c>Dictionary`2[System.String,System.Int32]</c> as
    /// <c>Dictionary&lt;System.String, System.Int32&gt;</c>), an argument's assembly left out, arrays as written.</summary>
    public static string TypeName(string written)
    {
        StringBuilder shown = new(written.Length);
        int at = 0;
        Type(written, ref at, shown);
        return at < written.Length ? shown.Append(written, at, written.Length - at).ToString() : shown.ToString();
    }

    private static void Type(string written, ref int at, StringBuilder shown)
    {
        while (at < written.Length && written[at] is not ('[' or ']' or ','))
        {
            if (written[at] == '`')
            {
                for (at++; at < written.Length && char.IsAsciiDigit(written[at]); at++)
                {
                }
                continue;
            }
            shown.Append(written[at++]);
        }
        while (at < written.Length && written[at] == '[')
        {
            if (at + 1 < written.Length && written[at + 1] is ']' or ',')
            {
                int close = written.IndexOf(']', at);
                close = close < 0 ? written.Length - 1 : close;
                shown.Append(written, at, close - at + 1);
                at = close + 1;
                continue;
            }
            at++;
            shown.Append('<');
            bool first = true;
            while (at < written.Length && written[at] != ']')
            {
                for (; at < written.Length && written[at] == ' '; at++)
                {
                }
                if (!first)
                {
                    shown.Append(", ");
                }
                first = false;
                if (at < written.Length && written[at] == '[')
                {
                    // An argument with its assembly: the type alone.
                    at++;
                    Type(written, ref at, shown);
                    for (int depth = 0; at < written.Length && (depth > 0 || written[at] != ']'); at++)
                    {
                        depth += written[at] == '[' ? 1 : written[at] == ']' ? -1 : 0;
                    }
                    at++;
                }
                else
                {
                    Type(written, ref at, shown);
                }
                if (at < written.Length && written[at] == ',')
                {
                    at++;
                }
            }
            at++;
            shown.Append('>');
        }
    }

    /// <summary>What a session hears of the heap dump: the types logged, and between the start and the end of the collection
    /// that walks the heap, the objects, their references and the roots, each kind's blocks by their index.</summary>
    private sealed class Heard
    {
        private readonly TaskCompletionSource walked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<ulong, (string Name, bool Array)> types = [];
        private readonly SortedDictionary<int, (ulong[] Address, long[] Size, ulong[] Type, int[] Edges)> nodes = [];
        private readonly SortedDictionary<int, ulong[]> edges = [];
        private readonly List<(ulong Object, RootKind Kind)> roots = [];
        private readonly List<(ulong Object, string Field, bool Thread)> statics = [];
        private readonly Dictionary<ulong, List<ulong>> dependents = [];
        private bool walking;
        private int pointerSize = 8;

        /// <summary>Done once the collection that walked the heap has ended.</summary>
        public Task Walked => walked.Task;

        /// <summary>The number of the collection that walked the heap, or -1 before it began.</summary>
        public int Collection { get; private set; } = -1;

        public void Follow(EventPipeEventSource source)
        {
            source.Clr.GCStart += e =>
            {
                if (Collection < 0 && e.Depth == 2 && e.Type != GCType.BackgroundGC)
                {
                    Collection = e.Count;
                    pointerSize = e.PointerSize;
                    walking = true;
                }
            };
            source.Clr.GCStop += e =>
            {
                if (walking && e.Count == Collection)
                {
                    walking = false;
                    walked.TrySetResult();
                }
            };
            // A type is logged once for the session, before or during the walk.
            source.Clr.TypeBulkType += e =>
            {
                for (int i = 0; i < e.Count; i++)
                {
                    GCBulkTypeValues type = e.Values(i);
                    types[type.TypeID] = (type.TypeName, (type.Flags & TypeFlags.Array) != 0);
                }
            };
            source.Clr.GCBulkNode += e =>
            {
                if (!walking)
                {
                    return;
                }
                (ulong[] Address, long[] Size, ulong[] Type, int[] Edges) block = (new ulong[e.Count], new long[e.Count], new ulong[e.Count], new int[e.Count]);
                for (int i = 0; i < e.Count; i++)
                {
                    GCBulkNodeValues node = e.Values(i);
                    (block.Address[i], block.Size[i], block.Type[i], block.Edges[i]) = (node.Address, (long)node.Size, node.TypeID, (int)node.EdgeCount);
                }
                nodes[e.Index] = block;
            };
            source.Clr.GCBulkEdge += e =>
            {
                if (!walking)
                {
                    return;
                }
                ulong[] targets = new ulong[e.Count];
                for (int i = 0; i < e.Count; i++)
                {
                    targets[i] = e.Values(i).Target;
                }
                edges[e.Index] = targets;
            };
            source.Clr.GCBulkRootEdge += e =>
            {
                for (int i = 0; walking && i < e.Count; i++)
                {
                    GCBulkRootEdgeValues root = e.Values(i);
                    // A weak handle keeps nothing alive.
                    if ((root.GCRootFlag & GCRootFlags.WeakRef) == 0 && root.RootedNodeAddress != 0)
                    {
                        roots.Add((root.RootedNodeAddress, KindOf(root.GCRootKind, root.GCRootFlag)));
                    }
                }
            };
            source.Clr.GCBulkRootStaticVar += e =>
            {
                for (int i = 0; walking && i < e.Count; i++)
                {
                    GCBulkRootStaticVarValues field = e.Values(i);
                    statics.Add((field.ObjectID, field.FieldName, (field.Flags & GCRootStaticVarFlags.ThreadLocal) != 0));
                }
            };
            source.Clr.GCBulkRootConditionalWeakTableElementEdge += e =>
            {
                for (int i = 0; walking && i < e.Count; i++)
                {
                    GCBulkRootConditionalWeakTableElementEdgeValues entry = e.Values(i);
                    if (entry.GCKeyNodeID != 0 && entry.GCValueNodeID != 0)
                    {
                        if (!dependents.TryGetValue(entry.GCKeyNodeID, out List<ulong>? values))
                        {
                            dependents[entry.GCKeyNodeID] = values = [];
                        }
                        values.Add(entry.GCValueNodeID);
                    }
                }
            };
        }

        private static RootKind KindOf(GCRootKind kind, GCRootFlags flags) => kind switch
        {
            GCRootKind.Stack => RootKind.LocalVariable,
            GCRootKind.Finalizer => RootKind.FReachableQueue,
            GCRootKind.SizedRef => RootKind.SizedRefHandle,
            _ when (flags & GCRootFlags.RefCounted) != 0 => RootKind.RefCountedHandle,
            _ when (flags & GCRootFlags.Pinning) != 0 => RootKind.PinningHandle,
            _ => RootKind.Handle,
        };

        /// <summary>The heap the dump told, read once its stream has ended. Throws an <see cref="InvalidOperationException"/>
        /// where a block of objects or references is missing, as the rest would be read against the wrong objects.</summary>
        public HeapGraph Graph(CancellationToken cancel)
        {
            if (!Whole(nodes.Keys) || !Whole(edges.Keys))
            {
                throw new InvalidOperationException("The runtime's heap dump lost some of its objects or references, so it cannot be read");
            }
            HeapGraph.Building parts = new();
            int block = 0;
            int next = 0;
            ulong[][] references = [.. edges.Values];
            foreach ((ulong[] address, long[] size, ulong[] type, int[] count) in nodes.Values)
            {
                for (int i = 0; i < address.Length; i++)
                {
                    parts.Add(address[i], Type(type[i]), size[i]);
                    for (int e = 0; e < count[i]; e++)
                    {
                        while (block < references.Length && next == references[block].Length)
                        {
                            (block, next) = (block + 1, 0);
                        }
                        if (block == references.Length)
                        {
                            throw new InvalidOperationException("The runtime's heap dump told fewer references than its objects hold, so it cannot be read");
                        }
                        parts.Refers(references[block][next++], HeapGraph.UnnamedOffset);
                    }
                    if (dependents.TryGetValue(address[i], out List<ulong>? kept))
                    {
                        foreach (ulong value in kept)
                        {
                            parts.Refers(value, HeapGraph.DependentOffset);
                        }
                    }
                }
                cancel.ThrowIfCancellationRequested();
            }
            parts.EndObjects();
            foreach ((ulong owned, string field, bool thread) in statics)
            {
                parts.Root(owned, new GcRoot(RootKind.StaticReference, thread ? $"{field} (thread static)" : field));
            }
            foreach ((ulong owned, RootKind kind) in roots)
            {
                parts.Root(owned, new GcRoot(kind, string.Empty));
            }
            return parts.Analyse(pointerSize, cancel, new GcRoot(RootKind.Runtime, string.Empty));

            int Type(ulong id) => parts.TypeOf(id, () =>
            {
                (string name, bool array) = types.TryGetValue(id, out (string Name, bool Array) known) ? (TypeName(known.Name), known.Array) : ($"Type 0x{id:x}", false);
                return new HeapTypeInfo(name, id, 0, array && name.EndsWith("[]", StringComparison.Ordinal));
            });
        }

        /// <summary>Whether a kind's blocks are all there, counted from 0.</summary>
        private static bool Whole(IEnumerable<int> indexes)
        {
            int expected = 0;
            foreach (int index in indexes)
            {
                if (index != expected++)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
