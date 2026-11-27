namespace Passenger.Core;

/// <summary>Which of the processes the system lists have their runtime's files (<see cref="RuntimeFiles"/>), looked at again
/// and again (each second, by <see cref="Lifeline"/>): a process is asked its start and its files at the first look that
/// lists it and the next few, while its runtime, starting, may not have made them yet, then no more until it goes, so a
/// look costs the listing alone, however many processes the machine runs. Not thread-safe: its caller holds a lock.</summary>
internal sealed class RuntimeFileLooks(Func<int, long?> startOf, Func<int, long, bool> holdsFiles)
{
    /// <summary>How many looks a process is asked for its files at, from the first that lists it, until it has them.</summary>
    internal const int Asked = 10;

    private readonly Dictionary<int, Known> known = [];
    private long look;

    /// <summary>A process as last looked at: its start, how many looks asked for its files, whether it has them, and the
    /// last look that listed it.</summary>
    private readonly record struct Known(long Started, int Asked, bool Files, long Listed);

    /// <summary>Looks at the processes <paramref name="listed"/> (as the system lists them now; any id 0 or below passed
    /// over), adding each with its runtime's files, by its id and start, to <paramref name="found"/>; those no longer listed
    /// are forgotten.</summary>
    public void Look(ReadOnlySpan<int> listed, List<(int Id, long Started)> found)
    {
        look++;
        foreach (int id in listed)
        {
            if (id <= 0)
            {
                continue;
            }
            if (!known.TryGetValue(id, out Known process))
            {
                if (startOf(id) is not long started)
                {
                    continue;
                }
                process = new Known(started, 0, false, look);
            }
            if (!process.Files && process.Asked < Asked)
            {
                process = process with { Asked = process.Asked + 1, Files = holdsFiles(id, process.Started) };
            }
            known[id] = process with { Listed = look };
            if (process.Files)
            {
                found.Add((id, process.Started));
            }
        }
        foreach ((int id, Known process) in known)
        {
            if (process.Listed != look)
            {
                // A dictionary's enumeration survives a removal (since .NET Core 3.0).
                known.Remove(id);
            }
        }
    }
}
