using System.Diagnostics;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>A child's output and errors heard as the IDE hears every child's (<see cref="ChildProcesses.Hear"/>: a
/// previewer's, the debug adapter's, a test host's, Prettier's runner, a language server's, the project host's) while the
/// child is ended at any moment, its process killed and disposed under the reads, beside busy processes.</summary>
[Collection(Beside.Name)]
public sealed class ChildOutputTests
{
    /// <summary>A child writing to its output and its errors without pause, killed and disposed fifty times at a moment
    /// after its first line (as the IDE ends a previewer or a server), beside two busy processes, has each stream read to
    /// its end every time, every line whole: no read faults as the child goes.</summary>
    [Fact]
    public async Task A_child_killed_and_disposed_as_it_writes_has_both_streams_read_to_their_end_with_no_read_faulted()
    {
        List<Process> busy = Busy();
        try
        {
            for (int round = 1; round <= 50; round++)
            {
                Process child = Chatter();
                long lines = 0;
                string? torn = null;
                void Heard(string line)
                {
                    if (!line.StartsWith("chatter ", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(8), out _))
                    {
                        Interlocked.CompareExchange(ref torn, line, null);
                    }
                    Interlocked.Increment(ref lines);
                }
                Task heard = ChildProcesses.Hear(child, Heard, Heard);
                // Ended a little further into its writing each round.
                long into = round * 97 % 1000 + 1;
                Assert.True(await Waiting.UntilAsync(() => Interlocked.Read(ref lines) >= into, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                    $"Round {round}: the child's lines were not heard");
                ChildProcesses.Kill(child);
                child.Dispose();
                Assert.True(await Waiting.Within(heard, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                    $"Round {round}: the child's streams were not read to their end once it was killed");
                Assert.True(heard.IsCompletedSuccessfully, $"Round {round}: a read of the child's streams ended {heard.Status}: {heard.Exception?.InnerException}");
                Assert.True(torn is null, $"Round {round}: a line was heard torn: '{torn}'");
            }
        }
        finally
        {
            End(busy);
        }
    }

    /// <summary>A child's process disposed while the child writes on leaves its streams heard: what it writes after is still
    /// read, and each stream ends only once the child has gone, so disposing a process never closes a read under it.</summary>
    [Fact]
    public async Task A_childs_process_disposed_as_it_writes_leaves_its_streams_heard_until_the_child_has_gone()
    {
        Process child = Chatter();
        int id = child.Id;
        long output = 0;
        long errors = 0;
        Task heard = ChildProcesses.Hear(child, _ => Interlocked.Increment(ref output), _ => Interlocked.Increment(ref errors));
        try
        {
            Assert.True(await Waiting.UntilAsync(() => Interlocked.Read(ref output) > 0 && Interlocked.Read(ref errors) > 0, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                "The child's lines were not heard");
            child.Dispose();
            long outputThen = Interlocked.Read(ref output);
            long errorsThen = Interlocked.Read(ref errors);
            Assert.True(await Waiting.UntilAsync(() => Interlocked.Read(ref output) > outputThen + 100 && Interlocked.Read(ref errors) > errorsThen + 100,
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                "Disposing the child's process stopped the reading of what it went on writing: a read was closed under it");
            Assert.False(heard.IsCompleted, $"The child's streams ended ({heard.Status}) while it still wrote to them");
        }
        finally
        {
            using Process running = Process.GetProcessById(id);
            ChildProcesses.Kill(running);
        }
        Assert.True(await Waiting.Within(heard, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "The child's streams were not read to their end once it was killed");
        Assert.True(heard.IsCompletedSuccessfully, $"A read of the child's streams ended {heard.Status}: {heard.Exception?.InnerException}");
    }

    /// <summary>A child that writes numbered lines to its output and its errors without pause until it is ended.</summary>
    private static Process Chatter()
    {
        ProcessStartInfo start = ChildProcesses.Command(Debuggees.Apphost("PassengerCases"), ["chatter"]);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return ChildProcesses.Start(start);
    }

    /// <summary>Two processes that spin until they are ended (<see cref="End"/>), for what is tested to share the machine with.</summary>
    internal static List<Process> Busy()
    {
        List<Process> busy = [];
        for (int spinning = 0; spinning < 2; spinning++)
        {
            ProcessStartInfo spin = ChildProcesses.Command(Debuggees.Apphost("LoopingConsoleApp"), []);
            spin.RedirectStandardOutput = true;
            busy.Add(ChildProcesses.Start(spin));
        }
        return busy;
    }

    internal static void End(List<Process> busy)
    {
        foreach (Process spinner in busy)
        {
            ChildProcesses.Kill(spinner);
            spinner.Dispose();
        }
    }
}
