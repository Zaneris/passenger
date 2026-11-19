using System.Diagnostics;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>A .NET program's runtime makes its diagnostic socket and its debugger's pipes in its temporary folder on macOS
/// and Linux and takes them away only as it ends of itself: what the run starts keeps them in the run's own folder, never
/// the person's, and what Passenger ends, a kill or a debug session's Stop, it takes away by the program's id and start.</summary>
[Collection(DebugSession.Collection)]
public sealed class RuntimeFilesTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_program_the_run_starts_keeps_its_socket_and_pipes_in_the_runs_folder_and_once_killed_leaves_none_anywhere()
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

    [Fact]
    public async Task A_program_a_debug_session_launched_and_stop_ended_leaves_no_socket_or_pipe()
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

    /// <summary>Those of a program's runtime files that are in the person's temporary folder.</summary>
    private static string[] Persons(RuntimeFiles files) =>
        [.. files.Paths.Select(path => Path.Combine(TestRun.PersonsTemporary, Path.GetFileName(path))).Where(File.Exists)];
}
