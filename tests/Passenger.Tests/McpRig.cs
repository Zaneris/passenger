using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.State;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What the MCP test classes share, an agent's side of them: Passengers started as its server (<c>--mcp</c>, or
/// <c>--mcp-attach</c> joining a person's window, which asks before it lets the agent in and before its first change), the
/// SDK's own client to them, the window's questions answered, a state folder of the test's own (<see cref="OwnState"/>),
/// the restored Sum project a class given RestoredFolders takes as a test first names it, and the folders a test made,
/// removed once the Passengers that worked in them have gone. Every exchange with Passenger has a time limit, and one that
/// runs out or fails ends the test naming it and quoting what Passenger said meanwhile.</summary>
public abstract class McpRig : IDisposable
{
    /// <summary>What names an installed Passenger for the agents to start in place of the build's.</summary>
    [IdentifierHome]
    public const string ExecutableVariable = "PASSENGER_EXECUTABLE";

    /// <summary>The build's, beside the tests, or an installed copy that <see cref="ExecutableVariable"/> names: CI's
    /// installer job has an agent debug through the Passenger its Windows installer installed.</summary>
    private static readonly string Passenger = Environment.GetEnvironmentVariable(ExecutableVariable) is { Length: > 0 } installed
        ? installed
        : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger");

    private readonly RestoredFolders? folders;
    private string? sum;
    private readonly List<string> errors = [];
    private readonly List<Process> passengers = [];
    private readonly List<string> made = [];

    protected McpRig()
    {
    }

    protected McpRig(RestoredFolders folders) => this.folders = folders;

    /// <summary>A state folder of the test's own: its own Passengers' (a joining agent's is the run's, where the windows'
    /// sockets are) and that of a window the test gives it to.</summary>
    protected string OwnState { get; } = TestFolders.Create("agents-");

    /// <summary>The test's own Sum project, restored, taken as a test first names it: most of the tests work in folders of
    /// their own and take none.</summary>
    protected string Folder => sum ??= (folders ?? throw new InvalidOperationException($"{GetType().Name} takes no Sum project: its fixture is not RestoredFolders"))
        .Take("mcp-", project =>
        {
            File.WriteAllText(Path.Combine(project, "Sum.csproj"),
                TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
            File.WriteAllText(Path.Combine(project, "Program.cs"),
                "int total = 0;\nfor (int i = 1; i <= 3; i++)\n{\n    total += Helper.Twice(i);\n}\nConsole.WriteLine($\"total {total}\");\nthrow new InvalidOperationException(\"the end\");\n");
            File.WriteAllText(Path.Combine(project, "Helper.cs"),
                "static class Helper\n{\n    public static int Twice(int value)\n    {\n        int unused = 1;\n        return value * 2;\n    }\n}\n");
            return project;
        });

    /// <summary>A folder a test made for itself, removed with the test once the Passengers that worked in it have gone.</summary>
    protected void DeleteAtEnd(string folder) => made.Add(folder);

    /// <summary>The Passenger the test started last.</summary>
    protected Process LastPassenger()
    {
        lock (passengers)
        {
            return passengers[^1];
        }
    }

    public virtual void Dispose()
    {
        lock (passengers)
        {
            foreach (Process passenger in passengers)
            {
                EndInput(passenger);
                if (!passenger.WaitForExit(MainWindow.EndLimit + TimeSpan.FromSeconds(5).Scaled()))
                {
                    ChildProcesses.Kill(passenger);
                }
                passenger.Dispose();
            }
        }
        if (sum is not null)
        {
            TestFolders.Delete(sum);
        }
        TestFolders.Delete(OwnState);
        foreach (string folder in made)
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Longer than any one exchange may take, for a wait on an agent's calls, whose own limits fail first and
    /// name the call.</summary>
    protected static readonly TimeSpan AgentLimit = TimeSpan.FromMinutes(6).Scaled();

    protected static CancellationToken Cancel => TestContext.Current.CancellationToken;

    protected static string ProcessId => Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The debugger engine's log of what Passenger debugs, which a failed call quotes.</summary>
    protected string EngineLog => Path.Combine(OwnState, "engine.log");

    /// <summary>Passenger started as an agent's MCP server (<see cref="StartPassenger"/>), and the agent's client to it.</summary>
    protected Task<McpClient> Start(IList<string> arguments, string? workingDirectory = null, Dictionary<string, string?>? environment = null, string? protocol = null,
        bool languageServer = true, string? executable = null) =>
        Connect(StartPassenger(arguments, workingDirectory, environment, languageServer, executable), arguments, protocol);

    /// <summary>Passenger started as the IDE starts a child, with a state folder of the test's own for its log (or, joining
    /// a window, the run's, where the windows' sockets are), the engine's log and any other change to the environment (null
    /// takes a variable away), its standard error kept; an <c>--mcp</c> Passenger whose agent asks nothing of C# starts no C#
    /// language server (<paramref name="languageServer"/>). Not the SDK's process transport, whose tree kill can hang this
    /// process. The agent going closes its input; <see cref="Dispose"/> ends the rest.</summary>
    protected Process StartPassenger(IList<string> arguments, string? workingDirectory = null, Dictionary<string, string?>? environment = null, bool languageServer = true,
        string? executable = null)
    {
        ProcessStartInfo start = ChildProcesses.Command(executable ?? Passenger, arguments, workingDirectory);
        start.Environment[StateStore.Variable] = arguments.Contains("--mcp-attach") ? TestRun.Settings : OwnState;
        start.Environment[global::Passenger.Dap.AdapterEnvironment.EngineLogVariable] = EngineLog;
        if (!languageServer)
        {
            start.Environment[McpHost.LanguageServerVariable] = "off";
        }
        foreach ((string name, string? value) in environment ?? new())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process passenger = ChildProcesses.Start(start);
        passenger.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    errors.Add(e.Data);
                }
            }
        };
        passenger.BeginErrorReadLine();
        lock (passengers)
        {
            passengers.Add(passenger);
        }
        return passenger;
    }

    /// <summary>An agent's client to a Passenger this test started, over its standard input and output, asking for the
    /// protocol version given (the SDK's latest else). Its probe of the latest waits as long as the start may, since one given
    /// up while the server answers it leaves the initialize it falls back to refused.</summary>
    protected Task<McpClient> Connect(Process passenger, IList<string> arguments, string? protocol = null) =>
        Exchange($"Starting passenger {string.Join(' ', arguments)}", TimeSpan.FromMinutes(1),
            cancel => McpClient.CreateAsync(new PassengerTransport(passenger), new McpClientOptions
            {
                ClientInfo = Agent,
                ProtocolVersion = protocol,
                InitializationTimeout = TimeSpan.FromMinutes(1).Scaled(),
                DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
            }, cancellationToken: cancel));

    /// <summary>The agent as its client names itself, which a window asking to let it in shows the person.</summary>
    private static readonly Implementation Agent = new() { Name = "test-agent", Title = "Test Agent", Version = "1.0" };

    /// <summary>The SDK's stream transport over a Passenger's input and output; the agent going closes the input, and
    /// Passenger ends on its own. What it tells a Passenger that has gone (killed, as a test may) is dropped: the SDK sends a
    /// call's cancellation unawaited, and its failure would be the next window test's caught error.</summary>
    private sealed class PassengerTransport(Process passenger) : IClientTransport
    {
        public string Name => "passenger";

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            new Session(new PeerTransport(await new StreamClientTransport(passenger.StandardInput.BaseStream, passenger.StandardOutput.BaseStream).ConnectAsync(cancellationToken)), passenger);

        private sealed class Session(ITransport inner, Process passenger) : ITransport
        {
            public string? SessionId => inner.SessionId;

            public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => inner.SendMessageAsync(message, cancellationToken);

            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                EndInput(passenger);
            }
        }
    }

    /// <summary>Closes a Passenger's input, which ends it as an agent going does; one that has gone already is left.</summary>
    protected static void EndInput(Process passenger)
    {
        try
        {
            passenger.StandardInput.Close();
        }
        catch (Exception gone) when (gone is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // It had gone, its pipe with it.
        }
    }

    /// <summary>A window over a folder whose person has let agents in; with no C# language server for a test whose agent
    /// asks nothing of C# code.</summary>
    protected static MainWindow AgentsWindow(string at, bool languageServer = true)
    {
        MainWindow window = Ui.OpenFolder(at, languageServer: languageServer);
        window.ApplySettings(window.Settings with { McpServer = true });
        Assert.True(window.AgentSocket is not null, $"The window does not listen for agents: {window.Status}");
        return window;
    }

    /// <summary>The windows' sockets for agents, in the run's state's socket folder (<see cref="TestRun"/>), not the person's.</summary>
    protected static string[] Sockets() =>
        Directory.Exists(global::Passenger.App.Mcp.McpHost.SocketFolder) ? Directory.GetFiles(global::Passenger.App.Mcp.McpHost.SocketFolder, "*.sock") : [];

    protected string[] Errors()
    {
        lock (errors)
        {
            return [.. errors];
        }
    }

    /// <summary>The id a program Passenger started wrote to a file in the folder; a wait that runs out says what Passenger
    /// said meanwhile.</summary>
    protected async Task<int> WrittenId(string file, string what)
    {
        try
        {
            return await Processes.WrittenIdAsync(Path.Combine(Folder, file), what);
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException(waited.Message + Said(), waited);
        }
    }

    /// <summary>A task's result once done, the window's loop running meanwhile, since the agent's calls are answered on it.</summary>
    protected T Await<T>(Task<T> task, string what)
    {
        WaitFor(() => task.IsCompleted, what, AgentLimit);
        return task.GetAwaiter().GetResult();
    }

    /// <summary><see cref="Ui.WaitFor"/>, failing with what Passenger said meanwhile.</summary>
    protected void WaitFor(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        try
        {
            Ui.WaitFor(condition, what, limit);
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException(waited.Message + Said(), waited);
        }
    }

    /// <summary>The window's question to the person, once it is asked.</summary>
    protected ConfirmDialog Question(MainWindow window, string what)
    {
        WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), what);
        return window.OwnedWindows.OfType<ConfirmDialog>().Single();
    }

    /// <summary>An agent's call the window first asks the person about, its question naming the tool and what it would do;
    /// allowed, the call's answer.</summary>
    protected JsonElement Asked(MainWindow window, McpClient client, [IdentifierOf(typeof(McpTools))] string tool, string what, Dictionary<string, object?>? arguments = null)
    {
        Task<JsonElement> calling = Task.Run(() => Call(client, tool, arguments));
        ConfirmDialog asked = Question(window, $"the question for the agent's {tool}");
        string[] said = Texts(asked);
        Assert.True(said.Contains($"Allow {tool}?") && said.Any(t => t.Contains($"wants to {what} (its {tool} tool)", StringComparison.Ordinal)),
            $"The question for the agent's {tool} said: {string.Join(" | ", said)}");
        Press(asked, "Allow");
        return Await(calling, $"the agent's {tool}");
    }

    /// <summary>An agent's call answered without a question to the person.</summary>
    protected JsonElement Unasked(MainWindow window, McpClient client, [IdentifierOf(typeof(McpTools))] string tool, Dictionary<string, object?>? arguments = null)
    {
        Task<JsonElement> calling = Task.Run(() => Call(client, tool, arguments));
        WaitFor(() => calling.IsCompleted || window.OwnedWindows.OfType<ConfirmDialog>().Any(), $"the agent's {tool}", AgentLimit);
        Assert.True(!window.OwnedWindows.OfType<ConfirmDialog>().Any(),
            $"The agent's {tool} asked the person: {string.Join(" | ", window.OwnedWindows.OfType<ConfirmDialog>().SelectMany(Texts))}");
        return calling.GetAwaiter().GetResult();
    }

    protected static string[] Texts(Avalonia.Controls.Window dialog) =>
        [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? string.Empty)];

    protected static void Press(Avalonia.Controls.Window dialog, string button)
    {
        Ui.Click(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>().First(b => (string?)b.Content == button));
    }

    /// <summary>A tool's answer, read as the JSON it is; a tool's error fails the test with its words and
    /// what Passenger said meanwhile.</summary>
    protected async Task<JsonElement> Call(McpClient client, [IdentifierOf(typeof(McpTools))] string tool, Dictionary<string, object?>? arguments = null)
    {
        CallToolResult result = await Result(client, tool, arguments);
        string text = Text(result);
        if (result.IsError == true)
        {
            Assert.Fail($"{tool} failed: {text}{Said()}");
        }
        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(text);
                return json.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Words that begin with a bracket, as a terminal's screen does under a prompt like "[me@host ~]$".
            }
        }
        // A tool that answers in words (stop's "Stopped") is a JSON string of them.
        return JsonSerializer.SerializeToElement(text);
    }

    /// <summary>A tool's whole result, an error's included, within the time the tool may take, or until the test gives up
    /// on it; with <paramref name="heard"/>, the call asks for progress and keeps what it is told.</summary>
    protected Task<CallToolResult> Result(McpClient client, [IdentifierOf(typeof(McpTools))] string tool, Dictionary<string, object?>? arguments = null, CancellationTokenSource? giveUp = null, Heard? heard = null) =>
        Exchange($"The {tool} call", Limit(tool), cancel => client.CallToolAsync(tool, arguments, heard, cancellationToken: cancel).AsTask(), giveUp);

    /// <summary>The progress an agent's call was told, as the SDK hands it over, on any thread.</summary>
    protected sealed class Heard : IProgress<ModelContextProtocol.ProgressNotificationValue>
    {
        private readonly List<ModelContextProtocol.ProgressNotificationValue> told = [];

        public IReadOnlyList<ModelContextProtocol.ProgressNotificationValue> Told
        {
            get
            {
                lock (told)
                {
                    return [.. told];
                }
            }
        }

        public void Report(ModelContextProtocol.ProgressNotificationValue value)
        {
            lock (told)
            {
                told.Add(value);
            }
        }

        /// <summary>What it was told, as a failure quotes it.</summary>
        public override string ToString() => Told.Count == 0 ? "nothing" : string.Join(" | ", Told.Select(t => $"{t.Progress}: {t.Message}"));
    }

    /// <summary>A tool call the agent gives up on as the protocol has it: sent under an id of its own, and on giving up a
    /// "cancelled" notification naming that id. The SDK's own call can drop that notification (1 in 200 under load), leaving
    /// the window waiting.</summary>
    protected static async Task<CallToolResult> CallGivingUp(McpClient client, [IdentifierOf(typeof(McpTools))] string tool, Dictionary<string, object?> arguments, CancellationToken giveUp)
    {
        RequestId id = new($"given-up-{Guid.NewGuid():N}");
        CallToolRequestParams call = new()
        {
            Name = tool,
            Arguments = arguments.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value, ModelContextProtocol.McpJsonUtilities.DefaultOptions)),
        };
        Task<CallToolResult> answer = client.SendRequestAsync<CallToolRequestParams, CallToolResult>(RequestMethods.ToolsCall, call, requestId: id).AsTask();
        try
        {
            return await answer.WaitAsync(giveUp);
        }
        catch (OperationCanceledException) when (giveUp.IsCancellationRequested)
        {
            // Sending it also ends the SDK's wait for the answer that will not come.
            await client.SendNotificationAsync(NotificationMethods.CancelledNotification, new CancelledNotificationParams { RequestId = id });
            throw;
        }
    }

    /// <summary>How long a tool may take before the test calls it hung: minutes for one that builds first; for one that asks
    /// the language server, longer than the 120 s Passenger waits for it to load the folder, so its own words come back;
    /// a minute for any other.</summary>
    protected static TimeSpan Limit([IdentifierOf(typeof(McpTools))] string tool) => tool switch
    {
        McpTools.Build or McpTools.DebugStart or McpTools.Run or McpTools.TestsRun or McpTools.Profile or McpTools.NugetInstall => TimeSpan.FromMinutes(5),
        McpTools.Diagnostics or McpTools.SymbolInfo or McpTools.Definition or McpTools.Usages or McpTools.Hierarchy or McpTools.Rename or McpTools.CodeActions
            or McpTools.ApplyCodeAction or McpTools.Format or McpTools.FindSymbol or McpTools.Outline or McpTools.Refactor => TimeSpan.FromMinutes(3),
        _ => TimeSpan.FromMinutes(1),
    };

    /// <summary>One exchange with Passenger, given up after a limit: one that runs out of time or fails ends the test naming
    /// it, with what Passenger said meanwhile, so a hang fails saying why rather than stopping the run in silence.</summary>
    protected async Task<T> Exchange<T>(string what, TimeSpan limit, Func<CancellationToken, Task<T>> exchange, CancellationTokenSource? giveUp = null)
    {
        CancellationToken given = giveUp?.Token ?? default;
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(Cancel, given);
        limit = limit.Scaled();
        timer.CancelAfter(limit);
        try
        {
            return await exchange(timer.Token);
        }
        catch (Exception error) when (!Cancel.IsCancellationRequested && !given.IsCancellationRequested
            && (error is TimeoutException || (error is OperationCanceledException && timer.IsCancellationRequested)))
        {
            throw new TimeoutException($"{what} had no answer in {limit.TotalSeconds:0} s.{Said()}", error);
        }
        catch (Exception error) when (!Cancel.IsCancellationRequested && !given.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{what} failed: {error.Message}{Said()}", error);
        }
    }

    /// <summary>What Passenger said before something went wrong: its standard error, and the end of its log and of the
    /// debugger engine's, which on a machine nobody can watch (CI's) are all there is to go by.</summary>
    protected string Said()
    {
        string[] written = Errors();
        return $"\nPassenger's standard error:{(written.Length == 0 ? " nothing" : "\n  " + string.Join("\n  ", written.TakeLast(40)))}"
            + Tail("Passenger's log", Path.Combine(OwnState, "logs", "passenger.log"), 20)
            // An attached agent's window and proxy keep the run's own state, not the test's.
            + Tail("The run's Passenger log", Path.Combine(TestRun.Settings, "logs", "passenger.log"), 20)
            + Tail("The run's MCP log", Path.Combine(TestRun.Settings, "logs", "mcp.log"), 30)
            + Tail("The debugger engine's log", EngineLog, 30);

        static string Tail(string name, string path, int lines)
        {
            try
            {
                // Shared, since its writer may still have it open, which Windows otherwise refuses a reader.
                using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                return $"\n{name} ends:\n  {string.Join("\n  ", reader.ReadToEnd().TrimEnd().Split('\n').TakeLast(lines))}";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    protected static string? Local(JsonElement stop, string name) =>
        stop.GetProperty("locals").EnumerateArray().FirstOrDefault(l => l.GetProperty("name").GetString() == name) is { ValueKind: JsonValueKind.Object } local
            ? local.GetProperty("value").GetString()
            : null;

    protected void Write(string relative, string text) => TestFolders.Write(Folder, relative, text);

    /// <summary>A working tree under a folder (work), one commit in and Notes.txt changed since, whose origin is a bare
    /// repository beside it (remote.git) that refuses every push when <paramref name="refusing"/>, its pre-receive hook
    /// saying why.</summary>
    protected static string Repository(string root, bool refusing)
    {
        string remote = Path.Combine(root, "remote.git");
        string work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        TestGit.Run(root, "init", "-q", "--bare", remote);
        if (refusing)
        {
            string hook = Path.Combine(remote, "hooks", "pre-receive");
            File.WriteAllText(hook, "#!/bin/sh\necho 'pushes are closed here' >&2\nexit 1\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        File.WriteAllText(Path.Combine(work, "Notes.txt"), "one\ntwo\nthree\n");
        TestGit.Init(work);
        TestGit.Run(work, "remote", "add", "origin", remote);
        File.WriteAllText(Path.Combine(work, "Notes.txt"), "one\n2\nthree\n");
        return work;
    }

    /// <summary>A folder of the test's own with one text file, which opens with no language server to wait for.</summary>
    protected string Plain(string name)
    {
        string made = TestFolders.Create(name);
        File.WriteAllText(Path.Combine(made, "Notes.txt"), "notes\n");
        DeleteAtEnd(made);
        return made;
    }

    /// <summary>The window's question whether an agent may work there, once asked, held to the dialogs' rules.</summary>
    protected AgentAdmissionDialog Admission(MainWindow window, string what)
    {
        WaitFor(() => window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(d => d.IsVisible), what);
        return DialogButtonsTests.Check(window.OwnedWindows.OfType<AgentAdmissionDialog>().Single(d => d.IsVisible));
    }

    /// <summary>The agent's session ended, the window's loop running meanwhile.</summary>
    protected void Gone(McpClient client) => Await(Task.Run(async () =>
    {
        await client.DisposeAsync();
        return true;
    }), "the agent going");

    /// <summary>Waits off the UI thread for a condition, failing with what Passenger said meanwhile.</summary>
    protected async Task Until(Func<bool> condition, string what)
    {
        TimeSpan limit = TimeSpan.FromMinutes(2);
        if (!await Waiting.UntilAsync(condition, limit, Cancel, TimeSpan.FromMilliseconds(100)))
        {
            throw new TimeoutException($"Waited {limit.Scaled().TotalSeconds} s for {what}.{Said()}");
        }
    }

    /// <summary>What a tool answered in words, or an error's words.</summary>
    protected static string Text(JsonElement answer) => answer.ValueKind == JsonValueKind.String ? answer.GetString()! : answer.GetRawText();

    protected static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    /// <summary>usages' places, each file's "line:column text" read apart, in the answer's order.</summary>
    protected static IReadOnlyList<(string Path, int Line, int Column, string Text)> Used(JsonElement usages) =>
        [.. usages.GetProperty("files").EnumerateArray().SelectMany(file => file.GetProperty("places").EnumerateArray().Select(place =>
        {
            string said = place.GetString()!;
            int space = said.IndexOf(' ', StringComparison.Ordinal);
            string[] at = (space < 0 ? said : said[..space]).Split(':');
            return (file.GetProperty("path").GetString()!, int.Parse(at[0], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(at[1], System.Globalization.CultureInfo.InvariantCulture), space < 0 ? string.Empty : said[(space + 1)..]);
        }))];

    /// <summary>A PNG's width and height, from its header.</summary>
    protected static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }
}
