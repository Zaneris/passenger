using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Whether a process runs, as the sweep of heap dumps asks it of each dump's writer: one this user may not open
/// (another user's, an elevated one) runs, so the dump an elevated Passenger has under way is never swept from under it,
/// and an id no process has does not.</summary>
[Collection(Beside.Name)]
public sealed class ProcessRunsTests
{
    [Fact]
    public void A_process_this_user_may_not_open_runs_and_an_id_no_process_has_does_not()
    {
        // The system's own, run as another user: Windows' System process, macOS's launchd and Linux's init.
        int system = OperatingSystem.IsWindows() ? 4 : 1;
        Assert.True(DotnetProcesses.Runs(system),
            $"Process {system}, the system's own, which this user may not open, was taken for ended: an elevated Passenger's heap dump would be swept as it is written");
        Assert.True(DotnetProcesses.Runs(Environment.ProcessId), "This test's own process was taken for ended");
        // Above every system's largest id, so no process has it.
        Assert.False(DotnetProcesses.Runs(int.MaxValue), "An id no process has was taken for a running process: its writer's heap dump would never be swept");
    }
}
