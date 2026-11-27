using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>A folder's test runners as one: each built test assembly is found and run by the runner its build asks for,
/// a Testing Platform application (xUnit v3, MSTest's runner, TUnit) through <see cref="TestingPlatformRunner"/>, every
/// other through VSTest (<see cref="VsTestRunner"/>, started the first time one needs it), a Godot project's GUT tests,
/// found from its GDScript, through GUT's command line (<see cref="GutRunner"/>), and a Godot C# game's GoDotTest tests,
/// found from its code, in the game (<see cref="GoDotTestRunner"/>), each of those two taking the tests the code declares
/// (<paramref name="declared"/>) and the game's where its run starts (<paramref name="goDotStart"/>), and a device test
/// project's app on its target (<paramref name="devices"/>), an Android one's tests those its code declares, run through its
/// instrumentation (<see cref="InstrumentationRunner"/>). So a solution of all of them shows them in one tree, and Run All
/// runs them all.</summary>
public sealed class TestRunners(string folder, Func<string, CancellationToken, Task<ITestRunner>>? startVsTest = null,
    Func<string, IReadOnlyList<TestItem>>? declared = null, Func<string, Task<GoDotTests.Start>>? goDotStart = null, DeviceTestHosts? devices = null) : ITestRunner
{
    /// <summary>What a runner or a test host said: a <paramref name="notable"/> warning or error in the run's output and
    /// vstest.log, the rest (an adapter's progress) in vstest.log only while protocols are traced.</summary>
    internal static void Said(bool notable, string level, string message, Action<string>? log)
    {
        if (notable || IdeLog.Traces)
        {
            IdeLog.Write(IdeLog.Tests, $"{level}: {message}");
        }
        if (notable)
        {
            log?.Invoke(message);
        }
    }

    private readonly TestingPlatformRunner platform = new() { Devices = devices };
    private readonly GutRunner gut = new(declared);
    private readonly GoDotTestRunner goDot = new(declared ?? (_ => []), goDotStart);
    private readonly InstrumentationRunner? instrumented = devices is null ? null : new(declared ?? (_ => []), devices);
    private readonly Func<string, CancellationToken, Task<ITestRunner>> startVsTest = startVsTest ?? VsTestRunner.StartAsync;
    private ITestRunner? vsTest;

    /// <summary>The runner that found each assembly's tests, which runs them, by the assembly's project and framework.</summary>
    private readonly Dictionary<(string Project, string? Framework), ITestRunner> byAssembly = [];

    public bool IsUsable => vsTest?.IsUsable ?? true;

    /// <summary>The tests in built test assemblies, each assembly asked of its runner, the runners at once: an Android device
    /// test project's of its instrumentation's, the rest of the platform's or VSTest's.</summary>
    public async Task<FoundTests> DiscoverAsync(IReadOnlyList<TestAssembly> found, Action<string>? log = null, CancellationToken cancel = default)
    {
        List<TestAssembly> onAndroid = instrumented is null ? [] : [.. found.Where(a => Run.Devices.DeviceTargets.PlatformOf(a.Framework) == Run.Devices.TargetPlatform.Android)];
        FoundTests fromAndroid = onAndroid.Count > 0 ? await instrumented!.DiscoverAsync(onAndroid, log, cancel) : FoundTests.All([]);
        foreach (TestAssembly assembly in onAndroid)
        {
            byAssembly[(assembly.Project.Path, assembly.Framework)] = instrumented!;
        }
        IReadOnlyList<TestAssembly> assemblies = [.. found.Except(onAndroid)];
        bool[] onPlatform = await BackgroundWork.Run(() => assemblies.Select(a => TestingPlatformRunner.IsApplication(a.Path)).ToArray(), cancel);
        List<TestAssembly> platformOnes = [.. assemblies.Where((_, at) => onPlatform[at])];
        List<TestAssembly> vsTestOnes = [.. assemblies.Where((_, at) => !onPlatform[at])];
        Task<FoundTests> fromPlatform = platformOnes.Count > 0 ? platform.DiscoverAsync(platformOnes, log, cancel) : Task.FromResult(FoundTests.All([]));
        Task<FoundTests> fromVsTest = vsTestOnes.Count > 0 ? DiscoverWithVsTest(vsTestOnes, log, cancel) : Task.FromResult(FoundTests.All([]));
        FoundTests[] each = await Task.WhenAll(fromVsTest, fromPlatform);
        foreach (TestAssembly assembly in vsTestOnes)
        {
            byAssembly[(assembly.Project.Path, assembly.Framework)] = vsTest!;
        }
        foreach (TestAssembly assembly in platformOnes)
        {
            byAssembly[(assembly.Project.Path, assembly.Framework)] = platform;
        }
        return new FoundTests([.. each[0].Tests, .. each[1].Tests, .. fromAndroid.Tests], [.. each[0].Unfound, .. each[1].Unfound, .. fromAndroid.Unfound]);
    }

    private async Task<FoundTests> DiscoverWithVsTest(IReadOnlyList<TestAssembly> assemblies, Action<string>? log, CancellationToken cancel)
    {
        vsTest ??= await startVsTest(folder, cancel);
        return await vsTest.DiscoverAsync(assemblies, log, cancel);
    }

    /// <summary>Runs tests, VSTest's, the platform's, Android instrumentations', GUT's then GoDotTest's, each by the runner
    /// that found them; a run stopped stops there.</summary>
    public async Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, GodotStart? godot = null, CancellationToken cancel = default)
    {
        RunSummary? summary = null;
        foreach (ITestRunner runner in new ITestRunner?[] { vsTest, platform, instrumented, gut, goDot }.OfType<ITestRunner>())
        {
            List<TestItem> its = [.. tests.Where(t => runner == gut ? t.Framework == GutScripts.Framework : runner == goDot ? t.Framework == GoDotTests.Framework
                : byAssembly.TryGetValue((t.Project, t.Framework), out ITestRunner? by) && by == runner)];
            if (its.Count == 0)
            {
                continue;
            }
            RunSummary part = await runner.RunAsync(its, outcomes, log, debugger, environment, godot, cancel);
            summary = summary is null ? part : summary.And(part);
            if (part.Canceled)
            {
                break;
            }
        }
        return summary ?? new RunSummary(TimeSpan.Zero, cancel.IsCancellationRequested, false, null);
    }

    public void Dispose()
    {
        vsTest?.Dispose();
        platform.Dispose();
        instrumented?.Dispose();
        gut.Dispose();
        goDot.Dispose();
    }
}
