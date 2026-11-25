using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A child ended with everything it started (<see cref="ChildProcesses.Kill"/>) while another thread lets go of
/// its process, as the IDE's reader of a child (the container engine's events) disposes it once the child has ended.</summary>
[Collection(Beside.Name)]
public sealed class ChildKillTests
{
    /// <summary>The child's process is disposed the moment the child has ended, while what it started is still being
    /// ended; twenty rounds, so a kill that read the process as it went would have been cut short in one of them. What
    /// the test ends itself it ends through the processes it holds, never by an id another process may have been given.</summary>
    internal async Task A_child_disposed_on_another_thread_as_it_is_killed_has_what_it_started_ended_too()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string waiter = OperatingSystem.IsWindows() ? "PING.EXE" : "sleep";
        for (int round = 1; round <= 20; round++)
        {
            Process child = ChildProcesses.Start(OperatingSystem.IsWindows()
                ? ChildProcesses.Command("cmd.exe", ["/c", "ping -n 120 127.0.0.1 >nul"])
                : ChildProcesses.Command("/bin/sh", ["-c", "sleep 120; true"]));
            List<Process> held = [];
            try
            {
                DateTime began = child.StartTime;
                Assert.True(await Waiting.UntilAsync(() => Hold(child.Id, began, held).Any(p => DotnetProcesses.Describe(p.Id)?.Name == waiter),
                    TimeSpan.FromSeconds(30), cancel), $"Round {round}: the child's {waiter} never started");
                TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task letGo = Waiting.Apart(() =>
                {
                    waiting.SetResult();
                    child.WaitForExit();
                    child.Dispose();
                }, cancel);
                await waiting.Task;
                ChildProcesses.Kill(child);
                Assert.True(await Waiting.Within(letGo, TimeSpan.FromSeconds(30), cancel), $"Round {round}: the killed child never ended");
                foreach (Process process in held)
                {
                    Assert.True(await Waiting.UntilAsync(() => !DotnetProcesses.Runs(process), TimeSpan.FromSeconds(15), cancel),
                        $"Round {round}: process {process.Id}, which the killed child started, still ran: what a child the IDE ends started "
                        + "outlives it whenever its process is let go as it is killed");
                }
            }
            finally
            {
                // Once the child's process is let go, this finds nothing to end.
                ChildProcesses.Kill(child);
                foreach (Process process in held)
                {
                    ChildProcesses.Kill(process);
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>Node.js keeps what it starts in a job that ends it as Node.js ends, so the cmd.exe it starts here is gone by
    /// the time a walk that ends a level before listing the next would reach it, and the PING cmd.exe started, which
    /// left that job, would run on with the port or files it holds (an npm script's ng serve, #2201).</summary>
    internal async Task A_child_whose_end_ends_what_it_started_first_has_their_children_ended_too_on_Windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Elsewhere a child's process group holds all it started, whatever ends first");
        string? node = Programs.Node();
        Assert.SkipWhen(node is null, "No Node.js, whose job ends what it started with it, is on the PATH");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Process child = ChildProcesses.Start(ChildProcesses.Command(node,
            ["-e", "require('child_process').spawn('cmd.exe', ['/d', '/s', '/c', 'ping -n 120 127.0.0.1 >nul'], { stdio: 'ignore' }); setInterval(() => {}, 1000);"]));
        List<Process> shells = [];
        List<Process> pings = [];
        try
        {
            DateTime began = child.StartTime;
            Assert.True(await Waiting.UntilAsync(() => Hold(child.Id, began, shells).Any(shell => Hold(shell.Id, Began(shell), pings).Any(p => DotnetProcesses.Describe(p.Id)?.Name == "PING.EXE")),
                TimeSpan.FromSeconds(30), cancel), "Node.js's cmd.exe never started its PING");
            ChildProcesses.Kill(child);
            foreach (Process ping in pings)
            {
                Assert.True(await Waiting.UntilAsync(() => !DotnetProcesses.Runs(ping), TimeSpan.FromSeconds(15), cancel),
                    $"PING {ping.Id}, which the cmd.exe Node.js started started, still ran once Node.js was ended: what an npm script runs outlives its Stop and Rerun on Windows, holding its port");
            }
        }
        finally
        {
            ChildProcesses.Kill(child);
            foreach (Process process in shells.Concat(pings))
            {
                ChildProcesses.Kill(process);
                process.Dispose();
            }
            child.Dispose();
        }
    }

    /// <summary>The processes the child started, each held as it is found (by its handle on Windows, so its id stays its
    /// own) and kept only where, so held, it still names the child as its parent and began no earlier than the child.</summary>
    private static List<Process> Hold(int child, DateTime began, List<Process> held)
    {
        List<Process> found = [];
        foreach (int id in DotnetProcesses.StartedBy(child).Where(id => held.TrueForAll(p => p.Id != id)))
        {
            try
            {
                Process process = Process.GetProcessById(id);
                found.Add(process);
                _ = process.SafeHandle;
            }
            catch (Exception gone) when (gone is ArgumentException or InvalidOperationException)
            {
                // One that ended by itself has nothing left to end.
            }
        }
        IReadOnlyList<int> startedNow = DotnetProcesses.StartedBy(child);
        foreach (Process process in found)
        {
            if (startedNow.Contains(process.Id) && Began(process) >= began)
            {
                held.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }
        return held;
    }

    private static DateTime Began(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception gone) when (gone is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTime.MinValue;
        }
    }
}
