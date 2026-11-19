namespace Passenger.UnitTests;

/// <summary>What runs after every other class, one class at a time with nothing beside it: a class that unloads a load
/// context. Once collected, each unloaded context's loader allocator empties the runtime's cast cache, and the next thread
/// whose cast misses it builds it again at the size it had, 98 KB at its largest, as that thread's own allocation, which
/// beside the others could be a test's holding its code to what it allocates.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Alone
{
    public const string Name = "Alone";
}
