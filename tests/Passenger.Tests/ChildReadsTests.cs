using System.Diagnostics;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>A child run to its end with its output read as bytes as it comes (<see cref="ChildProcesses.RunAsync{T}"/>, as
/// every git call is): a read that fails part of the way ends the child and fails the run with what the read threw, which
/// git's runner takes as a failure.</summary>
[Collection(Beside.Name)]
public sealed class ChildReadsTests
{
    /// <summary>A child writing without end, whose output's read fails after its first 64 KB, is ended, and the run fails
    /// with the read's IOException: neither the child, writing to a pipe nobody reads, nor the run waits for good.</summary>
    [Fact]
    public async Task A_read_of_a_childs_output_that_fails_part_of_the_way_ends_the_child_and_fails_the_run_with_what_it_threw()
    {
        ProcessStartInfo start = ChildProcesses.Command(Debuggees.Apphost("PassengerCases"), ["chatter"]);
        int child = 0;
        void Started(object? sender, ChildStart started)
        {
            if (ReferenceEquals(started.Start, start))
            {
                child = started.ProcessId;
            }
        }
        ChildProcesses.Started += Started;
        try
        {
            Task<ChildRead<int>> run = ChildProcesses.RunAsync(start, FailingAsync, cancel: TestContext.Current.CancellationToken);
            Assert.True(await Waiting.Within(run, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                "The run whose read failed never ended: its child, no longer read, was left writing");
            IOException failed = await Assert.ThrowsAsync<IOException>(() => run);
            Assert.Equal("The output broke off", failed.Message);
            Assert.True(child > 0, "The child was never started");
            Assert.True(Processes.Gone(child), $"The child whose output's read failed ({Processes.Describe(child)}) was left running");
        }
        finally
        {
            ChildProcesses.Started -= Started;
        }

        static async Task<int> FailingAsync(Stream output)
        {
            byte[] taken = new byte[64 * 1024];
            int read = 0;
            int now;
            while (read < taken.Length && (now = await output.ReadAsync(taken.AsMemory(read))) > 0)
            {
                read += now;
            }
            throw new IOException("The output broke off");
        }
    }
}
