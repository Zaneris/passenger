using System.Diagnostics;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Passenger.Core;
using VsOutcome = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome;
using VsResult = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult;

namespace Passenger.App.Testing;

/// <summary>Tests found and run through VSTest: the vstest.console of the SDK the folder builds with (its global.json's),
/// driven through VSTest's own translation layer, so every framework with a VSTest adapter (xUnit, NUnit, MSTest) is
/// found and run with its source locations and results. VSTest's test cases stay in here, each told as the IDE's own
/// test and its results as outcomes. What VSTest warns of or fails with is in the logs folder's vstest.log, and with
/// Trace protocol messages on every message it sends.</summary>
public sealed class VsTestRunner : ITestRunner
{
    private const string RunSettings = "<RunSettings><RunConfiguration><DesignMode>true</DesignMode></RunConfiguration></RunSettings>";

    /// <summary>Names a file for VSTest's own diagnostic log, for a run that goes wrong inside it.</summary>
    [IdentifierHome]
    public const string LogVariable = "PASSENGER_VSTEST_LOG";

    /// <summary>The translation layer's constructor that names the dotnet running vstest.console.dll: the public ones run
    /// the .dll under this process's own executable, which is right inside dotnet and wrong under Passenger's apphost (it
    /// would start Passenger again). It is internal to the package version pinned, so a version without it fails every
    /// run with this message.</summary>
    private static readonly ConstructorInfo? NamesHost = typeof(VsTestConsoleWrapper).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(string), typeof(ConsoleParameters)]);

    /// <summary>How long a discovery told to stop has to say it has, before the runner is taken for stuck (an adapter that
    /// never returns) and ended, its test hosts with it.</summary>
    private static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(10);

    private readonly VsTestConsoleWrapper console;

    /// <summary>The test cases discovered, by the key of the test each was told as.</summary>
    private readonly TestCatalog<TestCase> cases = new();

    private VsTestRunner(VsTestConsoleWrapper console) => this.console = console;

    /// <summary>A runner for a folder: vstest.console from the SDK `dotnet --version` names there (<see cref="Dotnet.SdkAsync"/>),
    /// saying why when it names none.</summary>
    public static async Task<ITestRunner> StartAsync(string folder, CancellationToken cancellation = default)
    {
        string sdk;
        try
        {
            sdk = await Dotnet.SdkAsync(folder, cancellation);
        }
        catch (InvalidOperationException unnamed)
        {
            throw new InvalidOperationException($"VSTest cannot start: the folder's .NET SDK could not be named ({unnamed.Message})", unnamed);
        }
        string host = Dotnet.Host();
        string console = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(host))!, "sdk", sdk, "vstest.console.dll");
        if (!File.Exists(console))
        {
            throw new FileNotFoundException($"The SDK {sdk} has no vstest.console", console);
        }
        ConsoleParameters parameters = new()
        {
            InheritEnvironmentVariables = true,
        };
        if (Environment.GetEnvironmentVariable(LogVariable) is { Length: > 0 } logFile)
        {
            parameters.LogFilePath = logFile;
            parameters.TraceLevel = TraceLevel.Verbose;
        }
        VsTestConsoleWrapper wrapper = NamesHost?.Invoke([console, host, parameters]) as VsTestConsoleWrapper
            ?? throw new InvalidOperationException(
                $"VSTest's translation layer {typeof(VsTestConsoleWrapper).Assembly.GetName().Version} cannot be told which dotnet runs vstest.console: VsTestRunner must follow its constructors");
        // Its async calls wait for the handshake before starting vstest.console, so for ever: the blocking one, on a worker.
        await Told(BackgroundWork.Run(wrapper.StartSession));
        IdeLog.Write(IdeLog.Tests, $"vstest.console of the SDK {sdk} started for {folder}");
        VsTestRunner runner = new(wrapper);
        if (cancellation.IsCancellationRequested)
        {
            // Stopped while it started: it ends now, rather than run idle until the IDE does.
            IdeErrors.Observe(Task.Run(runner.Dispose));
            cancellation.ThrowIfCancellationRequested();
        }
        return runner;
    }

    public bool IsUsable { get; private set; } = true;

    /// <summary>The tests in built test assemblies, as their adapters find them. Canceled, VSTest stops finding them and
    /// ends its test hosts; one that does not within a few seconds leaves the runner unusable, to be ended.</summary>
    public async Task<IReadOnlyList<TestItem>> DiscoverAsync(IReadOnlyList<TestAssembly> assemblies, Action<string>? log = null, CancellationToken cancel = default)
    {
        Discovery handler = new(log);
        // The blocking call returns once VSTest has told the handler discovery is complete, stopped or not.
        Task discovering = BackgroundWork.Run(() => console.DiscoverTests([.. assemblies.Select(a => a.Path)], RunSettings, new TestPlatformOptions(), handler));
        using (cancel.Register(() => IdeErrors.Observe(Task.Run(console.CancelDiscovery))))
        {
            try
            {
                await Told(discovering.WaitAsync(cancel));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                try
                {
                    await discovering.WaitAsync(StopLimit);
                }
                catch (TimeoutException)
                {
                    IsUsable = false;
                    _ = discovering.ContinueWith(ended => _ = ended.Exception, TaskScheduler.Default);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or TestPlatformException)
                {
                }
                throw;
            }
        }
        IReadOnlyList<TestCase> found = await handler.Done.Task;
        return await BackgroundWork.Run(() => Tell(assemblies, found));
    }

    /// <summary>Runs tests, their outcomes told a batch at a time as VSTest sends them; done when the run ends, with its
    /// time and whether it was stopped, which cancellation does, keeping the outcomes of what has run. The environment
    /// given is set in the test hosts VSTest starts, as its run settings' EnvironmentVariables are.</summary>
    public async Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancel = default)
    {
        string settings = Settings(environment, log);
        // xUnit gives a test the same id in each target framework's assembly: its assembly tells them apart.
        Dictionary<(Guid, string), TestItem> running = [];
        List<TestCase> chosen = [];
        foreach (TestItem test in tests)
        {
            if (cases.Of(test.Key) is { } found && running.TryAdd((found.Id, found.Source), test))
            {
                chosen.Add(found);
            }
        }
        Run handler = new(running, outcomes, log);
        using (cancel.Register(() => IdeErrors.Observe(Task.Run(console.CancelTestRun))))
        {
            // Not held: a debugged run waits here while stopped at a breakpoint.
            await Told(Task.Run(() =>
            {
                if (debugger is null)
                {
                    console.RunTests(chosen, settings, new TestPlatformOptions(), handler);
                }
                else
                {
                    console.RunTestsWithCustomTestHost(chosen, settings, new TestPlatformOptions(), handler, new DebugLauncher(debugger, environment));
                }
            }));
            return await handler.Done.Task;
        }
    }

    /// <summary>A run's settings: VSTest's design mode, as an IDE runs it, and the environment, each variable an element as
    /// VSTest reads them; a name XML cannot hold is left out and said.</summary>
    private static string Settings(IReadOnlyDictionary<string, string>? environment, Action<string>? log)
    {
        if (environment is not { Count: > 0 })
        {
            return RunSettings;
        }
        XElement variables = new("EnvironmentVariables");
        foreach ((string name, string value) in environment)
        {
            try
            {
                variables.Add(new XElement(XmlConvert.VerifyName(name), value));
            }
            catch (XmlException)
            {
                log?.Invoke($"The environment variable {name} cannot be given to the tests: VSTest names each in XML, which cannot hold that name");
            }
        }
        return new XElement("RunSettings", new XElement("RunConfiguration", new XElement("DesignMode", "true"), variables)).ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>VSTest's failure told as the IDE's: the controller knows no VSTest type.</summary>
    private static async Task Told(Task work)
    {
        try
        {
            await work;
        }
        catch (TestPlatformException failed)
        {
            throw new InvalidOperationException(failed.Message, failed);
        }
    }

    public void Dispose()
    {
        try
        {
            console.EndSession();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>VSTest's test cases told as the IDE's tests, each with the project and framework of the assembly it is
    /// in, and kept to be run by the key of the test told. VSTest names an assembly and a source file as it was given or
    /// as the PDB says: the tree's are canonical.</summary>
    private IReadOnlyList<TestItem> Tell(IReadOnlyList<TestAssembly> assemblies, IReadOnlyList<TestCase> found)
    {
        Dictionary<string, TestAssembly> bySource = assemblies.GroupBy(a => a.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        IEnumerable<(TestAssembly, TestCase)> inAssemblies = found
            .Select(test => (Assembly: bySource.GetValueOrDefault(cases.Canonical(test.Source)), Test: test))
            .Where(pair => pair.Assembly is not null)
            .Select(pair => (pair.Assembly!, pair.Test));
        return cases.Tell(assemblies, inAssemblies, test => test.Id.ToString(), (assembly, test) => new TestItem(assembly.Project.Path, test.FullyQualifiedName, test.DisplayName)
        {
            Framework = assembly.Framework,
            Row = TestItem.RowOf(test.FullyQualifiedName, test.DisplayName),
            Path = string.IsNullOrEmpty(test.CodeFilePath) ? null : cases.Canonical(test.CodeFilePath),
            Line = test.LineNumber,
            Traits = [.. test.Traits.Select(t => new KeyValuePair<string, string>(t.Name, t.Value))],
        }, (_, test) => test);
    }

    private static TestOutcome Outcome(TestItem test, VsResult result) => new(test, StateOf(result.Outcome), result.Duration)
    {
        DisplayName = result.DisplayName ?? result.TestCase.DisplayName,
        Message = result.ErrorMessage,
        StackTrace = result.ErrorStackTrace,
        Output = string.Concat(result.Messages.Select(m => m.Text)) is { Length: > 0 } output ? output : null,
        // An adapter writes each attachment to a file and names it by a URI, the test's name for it as its description.
        Attachments = [.. result.Attachments.SelectMany(set => set.Attachments).Select(a =>
            TestAttachment.Of(a.Description ?? string.Empty, a.Uri.IsAbsoluteUri && a.Uri.IsFile ? a.Uri.LocalPath : a.Uri.OriginalString))],
    };

    private static TestState StateOf(VsOutcome outcome) => outcome switch
    {
        VsOutcome.Passed => TestState.Passed,
        VsOutcome.Failed => TestState.Failed,
        VsOutcome.Skipped => TestState.Skipped,
        _ => TestState.NotRun,
    };

    private sealed class Discovery(Action<string>? log) : ITestDiscoveryEventsHandler2
    {
        private readonly List<TestCase> found = [];

        public TaskCompletionSource<IReadOnlyList<TestCase>> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HandleDiscoveredTests(IEnumerable<TestCase>? discoveredTestCases)
        {
            lock (found)
            {
                found.AddRange(discoveredTestCases ?? []);
            }
        }

        public void HandleDiscoveryComplete(DiscoveryCompleteEventArgs discoveryCompleteEventArgs, IEnumerable<TestCase>? lastChunk)
        {
            HandleDiscoveredTests(lastChunk);
            lock (found)
            {
                IdeLog.Write(IdeLog.Tests, $"Discovery {(discoveryCompleteEventArgs.IsAborted ? "aborted" : "complete")}: {Plural.Of(found.Count, "test")} found");
                Done.TrySetResult([.. found]);
            }
        }

        public void HandleLogMessage(TestMessageLevel level, string? message) => Said(level, message, log);

        public void HandleRawMessage(string rawMessage) => Raw(rawMessage);
    }

    private sealed class Run(IReadOnlyDictionary<(Guid, string), TestItem> running, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log) : ITestRunEventsHandler
    {
        private readonly List<TestOutcome> all = [];

        public TaskCompletionSource<RunSummary> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HandleTestRunStatsChange(TestRunChangedEventArgs? testRunChangedArgs)
        {
            List<TestOutcome> batch = [.. (testRunChangedArgs?.NewTestResults ?? [])
                .Select(r => running.TryGetValue((r.TestCase.Id, r.TestCase.Source), out TestItem? test) ? Outcome(test, r) : null)
                .OfType<TestOutcome>()];
            if (batch.Count == 0)
            {
                return;
            }
            lock (all)
            {
                all.AddRange(batch);
            }
            outcomes(batch);
        }

        public void HandleTestRunComplete(TestRunCompleteEventArgs testRunCompleteArgs, TestRunChangedEventArgs? lastChunkArgs, ICollection<AttachmentSet>? runContextAttachments, ICollection<string>? executorUris)
        {
            HandleTestRunStatsChange(lastChunkArgs);
            if (testRunCompleteArgs.Error is { } error)
            {
                log?.Invoke(error.Message);
            }
            List<TestOutcome> told;
            lock (all)
            {
                told = [.. all];
            }
            string ended = testRunCompleteArgs.IsCanceled ? "stopped" : testRunCompleteArgs.IsAborted ? "aborted" : "complete";
            IdeLog.Write(IdeLog.Tests, $"Run {ended}: {Plural.Of(told.Count, "result")} in {testRunCompleteArgs.ElapsedTimeInRunningTests.TotalSeconds:0.00} s"
                + (testRunCompleteArgs.Error is { } failed ? $": {failed.Message}" : string.Empty));
            Done.TrySetResult(new RunSummary(testRunCompleteArgs.ElapsedTimeInRunningTests, testRunCompleteArgs.IsCanceled, testRunCompleteArgs.IsAborted, testRunCompleteArgs.Error?.Message) { Outcomes = told });
        }

        public int LaunchProcessWithDebuggerAttached(TestProcessStartInfo testProcessStartInfo) => -1;

        public void HandleLogMessage(TestMessageLevel level, string? message) => Said(level, message, log);

        public void HandleRawMessage(string rawMessage) => Raw(rawMessage);
    }

    /// <summary>VSTest's way to debug: it hands over the test host it would start, and the IDE starts it under its
    /// debugger instead, or attaches to the one it started. VSTest waits on its own thread for each answer.</summary>
    private sealed class DebugLauncher(ITestDebugLauncher debugger, IReadOnlyDictionary<string, string>? environment) : ITestHostLauncher3
    {
        public bool IsDebug => true;

        public int LaunchTestHost(TestProcessStartInfo defaultTestHostStartInfo) => LaunchTestHost(defaultTestHostStartInfo, CancellationToken.None);

        public int LaunchTestHost(TestProcessStartInfo defaultTestHostStartInfo, CancellationToken cancellationToken) =>
            debugger.LaunchAsync(new TestHostStart(
                defaultTestHostStartInfo.FileName ?? throw new InvalidOperationException("VSTest gave no test host"),
                Passenger.App.Run.CommandLine.Split(defaultTestHostStartInfo.Arguments),
                defaultTestHostStartInfo.WorkingDirectory,
                EnvironmentOf(defaultTestHostStartInfo)))
            .GetAwaiter().GetResult();

        private Dictionary<string, string> EnvironmentOf(TestProcessStartInfo start)
        {
            Dictionary<string, string> merged = start.EnvironmentVariables?.Where(variable => variable.Value is not null)
                .ToDictionary(variable => variable.Key, variable => variable.Value!) ?? [];
            foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
            {
                merged[name] = value;
            }
            return merged;
        }

        public bool AttachDebuggerToProcess(int pid) => AttachDebuggerToProcess(pid, CancellationToken.None);

        // VSTest starts a .NET test host itself and waits on this before it runs a test in it.
        public bool AttachDebuggerToProcess(int pid, CancellationToken cancellationToken) => debugger.AttachAsync(pid).GetAwaiter().GetResult();

        public bool AttachDebuggerToProcess(AttachDebuggerInfo attachDebuggerInfo, CancellationToken cancellationToken) =>
            AttachDebuggerToProcess(attachDebuggerInfo.ProcessId, cancellationToken);
    }

    /// <summary>What VSTest or a test adapter said: a warning or an error is notable.</summary>
    private static void Said(TestMessageLevel level, string? message, Action<string>? log)
    {
        if (message is not null)
        {
            TestRunners.Said(level != TestMessageLevel.Informational, level.ToString(), message, log);
        }
    }

    /// <summary>A message from vstest.console as it came, for the trace.</summary>
    private static void Raw(string message)
    {
        if (IdeLog.Traces)
        {
            IdeLog.Write(IdeLog.Tests, $"<-- {message}");
        }
    }
}
