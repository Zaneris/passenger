using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace Passenger.Profiler;

/// <summary>A kind of GC root.</summary>
public enum RootKind
{
    StaticReference,
    Handle,
    PinningHandle,
    AsyncPinningHandle,
    RefCountedHandle,
    SizedRefHandle,
    LocalVariable,
    FReachableQueue,
}

/// <summary>A GC root: its kind, and what names it (a static field, the method whose local it is).</summary>
public sealed record GcRoot(RootKind Kind, string Name)
{
    /// <summary>The kind as shown, with a handle's flag.</summary>
    public string KindName => Kind switch
    {
        RootKind.StaticReference => "Static reference",
        RootKind.Handle => "Handle",
        RootKind.PinningHandle => "Handle (pinning)",
        RootKind.AsyncPinningHandle => "Handle (async pinning)",
        RootKind.RefCountedHandle => "Handle (ref-counted)",
        RootKind.SizedRefHandle => "Handle (sized ref)",
        RootKind.LocalVariable => "Local Variable",
        _ => "F-Reachable Queue",
    };

    public string Text => Name.Length == 0 ? KindName : $"{KindName}: {Name}";
}

/// <summary>A type on the heap and the names of its reference fields by offset, kept while the dump is read since the
/// dump goes once it has been.</summary>
internal sealed class HeapTypeInfo(string name, ulong methodTable, int componentSize, bool isVector)
{
    public string Name { get; } = name;
    public ulong MethodTable { get; } = methodTable;
    public int ComponentSize { get; } = componentSize;

    /// <summary>A one-dimensional array, whose elements are named by index.</summary>
    public bool IsVector { get; } = isVector;

    public Dictionary<int, string> Fields { get; } = [];
}

/// <summary>What a dump's heap held: every object reachable from a GC root, in address order, with its references and
/// the roots; for each object the shortest path that keeps it (a breadth-first walk from the roots, statics first) and
/// what it retains (the dominator tree, Lengauer and Tarjan's). Read with ClrMD; arrays rather than objects, since a
/// heap has millions.</summary>
internal sealed class HeapGraph
{
    /// <summary>A reference held by a dependent handle (ConditionalWeakTable) rather than a field.</summary>
    public const int DependentOffset = -1;

    /// <summary>Each DAC a dump was read through, by its path, loaded once more so it is never unloaded (<see cref="Runtime"/>).</summary>
    private static readonly ConcurrentDictionary<string, IntPtr> KeptDacs = new(StringComparer.Ordinal);

    public required ulong[] Address { get; init; }
    public required int[] Type { get; init; }
    public required long[] Size { get; init; }

    /// <summary>The references, each object's from <c>EdgeStart[i]</c> to <c>EdgeStart[i + 1]</c>: the object referred to
    /// and the offset of the field or element that holds it.</summary>
    public required int[] EdgeStart { get; init; }
    public required int[] EdgeTarget { get; init; }
    public required int[] EdgeOffset { get; init; }

    public required HeapTypeInfo[] Types { get; init; }
    public required GcRoot[] Roots { get; init; }

    /// <summary>Per object, the root its shortest path starts at, the object before it on that path (-1 for a root's own
    /// object), the field of that object which holds it, and the path's length.</summary>
    public required int[] Root { get; init; }
    public required int[] Parent { get; init; }
    public required int[] ParentOffset { get; init; }
    public required int[] Depth { get; init; }

    /// <summary>Per object, its retained size: its own and that of every object only it keeps alive.</summary>
    public required long[] Retained { get; init; }

    /// <summary>Per object, where it enters and leaves the dominator tree's walk: one dominates another when its span
    /// holds the other's.</summary>
    public required int[] Enter { get; init; }
    public required int[] Leave { get; init; }

    /// <summary>Per type, the bytes its objects together retain: what would be freed with all of them, those only several
    /// of them keep alive included.</summary>
    public required long[] TypeRetained { get; init; }

    public required int PointerSize { get; init; }

    /// <summary>How long the dump's walk took, and the analysis after it.</summary>
    public TimeSpan Walked { get; internal set; }
    public TimeSpan Analysed { get; internal set; }

    public int Count => Address.Length;

    /// <summary>The object at an address, or -1.</summary>
    public int IndexOf(ulong address) => Array.BinarySearch(Address, address) is var at and >= 0 ? at : -1;

    /// <summary>How the field or element at an offset in an object is written: "_items", "at [3]".</summary>
    public string Holder(int holder, int offset)
    {
        HeapTypeInfo type = Types[Type[holder]];
        if (offset == DependentOffset)
        {
            return "dependent handle";
        }
        if (type.IsVector && type.ComponentSize > 0)
        {
            return $"at [{(offset - PointerSize) / type.ComponentSize}]";
        }
        return type.Fields.TryGetValue(offset, out string? field) ? field : $"+{offset}";
    }

    /// <summary>What some objects keep alive together: their bytes, and those of every object no root reaches without them
    /// (one only several of them hold too). A walk of the whole heap.</summary>
    public long RetainedTogether(IReadOnlyList<int> members)
    {
        // The members are marked as reached already, so the walk never passes them.
        bool[] reached = new bool[Count];
        foreach (int member in members)
        {
            reached[member] = true;
        }
        int[] queue = new int[Count];
        int tail = 0;
        for (int i = 0; i < Count; i++)
        {
            if (Parent[i] < 0 && !reached[i])
            {
                reached[i] = true;
                queue[tail++] = i;
            }
        }
        long all = 0;
        foreach (long bytes in Size)
        {
            all += bytes;
        }
        long kept = 0;
        for (int head = 0; head < tail; head++)
        {
            int from = queue[head];
            kept += Size[from];
            for (int e = EdgeStart[from]; e < EdgeStart[from + 1]; e++)
            {
                if (!reached[EdgeTarget[e]])
                {
                    reached[EdgeTarget[e]] = true;
                    queue[tail++] = EdgeTarget[e];
                }
            }
        }
        return all - kept;
    }

    /// <summary>The kind of holder an offset is, the same for every element of an array: what paths are merged by.</summary>
    public int HolderKey(int holder, int offset) => Types[Type[holder]].IsVector && offset != DependentOffset ? int.MinValue : offset;

    /// <summary>Reads a dump of a .NET program's heap. Throws <see cref="HeapUnreadableException"/> when the dump caught the
    /// GC mid-collection, which another dump will not.</summary>
    public static HeapGraph Read(string dump, CancellationToken cancel = default)
    {
        Stopwatch clock = Stopwatch.StartNew();
        using DataTarget target = DataTarget.LoadDump(dump, new DataTargetOptions { FileLocator = NothingDownloaded.Locator });
        if (target.ClrVersions.Length == 0)
        {
            throw new InvalidOperationException("The dump has no .NET runtime in it");
        }
        ClrInfo clr = target.ClrVersions[0];
        using ClrRuntime runtime = Runtime(clr);
        ClrHeap heap;
        try
        {
            heap = runtime.Heap;
        }
        catch (NotSupportedException)
        {
            // ClrMD reads no runtime newer than it knows (.NET 11's for ClrMD 3).
            throw new InvalidOperationException($"the program runs on .NET {clr.Version.Major}.{clr.Version.Minor}, newer than the memory profiler can read");
        }
        if (!heap.CanWalkHeap)
        {
            throw new HeapUnreadableException();
        }
        Reading reading = new(runtime, target.DataReader.PointerSize);
        reading.Walk(cancel);
        reading.FindRoots();
        TimeSpan walked = clock.Elapsed;
        HeapGraph graph = reading.Analyse(cancel);
        graph.Walked = walked;
        graph.Analysed = clock.Elapsed - walked;
        return graph;
    }

    /// <summary>The runtime in a dump, read through the DAC in the program's own runtime folder, which on macOS and
    /// Linux stays loaded for the IDE's life: the DAC leaves a thread-local destructor behind, which would run from
    /// unmapped memory as a thread that used it ends once it was unloaded.</summary>
    private static ClrRuntime Runtime(ClrInfo clr)
    {
        OSPlatform platform = OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
        string? folder = Path.GetDirectoryName(clr.ModuleInfo.FileName);
        string? dac = string.IsNullOrEmpty(folder) ? null : clr.DebuggingLibraries
            .Where(l => l.Kind == DebugLibraryKind.Dac && l.Platform == platform && l.TargetArchitecture == RuntimeInformation.ProcessArchitecture)
            .Select(l => Path.Combine(folder, Path.GetFileName(l.FileName)))
            .FirstOrDefault(File.Exists);
        if (dac is null)
        {
            throw new InvalidOperationException($"the program's .NET {clr.Version.Major}.{clr.Version.Minor} has no DAC beside it to read its heap with");
        }
        if (!OperatingSystem.IsWindows())
        {
            KeptDacs.GetOrAdd(dac, NativeLibrary.Load);
        }
        return clr.CreateRuntime(dac, ignoreMismatch: true);
    }

    /// <summary>ClrMD's file locator for what a dump lacks, which finds nothing: a program run here never needs
    /// downloads from symbol servers, nor the symbol cache and sign-in they would bring.</summary>
    private sealed class NothingDownloaded : IFileLocator
    {
        public static readonly NothingDownloaded Locator = new();

        public string? FindPEImage(string fileName, int buildTimeStamp, int imageSize, bool checkProperties) => null;

        public string? FindPEImage(string fileName, SymbolProperties archivedUnder, ImmutableArray<byte> buildIdOrUUID, OSPlatform originalPlatform, bool checkProperties) => null;
    }

    /// <summary>A dump's walk: the objects and references as found, then the roots, then what is reachable.</summary>
    private sealed class Reading(ClrRuntime runtime, int pointerSize)
    {
        private readonly ClrHeap heap = runtime.Heap;
        private readonly Dictionary<ulong, int> typeOf = [];
        private readonly List<HeapTypeInfo> types = [];
        private readonly Growing<ulong> addresses = new();
        private readonly Growing<int> objectTypes = new();
        private readonly Growing<long> sizes = new();
        private readonly Growing<int> edgeStarts = new();
        private readonly Growing<ulong> edgeTargets = new();
        private readonly Growing<int> edgeOffsets = new();
        private readonly List<GcRoot> roots = [];
        private readonly Dictionary<GcRoot, int> rootIds = [];
        private readonly List<(ulong Object, int Root)> rooted = [];

        /// <summary>The addresses sorted, and beside each its object's place in the walk, only when the walk found them out
        /// of order: in order, they are looked up where the walk wrote them.</summary>
        private ulong[]? sorted;
        private int[]? order;

        public void Walk(CancellationToken cancel)
        {
            (ulong Key, ulong Value)[] dependents = Dependents();
            foreach (ClrObject item in heap.EnumerateObjects())
            {
                if (item.IsFree || item.Type is not { } type)
                {
                    continue;
                }
                int index = TypeIndex(type);
                addresses.Add(item.Address);
                objectTypes.Add(index);
                sizes.Add((long)item.Size);
                edgeStarts.Add(edgeTargets.Count);
                if (type.ContainsPointers)
                {
                    foreach (ClrReference reference in item.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: false))
                    {
                        edgeTargets.Add(reference.Object.Address);
                        int offset = reference.Offset;
                        edgeOffsets.Add(offset);
                        if (!reference.IsArrayElement && !types[index].Fields.ContainsKey(offset))
                        {
                            types[index].Fields[offset] = FieldName(reference);
                        }
                    }
                }
                // What a ConditionalWeakTable keeps alive for a key, whatever the key's type holds.
                for (int at = FirstDependent(dependents, item.Address); at < dependents.Length && dependents[at].Key == item.Address; at++)
                {
                    edgeTargets.Add(dependents[at].Value);
                    edgeOffsets.Add(DependentOffset);
                }
                if ((addresses.Count & 0xFFFF) == 0)
                {
                    cancel.ThrowIfCancellationRequested();
                }
            }
            edgeStarts.Add(edgeTargets.Count);
            // Regions need not come in address order; what the rest looks up by address must.
            ReadOnlySpan<ulong> walked = addresses.Items;
            bool ascending = true;
            for (int i = 1; i < walked.Length && ascending; i++)
            {
                ascending = walked[i] > walked[i - 1];
            }
            if (!ascending)
            {
                sorted = walked.ToArray();
                order = new int[sorted.Length];
                for (int i = 0; i < order.Length; i++)
                {
                    order[i] = i;
                }
                Array.Sort(sorted, order);
            }
        }

        /// <summary>The runtime's dependent handles (a ConditionalWeakTable's entries), each key and the object it keeps alive,
        /// in the keys' order.</summary>
        private (ulong Key, ulong Value)[] Dependents()
        {
            List<(ulong Key, ulong Value)> found = [];
            foreach (ClrHandle handle in runtime.EnumerateHandles())
            {
                if (handle.HandleKind == ClrHandleKind.Dependent && handle.Object.Address != 0 && handle.Dependent.Address != 0)
                {
                    found.Add((handle.Object.Address, handle.Dependent.Address));
                }
            }
            found.Sort();
            return [.. found];
        }

        /// <summary>Where a key's dependent handles begin among them, or past their end when it has none.</summary>
        private static int FirstDependent((ulong Key, ulong Value)[] dependents, ulong key)
        {
            int low = 0;
            int high = dependents.Length;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (dependents[middle].Key < key)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }

        private static string FieldName(ClrReference reference)
        {
            string outer = reference.Field?.Name ?? "?";
            return reference.InnerField?.Field?.Name is { } inner ? $"{outer}.{inner}" : outer;
        }

        private int TypeIndex(ClrType type)
        {
            if (!typeOf.TryGetValue(type.MethodTable, out int index))
            {
                index = types.Count;
                typeOf[type.MethodTable] = index;
                bool vector = type.IsArray && (type.Name?.EndsWith("[]", StringComparison.Ordinal) ?? false);
                types.Add(new HeapTypeInfo(type.Name ?? "(unnamed)", type.MethodTable, type.ComponentSize, vector));
            }
            return index;
        }

        /// <summary>Statics first, named by their field, then what the runtime itself counts as roots.</summary>
        public void FindRoots()
        {
            foreach (ClrModule module in runtime.EnumerateModules())
            {
                foreach ((ulong methodTable, int _) in module.EnumerateTypeDefToMethodTableMap())
                {
                    if (runtime.GetTypeByMethodTable(methodTable) is not { } type || type.StaticFields.Length == 0)
                    {
                        continue;
                    }
                    foreach (ClrStaticField field in type.StaticFields)
                    {
                        if (field.IsObjectReference && StaticSlot(field) is var slot and not 0
                            && runtime.DataTarget.DataReader.ReadPointer(slot) is var value and not 0)
                        {
                            Add(value, new GcRoot(RootKind.StaticReference, $"{type.Name}.{field.Name}"));
                        }
                    }
                }
            }
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                Add(root.Object.Address, root.RootKind switch
                {
                    ClrRootKind.Stack when root is ClrStackRoot { StackFrame: { } frame } => new GcRoot(RootKind.LocalVariable, Method(frame)),
                    ClrRootKind.Stack => new GcRoot(RootKind.LocalVariable, string.Empty),
                    ClrRootKind.PinnedHandle => new GcRoot(RootKind.PinningHandle, string.Empty),
                    ClrRootKind.AsyncPinnedHandle => new GcRoot(RootKind.AsyncPinningHandle, string.Empty),
                    ClrRootKind.RefCountedHandle => new GcRoot(RootKind.RefCountedHandle, string.Empty),
                    ClrRootKind.SizedRefHandle => new GcRoot(RootKind.SizedRefHandle, string.Empty),
                    ClrRootKind.FinalizerQueue => new GcRoot(RootKind.FReachableQueue, string.Empty),
                    _ => new GcRoot(RootKind.Handle, string.Empty),
                });
            }
        }

        private static string Method(ClrStackFrame frame)
        {
            string method = frame.Method is { } m ? $"{m.Type?.Name}.{m.Name}" : frame.FrameName ?? "?";
            return frame.Thread is { } thread ? $"{method} (thread #{thread.ManagedThreadId})" : method;
        }

        /// <summary>Where a static reference is kept, once its type's statics are made.</summary>
        private ulong StaticSlot(ClrStaticField field)
        {
            foreach (ClrAppDomain domain in runtime.AppDomains)
            {
                if (field.IsInitialized(domain) && field.GetAddress(domain) is var address and not 0)
                {
                    return address;
                }
            }
            return 0;
        }

        private void Add(ulong address, GcRoot root)
        {
            if (!rootIds.TryGetValue(root, out int id))
            {
                id = roots.Count;
                rootIds[root] = id;
                roots.Add(root);
            }
            rooted.Add((address, id));
        }

        private int Find(ulong address)
        {
            if (sorted is null)
            {
                return addresses.Items.BinarySearch(address) is var walked and >= 0 ? walked : -1;
            }
            int at = Array.BinarySearch(sorted, address);
            return at >= 0 ? order![at] : -1;
        }

        /// <summary>The reachable objects, their shortest paths, and their retained sizes.</summary>
        public HeapGraph Analyse(CancellationToken cancel)
        {
            int count = addresses.Count;
            ReadOnlySpan<int> starts = edgeStarts.Items;
            ReadOnlySpan<ulong> targets = edgeTargets.Items;
            int[] targetIndex = new int[targets.Length];
            for (int e = 0; e < targets.Length; e++)
            {
                targetIndex[e] = Find(targets[e]);
            }
            ReadOnlySpan<int> offsets = edgeOffsets.Items;

            // Breadth first from the roots, statics first: each object's shortest path.
            int[] root = new int[count];
            Array.Fill(root, -1);
            int[] parent = new int[count];
            Array.Fill(parent, -1);
            int[] parentOffset = new int[count];
            int[] depth = new int[count];
            int[] queue = new int[count];
            int tail = 0;
            foreach ((ulong rootObject, int id) in rooted.OrderBy(r => roots[r.Root].Kind))
            {
                if (Find(rootObject) is var at and >= 0 && root[at] < 0)
                {
                    root[at] = id;
                    queue[tail++] = at;
                }
            }
            int rootObjects = tail;
            for (int head = 0; head < tail; head++)
            {
                int from = queue[head];
                for (int e = starts[from]; e < starts[from + 1]; e++)
                {
                    int to = targetIndex[e];
                    if (to >= 0 && root[to] < 0)
                    {
                        root[to] = root[from];
                        parent[to] = from;
                        parentOffset[to] = offsets[e];
                        depth[to] = depth[from] + 1;
                        queue[tail++] = to;
                    }
                }
                if ((head & 0xFFFF) == 0)
                {
                    cancel.ThrowIfCancellationRequested();
                }
            }

            // Only what is reachable is kept, in address order.
            int[] kept = new int[count];
            Array.Fill(kept, -1);
            int live = 0;
            int[] liveOld = new int[tail];
            for (int at = 0; at < count; at++)
            {
                int old = order is null ? at : order[at];
                if (root[old] >= 0)
                {
                    kept[old] = live;
                    liveOld[live++] = old;
                }
            }
            ulong[] address = new ulong[live];
            int[] type = new int[live];
            long[] size = new long[live];
            int[] newRoot = new int[live];
            int[] newParent = new int[live];
            int[] newParentOffset = new int[live];
            int[] newDepth = new int[live];
            int[] edgeStart = new int[live + 1];
            int edgeCount = 0;
            for (int i = 0; i < live; i++)
            {
                int old = liveOld[i];
                for (int e = starts[old]; e < starts[old + 1]; e++)
                {
                    edgeCount += targetIndex[e] >= 0 ? 1 : 0;
                }
            }
            int[] edgeTarget = new int[edgeCount];
            int[] edgeOffset = new int[edgeCount];
            int edge = 0;
            ReadOnlySpan<ulong> allAddresses = addresses.Items;
            ReadOnlySpan<int> allTypes = objectTypes.Items;
            ReadOnlySpan<long> allSizes = sizes.Items;
            for (int i = 0; i < live; i++)
            {
                int old = liveOld[i];
                address[i] = allAddresses[old];
                type[i] = allTypes[old];
                size[i] = allSizes[old];
                newRoot[i] = root[old];
                newParent[i] = parent[old] < 0 ? -1 : kept[parent[old]];
                newParentOffset[i] = parentOffset[old];
                newDepth[i] = depth[old];
                edgeStart[i] = edge;
                for (int e = starts[old]; e < starts[old + 1]; e++)
                {
                    if (targetIndex[e] >= 0)
                    {
                        edgeTarget[edge] = kept[targetIndex[e]];
                        edgeOffset[edge++] = offsets[e];
                    }
                }
            }
            edgeStart[live] = edge;
            int[] rootObjectList = new int[rootObjects];
            for (int i = 0; i < rootObjects; i++)
            {
                rootObjectList[i] = kept[queue[i]];
            }
            cancel.ThrowIfCancellationRequested();

            Dominators dominators = Dominators.Of(live, edgeStart, edgeTarget, rootObjectList, size, type, types.Count, cancel);
            return new HeapGraph
            {
                Address = address,
                Type = type,
                Size = size,
                EdgeStart = edgeStart,
                EdgeTarget = edgeTarget,
                EdgeOffset = edgeOffset,
                Types = [.. types],
                Roots = [.. roots],
                Root = newRoot,
                Parent = newParent,
                ParentOffset = newParentOffset,
                Depth = newDepth,
                Retained = dominators.Retained,
                Enter = dominators.Enter,
                Leave = dominators.Leave,
                TypeRetained = dominators.TypeRetained,
                PointerSize = pointerSize,
            };
        }
    }

    /// <summary>The dominator tree of the reachable graph under a root of its own that refers to every root object
    /// (Lengauer and Tarjan's, path compression without balancing), and what it gives: each object's retained size, its
    /// span in the tree's walk, and each type's retained bytes.</summary>
    internal sealed class Dominators
    {
        public required long[] Retained { get; init; }
        public required int[] Enter { get; init; }
        public required int[] Leave { get; init; }
        public required long[] TypeRetained { get; init; }

        public static Dominators Of(int count, int[] edgeStart, int[] edgeTarget, int[] rootObjects, long[] size, int[] type, int typeCount, CancellationToken cancel,
            Steps? steps = null)
        {
            steps ??= new Steps(cancel);
            int nodes = count + 1;
            int top = count;
            // Successors of the root of its own are the root objects; predecessors are counted from both.
            int[] predecessorStart = new int[nodes + 1];
            for (int e = 0; e < edgeTarget.Length; e++)
            {
                predecessorStart[edgeTarget[e] + 1]++;
            }
            foreach (int r in rootObjects)
            {
                predecessorStart[r + 1]++;
            }
            for (int i = 0; i < nodes; i++)
            {
                predecessorStart[i + 1] += predecessorStart[i];
            }
            int[] predecessors = new int[predecessorStart[nodes]];
            int[] filled = new int[nodes];
            for (int from = 0; from < count; from++)
            {
                for (int e = edgeStart[from]; e < edgeStart[from + 1]; e++)
                {
                    int to = edgeTarget[e];
                    predecessors[predecessorStart[to] + filled[to]++] = from;
                }
            }
            foreach (int r in rootObjects)
            {
                predecessors[predecessorStart[r] + filled[r]++] = top;
            }

            // Depth-first numbering from the root of its own, and the order the walk leaves the objects in.
            int[] number = new int[nodes];
            Array.Fill(number, -1);
            int[] vertex = new int[nodes];
            int[] dfsParent = new int[nodes];
            int[] finished = new int[nodes];
            int numbered = 0;
            int left = 0;
            int[] stack = new int[nodes];
            int[] next = new int[nodes];
            int sp = 0;
            number[top] = numbered;
            vertex[numbered++] = top;
            dfsParent[top] = -1;
            stack[sp++] = top;
            while (sp > 0)
            {
                int v = stack[sp - 1];
                int successor = -1;
                if (v == top)
                {
                    while (next[v] < rootObjects.Length && successor < 0)
                    {
                        int w = rootObjects[next[v]++];
                        successor = number[w] < 0 ? w : -1;
                    }
                }
                else
                {
                    while (edgeStart[v] + next[v] < edgeStart[v + 1] && successor < 0)
                    {
                        int w = edgeTarget[edgeStart[v] + next[v]++];
                        successor = number[w] < 0 ? w : -1;
                    }
                }
                if (successor < 0)
                {
                    finished[left++] = v;
                    sp--;
                    continue;
                }
                number[successor] = numbered;
                vertex[numbered++] = successor;
                dfsParent[successor] = v;
                stack[sp++] = successor;
            }
            cancel.ThrowIfCancellationRequested();

            int[] semi = new int[nodes];
            int[] ancestor = new int[nodes];
            int[] best = new int[nodes];
            int[] idom = new int[nodes];
            int[] sameDominator = new int[nodes];
            int[] bucketHead = new int[nodes];
            int[] bucketNext = new int[nodes];
            Array.Fill(ancestor, -1);
            Array.Fill(sameDominator, -1);
            Array.Fill(bucketHead, -1);
            for (int i = 0; i < nodes; i++)
            {
                semi[i] = i;
                best[i] = i;
            }
            int[] path = new int[nodes];
            for (int i = numbered - 1; i > 0; i--)
            {
                int n = vertex[i];
                int p = dfsParent[n];
                int s = p;
                for (int k = predecessorStart[n]; k < predecessorStart[n + 1]; k++)
                {
                    int v = predecessors[k];
                    if (number[v] < 0)
                    {
                        continue;
                    }
                    int candidate = number[v] <= number[n] ? v : semi[Lowest(v)];
                    if (number[candidate] < number[s])
                    {
                        s = candidate;
                    }
                }
                semi[n] = s;
                bucketNext[n] = bucketHead[s];
                bucketHead[s] = n;
                ancestor[n] = p;
                best[n] = n;
                for (int v = bucketHead[p]; v >= 0; v = bucketNext[v])
                {
                    int y = Lowest(v);
                    if (semi[y] == semi[v])
                    {
                        idom[v] = p;
                    }
                    else
                    {
                        sameDominator[v] = y;
                    }
                }
                bucketHead[p] = -1;
                if ((i & 0xFFFF) == 0)
                {
                    cancel.ThrowIfCancellationRequested();
                }
            }
            for (int i = 1; i < numbered; i++)
            {
                int n = vertex[i];
                if (sameDominator[n] >= 0)
                {
                    idom[n] = idom[sameDominator[n]];
                }
            }

            // Retained sizes, children before their dominators; the root of its own retains everything, kept nowhere.
            long[] retained = new long[count];
            size.AsSpan().CopyTo(retained);
            for (int i = numbered - 1; i > 0; i--)
            {
                int n = vertex[i];
                if (idom[n] != top)
                {
                    retained[idom[n]] += retained[n];
                }
            }

            // The dominator tree's walk: each object's span.
            int[] childStart = new int[nodes + 1];
            for (int i = 1; i < numbered; i++)
            {
                childStart[idom[vertex[i]] + 1]++;
            }
            for (int i = 0; i < nodes; i++)
            {
                childStart[i + 1] += childStart[i];
            }
            // Siblings in reverse of the order the numbering walk left them, so those referring to an object mostly come before it.
            int[] children = new int[childStart[nodes]];
            Array.Clear(filled);
            for (int i = left - 1; i >= 0; i--)
            {
                int n = finished[i];
                if (n != top)
                {
                    children[childStart[idom[n]] + filled[idom[n]]++] = n;
                }
            }
            // The root of its own enters first and leaves last, its span kept nowhere.
            int[] enter = new int[count];
            int[] leave = new int[count];
            Array.Clear(next);
            sp = 0;
            int clock = 1;
            stack[sp++] = top;
            while (sp > 0)
            {
                int v = stack[sp - 1];
                if (childStart[v] + next[v] < childStart[v + 1])
                {
                    int child = children[childStart[v] + next[v]++];
                    enter[child] = clock++;
                    stack[sp++] = child;
                    continue;
                }
                if (v != top)
                {
                    leave[v] = clock++;
                }
                sp--;
            }
            cancel.ThrowIfCancellationRequested();

            // Each type's retained bytes: those of its outermost objects, and of each object only its objects together keep.
            Dictionary<int, int[]> together = Together(count, idom, childStart, children, predecessorStart, predecessors, enter, leave, edgeStart, edgeTarget,
                type, typeCount, stack, next, steps);
            long[] typeRetained = new long[typeCount];
            int[] open = new int[typeCount];
            Array.Clear(next);
            sp = 0;
            stack[sp++] = top;
            while (sp > 0)
            {
                int v = stack[sp - 1];
                if (childStart[v] + next[v] < childStart[v + 1])
                {
                    int child = children[childStart[v] + next[v]++];
                    if (open[type[child]]++ == 0)
                    {
                        typeRetained[type[child]] += retained[child];
                    }
                    foreach (int t in together.GetValueOrDefault(child, []))
                    {
                        if (open[t]++ == 0)
                        {
                            typeRetained[t] += retained[child];
                        }
                    }
                    stack[sp++] = child;
                    continue;
                }
                if (v != top)
                {
                    open[type[v]]--;
                    foreach (int t in together.GetValueOrDefault(v, []))
                    {
                        open[t]--;
                    }
                }
                sp--;
            }
            return new Dominators
            {
                Retained = retained,
                Enter = enter,
                Leave = leave,
                TypeRetained = typeRetained,
            };

            // The ancestor of v, on the forest built so far, whose semidominator is numbered lowest; compresses the path.
            int Lowest(int v)
            {
                if (ancestor[v] < 0)
                {
                    return v;
                }
                int length = 0;
                for (int a = v; ancestor[ancestor[a]] >= 0; a = ancestor[a])
                {
                    path[length++] = a;
                }
                for (int i = length - 1; i >= 0; i--)
                {
                    int a = path[i];
                    int up = ancestor[a];
                    if (number[semi[best[up]]] < number[semi[best[a]]])
                    {
                        best[a] = best[up];
                    }
                    ancestor[a] = ancestor[up];
                }
                return best[v];
            }
        }

        /// <summary>The types whose objects together, and none alone, keep an object alive, by object (only those with any):
        /// every path from the roots to it passes one of them. An object is so kept by a type when each object referring to
        /// it (but those it dominates) is of the type or so kept, down the dominator tree to its own dominator. Found in walks
        /// of the dominator tree holding each type's deepest object on the way, until nothing changes; an object not yet found
        /// counts as kept by every type, so each settles on the most types it can be kept by.</summary>
        private static Dictionary<int, int[]> Together(int count, int[] idom, int[] childStart, int[] children, int[] predecessorStart, int[] predecessors,
            int[] enter, int[] leave, int[] edgeStart, int[] edgeTarget, int[] type, int typeCount, int[] stack, int[] next, Steps steps)
        {
            const byte Held = 0;
            const byte Found = 1;
            const byte Unfound = 2;
            int top = count;
            // Only an object no edge from its dominator reaches can be kept by objects together; the rest are held.
            byte[] shared = new byte[count];
            bool any = false;
            for (int v = 0; v < count; v++)
            {
                bool fromDominator = enter[v] == 0;
                for (int k = predecessorStart[v]; k < predecessorStart[v + 1] && !fromDominator; k++)
                {
                    fromDominator = predecessors[k] == idom[v];
                }
                shared[v] = fromDominator ? Held : Unfound;
                any |= !fromDominator;
                steps.Take(1 + predecessorStart[v + 1] - predecessorStart[v]);
            }

            Dictionary<int, int[]> together = [];
            // Per type, its deepest object on the way down; the types with one, deepest first, linked around head.
            int[] near = new int[typeCount];
            Array.Fill(near, -1);
            int head = typeCount;
            int[] previous = new int[typeCount + 1];
            int[] following = new int[typeCount + 1];
            previous[head] = head;
            following[head] = head;
            // What each object on the way down changed, after a mark (Type -1) holding the deepest unfound object above it.
            List<(int Type, int Near, int Previous, int Following)> undo = [];
            int unfound = -1;
            // Per shared object, its references not walked this pass, and where kept holds its types so far (-1, none).
            int[] remaining = new int[count];
            int[] keptAt = new int[count];
            List<int> kept = [];
            bool changed = any;
            while (changed)
            {
                changed = false;
                kept.Clear();
                for (int v = 0; v < count; v++)
                {
                    if (shared[v] != Held)
                    {
                        keptAt[v] = -1;
                        remaining[v] = 0;
                        for (int k = predecessorStart[v]; k < predecessorStart[v + 1]; k++)
                        {
                            int p = predecessors[k];
                            if (p != top && enter[p] > 0 && !Dominates(v, p))
                            {
                                remaining[v]++;
                            }
                        }
                        steps.Take(1 + predecessorStart[v + 1] - predecessorStart[v]);
                    }
                }
                Array.Clear(next);
                int sp = 0;
                stack[sp++] = top;
                while (sp > 0)
                {
                    int v = stack[sp - 1];
                    if (childStart[v] + next[v] < childStart[v + 1])
                    {
                        int child = children[childStart[v] + next[v]++];
                        stack[sp++] = child;
                        Enter(child);
                        continue;
                    }
                    if (v != top)
                    {
                        Leave();
                    }
                    sp--;
                }
            }
            return together;

            bool Dominates(int v, int p) => enter[v] <= enter[p] && leave[p] <= leave[v];

            void Enter(int a)
            {
                undo.Add((-1, unfound, 0, 0));
                Touch(type[a], a);
                int walked = 2;
                if (shared[a] == Unfound)
                {
                    unfound = a;
                }
                else if (together.TryGetValue(a, out int[]? types))
                {
                    foreach (int t in types)
                    {
                        Touch(t, a);
                    }
                    walked += types.Length;
                }
                for (int e = edgeStart[a]; e < edgeStart[a + 1]; e++)
                {
                    int w = edgeTarget[e];
                    if (shared[w] != Held && !Dominates(w, a))
                    {
                        Reach(w);
                    }
                }
                steps.Take(walked + edgeStart[a + 1] - edgeStart[a]);
            }

            void Leave()
            {
                int walked = 0;
                while (undo[^1] is var (t, wasNear, wasPrevious, wasFollowing) && t >= 0)
                {
                    undo.RemoveAt(undo.Count - 1);
                    following[head] = following[t];
                    previous[following[t]] = head;
                    if (wasNear >= 0)
                    {
                        previous[t] = wasPrevious;
                        following[t] = wasFollowing;
                        following[wasPrevious] = t;
                        previous[wasFollowing] = t;
                    }
                    near[t] = wasNear;
                    walked++;
                }
                unfound = undo[^1].Near;
                undo.RemoveAt(undo.Count - 1);
                steps.Take(walked + 1);
            }

            // The type's deepest object on the way down is now a, first in the list.
            void Touch(int t, int a)
            {
                undo.Add((t, near[t], previous[t], following[t]));
                if (near[t] >= 0)
                {
                    following[previous[t]] = following[t];
                    previous[following[t]] = previous[t];
                }
                previous[t] = head;
                following[t] = following[head];
                previous[following[head]] = t;
                following[head] = t;
                near[t] = a;
            }

            // A reference from the object entered last to a shared one: the types between them and its dominator bound it.
            void Reach(int w)
            {
                int floor = idom[w] == top ? 0 : enter[idom[w]];
                if (unfound < 0 || enter[unfound] <= floor)
                {
                    if (keptAt[w] < 0)
                    {
                        keptAt[w] = kept.Count;
                        kept.Add(0);
                        for (int t = following[head]; t != head && enter[near[t]] > floor; t = following[t])
                        {
                            kept.Add(t);
                        }
                        kept[keptAt[w]] = kept.Count - keptAt[w] - 1;
                    }
                    else
                    {
                        int at = keptAt[w];
                        int had = kept[at];
                        int still = 0;
                        for (int i = 1; i <= had; i++)
                        {
                            int t = kept[at + i];
                            if (near[t] >= 0 && enter[near[t]] > floor)
                            {
                                kept[at + 1 + still++] = t;
                            }
                        }
                        kept[at] = still;
                        steps.Take(had);
                    }
                    steps.Take(1 + kept[keptAt[w]]);
                }
                if (--remaining[w] == 0 && keptAt[w] >= 0)
                {
                    Find(w);
                }
            }

            // Every reference to the shared object walked: its types are those all of them passed, but its own.
            void Find(int w)
            {
                int at = keptAt[w];
                List<int> types = [];
                for (int i = 1; i <= kept[at]; i++)
                {
                    if (kept[at + i] != type[w])
                    {
                        types.Add(kept[at + i]);
                    }
                }
                int before = shared[w] == Unfound ? -1 : together.TryGetValue(w, out int[]? had) ? had.Length : 0;
                changed |= before != types.Count;
                shared[w] = Found;
                if (types.Count == 0)
                {
                    together.Remove(w);
                }
                else
                {
                    together[w] = [.. types];
                }
            }
        }
    }

    /// <summary>The steps a retained-size pass walks, counted so its cost is held by a count rather than a clock, and
    /// Stop, heard every 4096th step.</summary>
    internal sealed class Steps(CancellationToken cancel)
    {
        public long Count { get; private set; }

        /// <summary>Told the steps walked each time Stop is heard, before it is: what a test stops a pass under way with.</summary>
        public Action<long>? Hearing { get; init; }

        public void Take(long steps)
        {
            long was = Count;
            Count += steps;
            if (was >> 12 != Count >> 12)
            {
                Hearing?.Invoke(Count);
                cancel.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>A growing array that never holds more than twice what it needs.</summary>
    private sealed class Growing<T>
    {
        private T[] items = new T[1024];

        public int Count { get; private set; }

        public void Add(T item)
        {
            if (Count == items.Length)
            {
                Array.Resize(ref items, items.Length * 2);
            }
            items[Count++] = item;
        }

        /// <summary>What it holds, over its own buffer.</summary>
        public ReadOnlySpan<T> Items => items.AsSpan(0, Count);
    }
}

/// <summary>A dump that caught the GC mid-collection, whose heap cannot be walked.</summary>
public sealed class HeapUnreadableException() : Exception("The dump caught the program mid-collection");
