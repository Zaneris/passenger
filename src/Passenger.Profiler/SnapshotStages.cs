namespace Passenger.Profiler;

/// <summary>The stages of a memory snapshot (<see cref="HeapHistory"/>), each told as it begins, in words that follow
/// "while" in a sentence for the person and for an agent. Each request a stage makes of the program's runtime has a limit
/// of its own, and one past it fails the snapshot naming its stage.</summary>
public static class SnapshotStages
{
    /// <summary>The session that hears every collection from the run's first snapshot on, begun once.</summary>
    public const string Following = "starting to follow the program's collections";

    /// <summary>The runtime's sampler, which tells when the dump held the program.</summary>
    public const string Sampling = "starting to sample the program as its heap is dumped";

    /// <summary>The dump written, by Passenger or by the program's runtime.</summary>
    public const string Dumping = "dumping the program's heap";

    /// <summary>The full collection induced after the dump, and its end heard.</summary>
    public const string Collecting = "having the program's runtime collect in full";

    /// <summary>The dump read with the program's DAC.</summary>
    public const string Reading = "reading the dump of the program's heap";

    /// <summary>The runtime's own GC heap dump, for a program no other may read.</summary>
    public const string Walking = "having the program's runtime walk its heap";

    /// <summary>How long a heap's dump may take to write: generous for a heap of millions of objects, which take a minute or
    /// two, as much longer as <see cref="Core.Waits"/> says.</summary>
    public static TimeSpan DumpLimit => Core.Waits.Of(TimeSpan.FromMinutes(5));
}
