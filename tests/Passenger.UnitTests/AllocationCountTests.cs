using System.Runtime;

namespace Passenger.UnitTests;

/// <summary>What the tests holding code to what it allocates read: a thread's own count of the bytes it allocated, which a
/// collection must leave as it was for a thread that allocated nothing meanwhile.</summary>
public class AllocationCountTests
{
    private static byte[]? taken;

    /// <summary>A background collection's mark would end while this thread holds the fresh allocation context it took as the
    /// collection began, and count the context's unused rest against it.</summary>
    [Fact]
    public void A_collection_counts_nothing_against_a_thread_that_allocated_nothing()
    {
        Assert.False(GCSettings.LatencyMode == GCLatencyMode.Interactive, "Collections here run in the background: a test's count of what it allocated may take in up to 8 KB it never did");
        GC.Collect(2, GCCollectionMode.Forced, blocking: false);
        taken = new byte[64];
        long before = GC.GetAllocatedBytesForCurrentThread();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long counted = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(counted == 0, $"A collection counted {counted:N0} bytes against a thread that allocated nothing: a test holding code to allocating nothing fails on bytes it never made");
        Assert.NotNull(taken);
    }
}
