using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>On Windows, the processes the host starts during a build session, MSBuild's nodes among them: the session's end
/// lets its nodes go, but each goes on working in the folder of the project it built last until its process is gone, and
/// Windows moves no folder a process works in. A round of design-time builds is done once they are gone, so a folder going,
/// which waits for the round (FolderBuilds), finds none of them working in it. None off Windows.</summary>
internal static class SessionProcesses
{
    /// <summary>The longest a session's end waits, all told, for what it started to be gone.</summary>
    private static readonly TimeSpan Going = TimeSpan.FromSeconds(5);

    /// <summary>The processes the host started that run now, by id and when each began, read as a session begins.</summary>
    public static HashSet<(int Id, long Began)> Running()
    {
        HashSet<(int Id, long Began)> running = [];
        if (OperatingSystem.IsWindows())
        {
            foreach ((SafeProcessHandle process, int id, long began) in Started())
            {
                process.Dispose();
                running.Add((id, began));
            }
        }
        return running;
    }

    /// <summary>Waits for each process the host started since <paramref name="before"/> was read to be gone, for
    /// <see cref="Going"/> at most all told.</summary>
    public static void WaitOut(HashSet<(int Id, long Began)> before)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        Deadline until = Deadline.In(Going);
        foreach ((SafeProcessHandle process, int id, long began) in Started())
        {
            using (process)
            {
                if (!before.Contains((id, began)))
                {
                    _ = Kernel32.WaitForSingleObject(process, (uint)Math.Ceiling(until.Left.TotalMilliseconds));
                }
            }
        }
    }

    /// <summary>The processes running that the host started, each held by a handle of the caller's to wait for, with its id
    /// and when it began: a process naming the host as its parent that began no earlier than the host.</summary>
    [SupportedOSPlatform("windows")]
    private static List<(SafeProcessHandle Process, int Id, long Began)> Started()
    {
        int host = Environment.ProcessId;
        List<(SafeProcessHandle Process, int Id, long Began)> started = [];
        using SafeProcessHandle own = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, (uint)host);
        if (Kernel32.BeganAt(own) is not { } hostBegan)
        {
            return started;
        }
        foreach ((int child, int parent) in Kernel32.Processes())
        {
            if (parent != host)
            {
                continue;
            }
            SafeProcessHandle process = Kernel32.OpenProcess(Kernel32.Synchronize | Kernel32.ProcessQueryLimitedInformation, false, (uint)child);
            if (!process.IsInvalid && Kernel32.BeganUnder(process, host, hostBegan) is { } began)
            {
                started.Add((process, child, began));
            }
            else
            {
                process.Dispose();
            }
        }
        return started;
    }
}
