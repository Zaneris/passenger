using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>A frame of an assembly that ships no symbols (CoreLib's, System.Linq's) is shown with source once its
/// symbols are decompiled into the folder the adapter is given: written whole or not at all, so a decompile cut short
/// (the adapter killed) or a PDB that does not load costs the next session no more than a decompile; pruned as an
/// adapter starts.</summary>
[Collection(DebugSession.Collection)]
public sealed class DecompiledSymbolsTests(ITestOutputHelper testOutputHelper) : DecompiledSymbolsSessions(testOutputHelper)
{
    [Fact]
    public async Task A_frame_whose_decompile_the_killed_adapter_cut_short_is_resolved_by_the_next_session()
    {
        string cache = TestFolders.Create("symbols");
        await using (DebugSession killed = await StoppedInLinqCallback(cache))
        {
            Task<ResolveStackFrameResponse> resolving = killed.Send<ResolveStackFrameResponse>(DapCommands.ResolveStackFrame, new ResolveStackFrameArguments((await LinqFrame(killed)).Id));
            await DecompileStarted(cache, resolving);
            killed.Kill();
        }
        Assert.True(Pdbs(cache).Count == 0, $"The adapter killed mid-decompile left symbols in the cache, which the next session would take for whole: {string.Join(", ", Pdbs(cache))}");

        await using DebugSession next = await StoppedInLinqCallback(cache);
        DapStackFrame frame = await LinqFrame(next);
        Assert.False(frame.IsResolved, "System.Linq's frame had source before it was resolved: the cut-short decompile left symbols behind");
        ResolveStackFrameResponse resolved = await next.RequestAsync<ResolveStackFrameResponse>(DapCommands.ResolveStackFrame, new ResolveStackFrameArguments(frame.Id), Decompile);
        Assert.True(resolved.StackFrame.IsResolved == true && resolved.StackFrame.Line > 0 && resolved.StackFrame.Source?.Path?.EndsWith(".cs", StringComparison.Ordinal) == true,
            $"The next session's System.Linq frame was not resolved ({resolved.StackFrame.Source?.Path}:{resolved.StackFrame.Line})");
        Assert.Equal("System.Linq.Enumerable+RangeSelectIterator`2", resolved.StackFrame.DecompiledSourceInfo?.TypeFullName);
        Assert.Single(Pdbs(cache));
    }

    [Fact]
    public async Task A_cached_PDB_that_does_not_load_is_decompiled_again_and_a_step_into_its_code_stops_on_its_line()
    {
        string cache = TestFolders.Create("symbols");
        (string Path, int Line) first;
        await using (DebugSession host = await StoppedBeforeConsoleWrite(cache))
        {
            (string firstPath, int firstLine, _) = (await host.StepInAsync(thread).WaitForStoppedEvent()).ReadStopInfo();
            first = (firstPath, firstLine);
        }
        Assert.EndsWith("Console.cs", first.Path, StringComparison.Ordinal);
        // Cut short, as a write that is not atomic leaves it when its adapter is killed.
        string pdb = Assert.Single(Pdbs(cache));
        byte[] whole = File.ReadAllBytes(pdb);
        File.WriteAllBytes(pdb, whole[..(whole.Length / 2)]);

        await using DebugSession next = await StoppedBeforeConsoleWrite(cache);
        (string path, int line, _) = (await next.StepInAsync(thread).WaitForStoppedEvent()).ReadStopInfo();
        Assert.True((path, line) == first,
            $"With its cached PDB cut in half, a step into Console.WriteLine stopped at {path}:{line}, not at {first.Path}:{first.Line} as when it was first decompiled");
        Assert.True(new FileInfo(Assert.Single(Pdbs(cache))).Length > whole.Length / 2,
            "The PDB that did not load is still the one in the cache, so every session after would refuse it again");
    }

    [Fact]
    public async Task An_adapter_starting_deletes_symbols_unused_for_a_month_and_writes_never_finished_and_keeps_those_in_use()
    {
        string cache = TestFolders.Create("symbols");
        string stale = Plant(cache, "Old.Library.pdb", TimeSpan.FromDays(40));
        string recent = Plant(cache, "Recent.Library.pdb", TimeSpan.FromDays(3));
        string abandoned = Plant(cache, "Killed.Library.pdb.0a1b.tmp", TimeSpan.FromHours(2));
        string writing = Plant(cache, "Writing.Library.pdb.2c3d.tmp", TimeSpan.FromMinutes(1));

        await using DebugSession host = await DebugSession.ForLaunch(Output, cache).InitializeAsync();
        Assert.True(await Waiting.UntilAsync(() => !File.Exists(stale) && !File.Exists(abandoned), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(50)),
            $"The adapter had not pruned in {TimeSpan.FromSeconds(30).Scaled().TotalSeconds} s: {(File.Exists(stale) ? "symbols unused for 40 days" : "a temporary file 2 hours old")} still there");
        Assert.True(File.Exists(recent), "Symbols used 3 days ago were pruned");
        Assert.True(File.Exists(writing), "A temporary file a minute old, which another adapter may still be writing, was pruned");
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(stale))), "The pruned entry's folders were left behind, empty");
    }
}

/// <summary>A decompile runs off the engine's lock, so the session answers meanwhile: held to how soon the adapter answers
/// during a decompile, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class DecompiledSymbolsTimingTests(ITestOutputHelper testOutputHelper) : DecompiledSymbolsSessions(testOutputHelper)
{
    [Fact]
    public async Task A_disconnect_while_CoreLib_is_decompiled_is_answered_at_once_and_leaves_no_symbols_half_written()
    {
        string cache = TestFolders.Create("symbols");
        await using DebugSession host = await StoppedInCoreLibCallback(cache);
        DapStackFrame coreLib = await Frame(host, "System.Private.CoreLib.dll!System.Collections.Generic.List<int>.ForEach");
        Task<ResolveStackFrameResponse> resolving = host.Send<ResolveStackFrameResponse>(DapCommands.ResolveStackFrame, new ResolveStackFrameArguments(coreLib.Id));
        await DecompileStarted(cache, resolving);

        Stopwatch asked = Stopwatch.StartNew();
        Assert.NotEmpty((await host.RequestAsync<ThreadsResponse>(DapCommands.Threads, limit: Decompile)).Threads);
        Assert.True(asked.Elapsed < Promptly, $"A request made while CoreLib was decompiled took {asked.Elapsed.TotalSeconds:0.0} s: it waited for the decompile");
        Assert.True(await host.DisconnectAsync(Promptly), $"The disconnect went unanswered for {Promptly.TotalSeconds:0} s while CoreLib was decompiled, so an IDE would kill the adapter");
        Assert.False(resolving.IsCompletedSuccessfully, "CoreLib's frame was resolved before the disconnect, so the disconnect never waited on a decompile");
        Assert.True(Pdbs(cache).Count == 0, $"The session ended mid-decompile left symbols in the cache, which the next would take for whole: {string.Join(", ", Pdbs(cache))}");
    }

}

/// <summary>The programs the decompiled symbols' tests stop, each adapter given a cache folder of the test's own, and what
/// they read of its stack and its cache.</summary>
public abstract class DecompiledSymbolsSessions(ITestOutputHelper testOutputHelper)
{
    /// <summary>The test's output, which its debug sessions write to.</summary>
    protected ITestOutputHelper Output => testOutputHelper;

    /// <summary>How long a request may take while CoreLib is decompiled beside it, which takes a minute or
    /// more.</summary>
    protected static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    /// <summary>The longest a decompile may take on a slow runner.</summary>
    protected static readonly TimeSpan Decompile = TimeSpan.FromMinutes(3);

    protected int thread;

    /// <summary>A file in an entry's folder of a cache (NAME/MVID/FILE), last written as long ago as given.</summary>
    protected static string Plant(string cache, string file, TimeSpan ago)
    {
        string name = file[..file.IndexOf(".pdb", StringComparison.Ordinal)];
        string path = Path.Combine(cache, name, Guid.NewGuid().ToString("N"), file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[1024]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - ago);
        return path;
    }

    /// <summary>PassengerCases launched to its callback from List.ForEach, the frame below CoreLib's.</summary>
    protected async Task<DebugSession> StoppedInCoreLibCallback(string cache)
    {
        DebugSession host = DebugSession.ForLaunch(testOutputHelper, cache);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["corelib"])
            .WaitForInitializedEvent();
        int called = Debuggees.Line("PassengerCases", "Console.WriteLine($\"called by CoreLib with {value}\");");
        await host.BreakpointsAsync(called, Debuggees.Source("PassengerCases", "Program.cs")).ConfigurationDoneAsync();
        thread = (await host.WaitForStoppedEvent()).ThreadId!.Value;
        return host;
    }

    /// <summary>DebuggableConsoleApp attached to at its selector, which System.Linq's iterator calls.</summary>
    protected Task<DebugSession> StoppedInLinqCallback(string cache) =>
        StoppedAt(cache, Debuggees.Source("DebuggableConsoleApp", "ClassWithBclCall.cs"), 12);

    /// <summary>DebuggableConsoleApp attached to without Just My Code, at its call of Console.WriteLine.</summary>
    protected Task<DebugSession> StoppedBeforeConsoleWrite(string cache) =>
        StoppedAt(cache, Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), 15);

    protected async Task<DebugSession> StoppedAt(string cache, string source, int line)
    {
        DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended: true, out Process program, cache);
        await host
            .InitializeAsync()
            .AttachAsync(program.Id, justMyCode: false)
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([line], source)
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(program.Id, startSuspended: true);
        thread = (await host.WaitForStoppedEvent()).ThreadId!.Value;
        return host;
    }

    protected Task<DapStackFrame> LinqFrame(DebugSession host) => Frame(host, "System.Linq.dll!");

    protected async Task<DapStackFrame> Frame(DebugSession host, string named)
    {
        StackTraceResponse stack = await host.StackTraceAsync(thread, levels: null);
        return stack.StackFrames.FirstOrDefault(frame => frame.Name.StartsWith(named, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No frame of {named} on the stack: {string.Join(" < ", stack.StackFrames.Select(frame => frame.Name))}");
    }

    /// <summary>Waits for the decompile's temporary file in the cache, the sign that it is under way.</summary>
    protected static async Task DecompileStarted(string cache, Task resolving)
    {
        bool Started() => Directory.EnumerateFiles(cache, "*.tmp", SearchOption.AllDirectories).Any();
        await Waiting.UntilAsync(() => Started() || resolving.IsCompleted, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(20));
        if (Started())
        {
            return;
        }
        if (resolving.IsCompleted)
        {
            await resolving;
            throw new InvalidOperationException("The frame was resolved before its decompile was seen to start");
        }
        throw new TimeoutException($"No decompile started in {TimeSpan.FromSeconds(60).Scaled().TotalSeconds} s of resolveStackFrame");
    }

    protected static List<string> Pdbs(string cache) => [.. Directory.EnumerateFiles(cache, "*.pdb", SearchOption.AllDirectories)];
}
