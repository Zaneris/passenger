using System.Diagnostics;
using Passenger.App.Run.Devices;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>An Android device test project's tests (.NET's androidtest makes one), run as its workload runs them: through the
/// app's instrumentation, which takes no arguments and so reaches no Testing Platform client of Passenger's. Its tests are
/// those the code declares (<paramref name="declared"/>). A run installs the app where its project runs (<see cref="DeviceTestHosts"/>),
/// held there as Run holds a start, runs the instrumentation (<see cref="AndroidApp.Instrument"/>), which runs every test of the
/// app whichever were chosen, each told as its status comes (<see cref="InstrumentationLines"/>); once it has ended, the
/// results file it wrote is brought beside the build and tells each again with its details (<see cref="TrxResults"/>).
/// Stopped, or as the folder or Passenger ends, the app is ended there. Debugged, the debugger starts it as Debug starts an
/// app there, through Passenger's relay in it, its lines heard from its session's console.</summary>
public sealed class InstrumentationRunner(Func<string, IReadOnlyList<TestItem>> declared, DeviceTestHosts devices) : ITestRunner
{
    private readonly Lock gate = new();

    /// <summary>Each assembly whose tests were told, by its project and framework.</summary>
    private readonly Dictionary<(string Project, string Framework), TestAssembly> assemblies = [];

    public bool IsUsable => true;

    /// <summary>The tests each assembly's code declares, as of its framework: the instrumentation lists none.</summary>
    public Task<FoundTests> DiscoverAsync(IReadOnlyList<TestAssembly> found, Action<string>? log = null, CancellationToken cancel = default)
    {
        List<TestItem> tests = [];
        lock (gate)
        {
            foreach (TestAssembly assembly in found)
            {
                assemblies[(assembly.Project.Path, assembly.Framework)] = assembly;
                tests.AddRange(declared(assembly.Project.Path).Select(t => t with { Framework = assembly.Framework }));
            }
        }
        return Task.FromResult(FoundTests.All(tests));
    }

    /// <summary>Runs each assembly's app's instrumentation in turn, under the debugger where one is given.</summary>
    public async Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, GodotStart? godot = null, CoverageRun? coverage = null, CancellationToken cancel = default)
    {
        coverage?.Missed(Refusals.CoverageOnDevice.Shown);
        RunSummary? summary = null;
        foreach (IGrouping<(string Project, string? Framework), TestItem> asked in tests.GroupBy(t => (t.Project, t.Framework)))
        {
            TestAssembly? assembly;
            lock (gate)
            {
                assembly = asked.Key.Framework is { } framework ? assemblies.GetValueOrDefault((asked.Key.Project, framework)) : null;
            }
            if (assembly is null)
            {
                continue;
            }
            RunSummary part = await RunInAsync(assembly, [.. asked], outcomes, log, debugger, environment ?? new Dictionary<string, string>(), cancel);
            summary = summary is null ? part : summary.And(part);
            if (part.Canceled)
            {
                break;
            }
        }
        return summary ?? new RunSummary(TimeSpan.Zero, cancel.IsCancellationRequested, false, null);
    }

    /// <summary>One run of an assembly's app's instrumentation on its device: what it told, or why it could not run or broke off.</summary>
    private async Task<RunSummary> RunInAsync(TestAssembly assembly, IReadOnlyList<TestItem> asked, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log,
        ITestDebugLauncher? debugger, IReadOnlyDictionary<string, string> environment, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        string name = $"{Path.GetFileNameWithoutExtension(assembly.Path)} ({assembly.Framework})";
        DeviceTestApp? ready;
        string? unready;
        try
        {
            (ready, unready) = await devices.ReadyAsync(assembly, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return new RunSummary(Stopwatch.GetElapsedTime(began), true, false, null);
        }
        if (cancel.IsCancellationRequested)
        {
            return new RunSummary(Stopwatch.GetElapsedTime(began), true, false, null);
        }
        if (ready?.Android is not { } app)
        {
            return Refused($"{name} could not start: {unready}", log);
        }
        Telling telling = new(assembly, asked, declared(assembly.Project.Path), outcomes);
        List<string> errors = [];
        Run.LaunchPlan plan = ready.Plan(null, environment);
        if (debugger is not null)
        {
            try
            {
                // The debugger's start holds the app on its device as Debug's does, and ends it there with its session.
                Task debugged = await debugger.DebugOnDeviceAsync(plan, telling.Heard);
                await debugged.WaitAsync(cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return telling.Summary(Stopwatch.GetElapsedTime(began), canceled: true, null);
            }
            catch (InvalidOperationException undebugged)
            {
                return Refused($"{name} could not be debugged: {undebugged.Message}", log);
            }
        }
        else if (await RanAsync(plan, app, assembly, telling, errors, cancel) is { } unstarted)
        {
            return unstarted.Error is null ? unstarted with { Elapsed = Stopwatch.GetElapsedTime(began) } : Refused(unstarted.Error, log);
        }
        if (cancel.IsCancellationRequested)
        {
            return telling.Summary(Stopwatch.GetElapsedTime(began), canceled: true, null);
        }
        string? problem = telling.Lines.Problem();
        if (problem is not null && telling.Lines.ResultsPath is null)
        {
            lock (errors)
            {
                // adb's own words where it never reached the instrumentation (the device gone), but its notices of starting its server.
                problem = errors.FirstOrDefault(e => e.Trim().Length > 0 && !e.StartsWith("* daemon", StringComparison.Ordinal)) is { } said ? $"adb: {said.Trim()}" : problem;
            }
        }
        if (telling.Lines.ResultsPath is { } results && await BroughtAsync(app, assembly, results, log) is { } details)
        {
            telling.Detailed(details);
        }
        if (telling.Beside > 0)
        {
            log?.Invoke($"{assembly.Project.Name}'s instrumentation runs all its tests at once, so it ran {Plural.Of(telling.Beside, "test")} not chosen beside those chosen");
        }
        if (problem is not null)
        {
            log?.Invoke($"{name}: {problem}");
        }
        return telling.Summary(Stopwatch.GetElapsedTime(began), canceled: false, problem is null ? null : $"{name}: {problem}");
    }

    /// <summary>The app's instrumentation run on its device by adb (<paramref name="plan"/>'s program) to its end, each line
    /// told to <paramref name="telling"/> and adb's own words kept in <paramref name="errors"/>, held there as Run holds a
    /// start: null once it has run (or was stopped), else a summary of why it did not: canceled where the folder had ended,
    /// else its error.</summary>
    private async Task<RunSummary?> RanAsync(Run.LaunchPlan plan, AndroidApp app, TestAssembly assembly, Telling telling, List<string> errors, CancellationToken cancel)
    {
        if (await devices.StartsOnDeviceAsync(assembly.Project.Name, app) is not { } start)
        {
            return new RunSummary(TimeSpan.Zero, true, false, null);
        }
        Process process;
        try
        {
            ProcessStartInfo program = plan.Start();
            program.RedirectStandardOutput = true;
            program.RedirectStandardError = true;
            process = await ChildProcesses.StartAsync(program);
        }
        catch (Exception unstarted) when (unstarted is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            start.Dispose();
            return new RunSummary(TimeSpan.Zero, false, true, $"{Path.GetFileNameWithoutExtension(assembly.Path)} ({assembly.Framework}) could not start: {unstarted.Message}");
        }
        Task heard = ChildProcesses.Hear(process, telling.Heard, line =>
        {
            lock (errors)
            {
                errors.Add(line);
            }
        });
        // However the run ends, stopped or not, the app is ended on its device once adb's run of it has.
        start.EndsWith(EndAsync(heard, start));
        using (process)
        using (cancel.Register(() => ChildProcesses.Kill(process)))
        {
            await heard.Ended();
        }
        return null;
    }

    /// <summary>The results file a run wrote on the device brought beside the build (TestResults), as the workload's own run
    /// brings it, and read; null where it could not be, saying why.</summary>
    private static async Task<IReadOnlyList<TrxResult>?> BroughtAsync(AndroidApp app, TestAssembly assembly, string results, Action<string>? log)
    {
        string local = Path.Combine(Path.GetDirectoryName(assembly.Path)!, "TestResults", results[(results.LastIndexOf('/') + 1)..]);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            ChildResult pulled = await AndroidDevices.AdbAsync(app.Adb, app.On("pull", results, local), CancellationToken.None);
            if (!pulled.Succeeded)
            {
                log?.Invoke($"The tests' results file could not be brought from the device, so their failures are told without their details: {pulled.Said}");
                return null;
            }
            return await BackgroundWork.Run(() =>
            {
                using FileStream read = File.OpenRead(local);
                return TrxResults.Read(read);
            });
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            log?.Invoke($"The tests' results file {PathNames.Shown(local, null)} could not be read, so their failures are told without their details: {unread.Message}");
            return null;
        }
    }

    /// <summary>The app ended on its device once adb's run of it has ended, however it ended (<see cref="DeviceStart.TakeOutAsync"/>).</summary>
    private static async Task EndAsync(Task heard, DeviceStart start)
    {
        await heard.Ended();
        await start.TakeOutAsync();
    }

    private static RunSummary Refused(string said, Action<string>? log)
    {
        log?.Invoke(said);
        return new RunSummary(TimeSpan.Zero, false, true, said);
    }

    public void Dispose()
    {
    }

    /// <summary>A run's lines as adb hears them: each test's end told as its status comes, as the test chosen of its name,
    /// else the code's of it, else one of its name alone; then each again with what the results file says of it. Each
    /// test and row is held once, at its latest.</summary>
    private sealed class Telling(TestAssembly assembly, IReadOnlyList<TestItem> asked, IReadOnlyList<TestItem> code, Action<IReadOnlyList<TestOutcome>> outcomes)
    {
        private readonly Lock gate = new();
        private readonly ILookup<string, TestItem> chosen = asked.ToLookup(t => t.Method, StringComparer.Ordinal);
        private readonly ILookup<string, TestItem> declared = code.ToLookup(t => t.Method, StringComparer.Ordinal);
        private readonly List<TestOutcome> all = [];
        private readonly Dictionary<(string Method, string? Row), int> held = [];
        private readonly HashSet<string> beside = new(StringComparer.Ordinal);

        public InstrumentationLines Lines { get; } = new();

        /// <summary>How many tests it ran that none chosen is.</summary>
        public int Beside
        {
            get
            {
                lock (gate)
                {
                    return beside.Count;
                }
            }
        }

        public void Heard(string line)
        {
            TestOutcome? outcome = null;
            lock (gate)
            {
                if (Lines.Read(line) is { } status && InstrumentationLines.Ended(status) is { } ended)
                {
                    outcome = new(Of(ended.Test), ended.State, TimeSpan.Zero) { Message = ended.Message, StackTrace = FailureStacks.Once(ended.StackTrace, ended.Message) };
                    Hold(outcome);
                }
            }
            if (outcome is not null)
            {
                outcomes([outcome]);
            }
        }

        /// <summary>Each test the results file holds, told again with its time and what it said and wrote.</summary>
        public void Detailed(IReadOnlyList<TrxResult> results)
        {
            List<TestOutcome> told = [];
            lock (gate)
            {
                foreach (TrxResult result in results)
                {
                    TestOutcome outcome = new(Of(result.FullyQualifiedName), result.State, result.Duration)
                    {
                        DisplayName = result.DisplayName,
                        Message = result.Message,
                        StackTrace = FailureStacks.Once(result.StackTrace, result.Message),
                        Output = result.Output,
                    };
                    outcome = outcome with { Again = held.ContainsKey((outcome.Test.Method, outcome.Row)) };
                    Hold(outcome);
                    told.Add(outcome);
                }
            }
            if (told.Count > 0)
            {
                outcomes(told);
            }
        }

        public RunSummary Summary(TimeSpan elapsed, bool canceled, string? error)
        {
            lock (gate)
            {
                return new RunSummary(elapsed, canceled, error is not null, error) { Outcomes = [.. all] };
            }
        }

        private void Hold(TestOutcome outcome)
        {
            if (held.TryGetValue((outcome.Test.Method, outcome.Row), out int at))
            {
                all[at] = outcome;
            }
            else
            {
                held[(outcome.Test.Method, outcome.Row)] = all.Count;
                all.Add(outcome);
            }
        }

        /// <summary>The test a full name the instrumentation gives is: the one chosen of it, else the code's, else one of it alone.</summary>
        private TestItem Of(string name)
        {
            string method = TestItem.MethodOf(name);
            if ((chosen[method].FirstOrDefault(t => t.Row is null) ?? chosen[method].FirstOrDefault()) is { } asked)
            {
                return asked;
            }
            beside.Add(method);
            return declared[method].FirstOrDefault() is { } written ? written with { Framework = assembly.Framework }
                : new TestItem(assembly.Project.Path, method, method[(method.LastIndexOf('.') + 1)..]) { Framework = assembly.Framework };
        }
    }
}
