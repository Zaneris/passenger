using System.Collections;

namespace Passenger.Profiler;

/// <summary>An object as an object set lists it: where it is, its type, its own bytes and what it retains.</summary>
public sealed record HeapObject(ulong Address, string Type, long Bytes, long RetainedBytes);

/// <summary>A step of the shortest paths to roots, merged across a set's objects: the set itself at the top, what holds
/// its objects under it (a field of a type, an array's elements), down to the GC roots, each step counting the set's
/// objects kept through it and the bytes they retain.</summary>
public sealed class RetentionNode(string text, GcRoot? root, int objects, long retainedBytes, IReadOnlyList<RetentionNode> children)
{
    /// <summary>"List&lt;Item&gt;._items", "Item[] at [3]", "Static reference: Holder.Items".</summary>
    public string Text { get; } = text;

    /// <summary>The root this step is, at a path's end; null for the set and its holders.</summary>
    public GcRoot? Root { get; } = root;

    public int Objects { get; } = objects;

    public long RetainedBytes { get; } = retainedBytes;

    /// <summary>The steps after this one, the most retained first.</summary>
    public IReadOnlyList<RetentionNode> Children { get; } = children;
}

/// <summary>One of a set's key retention paths: a GC root, the holders after it, and how many of the set's objects it keeps
/// with the bytes they retain.</summary>
public sealed record RetentionPath(GcRoot Root, IReadOnlyList<string> Steps, int Objects, long RetainedBytes)
{
    /// <summary>The path written out, root first.</summary>
    public string Text => string.Join(" → ", [Root.Text, .. Steps]);
}

/// <summary>A set of a snapshot's objects: a type's, a comparison's new or dead objects of a type, or a single object.
/// It counts them and what they retain together (each object once however many of the set hold it), lists them, and
/// finds the shortest paths that keep them. Building one, and its paths, takes a while on a large heap: call them off
/// the UI thread.</summary>
public sealed class ObjectSet
{
    /// <summary>How many holders a path shows before it skips to its root: a linked list's would be as long as the list.</summary>
    public const int ShownSteps = 32;

    private readonly int[] members;
    private readonly bool[] outermost;

    /// <summary>Its snapshot's objects, held for as long as the set is, though the snapshot let go of them.</summary>
    private readonly HeapGraph graph;

    internal ObjectSet(MemorySnapshot snapshot, HeapGraph graph, string title, int[] members)
    {
        Snapshot = snapshot;
        Title = title;
        this.graph = graph;
        Array.Sort(members, (a, b) => graph.Retained[b].CompareTo(graph.Retained[a]) is var c and not 0 ? c : a.CompareTo(b));
        this.members = members;
        outermost = Outermost(graph, members);
        int outer = 0;
        for (int i = 0; i < members.Length; i++)
        {
            Bytes += graph.Size[members[i]];
            RetainedBytes += outermost[i] ? graph.Retained[members[i]] : 0;
            outer += outermost[i] ? 1 : 0;
        }
        // Several outermost objects may together keep alive what none does alone.
        if (outer > 1)
        {
            RetainedBytes = graph.RetainedTogether(members);
        }
        Objects = new Listing(graph, members);
    }

    public MemorySnapshot Snapshot { get; }

    /// <summary>What it is called: "New objects of the type Item".</summary>
    public string Title { get; }

    public int Count => members.Length;

    public long Bytes { get; }

    /// <summary>What the set keeps alive together: its objects and every object only they hold, one several of them hold
    /// included.</summary>
    public long RetainedBytes { get; }

    /// <summary>The objects, the most retained first.</summary>
    public IReadOnlyList<HeapObject> Objects { get; }

    /// <summary>The objects the set holds that no other of its objects dominates: only theirs count to what it retains.</summary>
    private static bool[] Outermost(HeapGraph graph, int[] members)
    {
        int[] byEntry = [.. Enumerable.Range(0, members.Length)];
        Array.Sort(byEntry, (a, b) => graph.Enter[members[a]].CompareTo(graph.Enter[members[b]]));
        bool[] outermost = new bool[members.Length];
        int leave = -1;
        foreach (int i in byEntry)
        {
            int member = members[i];
            if (graph.Enter[member] > leave)
            {
                outermost[i] = true;
                leave = graph.Leave[member];
            }
        }
        return outermost;
    }

    /// <summary>The set's shortest paths to roots: each object's shortest path from a root, merged where its holders
    /// are of one type and field, from the set at the top down to the roots.</summary>
    public RetentionNode ShortestPathsToRoots()
    {
        Step top = new(Title, null);
        for (int i = 0; i < members.Length; i++)
        {
            int member = members[i];
            long retained = outermost[i] ? graph.Retained[member] : 0;
            top.Count(retained);
            Step at = top;
            int x = member;
            for (int steps = 0; graph.Parent[x] >= 0; steps++)
            {
                if (steps == ShownSteps)
                {
                    at = at.Child(long.MinValue, "…", null);
                    at.Count(retained);
                    break;
                }
                int holder = graph.Parent[x];
                int offset = graph.ParentOffset[x];
                at = at.Child(((long)graph.Type[holder] << 32) | (uint)graph.HolderKey(holder, offset), string.Empty, null);
                at.Count(retained);
                at.Holds(holder, offset);
                x = holder;
            }
            // Every object on a path has the path's root.
            int root = graph.Root[member];
            at = at.Child(-2L - root, string.Empty, graph.Roots[root]);
            at.Count(retained);
        }
        // The set's own step retains what the set does together; each path what its objects do alone.
        RetentionNode whole = top.Finish(graph);
        return new RetentionNode(whole.Text, null, whole.Objects, RetainedBytes, whole.Children);
    }

    /// <summary>The set's key retention paths: the distinct paths from a root that keep its objects, the most retained
    /// first.</summary>
    public IReadOnlyList<RetentionPath> KeyRetentionPaths()
    {
        // The path ends at what the set's objects are: their type.
        List<string> from = [members.Length == 0 ? Title : graph.Types[graph.Type[members[0]]].Name];
        List<RetentionPath> paths = [];
        foreach (RetentionNode child in ShortestPathsToRoots().Children)
        {
            Walk(child);
        }
        return [.. paths.OrderByDescending(p => p.RetainedBytes).ThenByDescending(p => p.Objects)];

        void Walk(RetentionNode node)
        {
            if (node.Root is { } root)
            {
                List<string> steps = [.. from];
                steps.Reverse();
                paths.Add(new RetentionPath(root, steps, node.Objects, node.RetainedBytes));
                return;
            }
            from.Add(node.Text);
            foreach (RetentionNode child in node.Children)
            {
                Walk(child);
            }
            from.RemoveAt(from.Count - 1);
        }
    }

    /// <summary>A step while paths are merged: what it counts, its holders, and the steps after it by key.</summary>
    private sealed class Step(string text, GcRoot? root)
    {
        private Dictionary<long, Step>? children;
        private int objects;
        private long retained;
        private int holder = -1;
        private int offset;
        private bool many;

        public void Count(long bytes)
        {
            objects++;
            retained += bytes;
        }

        public void Holds(int holder, int offset)
        {
            if (this.holder < 0)
            {
                this.holder = holder;
                this.offset = offset;
            }
            else if (this.holder != holder || this.offset != offset)
            {
                many = true;
            }
        }

        public Step Child(long key, string text, GcRoot? root)
        {
            children ??= [];
            if (!children.TryGetValue(key, out Step? child))
            {
                child = new Step(text, root);
                children[key] = child;
            }
            return child;
        }

        public RetentionNode Finish(HeapGraph graph)
        {
            string shown = root?.Text ?? text;
            if (holder >= 0)
            {
                string type = graph.Types[graph.Type[holder]].Name;
                string field = graph.Holder(holder, offset);
                shown = field.StartsWith("at [", StringComparison.Ordinal)
                    ? many ? $"{type} elements" : $"{type} {field}"
                    : $"{type}.{field}";
            }
            RetentionNode[] next = children is null ? [] : [.. children.Values
                .Select(c => c.Finish(graph))
                .OrderByDescending(c => c.RetainedBytes)
                .ThenByDescending(c => c.Objects)
                .ThenBy(c => c.Text, StringComparer.Ordinal)];
            return new RetentionNode(shown, root, objects, retained, next);
        }
    }

    /// <summary>The set's objects, made as a list asks for them: a million records at once would be the heap again.</summary>
    private sealed class Listing(HeapGraph graph, int[] members) : IReadOnlyList<HeapObject>, IList
    {
        public HeapObject this[int index]
        {
            get
            {
                int at = members[index];
                return new HeapObject(graph.Address[at], graph.Types[graph.Type[at]].Name, graph.Size[at], graph.Retained[at]);
            }
        }

        public int Count => members.Length;

        object? IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        bool IList.IsFixedSize => true;

        bool IList.IsReadOnly => true;

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => this;

        public IEnumerator<HeapObject> GetEnumerator()
        {
            for (int i = 0; i < members.Length; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        int IList.Add(object? value) => throw new NotSupportedException();

        void IList.Clear() => throw new NotSupportedException();

        bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

        int IList.IndexOf(object? value)
        {
            if (value is HeapObject wanted && graph.IndexOf(wanted.Address) is var at and >= 0)
            {
                return Array.IndexOf(members, at);
            }
            return -1;
        }

        void IList.Insert(int index, object? value) => throw new NotSupportedException();

        void IList.Remove(object? value) => throw new NotSupportedException();

        void IList.RemoveAt(int index) => throw new NotSupportedException();

        void ICollection.CopyTo(Array array, int index)
        {
            for (int i = 0; i < members.Length; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }
    }
}
