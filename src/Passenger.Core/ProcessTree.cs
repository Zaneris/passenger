using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>On Windows, a process and everything it started, and what those started in turn, ended by a walk that reads
/// nothing of the caller's <see cref="System.Diagnostics.Process"/>: each process is held by a handle of the walk's own
/// from when it is found until the walk is done, so no id the walk follows is given to another process meanwhile. The
/// whole tree as it stands is found and held before any of it is ended, then what its processes started meanwhile, a
/// level ended and waited out before the next is listed; a child is a process naming its parent's id, as it says itself
/// once opened, that began no earlier than its parent. One Windows lets Passenger read but not end (a higher integrity's)
/// is walked through, what it started ended; one it will not open at all (ended, or another user's) is passed over, and
/// the walk never ends Passenger itself.</summary>
[SupportedOSPlatform("windows")]
internal static partial class ProcessTree
{
    /// <summary>The exit code a process ended here is given, as .NET's own kill gives it.</summary>
    private const int Killed = -1;

    /// <summary>The longest a walk waits, all told, for the processes it ended to be gone before it lists what they
    /// started: Windows ends a process after the call that ends it returns, and one part-way through starting another
    /// may still start it.</summary>
    private static readonly TimeSpan EndingWait = TimeSpan.FromSeconds(2);

    /// <summary>Ends the process <paramref name="root"/> holds, whose id is <paramref name="id"/>, then everything it
    /// started, whether or not it had ended itself. The handle stays the caller's.</summary>
    public static void End(int id, SafeProcessHandle root)
    {
        Deadline waited = Deadline.In(Waits.Of(EndingWait));
        if (Kernel32.BeganAt(root) is not { } began)
        {
            _ = TerminateProcess(root, Killed);
            return;
        }
        List<SafeProcessHandle> held = [];
        try
        {
            // Found whole first: an end can end what it started before the walk gets there (Node.js's job), orphaning theirs.
            HashSet<int> found = [id, Environment.ProcessId];
            Dictionary<int, long> known = new() { [id] = began };
            List<(SafeProcessHandle Process, bool Endable)> tree = [(root, true)];
            for (Dictionary<int, long> level = known; level.Count > 0;)
            {
                level = Below(level, found, held, tree);
                foreach ((int child, long childBegan) in level)
                {
                    known[child] = childBegan;
                }
            }
            List<SafeProcessHandle> ending = Ended(tree);
            for (Dictionary<int, long> parents = known; parents.Count > 0;)
            {
                WaitOut(ending, waited);
                List<(SafeProcessHandle Process, bool Endable)> started = [];
                parents = Below(parents, found, held, started);
                ending = Ended(started);
            }
        }
        finally
        {
            foreach (SafeProcessHandle process in held)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>The processes started by those of <paramref name="level"/>, each held as it is found and added to
    /// <paramref name="into"/>, with when each began; none already found.</summary>
    private static Dictionary<int, long> Below(Dictionary<int, long> level, HashSet<int> found, List<SafeProcessHandle> held,
        List<(SafeProcessHandle Process, bool Endable)> into)
    {
        Dictionary<int, long> next = [];
        foreach ((int child, int parent) in Kernel32.Processes())
        {
            if (found.Contains(child) || !level.TryGetValue(parent, out long parentBegan) || Opened(child, out bool endable) is not { } opened)
            {
                continue;
            }
            if (Kernel32.BeganUnder(opened, parent, parentBegan) is not { } childBegan)
            {
                // The id is now another process's, or the parent's id named an earlier process.
                opened.Dispose();
                continue;
            }
            held.Add(opened);
            found.Add(child);
            into.Add((opened, endable));
            next[child] = childBegan;
        }
        return next;
    }

    /// <summary>Ends each process that Windows lets Passenger end, and gives those to wait out.</summary>
    private static List<SafeProcessHandle> Ended(List<(SafeProcessHandle Process, bool Endable)> processes)
    {
        List<SafeProcessHandle> ending = [];
        foreach ((SafeProcessHandle process, bool endable) in processes)
        {
            if (endable)
            {
                _ = TerminateProcess(process, Killed);
                ending.Add(process);
            }
        }
        return ending;
    }

    /// <summary>A process opened to be ended and waited for, else only to be read (one of a higher integrity); null when
    /// Windows will not open it (ended, or another user's).</summary>
    private static SafeProcessHandle? Opened(int id, out bool endable)
    {
        endable = true;
        SafeProcessHandle opened = Kernel32.OpenProcess(Kernel32.ProcessTerminate | Kernel32.ProcessQueryLimitedInformation | Kernel32.Synchronize, false, (uint)id);
        if (!opened.IsInvalid)
        {
            return opened;
        }
        opened.Dispose();
        endable = false;
        opened = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, (uint)id);
        if (!opened.IsInvalid)
        {
            return opened;
        }
        opened.Dispose();
        return null;
    }

    /// <summary>Waits until each process ended is gone, or the walk's wait has run out.</summary>
    private static void WaitOut(List<SafeProcessHandle> ending, Deadline waited)
    {
        foreach (SafeProcessHandle process in ending)
        {
            _ = Kernel32.WaitForSingleObject(process, (uint)Math.Ceiling(waited.Left.TotalMilliseconds));
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, int exitCode);
}
