using System.Diagnostics;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>A .NET program's runtime makes its diagnostic socket and its debugger's pipes in its temporary folder on macOS
/// and Linux and takes them away only as it ends of itself: what the run starts keeps them in the run's own folder, never
/// the person's, and what Passenger ends, a kill or a debug session's Stop, it takes away by the program's id and start, as
/// it does those of a program one of its own started apart that another ended.</summary>
[Collection(DebugSession.Collection)]
public sealed class RuntimeFilesTests(ITestOutputHelper output)
{
    /// <summary>A program the run starts keeps its runtime's socket and pipes in the run's folder and leaves none once
    /// killed, one a debug session launched and Stop ended leaves none, and nor does one started apart and killed by
    /// another, as one flow of the steps of systems whose runtime keeps them past its process, each in an instance of its own.</summary>
    [Fact]
    public async Task A_program_run_or_debugged_leaves_no_socket_or_pipe_of_its_runtime_once_ended()
    {
        await Flow.StepApartAsync("a program the run starts keeps its socket and pipes in the runs folder and once killed leaves none anywhere", () => new RuntimeFilesTests(output), t => t.A_program_the_run_starts_keeps_its_socket_and_pipes_in_the_runs_folder_and_once_killed_leaves_none_anywhere());
        await Flow.StepApartAsync("a program a debug session launched and stop ended leaves no socket or pipe", () => new RuntimeFilesTests(output), t => t.A_program_a_debug_session_launched_and_stop_ended_leaves_no_socket_or_pipe());
        await Flow.StepApartAsync("a program one of passengers started apart and another killed leaves no socket or pipe", () => new RuntimeFilesTests(output), t => t.A_program_one_of_passengers_started_apart_and_another_killed_leaves_no_socket_or_pipe());
    }

    private async Task A_program_the_run_starts_keeps_its_socket_and_pipes_in_the_runs_folder_and_once_killed_leaves_none_anywhere()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' runtime pipes go with their process");
        ProcessStartInfo start = ChildProcesses.Command(Debuggees.Apphost("ReadsInputConsoleApp"), []);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        using Process program = ChildProcesses.Start(start);
        RuntimeFiles files = RuntimeFiles.Of(program.Id) ?? throw new InvalidOperationException($"Process {program.Id} could not be read");
        Assert.True(await Waiting.UntilAsync(() => files.Paths.All(File.Exists), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            $"The program's runtime never made its socket and pipes: [{string.Join(", ", files.Paths)}]");
        Assert.True(files.Paths.All(path => Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(TestRun.Temporary)),
            $"The program's runtime made its socket and pipes outside the run's folder {TestRun.Temporary}: [{string.Join(", ", files.Paths)}]");
        string[] persons = Persons(files);

        ChildProcesses.Kill(program);
        Assert.True(await Waiting.UntilAsync(() => !files.Paths.Any(File.Exists), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            $"The program killed left [{string.Join(", ", files.Paths.Where(File.Exists))}]");
        Assert.True(persons.Length == 0, $"The program's runtime made [{string.Join(", ", persons)}] in the person's temporary folder {TestRun.PersonsTemporary}");
    }

    private async Task A_program_a_debug_session_launched_and_stop_ended_leaves_no_socket_or_pipe()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' runtime pipes go with their process");
        await using DebugSession host = DebugSession.ForLaunch(output);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("ReadsInputConsoleApp"), stopAtEntry: true, justMyCode: true)
            .WaitForInitializedEvent();
        await host.ConfigurationDoneAsync();
        await host.WaitForStoppedEvent();
        int process = await host.LaunchedProcess.WaitAsync(TimeSpan.FromSeconds(30).Scaled(), TestContext.Current.CancellationToken);
        RuntimeFiles files = RuntimeFiles.Of(process) ?? throw new InvalidOperationException($"Process {process} could not be read");
        Assert.True(files.Paths.All(File.Exists), $"The debugged program's runtime never made its socket and pipes: [{string.Join(", ", files.Paths)}]");
        string[] persons = Persons(files);

        Assert.True(await host.DisconnectAsync(TimeSpan.FromSeconds(30).Scaled()), "The adapter never answered the disconnect that ends its program");
        Assert.True(await Waiting.UntilAsync(() => !files.Paths.Any(File.Exists), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            $"The program the debugger ended left [{string.Join(", ", files.Paths.Where(File.Exists))}]");
        Assert.True(persons.Length == 0, $"The program's runtime made [{string.Join(", ", persons)}] in the person's temporary folder {TestRun.PersonsTemporary}");
    }

    /// <summary>A program that one Passenger started puts in a session of its own, as Aspire's orchestrator detaches and runs
    /// the dashboard, and that runs a while before another program kills it, as the orchestrator ends the dashboard, leaves
    /// no socket or pipe: Passenger follows a program started apart by the lineage it gave the one that started it.</summary>
    private async Task A_program_one_of_passengers_started_apart_and_another_killed_leaves_no_socket_or_pipe()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' runtime pipes go with their process");
        string folder = TestFolders.Create("apart-");
        string written = Path.Combine(folder, "id");
        try
        {
            // Its parent ends at once; its child, in a session of its own and holding none of its pipes, writes its id and runs the program in its place.
            ProcessStartInfo start = ChildProcesses.Command("perl", ["-MPOSIX", "-e",
                "exit if fork; POSIX::setsid(); open(STDIN, '<', '/dev/null'); open(STDOUT, '>', '/dev/null'); open(STDERR, '>', '/dev/null'); "
                + "my $to = shift; open(my $f, '>', \"$to.new\") or die; print $f $$; close $f; rename(\"$to.new\", $to) or die; exec @ARGV or die",
                written, Debuggees.Apphost("LoopingConsoleApp"), "asleep"]);
            ChildResult detached = await ChildProcesses.RunAsync(start, limit: TimeSpan.FromSeconds(30).Scaled());
            Assert.True(detached.Succeeded, $"perl did not start the program apart: {detached.Error}{detached.Output}");
            Assert.True(await Waiting.UntilAsync(() => File.Exists(written), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "The program started apart never wrote its id");
            OwnProcess program = OwnProcess.Of(int.Parse(File.ReadAllText(written), System.Globalization.CultureInfo.InvariantCulture));
            RuntimeFiles files = RuntimeFiles.Of(program.Id) ?? throw new InvalidOperationException($"Process {program.Id} could not be read");
            Assert.True(await Waiting.UntilAsync(() => File.Exists(files.Socket), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                $"The program's runtime never made its socket: {files.Socket}");
            await Waiting.ForAsync(TimeSpan.FromSeconds(3).Scaled(), "the program running apart a while, as a dashboard runs while its AppHost does", TestContext.Current.CancellationToken);

            program.Kill(alone: true, waited: true);
            Assert.True(await Waiting.UntilAsync(() => !files.Paths.Any(File.Exists), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                $"The program started apart, killed by another, left [{string.Join(", ", files.Paths.Where(File.Exists))}]");
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Those of a program's runtime files that are in the person's temporary folder.</summary>
    private static string[] Persons(RuntimeFiles files) =>
        [.. files.Paths.Select(path => Path.Combine(TestRun.PersonsTemporary, Path.GetFileName(path))).Where(File.Exists)];
}
