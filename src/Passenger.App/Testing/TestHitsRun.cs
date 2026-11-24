using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Passenger.Core;
using Passenger.TestHits;

namespace Passenger.App.Testing;

/// <summary>A run that maps which tests run which lines, over a test run's own copy of the code it tests: the copy's modules
/// instrumented once by Coverlet's instrumenter (src/Passenger.CoverageMap, <see cref="InstrumentAsync"/>), then its tests run
/// one at a time in one test host, Passenger's hook in it (src/Passenger.TestHits, carried under testhits/) handing over what
/// each test counted before the next begins (<see cref="RunAsync"/>), read as the lines it stands for. Its work is a
/// worker's; what it tells comes on any thread.</summary>
public static class TestHitsRun
{
    /// <summary>How long the hook has to connect once its test host has answered, before the run is taken for one it never reached.</summary>
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(30);

    /// <summary>The hook beside Passenger, as its build carries it.</summary>
    public static string HookPath => Path.Combine(AppContext.BaseDirectory, "testhits", "Passenger.TestHits.dll");

    /// <summary>The instrumenting, beside the Coverlet it drives.</summary>
    public static string Instrumenter => Path.Combine(CarriedFiles.Folder("coverage", "console"), "Passenger.CoverageMap.dll");

    /// <summary>The modules beside a test assembly in a run's own copy instrumented in place (those named alone, where any
    /// are), the files their counts would be written to in <paramref name="hits"/>, and the map of where each count stands;
    /// null, with why said, where it failed.</summary>
    public static async Task<InstrumentedCode?> InstrumentAsync(string assembly, IReadOnlyList<string> modules, string hits, Action<string> log, CancellationToken cancel)
    {
        Directory.CreateDirectory(hits);
        string map = Path.Combine(hits, $"{Path.GetFileNameWithoutExtension(assembly)}.{Guid.NewGuid():N}.map.json");
        // Coverlet names each module's counts' file in the temporary folder of the process instrumenting it.
        Dictionary<string, string> temporary = new(StringComparer.Ordinal) { ["TMPDIR"] = hits, ["TMP"] = hits, ["TEMP"] = hits };
        ChildResult result = await ChildProcesses.RunAsync(ChildProcesses.WithVariables(ChildProcesses.HelperCommand(Instrumenter, [assembly, map, .. modules], hits), temporary),
            cancel: cancel);
        foreach (string line in result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            log(line);
        }
        if (!File.Exists(map))
        {
            log($"{Path.GetFileName(assembly)}'s code could not be instrumented ({ExitCodes.Said(result.ExitCode)})");
            return null;
        }
        try
        {
            return InstrumentedCode.Of(await File.ReadAllBytesAsync(map, cancel), Canonical);
        }
        finally
        {
            File.Delete(map);
        }
    }

    /// <summary>Tests of one test host run one at a time over code <paramref name="code"/> maps, each test's outcomes told as
    /// they come and the lines it ran, by canonical source file, told once it ends (<paramref name="ran"/>); the hook given the
    /// host through its environment. A host the hook never reaches runs its tests unmapped, said once.</summary>
    public static async Task<RunSummary> RunAsync(TestingPlatformRunner runner, IReadOnlyList<TestItem> tests, InstrumentedCode code, IReadOnlyDictionary<string, string> environment,
        Action<IReadOnlyList<TestOutcome>> outcomes, Action<TestItem, Dictionary<string, HashSet<int>>> ran, Action<string> log, CancellationToken cancel)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Dictionary<string, string> given = new(environment, StringComparer.Ordinal)
        {
            [Variables.Port] = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
            [RuntimeVariables.StartupHooks] = StartupHooks.First(environment, HookPath),
        };
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<TcpClient> accepting = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        Hook? hook = null;
        try
        {
            return await runner.RunEachAsync(tests, outcomes, async () =>
            {
                if (hook is null && await Task.WhenAny(accepting, Task.Delay(ConnectLimit, cancel)) == accepting && accepting.IsCompletedSuccessfully)
                {
                    hook = new Hook(accepting.Result);
                    // What the host ran as it started is no test's.
                    await hook.TakeAsync(cancel);
                }
                else if (hook is null)
                {
                    log("The test host never reached Passenger's hook, so which lines its tests ran is not known");
                }
            }, async test => ran(test, hook is null ? [] : code.Ran(await hook.TakeAsync(cancel))), given, log, cancel);
        }
        finally
        {
            await stop.CancelAsync();
            hook?.Dispose();
            if (accepting.IsCompletedSuccessfully && hook is null)
            {
                accepting.Result.Dispose();
            }
        }
    }

    /// <summary>Tests of VSTest's run over code <paramref name="code"/> maps, one at a time in one test host, each test's
    /// outcomes told as they come and the lines it ran, by canonical source file, told under its full name as it ends
    /// (<paramref name="ran"/>): the hook given the host among its in-process data collectors through the run's settings, which
    /// ask each framework to run its tests one at a time. A host the hook never reaches runs its tests unmapped.</summary>
    public static async Task<RunSummary> RunVsTestAsync(ITestRunner runner, IReadOnlyList<TestItem> tests, InstrumentedCode code, string folder,
        Action<IReadOnlyList<TestOutcome>> outcomes, Action<string, Dictionary<string, HashSet<int>>> ran, Action<string> log, CancellationToken cancel)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<TcpClient> accepting = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        Task hearing = HearAsync(accepting, code, ran, stop.Token);
        CoverageRun settings = new(folder) { Settings = root => Into(root, port) };
        try
        {
            RunSummary summary = await runner.RunAsync(tests, outcomes, log, coverage: settings, cancel: cancel);
            if (accepting.IsCompletedSuccessfully)
            {
                // The host's last test is told before it ends; its connection's end ends the hearing.
                await Task.WhenAny(hearing, Task.Delay(ConnectLimit, cancel));
            }
            else if (!cancel.IsCancellationRequested)
            {
                log("The test host never reached Passenger's hook, so which lines its tests ran is not known");
            }
            return summary;
        }
        finally
        {
            await stop.CancelAsync();
            await hearing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>What each test host's hook tells, read until its connection ends: a test's full name, then its counts, each a line.</summary>
    private static async Task HearAsync(Task<TcpClient> accepting, InstrumentedCode code, Action<string, Dictionary<string, HashSet<int>>> ran, CancellationToken cancel)
    {
        using TcpClient client = await accepting;
        using StreamReader reader = new(client.GetStream(), new UTF8Encoding(false));
        while (await reader.ReadLineAsync(cancel) is { } named && await reader.ReadLineAsync(cancel) is { } counts)
        {
            ran(JsonSerializer.Deserialize<string>(named) ?? string.Empty, code.Ran(Counted(counts)));
        }
    }

    /// <summary>VSTest's settings given the hook among the in-process data collectors, with the port it tells Passenger on, and
    /// asking xUnit, MSTest and NUnit to run their tests one at a time, so what is counted between two ends is one test's.</summary>
    private static void Into(XElement root, int port)
    {
        XElement collectors = Child(Child(root, "InProcDataCollectionRunSettings"), "InProcDataCollectors");
        collectors.Add(new XElement("InProcDataCollector", new XAttribute("friendlyName", "Passenger test hits"), new XAttribute("enabled", "True"),
            // VSTest finds the collector by its type's full name, its assembly's version within it.
            new XAttribute("assemblyQualifiedName", $"Passenger.TestHits.TestHitsCollector, {AssemblyName.GetAssemblyName(HookPath).FullName}"),
            new XAttribute("codebase", HookPath),
            new XElement("Configuration", new XElement("Port", port.ToString(CultureInfo.InvariantCulture)))));
        XElement xunit = Child(root, "xUnit");
        xunit.SetElementValue("ParallelizeTestCollections", "false");
        xunit.SetElementValue("MaxParallelThreads", "1");
        XElement mstest = Child(root, "MSTest");
        mstest.SetElementValue("DisableParallelization", "true");
        Child(mstest, "Parallelize").SetElementValue("Workers", "1");
        Child(root, "NUnit").SetElementValue("NumberOfTestWorkers", "0");

        static XElement Child(XElement parent, string name)
        {
            if (parent.Element(name) is not { } child)
            {
                parent.Add(child = new XElement(name));
            }
            return child;
        }
    }

    /// <summary>A take's line read: each tracker's file and the places it counted.</summary>
    private static Dictionary<string, int[]> Counted(string line)
    {
        Dictionary<string, int[]> counted = new(StringComparer.Ordinal);
        using JsonDocument read = JsonDocument.Parse(line);
        foreach (JsonProperty module in read.RootElement.EnumerateObject())
        {
            counted[module.Name] = module.Value.ValueKind == JsonValueKind.Array
                ? [.. module.Value.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetInt32())] : [];
        }
        return counted;
    }

    private static string Canonical(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && File.Exists(path) ? PathNames.Canonical(path) : path;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>The hook's connection: what each module's tracker counted since the last take, by the file it names, asked
    /// and read a line each.</summary>
    private sealed class Hook(TcpClient client) : IDisposable
    {
        private readonly StreamReader reader = new(client.GetStream(), new UTF8Encoding(false));
        private readonly StreamWriter writer = new(client.GetStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };

        public async Task<Dictionary<string, int[]>> TakeAsync(CancellationToken cancel)
        {
            await writer.WriteLineAsync(Variables.Take.AsMemory(), cancel);
            return Counted(await reader.ReadLineAsync(cancel) ?? throw new IOException("The test host's hook went before it answered"));
        }

        public void Dispose()
        {
            reader.Dispose();
            writer.Dispose();
            client.Dispose();
        }
    }
}
