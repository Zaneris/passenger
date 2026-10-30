using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A type's retained bytes are what would be freed with all its objects: their own retained bytes, and those of
/// each object several of them keep alive together and nothing else reaches, however deep under them, whichever roots
/// hold them, and when what they keep refers back to itself.</summary>
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

    /// <summary>Each type's retained bytes in a graph of these edges, root objects, types and sizes.</summary>
    private static long[] TypeRetained(int count, (int From, int To)[] edges, int[] roots, int[] types, long[] sizes)
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
        return HeapGraph.Dominators.Of(count, edgeStart, edgeTarget, roots, sizes, types, 4, CancellationToken.None).TypeRetained;
    }
}
