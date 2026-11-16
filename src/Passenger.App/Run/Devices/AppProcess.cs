using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Passenger.Core;

namespace Passenger.App.Run.Devices;

/// <summary>The process one start of an app opened, where the system starts the app apart from Passenger (a Mac Catalyst
/// app's opener, simctl's launch on a simulator): told from every other process of its program, another start's or the
/// person's own, by the start's <see cref="Token"/>, which the start gives the app in its environment
/// (<see cref="Variable"/>) and nothing else does; once found, kept, so the start's end ends that process alone.</summary>
public sealed partial class AppProcess
{
    /// <summary>The variable a start gives the app it opens, holding the start's <see cref="Token"/>.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_APP_START";

    /// <summary>This start's own, which no other start gives an app.</summary>
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The process found, with its start as the system keeps it, by which a later process given its id is told
    /// apart; null until found.</summary>
    private (int Id, DateTime Began)? found;

    private readonly Lock gate = new();

    /// <summary>How often a start looks for its app among the processes.</summary>
    private static readonly TimeSpan Looking = TimeSpan.FromMilliseconds(100);

    /// <summary>The process found by now; null until it is.</summary>
    public int? Id
    {
        get
        {
            lock (gate)
            {
                return found?.Id;
            }
        }
    }

    /// <summary>The start's process, among those whose program <paramref name="runs"/> takes for the app's, looked for until
    /// it is found or what started it has <paramref name="ended"/>, then once more, and kept; its id, or null where none is.</summary>
    public async Task<int?> FindAsync(Func<string, bool> runs, Task ended, CancellationToken cancel = default)
    {
        while (true)
        {
            bool last = ended.IsCompleted;
            if (Find(runs) is var (id, began))
            {
                lock (gate)
                {
                    found = (id, began);
                }
                return id;
            }
            if (last)
            {
                return null;
            }
            await Task.WhenAny(ended, Task.Delay(Looking, cancel));
            cancel.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The process running a program <paramref name="runs"/> takes whose environment holds this start's token.</summary>
    private (int Id, DateTime Began)? Find(Func<string, bool> runs)
    {
        int[] ids = new int[4096];
        int count = OperatingSystem.IsMacOS() ? ListAllProcesses(ids, ids.Length * sizeof(int)) : 0;
        for (int i = 0; i < Math.Min(count, ids.Length); i++)
        {
            if (ids[i] > 0 && Libc.ExecutableOf(ids[i]) is { } program && runs(program)
                && DotnetProcesses.VariableOf(ids[i], Variable) == Token && DotnetProcesses.StartedAt(ids[i]) is { } began)
            {
                return (ids[i], began);
            }
        }
        return null;
    }

    /// <summary>The process found taken on, so it ends with Passenger as its children do, <paramref name="program"/> naming
    /// it in the log; nothing where none was found.</summary>
    public void Adopt(string program)
    {
        if (Id is int id)
        {
            ChildProcesses.Adopt(id, new ProcessStartInfo(program));
        }
    }

    /// <summary>What is left of the process found ended, where it still runs as found: its id is another's once it has gone.
    /// Nothing where none was found. Never throws.</summary>
    public void End()
    {
        (int Id, DateTime Began)? kept;
        lock (gate)
        {
            kept = found;
        }
        if (kept is var (id, began) && DotnetProcesses.StartedAt(id) == began)
        {
            ChildProcesses.KillLeft(id);
        }
    }

    /// <summary>proc_listallpids: the ids of every process, as many as fit; their count.</summary>
    [LibraryImport("libc", EntryPoint = "proc_listallpids")]
    private static partial int ListAllProcesses(int[] ids, int size);
}
