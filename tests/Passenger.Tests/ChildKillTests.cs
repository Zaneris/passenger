using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A child ended with everything it started (<see cref="ChildProcesses.Kill"/>) while another thread lets go of
/// its process, as the IDE's reader of a child (the container engine's events) disposes it once the child has ended.</summary>
[Collection(Beside.Name)]
public sealed class ChildKillTests
{
    /// <summary>The child's process is disposed the moment the child has ended, while what it started is still being
    /// ended; twenty rounds, so a kill that read the process as it went would have been cut short in one of them.</summary>
    [Fact]
    public async Task A_child_disposed_on_another_thread_as_it_is_killed_has_what_it_started_ended_too()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string waiter = OperatingSystem.IsWindows() ? "PING.EXE" : "sleep";
        for (int round = 1; round <= 20; round++)
        {
            Process child = ChildProcesses.Start(OperatingSystem.IsWindows()
                ? ChildProcesses.Command("cmd.exe", ["/c", "ping -n 120 127.0.0.1 >nul"])
                : ChildProcesses.Command("/bin/sh", ["-c", "sleep 120; true"]));
            int id = child.Id;
            int[] started = [];
            List<Process> held = [];
            try
            {
                Assert.True(await Waiting.UntilAsync(() => (started = [.. DotnetProcesses.StartedBy(id)]).Any(s => DotnetProcesses.Describe(s)?.Name == waiter),
                    TimeSpan.FromSeconds(30), cancel), $"Round {round}: the child's {waiter} never started");
                foreach (int startedId in started)
                {
                    try
                    {
                        // Each held by its handle, so another process given its id later is never taken for it.
                        Process process = Process.GetProcessById(startedId);
                        held.Add(process);
                        _ = process.SafeHandle;
                    }
                    catch (Exception gone) when (gone is ArgumentException or InvalidOperationException)
                    {
                        // One that ended by itself has nothing left to end.
                    }
                }
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
                foreach (Process process in held)
                {
                    process.Dispose();
                }
                foreach (int left in started.Append(id))
                {
                    Processes.Kill(left);
                }
            }
        }
    }
}
