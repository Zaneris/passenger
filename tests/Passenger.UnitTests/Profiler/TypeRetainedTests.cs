using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A type's retained bytes are what would be freed with all its objects: their own retained bytes, and those of
/// each object several of them keep alive together and nothing else reaches, however deep under them, whichever roots
/// hold them, and when what they keep refers back to itself; found in steps that grow with the heap, whatever its shape,
/// and stopped at once by Stop.</summary>
public sealed class TypeRetainedTests
{
    private const int Holder = 0;
    private const int Entry = 1;
    private const int Payload = 2;
    private const int Wrapper = 3;

    [Fact]
    public void Two_entries_sharing_a_payload_retain_it_together()
    {
        // A list (0) of two entries (1, 2), both holding one payload (3).
        long[] retained = TypeRetained(4, [(0, 1), (0, 2), (1, 3), (2, 3)], [0], [Holder, Entry, Entry, Payload], [32, 24, 24, 10_000_000]);

        Assert.Equal(24 + 24 + 10_000_000, retained[Entry]);
        Assert.Equal(32 + 24 + 24 + 10_000_000, retained[Holder]);
        Assert.Equal(10_000_000, retained[Payload]);
    }

    [Fact]
    public void A_payload_something_else_holds_too_is_retained_by_no_entry()
    {
        long[] retained = TypeRetained(4, [(0, 1), (0, 2), (1, 3), (2, 3), (0, 3)], [0], [Holder, Entry, Entry, Payload], [32, 24, 24, 10_000_000]);

        Assert.Equal(48, retained[Entry]);
    }

    [Fact]
    public void A_payload_shared_under_wrappers_is_retained_by_the_entries_and_by_the_wrappers()
    {
        // Each entry (1, 2) holds a wrapper (3, 4) of its own, and both wrappers the payload (5).
        long[] retained = TypeRetained(6, [(0, 1), (0, 2), (1, 3), (2, 4), (3, 5), (4, 5)], [0], [Holder, Entry, Entry, Wrapper, Wrapper, Payload],
            [32, 24, 24, 16, 16, 1000]);

        Assert.Equal(24 + 16 + 24 + 16 + 1000, retained[Entry]);
        Assert.Equal(16 + 16 + 1000, retained[Wrapper]);
    }

    [Fact]
    public void A_payload_two_roots_entries_share_is_retained_by_the_entries()
    {
        // Two root objects (0, 1), each an entry, both holding the payload (2).
        long[] retained = TypeRetained(3, [(0, 2), (1, 2)], [0, 1], [Entry, Entry, Payload], [24, 24, 1000]);

        Assert.Equal(24 + 24 + 1000, retained[Entry]);
    }

    [Fact]
    public void Payloads_that_refer_to_each_other_are_retained_by_the_entries_holding_them()
    {
        // Two entries (1, 2) each holding a payload (3, 4); the payloads refer to each other.
        long[] retained = TypeRetained(5, [(0, 1), (0, 2), (1, 3), (2, 4), (3, 4), (4, 3)], [0], [Holder, Entry, Entry, Payload, Payload],
            [32, 24, 24, 500, 700]);

        Assert.Equal(24 + 24 + 500 + 700, retained[Entry]);
        Assert.Equal(500 + 700, retained[Payload]);
    }

    [Fact]
    public void A_long_chain_sharing_an_object_another_root_holds_is_walked_in_steps_that_grow_with_its_length()
    {
        (long half, long[] _) = Chain(50_000, new HeapGraph.Steps(TestContext.Current.CancellationToken));
        (long walked, long[] retained) = Chain(100_000, new HeapGraph.Steps(TestContext.Current.CancellationToken));

        // The entries retain themselves alone: the payload they all refer to is held by the second root too.
        Assert.Equal(100_000 * 24, retained[Entry]);
        Assert.True(walked <= half * 21 / 10 && walked < 20L * 100_000,
            $"A 100,000-node chain took {walked:N0} steps to a 50,000-node one's {half:N0}: a snapshot of a long list held from outside "
            + "too takes minutes to find its types' retained sizes");
    }

    [Fact]
    public void Stop_heard_while_a_pass_walks_ends_it_at_that_check()
    {
        (long whole, long[] _) = Chain(100_000, new HeapGraph.Steps(TestContext.Current.CancellationToken));
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        long heardAt = 0;
        HeapGraph.Steps steps = new(stop.Token)
        {
            Hearing = walked =>
            {
                if (walked > whole / 4 && heardAt == 0)
                {
                    heardAt = walked;
                    stop.Cancel();
                }
            },
        };

        Assert.Throws<OperationCanceledException>(() => Chain(100_000, steps));
        Assert.True(heardAt > 0 && steps.Count == heardAt,
            $"Stop asked {heardAt:N0} steps into a walk of {whole:N0} ended it at {steps.Count:N0}: Stop leaves a snapshot's analysis running on");
    }

    /// <summary>A chain of entries under a holder, each referring to one payload that a second root holds too: the steps
    /// its types' retained bytes took, and those bytes.</summary>
    private static (long Steps, long[] Retained) Chain(int length, HeapGraph.Steps steps)
    {
        // The holder (0), the entries (1 to length), the payload and the second root.
        int payload = length + 1;
        int count = length + 3;
        List<(int From, int To)> edges = [(0, 1)];
        for (int i = 1; i <= length; i++)
        {
            if (i < length)
            {
                edges.Add((i, i + 1));
            }
            edges.Add((i, payload));
        }
        edges.Add((payload + 1, payload));
        int[] types = new int[count];
        Array.Fill(types, Entry, 1, length);
        types[payload] = Payload;
        types[payload + 1] = Wrapper;
        long[] retained = TypeRetained(count, [.. edges], [0, payload + 1], types, [.. Enumerable.Repeat(24L, count)], steps);
        return (steps.Count, retained);
    }

    /// <summary>Each type's retained bytes in a graph of these edges, root objects, types and sizes.</summary>
    private static long[] TypeRetained(int count, (int From, int To)[] edges, int[] roots, int[] types, long[] sizes, HeapGraph.Steps? steps = null)
    {
        int[] edgeStart = new int[count + 1];
        foreach ((int from, int _) in edges)
        {
            edgeStart[from + 1]++;
        }
        for (int i = 0; i < count; i++)
        {
            edgeStart[i + 1] += edgeStart[i];
        }
        int[] edgeTarget = new int[edges.Length];
        int[] filled = new int[count];
        foreach ((int from, int to) in edges)
        {
            edgeTarget[edgeStart[from] + filled[from]++] = to;
        }
        return HeapGraph.Dominators.Of(count, edgeStart, edgeTarget, roots, sizes, types, 4, CancellationToken.None, steps).TypeRetained;
    }
}
