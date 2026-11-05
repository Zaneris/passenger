using System.Diagnostics;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Whether a process runs, as the sweep of heap dumps asks it of each dump's writer: one this user may not open
/// (another user's, an elevated one) runs, so the dump an elevated Passenger has under way is never swept from under it,
/// and an id no process has does not; and one that has ended and waits for its parent to take its exit, as a debugged
/// Godot game waits for its adapter, does not, though .NET's HasExited reads it as running. One Passenger started is
/// that parent: it runs until .NET has taken its exit, so what asks and then reads its exit code finds one.</summary>
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

    /// <summary>A child has ended to the system a moment before .NET's signal thread takes its exit, a moment a loop asking
    /// with nothing between its questions is sure to meet.</summary>
    [Fact]
    public void A_process_passenger_started_runs_until_its_exit_code_can_be_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no process waiting for its parent once it has ended");
        for (int round = 0; round < 50; round++)
        {
            using Process ended = ChildProcesses.Start(ChildProcesses.Command("/bin/sh", ["-c", "exit 3"]));
            Deadline until = Deadline.In(TimeSpan.FromSeconds(30).Scaled());
            while (DotnetProcesses.Runs(ended))
            {
                Assert.False(until.Passed, $"Round {round}'s shell, which only exits, was still taken for running: .NET never took its exit");
            }
            int? code = null;
            try
            {
                code = ended.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // Asked what it ended with too soon, which the assertion names.
            }
            Assert.True(code == 3, $"Round {round}'s shell was taken for ended with its exit code {(code is null ? "not yet readable" : $"{code}")}: "
                + "whatever says what a program ended with (a test host's failure, a launch's end, a language server's) would throw instead");
        }
    }

    [Fact]
    public void A_process_ended_and_waiting_for_its_parent_to_take_its_exit_does_not_run()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no process waiting for its parent once it has ended");
        string named = Path.Combine(TestFolders.Create("zombie-"), "pid");
        // The shell's child outlives its end under the program the shell becomes, which never takes a child's exit.
        using Process parent = ChildProcesses.Start(ChildProcesses.Command("/bin/sh", ["-c", $"sleep 0.2 & echo $! > '{named}'; exec sleep 10"]));
        try
        {
            Assert.True(Waiting.Until(() => File.Exists(named) && File.ReadAllText(named).Trim().Length > 0, TimeSpan.FromSeconds(10)), "The shell never said its child's id");
            int child = int.Parse(File.ReadAllText(named).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(Waiting.Until(() => !DotnetProcesses.Runs(child), TimeSpan.FromSeconds(10)),
                $"Process {child}, ended and waiting for its parent to take its exit, was taken for running: a debugged program gone so would be ended as left running");
            Process? found = null;
            try
            {
                found = Process.GetProcessById(child);
            }
            catch (ArgumentException)
            {
            }
            Assert.True(found is not null, $"Process {child} was gone before its parent ended, so it was no zombie the test could read");
            using Process held = found;
            Assert.False(DotnetProcesses.Runs(held),
                $"Process {child}, ended and waiting for its parent to take its exit, was taken for running as a process held: a debugger's attached program gone so would be waited on as running");
        }
        finally
        {
            ChildProcesses.Kill(parent);
        }
    }
}
