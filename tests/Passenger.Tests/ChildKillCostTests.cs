using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What a kill of a child costs the thread that kills it (the window's, at a Stop): on macOS and Linux it finds
/// its group's processes among the machine's, so what it allocates is held to the same with hundreds of other processes
/// running as with the machine as it is.</summary>
[Collection(Beside.Name)]
public sealed class ChildKillCostTests(ITestOutputHelper output)
{
    /// <summary>The processes the large case adds, idle.</summary>
    private const int Others = 300;

    /// <summary>The kills each case takes the least of, as a collection on the thread may count a few kilobytes it never used.</summary>
    private const int Rounds = 5;

    [Fact]
    public async Task A_kill_allocates_no_more_with_three_hundred_other_processes_running()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows ends a child with its tree and held family, not a process group");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        // What only a first kill pays is set aside.
        await KilledAsync(cancel);
        (long few, TimeSpan fewTook) = await LeastAsync(cancel);
        Process others = Started(
            ChildProcesses.Command("/bin/sh", ["-c", $"i=0; while [ $i -lt {Others} ]; do sleep 600 & i=$((i + 1)); done; echo started; wait"]));
        try
        {
            await SaidAsync(others, $"The {Others} other processes never all started", cancel);
            (long many, TimeSpan manyTook) = await LeastAsync(cancel);
            output.WriteLine($"A kill took {fewTook.TotalMilliseconds:F2} ms and allocated {few:N0} bytes; with {Others} other processes "
                + $"running, {manyTook.TotalMilliseconds:F2} ms and {many:N0} bytes");
            Assert.True(many - few < 32 * 1024,
                $"A kill allocated {few:N0} bytes, and {many:N0} with {Others} other processes running: each Stop, test host's or "
                + "build's end reads every process the machine runs");
        }
        finally
        {
            ChildProcesses.Kill(others);
            others.Dispose();
        }
    }

    /// <summary>The least a kill allocated and took over <see cref="Rounds"/>.</summary>
    private static async Task<(long Allocated, TimeSpan Took)> LeastAsync(CancellationToken cancel)
    {
        long allocated = long.MaxValue;
        TimeSpan took = TimeSpan.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            (long kill, TimeSpan time) = await KilledAsync(cancel);
            allocated = Math.Min(allocated, kill);
            took = time < took ? time : took;
        }
        return (allocated, took);
    }

    /// <summary>A child of one process killed once it has said it runs, so the kill reads all a running program gives (its
    /// environment, which reads empty while the system still sets the program up): what that allocated on this thread and
    /// how long it took.</summary>
    private static async Task<(long Allocated, TimeSpan Took)> KilledAsync(CancellationToken cancel)
    {
        using Process child = Started(ChildProcesses.Command("/bin/sh", ["-c", "echo started; read line"]));
        await SaidAsync(child, "The child never started", cancel);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long began = Stopwatch.GetTimestamp();
        ChildProcesses.Kill(child);
        TimeSpan took = Stopwatch.GetElapsedTime(began);
        return (GC.GetAllocatedBytesForCurrentThread() - before, took);
    }

    /// <summary>A child started with its input held open and its output read.</summary>
    private static Process Started(ProcessStartInfo start)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        return ChildProcesses.Start(start);
    }

    private static async Task SaidAsync(Process child, string never, CancellationToken cancel)
    {
        Task<string?> said = ChildProcesses.Output(child).ReadLineAsync(cancel).AsTask();
        Assert.True(await Waiting.Within(said, TimeSpan.FromSeconds(60), cancel), never);
        Assert.Equal("started", await said);
    }
}
