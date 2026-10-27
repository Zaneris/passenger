using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.App.Testing;

/// <summary>Tests found and run through Microsoft.Testing.Platform's own protocol, for a test project that is a Testing
/// Platform application (xUnit v3, MSTest's runner, TUnit): the built program started with <c>--server</c> and the port
/// it connects back to, then asked over JSON-RPC to find or to run tests, one request a process. Its test nodes stay in
/// here, each told as the IDE's own test and its results as outcomes. What a test host warns of or fails with is in the
/// logs folder's vstest.log, and with Trace protocol messages on every message it sends.</summary>
public sealed class TestingPlatformRunner : ITestRunner
{
    /// <summary>The assembly attribute the platform's build gives a Testing Platform application (IsTestingPlatformApplication).</summary>
    private const string ApplicationMetadata = "Microsoft.Testing.Platform.Application";

    /// <summary>How long a test host has to connect once started, before it is taken for one that never will.</summary>
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromMinutes(1);

    /// <summary>How long a test host told to stop has to end what it runs, and then to exit, before it is ended.</summary>
    private static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(3);

    /// <summary>How long a test host done with its request has to tell its last results and exit before it is ended.</summary>
    private static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(10);

    /// <summary>The test nodes discovered, by the key of the test each was told as.</summary>
    private readonly TestCatalog<Found> found = new();

    public bool IsUsable => true;

    /// <summary>Whether a built assembly is a Testing Platform application, whose own program finds and runs its tests: the
    /// platform's build says so in the assembly (since 1.8); one built by an older platform is a program that references it.</summary>
    public static bool IsApplication(string assembly)
    {
        if (AssemblyFacts.Read(assembly) is not { } facts)
        {
            return false;
        }
        foreach ((string key, string? value) in facts.Metadata)
        {
            if (key == ApplicationMetadata)
            {
                return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }
        Version? platform = facts.References.Where(r => r.Name == "Microsoft.Testing.Platform").Select(r => r.Version).FirstOrDefault();
        return platform is { Major: 1, Minor: < 8 } && facts.HasEntryPoint;
    }

    /// <summary>The tests in built Testing Platform applications, each asked in its own process, all at once. One whose
    /// program fails to start or answer is said in the log and has no tests; canceled, every one is ended.</summary>
    public async Task<IReadOnlyList<TestItem>> DiscoverAsync(IReadOnlyList<TestAssembly> assemblies, Action<string>? log = null, CancellationToken cancel = default)
    {
        (TestAssembly Assembly, IReadOnlyList<PlatformNode> Nodes)[] asked = await Task.WhenAll(assemblies.Select(a => DiscoverIn(a, log, cancel)));
        cancel.ThrowIfCancellationRequested();
        return await BackgroundWork.Run(() => Tell(asked));
    }

    private static async Task<(TestAssembly, IReadOnlyList<PlatformNode>)> DiscoverIn(TestAssembly assembly, Action<string>? log, CancellationToken cancel)
    {
        List<PlatformNode> nodes = [];
        try
        {
            await using Host host = await Host.StartAsync(assembly, null, null, log, cancel);
            bool asked = await host.RequestAsync("testing/discoverTests", null, changed =>
            {
                lock (nodes)
                {
                    nodes.AddRange(changed);
                }
            }, cancel);
            if (!asked)
            {
                cancel.ThrowIfCancellationRequested();
            }
        }
        catch (TestHostFailed failed) when (!cancel.IsCancellationRequested)
        {
            IdeLog.Write(IdeLog.Tests, $"Discovery failed: {failed.Message}");
            log?.Invoke(failed.Message);
            return (assembly, []);
        }
        lock (nodes)
        {
            int count = nodes.Count(n => n.IsTest);
            IdeLog.Write(IdeLog.Tests, $"Discovery complete in {Path.GetFileName(assembly.Path)} ({assembly.Framework}): {Plural.Of(count, "test")} found");
            return (assembly, [.. nodes]);
        }
    }

    /// <summary>Runs tests it discovered, an assembly at a time, each in a test host of its own (under the debugger when a
    /// launcher is given) with the environment given; their outcomes told a notification at a time as they come. Canceled,
    /// the test host is asked to stop and given a moment, then ended, keeping the outcomes of what had run.</summary>
    public async Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancel = default)
    {
        RunSummary summary = new(TimeSpan.Zero, false, false, null);
        foreach (IGrouping<string, (TestItem Test, Found Node)> assembly in tests
            .Select(t => (Test: t, Node: found.Of(t)!))
            .Where(t => t.Node is not null)
            .GroupBy(t => t.Node.Assembly.Path, StringComparer.Ordinal))
        {
            if (cancel.IsCancellationRequested)
            {
                summary = summary with { Canceled = true };
                break;
            }
            RunSummary part = await RunIn(assembly.First().Node.Assembly, [.. assembly], outcomes, log, debugger, environment, cancel);
            summary = summary.And(part);
            if (part.Canceled)
            {
                break;
            }
        }
        IdeLog.Write(IdeLog.Tests, $"Run {(summary.Canceled ? "stopped" : summary.Aborted ? "aborted" : "complete")}: {Plural.Of(summary.Outcomes.Count, "result")} in {summary.Elapsed.TotalSeconds:0.00} s"
            + (summary.Error is { } failed ? $": {failed}" : string.Empty));
        return summary;
    }

    private static async Task<RunSummary> RunIn(TestAssembly assembly, IReadOnlyList<(TestItem Test, Found Node)> tests, Action<IReadOnlyList<TestOutcome>> outcomes,
        Action<string>? log, ITestDebugLauncher? debugger, IReadOnlyDictionary<string, string>? environment, CancellationToken cancel)
    {
        Dictionary<string, TestItem> byUid = new(tests.Count, StringComparer.Ordinal);
        List<Found> chosen = new(tests.Count);
        foreach ((TestItem test, Found node) in tests)
        {
            if (byUid.TryAdd(node.Uid, test))
            {
                chosen.Add(node);
            }
        }
        List<TestOutcome> all = [];
        long began = Stopwatch.GetTimestamp();
        try
        {
            await using Host host = await Host.StartAsync(assembly, environment, debugger, log, cancel);
            bool ran = await host.RequestAsync("testing/runTests", chosen, changed =>
            {
                List<TestOutcome> batch = [];
                foreach (PlatformNode node in changed)
                {
                    if (byUid.TryGetValue(node.Uid, out TestItem? test) && Outcome(test, node) is { } outcome)
                    {
                        batch.Add(outcome);
                    }
                }
                if (batch.Count == 0)
                {
                    return;
                }
                lock (all)
                {
                    all.AddRange(batch);
                }
                outcomes(batch);
            }, cancel);
            return Summary(!ran, null);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return Summary(true, null);
        }
        catch (TestHostFailed failed)
        {
            log?.Invoke(failed.Message);
            return Summary(cancel.IsCancellationRequested, failed.Message);
        }

        RunSummary Summary(bool canceled, string? error)
        {
            lock (all)
            {
                return new RunSummary(Stopwatch.GetElapsedTime(began), canceled, error is not null, error) { Outcomes = [.. all] };
            }
        }
    }

    public void Dispose()
    {
    }

    /// <summary>The test nodes found told as the IDE's tests, each with the project and framework of its assembly, and kept
    /// to be run by the key of the test told. The platform names a source file as the compiler was given it: the tree's are
    /// canonical.</summary>
    private IReadOnlyList<TestItem> Tell((TestAssembly Assembly, IReadOnlyList<PlatformNode> Nodes)[] asked) =>
        found.Tell(asked.Select(a => a.Assembly), asked.SelectMany(a => a.Nodes.Where(n => n.IsTest).Select(n => (a.Assembly, n))), node => node.Uid,
            (assembly, node) => new TestItem(assembly.Project.Path, node.FullyQualifiedName, node.DisplayName)
            {
                Framework = assembly.Framework,
                Row = TestItem.RowOf(node.FullyQualifiedName, node.DisplayName),
                Path = node.File is { Length: > 0 } file ? found.Canonical(file) : null,
                Line = node.Line,
                Traits = node.Traits,
            },
            (assembly, node) => new Found(assembly, node.Uid, node.DisplayName));

    /// <summary>A node's result as the IDE's outcome; null for an update that is not one (discovered, in progress, an attempt
    /// a retry replaces, or one the stop canceled, which stays not run).</summary>
    private static TestOutcome? Outcome(TestItem test, PlatformNode node)
    {
        TestState? state = node.State switch
        {
            "passed" => TestState.Passed,
            "failed" or "error" or "timed-out" => TestState.Failed,
            "skipped" => TestState.Skipped,
            _ => null,
        };
        return state is { } outcome && node.IsTest && !node.Superseded
            ? new TestOutcome(test, outcome, node.Duration)
            {
                DisplayName = node.DisplayName,
                Message = node.Message,
                StackTrace = node.StackTrace,
                Output = node.Output,
                Attachments = node.Attachments,
            }
            : null;
    }

    /// <summary>What the platform said of a test host: a warning, an error or worse is notable.</summary>
    private static void Said(string level, string message, Action<string>? log) =>
        TestRunners.Said(level is "Warning" or "Error" or "Critical", level, message, log);

    /// <summary>A test the platform discovered: the node it is run by, in the assembly it is in.</summary>
    private sealed record Found(TestAssembly Assembly, string Uid, string DisplayName);

    /// <summary>A discovery's or a run's parameters: the tests a run runs, by the nodes they were found as, then the
    /// request's id; written straight from the tests found.</summary>
    [JsonConverter(typeof(Writer))]
    private sealed record TestRequest(IReadOnlyList<Found>? Tests, Guid Run)
    {
        private sealed class Writer : JsonConverter<TestRequest>
        {
            public override TestRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                throw new NotSupportedException("A test request is only written");

            public override void Write(Utf8JsonWriter writer, TestRequest value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                if (value.Tests is { } tests)
                {
                    writer.WriteStartArray("tests"u8);
                    foreach (Found test in tests)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("uid"u8, test.Uid);
                        writer.WriteString("display-name"u8, test.DisplayName);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteString("runId"u8, value.Run.ToString());
                writer.WriteEndObject();
            }
        }
    }

    /// <summary>A test host that could not be started or asked, in words for the person.</summary>
    private sealed class TestHostFailed(string message) : Exception(message);

    /// <summary>One test host: its program started by the runner (or by the debugger), connected back, initialized, asked
    /// one thing, then told to exit; ended if it does not. What it writes is kept, the last of it, to say why it failed.</summary>
    private sealed class Host : IAsyncDisposable
    {
        /// <summary>The most a test host's own output kept, for what it said before it failed.</summary>
        private const int Kept = 8 * 1024;

        private readonly string name;
        private readonly Process? process;
        private readonly int processId;
        private readonly TcpClient client;
        private readonly JsonRpcPeer peer;
        private readonly Notifications target;
        private readonly StringBuilder said;
        private bool stopped;

        private Host(string name, Process? process, int processId, TcpClient client, JsonRpcPeer peer, Notifications target, StringBuilder said)
        {
            this.name = name;
            this.process = process;
            this.processId = processId;
            this.client = client;
            this.peer = peer;
            this.target = target;
            this.said = said;
        }

        /// <summary>Starts an assembly's program as a server that connects to a port of the IDE's own, and initializes it.</summary>
        public static async Task<Host> StartAsync(TestAssembly assembly, IReadOnlyDictionary<string, string>? environment, ITestDebugLauncher? debugger,
            Action<string>? log, CancellationToken cancel)
        {
            string name = $"{Path.GetFileNameWithoutExtension(assembly.Path)} ({assembly.Framework})";
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            List<string> arguments =
            [
                "exec", assembly.Path, "--server",
                "--client-host", IPAddress.Loopback.ToString(),
                "--client-port", ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
            ];
            string folder = Path.GetDirectoryName(assembly.Path)!;
            StringBuilder said = new();
            Process? process = null;
            int id;
            if (debugger is null)
            {
                ProcessStartInfo start = ChildProcesses.Command(Dotnet.Host(), arguments, folder);
                foreach ((string variable, string value) in environment ?? new Dictionary<string, string>())
                {
                    start.Environment[variable] = value;
                }
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                try
                {
                    process = await ChildProcesses.StartAsync(start);
                }
                catch (System.ComponentModel.Win32Exception failed)
                {
                    throw new TestHostFailed($"{name} could not start: {failed.Message}");
                }
                process.OutputDataReceived += (_, line) => Keep(said, line.Data);
                process.ErrorDataReceived += (_, line) => Keep(said, line.Data);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                id = process.Id;
            }
            else
            {
                id = await debugger.LaunchAsync(new TestHostStart(Dotnet.Host(), arguments, folder, environment ?? new Dictionary<string, string>()));
            }
            TcpClient client;
            try
            {
                client = await AcceptAsync(listener, process, id, name, said, cancel);
            }
            catch
            {
                End(process, id);
                process?.Dispose();
                throw;
            }
            Notifications target = new(name, log);
            // What the test host sends is handled in the order it came, so its run's end is told after its last result.
            JsonRpcPeer peer = new(client.GetStream());
            peer.On<TestUpdate>("testing/testUpdates/tests", target.Tests);
            peer.On<PlatformLog>("client/log", target.Log);
            peer.On<PlatformLog>("client/showMessage", target.Log);
            peer.On<TracedJson>("telemetry/update", update => Trace("<--", "telemetry/update", update?.Json));
            peer.On<TracedJson>("testing/testUpdates/attachments", update => Trace("<--", "testing/testUpdates/attachments", update?.Json));
            peer.Start();
            Host host = new(name, process, id, client, peer, target, said);
            try
            {
                JsonObject initialize = new()
                {
                    ["processId"] = Environment.ProcessId,
                    ["clientInfo"] = new JsonObject { ["name"] = "Passenger", ["version"] = Product.Version },
                    // Stateful, as an IDE's test window is, which the frameworks run in their design mode for.
                    ["capabilities"] = new JsonObject { ["testing"] = new JsonObject { ["debuggerProvider"] = false, ["isStateful"] = true } },
                };
                Trace("-->", "initialize", initialize);
                await host.AskAsync("initialize", initialize, tellCanceled: false, cancel);
            }
            catch
            {
                host.stopped = true;
                await host.DisposeAsync();
                throw;
            }
            return host;
        }

        /// <summary>Asks for a discovery or a run, each test node it changes handed over a notification at a time, until the
        /// platform says the request has told all it will. False when it was canceled first: the test host is then given a
        /// moment to stop, what it reports meanwhile still handed over.</summary>
        public async Task<bool> RequestAsync(string method, IReadOnlyList<Found>? tests, Action<IReadOnlyList<PlatformNode>> changed, CancellationToken cancel)
        {
            Guid run = Guid.NewGuid();
            TestRequest parameters = new(tests, run);
            Task told = target.Expect(run, changed);
            Trace("-->", method, parameters);
            try
            {
                // Canceled, the platform is sent $/cancelRequest.
                await AskAsync(method, parameters, tellCanceled: true, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                stopped = true;
                await Task.WhenAny(told, Task.Delay(StopLimit, CancellationToken.None));
                return false;
            }
            // The answer can overtake the notifications before it: the run's end is told after the last of them.
            if (await Task.WhenAny(told, Task.Delay(ExitLimit, CancellationToken.None)) != told)
            {
                IdeLog.Write(IdeLog.Tests, $"{name} answered {method} without saying its updates had ended");
            }
            return true;
        }

        /// <summary>Tells the test host to exit and waits a while for it; one that does not is ended, unless the debugger
        /// has it and it was not stopped (the person may be stopped in it).</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await peer.TellAsync("exit", new JsonObject());
            }
            catch (JsonRpcFailed)
            {
                // Gone already.
            }
            TimeSpan limit = stopped ? StopLimit : ExitLimit;
            if (!await Exited(limit) && (process is not null || stopped))
            {
                IdeLog.Write(IdeLog.Tests, $"{name} (process {processId}) had not exited {limit.TotalSeconds:0} s after it was told to, and is ended");
                End(process, processId);
            }
            peer.Dispose();
            client.Dispose();
            process?.Dispose();
        }

        /// <summary>Asks the test host, its answer in the trace; one it refuses or never gives fails the host.</summary>
        private async Task AskAsync(string method, object parameters, bool tellCanceled, CancellationToken cancel)
        {
            try
            {
                TracedJson? answer = await peer.AskAsync<TracedJson>(method, parameters, tellCanceled, cancel);
                Trace("<--", method, answer?.Json);
            }
            catch (JsonRpcFailed failed)
            {
                throw await FailedAsync(failed.Refused ? $"{name} refused {method}: {failed.Message}" : $"{name} ended before it answered {method}");
            }
        }

        private async Task<bool> Exited(TimeSpan limit)
        {
            long began = Stopwatch.GetTimestamp();
            while (Alive(process, processId))
            {
                if (Stopwatch.GetElapsedTime(began) >= limit)
                {
                    return false;
                }
                await Task.Delay(50, CancellationToken.None);
            }
            return true;
        }

        /// <summary>Why the host failed, with what its program wrote last, once it has had a moment to end.</summary>
        private async Task<TestHostFailed> FailedAsync(string why)
        {
            if (process is not null && await Exited(TimeSpan.FromSeconds(1)))
            {
                why += $" (exit code {process.ExitCode})";
            }
            return new TestHostFailed(Written(why, said));
        }

        /// <summary>The test host's connection, once its program makes it; failed if the program ends first or, run by the
        /// runner, takes longer than <see cref="ConnectLimit"/> (under the debugger the person may be stopped in it).</summary>
        private static async Task<TcpClient> AcceptAsync(TcpListener listener, Process? process, int id, string name, StringBuilder said, CancellationToken cancel)
        {
            using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(giveUp.Token).AsTask();
            _ = accepting.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            long began = Stopwatch.GetTimestamp();
            try
            {
                while (!accepting.IsCompleted)
                {
                    await Task.WhenAny(accepting, Task.Delay(100, CancellationToken.None));
                    cancel.ThrowIfCancellationRequested();
                    if (!accepting.IsCompleted && !Alive(process, id))
                    {
                        string exit = string.Empty;
                        if (process is not null)
                        {
                            // What it wrote last is read to its end before it is quoted.
                            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                            exit = $" with exit code {process.ExitCode}";
                        }
                        throw new TestHostFailed(Written($"{name} ended{exit} before it connected", said));
                    }
                    if (!accepting.IsCompleted && process is not null && Stopwatch.GetElapsedTime(began) > ConnectLimit)
                    {
                        throw new TestHostFailed(Written($"{name} had not connected after {ConnectLimit.TotalSeconds:0} s", said));
                    }
                }
                return await accepting;
            }
            finally
            {
                giveUp.Cancel();
            }
        }

        private static bool Alive(Process? process, int id)
        {
            if (process is not null)
            {
                return !process.HasExited;
            }
            try
            {
                using Process running = Process.GetProcessById(id);
                return !running.HasExited;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Ends a test host: the runner's own with all it started, the debugger's alone.</summary>
        private static void End(Process? process, int id)
        {
            if (process is not null)
            {
                ChildProcesses.Kill(process);
                return;
            }
            try
            {
                using Process running = Process.GetProcessById(id);
                running.Kill();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It had ended.
            }
        }

        private static void Keep(StringBuilder said, string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (said)
            {
                said.Append(line).Append('\n');
                if (said.Length > Kept)
                {
                    said.Remove(0, said.Length - Kept);
                }
            }
        }

        private static string Written(string why, StringBuilder said)
        {
            lock (said)
            {
                string output = said.ToString().Trim();
                return output.Length == 0 ? why : $"{why}:\n{output}";
            }
        }
    }

    /// <summary>A message to or from a test host, in vstest.log while protocols are traced.</summary>
    private static void Trace(string direction, string method, string? json)
    {
        if (IdeLog.Traces)
        {
            IdeLog.Write(IdeLog.Tests, $"{direction} {method} {json ?? "null"}");
        }
    }

    private static void Trace(string direction, string method, object parameters)
    {
        if (IdeLog.Traces)
        {
            Trace(direction, method, JsonSerializer.Serialize(parameters));
        }
    }

    /// <summary>What a test host sends: the test nodes a request changes, which the request under way takes, until the
    /// platform's update with no changes ends them; what its output device says.</summary>
    private sealed class Notifications(string name, Action<string>? log)
    {
        private readonly Lock gate = new();
        private (Guid Run, Action<IReadOnlyList<PlatformNode>> Changed, TaskCompletionSource Ended)? expected;

        public Task Expect(Guid run, Action<IReadOnlyList<PlatformNode>> changed)
        {
            TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                expected = (run, changed, ended);
            }
            return ended.Task;
        }

        public void Tests(TestUpdate? update)
        {
            Trace("<--", "testing/testUpdates/tests", update?.Json);
            (Guid Run, Action<IReadOnlyList<PlatformNode>> Changed, TaskCompletionSource Ended)? request;
            lock (gate)
            {
                request = expected;
            }
            if (request is not { } asked || update?.Run != asked.Run)
            {
                return;
            }
            if (update.Changes is not { } changes)
            {
                asked.Ended.TrySetResult();
                return;
            }
            if (changes.Count > 0)
            {
                asked.Changed(changes);
            }
        }

        public void Log(PlatformLog? message)
        {
            if (message?.Message is { Length: > 0 } text)
            {
                Said(message.Level ?? "Information", $"{name}: {text}", log);
            }
        }
    }
}
