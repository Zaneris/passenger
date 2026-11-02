using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>A folder's test runners as one: each built test assembly is found and run by the runner its build asks for,
/// a Testing Platform application (xUnit v3, MSTest's runner, TUnit) through <see cref="TestingPlatformRunner"/>, every
/// other through VSTest (<see cref="VsTestRunner"/>, started the first time one needs it), and a Godot project's GUT tests,
/// found from its GDScript, through GUT's command line (<see cref="GutRunner"/>). So a solution of all of them shows them in
/// one tree, and Run All runs them all.</summary>
public sealed class TestRunners(string folder, Func<string, CancellationToken, Task<ITestRunner>>? startVsTest = null) : ITestRunner
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

    private readonly TestingPlatformRunner platform = new();
    private readonly GutRunner gut = new();
    private readonly Func<string, CancellationToken, Task<ITestRunner>> startVsTest = startVsTest ?? VsTestRunner.StartAsync;
    private ITestRunner? vsTest;

    /// <summary>The runner that found each test, by the test's key, which runs it.</summary>
    private readonly Dictionary<string, ITestRunner> byKey = new(StringComparer.Ordinal);

    public bool IsUsable => vsTest?.IsUsable ?? true;

    /// <summary>The tests in built test assemblies, each assembly asked of its runner, the two runners at once.</summary>
    public async Task<IReadOnlyList<TestItem>> DiscoverAsync(IReadOnlyList<TestAssembly> assemblies, Action<string>? log = null, CancellationToken cancel = default)
    {
        bool[] onPlatform = await BackgroundWork.Run(() => assemblies.Select(a => TestingPlatformRunner.IsApplication(a.Path)).ToArray(), cancel);
        List<TestAssembly> platformOnes = [.. assemblies.Where((_, at) => onPlatform[at])];
        List<TestAssembly> vsTestOnes = [.. assemblies.Where((_, at) => !onPlatform[at])];
        Task<IReadOnlyList<TestItem>> fromPlatform = platformOnes.Count > 0 ? platform.DiscoverAsync(platformOnes, log, cancel) : Task.FromResult<IReadOnlyList<TestItem>>([]);
        Task<IReadOnlyList<TestItem>> fromVsTest = vsTestOnes.Count > 0 ? DiscoverWithVsTest(vsTestOnes, log, cancel) : Task.FromResult<IReadOnlyList<TestItem>>([]);
        IReadOnlyList<TestItem>[] found = await Task.WhenAll(fromVsTest, fromPlatform);
        foreach (TestItem test in found[0])
        {
            byKey[test.Key] = vsTest!;
        }
        foreach (TestItem test in found[1])
        {
            byKey[test.Key] = platform;
        }
        return [.. found[0], .. found[1]];
    }

    private async Task<IReadOnlyList<TestItem>> DiscoverWithVsTest(IReadOnlyList<TestAssembly> assemblies, Action<string>? log, CancellationToken cancel)
    {
        vsTest ??= await startVsTest(folder, cancel);
        return await vsTest.DiscoverAsync(assemblies, log, cancel);
    }

    /// <summary>Runs tests, VSTest's, the platform's then GUT's, each by the runner that found them; a run stopped stops there.</summary>
    public async Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancel = default)
    {
        RunSummary? summary = null;
        foreach (ITestRunner runner in new ITestRunner?[] { vsTest, platform, gut }.OfType<ITestRunner>())
        {
            List<TestItem> its = [.. tests.Where(t => runner == gut ? t.Framework == GutScripts.Framework : byKey.TryGetValue(t.Key, out ITestRunner? by) && by == runner)];
            if (its.Count == 0)
            {
                continue;
            }
            RunSummary part = await runner.RunAsync(its, outcomes, log, debugger, environment, cancel);
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
        gut.Dispose();
    }
}
