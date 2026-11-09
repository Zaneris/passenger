using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Passenger.Core;
using StreamJsonRpc;

namespace Passenger.Lsp;

/// <summary>A request that waited past its server's patience: which and how long (<see cref="Asked"/>), and where the
/// conversation with the server stood (<see cref="LanguageServer.Conversation"/>).</summary>
public readonly record struct LongWait(string Asked, string Conversation);

/// <summary>A language server process and the JSON-RPC connection to it over its standard input and
/// output, or over TCP on a loopback port (Godot's, in its editor: one Passenger started, or the person's own). Requests and notifications go out typed, and each failure of the server's comes back as a
/// <see cref="LanguageServerFailed"/>, the connection's own exceptions never past this class; what the server asks of
/// the client (its settings, a capability it registers, a refresh) is answered here and surfaced as events, each raised
/// on any thread, as is what it tells the person (<see cref="Logged"/>, <see cref="Shown"/>, <see cref="Progressed"/>).
/// Its standard error and its own logs go to <see cref="LogFolder"/>, and with <see cref="Trace"/> set every message.</summary>
public sealed class LanguageServer : IAsyncDisposable
{
    /// <summary>How long a request waits before the server is checked: one this old may be slow work (usages across a
    /// large solution) or a server that will never answer. Settable, with <see cref="ResponseLimit"/>, so a test waits
    /// them out quickly; both as much longer on a slower machine as <see cref="Waits"/> says.</summary>
    public TimeSpan SlowRequest { get; set; } = Waits.Of(TimeSpan.FromSeconds(5));

    /// <summary>How long the check may go unanswered before the server is called not responding. Roslyn answers it in
    /// milliseconds, through the queue every request goes through, even while it loads projects or runs a long request.</summary>
    public TimeSpan ResponseLimit { get; set; } = Waits.Of(TimeSpan.FromSeconds(10));

    /// <summary>How long a request may wait before its wait is said (<see cref="Waited"/>), once, in the server's log too:
    /// longer than any answer a person waits on, so what it says is the server's or Passenger's to explain.</summary>
    public TimeSpan Patience { get; set; } = Waits.Of(TimeSpan.FromSeconds(30));

    /// <summary>Servers running in this process, for a test's words on what each was doing as a wait gave up.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<LanguageServer, byte> Live = new();

    /// <summary>The check: the folding ranges of a document never opened, which Roslyn answers with none.</summary>
    private static readonly FoldingRangeParams Check = new(new TextDocumentIdentifier("file:///passenger-is-the-server-answering.cs"));

    /// <summary>Servers started in this IDE, for the handshake salt of each one's MSBuild nodes.</summary>
    private static int started;

    /// <summary>The server's process, null for one another program runs, reached on its port alone.</summary>
    private readonly Process? process;

    /// <summary>The connection to a server reached over TCP, null for one on its standard input and output.</summary>
    private readonly TcpClient? socket;
    private readonly JsonRpc rpc;
    private readonly CountedMessages messages;
    private readonly ClientTarget client;
    private readonly ServerLog log;

    /// <summary>Done once the server's error stream has been read into its log to its end.</summary>
    private readonly Task errorsHeard;

    private readonly Dictionary<long, (string Method, long Since)> waiting = [];
    private readonly Dictionary<string, (int Left, TaskCompletionSource Spent)> refusing = [];
    private readonly List<Hold> holding = [];
    private readonly Timer watch;
    private long requests;
    private long changes;

    /// <summary>A count of what may have changed the server's answers since it started: each notification it was sent (an
    /// open, a change or close of a document, files changed on disk, options), each of Passenger's own project
    /// requests, each time it said its projects loaded or its diagnostics changed, and each change it made that nothing
    /// said (<see cref="AnswersMoved"/>). Roslyn's server watches nothing itself, so an answer pulled at one count stands
    /// while the count does.</summary>
    public long Changes => Interlocked.Read(ref changes);

    /// <summary>Counts a change the server has made as it was asked, which it says only seconds later, if at all.</summary>
    internal void AnswersMoved() => Interlocked.Increment(ref changes);

    /// <summary>Counts one more of what may change the server's answers (<see cref="Changes"/>): a method of the protocol's
    /// own ($/cancelRequest, $/progress) is none.</summary>
    private void Changing(string method)
    {
        if (!method.StartsWith("$/", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref changes);
        }
    }
    private long partialResults;
    private long checkedAt;
    private long checkAnsweredAt;
    private readonly HashSet<long> waitSaid = [];
    private int checking;
    private volatile bool hung;
    private volatile bool killed;
    private volatile bool disposing;
    private volatile string? ended;

    /// <summary>The server's name among the requests in flight (<see cref="InFlight"/>).</summary>
    private readonly string who;

    private readonly Dictionary<string, object> options;

    /// <summary>What <c>workspace/configuration</c> answers for a section, for a server that is not Roslyn; null for
    /// Roslyn's options (<see cref="Option"/>).</summary>
    private readonly Func<string?, object?>? configure;

    /// <summary>What <c>vscode/content</c> answers (a JSON schema by its URI); null for a server that asks none.</summary>
    private readonly Func<string, CancellationToken, Task<string>>? content;

    /// <summary>The command running now that takes what the server asks of the client as it runs (<see cref="CommandRun"/>),
    /// one at a time (<see cref="commandRuns"/>); null while none runs, when every edit the server asks for is refused.</summary>
    private CommandRun? commandRun;
    private readonly SemaphoreSlim commandRuns = new(1, 1);

    private LanguageServer(Process? process, TcpClient? socket, string who, JsonRpc rpc, CountedMessages messages, ClientTarget client, ServerLog log, Task errorsHeard,
        string logFolder, IReadOnlyDictionary<string, object>? options, Func<string?, object?>? configure = null, Func<string, CancellationToken, Task<string>>? content = null)
    {
        this.socket = socket;
        this.configure = configure;
        this.content = content;
        this.options = new Dictionary<string, object>(options ?? new Dictionary<string, object>(), StringComparer.Ordinal);
        this.process = process;
        this.rpc = rpc;
        this.messages = messages;
        this.client = client;
        this.log = log;
        this.errorsHeard = errorsHeard;
        LogFolder = logFolder;
        WatchedFiles = new WatchedFiles(this);
        ProcessId = process?.Id ?? 0;
        this.who = who;
        Live[this] = 0;
        rpc.Disconnected += (_, e) =>
        {
            if (!disposing)
            {
                // Noted before it is told, so a handler added meanwhile hears it or finds it in Ended.
                ended = e.Description;
                Exited?.Invoke(this, e.Description);
            }
        };
        watch = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>The connection to the server ended without being asked to (it crashed or was killed), with why;
    /// raised on any thread, once: a handler added after the server started reads <see cref="Ended"/> after adding it.</summary>
    public event EventHandler<string>? Exited;

    /// <summary>Why the connection ended without being asked to, once <see cref="Exited"/> has been raised; else null.</summary>
    public string? Ended => ended;

    /// <summary>The server stopped answering: a request waited <see cref="SlowRequest"/>, and a question it answers at
    /// once then went unanswered for <see cref="ResponseLimit"/> (it is deadlocked, suspended or thrashing). Raised once,
    /// with what waited, on any thread; the server is left running for the caller to <see cref="Kill"/>.</summary>
    public event EventHandler<string>? NotResponding;

    /// <summary>A request waited past <see cref="Patience"/>: raised once for it, on any thread, with which, how long, and
    /// where the conversation stands (<see cref="Conversation"/>).</summary>
    public event EventHandler<LongWait>? Waited;

    /// <summary>The copy of Roslyn's server this one runs from, in the state's cache (<see cref="RoslynServerFolder"/>); null
    /// where it runs from the app's own roslyn/, and for every other server.</summary>
    public string? ServerCopy { get; private set; }

    /// <summary>The server's process id, kept once the process is gone; 0 for a server another program runs.</summary>
    public int ProcessId { get; }

    /// <summary>The folder the server said it works on as it was initialized, where that is another than the one it was
    /// started for (<see cref="LspMethods.GodotChangeWorkspace"/>: a Godot editor open on another project); else null.</summary>
    public string? OtherFolder { get; private set; }

    /// <summary>A message the server logged (<c>window/logMessage</c>), every one of which is in its own log too; raised on
    /// any thread.</summary>
    public event EventHandler<LogMessageParams>? Logged;

    /// <summary>A message the server shows the person: <c>window/showMessage</c>, or Roslyn's toast. Raised on any
    /// thread.</summary>
    public event EventHandler<LogMessageParams>? Shown;

    /// <summary>Work the server reports the progress of (<c>$/progress</c>: Roslyn's project loads); raised on any thread.</summary>
    public event EventHandler<WorkProgress>? Progressed;

    private readonly Lock saying = new();

    /// <summary>What the server logged, showed and reported the progress of while held, in order; null while not held.</summary>
    private List<Action>? heldSaid;

    /// <summary>Holds what the server logs, shows and reports the progress of (<see cref="Logged"/>, <see cref="Shown"/>,
    /// <see cref="Progressed"/>) for listeners that come later, which <see cref="ReleaseSaid"/> says it all to.</summary>
    public void HoldSaid()
    {
        lock (saying)
        {
            heldSaid ??= [];
        }
    }

    /// <summary>Says what was held to the listeners there now, in order, before anything the server says after.</summary>
    public void ReleaseSaid()
    {
        while (true)
        {
            List<Action> said;
            lock (saying)
            {
                if (heldSaid is not { Count: > 0 })
                {
                    heldSaid = null;
                    return;
                }
                said = heldSaid;
                heldSaid = [];
            }
            foreach (Action say in said)
            {
                say();
            }
        }
    }

    /// <summary>Raises one of the events <see cref="HoldSaid"/> holds, now or once released.</summary>
    private void Say(Action say)
    {
        lock (saying)
        {
            if (heldSaid is not null)
            {
                heldSaid.Add(say);
                return;
            }
        }
        say();
    }

    /// <summary>Where every message to and from every server goes while set, with the server's process and an arrow
    /// for its direction ("-->" to the server); called on the connection's threads.</summary>
    public static Action<int, string>? Trace { get; set; }

    /// <summary>Roslyn has finished loading the projects it was asked to open; raised on any thread.</summary>
    public event EventHandler? ProjectsLoaded;

    /// <summary>Where Passenger's project loader in the server gets the projects' design-time builds it asks for.</summary>
    public IProjectFeed? Feed { get; set; }

    /// <summary>A project's design-time builds given to Passenger's project loader in the server, which loads the project
    /// from them (or, given none, fails to): the project as it named it, and when it asked, so every file on disk before
    /// then is in what it loads. Raised on the answering thread, before the answer goes.</summary>
    public event EventHandler<ProjectGiven>? ProjectGiven;

    /// <summary>The server's colours moved in a way only it knows (a project reloaded, a file changed elsewhere): the
    /// semantic tokens should be asked for again. Raised on any thread.</summary>
    public event EventHandler? ColoursRefreshRequested;

    /// <summary>Has the request of these parameters, once sent, tell <paramref name="request"/> whether it reached the server
    /// after every word that its colours moved read so far (<see cref="ColoursRefreshRequested"/>, which comes later):
    /// placed as it crosses to the server, not as its caller sends it.</summary>
    internal void Follow(object parameters, SentRequest request) => messages.Follow(parameters, request);

    /// <summary>The server's diagnostics moved in a way only it knows: they should be pulled again, and only they, since
    /// asking a long file's colours again costs the server a second. Raised on any thread.</summary>
    public event EventHandler? DiagnosticsRefreshRequested;

    /// <summary>A document's diagnostics, as a server that pushes them sends them (<c>textDocument/publishDiagnostics</c>:
    /// TypeScript's); raised on any thread.</summary>
    public event EventHandler<PublishDiagnosticsParams>? DiagnosticsPublished;

    /// <summary>A message of the server's own beyond the protocol (ESLint's <c>eslint/status</c>, <c>eslint/noLibrary</c>,
    /// <c>eslint/noConfig</c>, <c>eslint/probeFailed</c>, <c>eslint/openDoc</c>): a request among them is answered at once
    /// with nothing. Raised on any thread.</summary>
    public event EventHandler<ServerMessage>? Said;

    /// <summary>The TypeScript server says which TypeScript it runs: its version, and where it is from (<c>workspace</c>,
    /// the project's own node_modules; <c>bundled</c>, the one carried beside it). Raised on any thread.</summary>
    public event EventHandler<(string Version, string? Source)>? TypeScriptVersion;

    /// <summary>The TypeScript the server last said it runs, null until it has (or for another server).</summary>
    public (string Version, string? Source)? TypeScript { get; private set; }

    /// <summary>Where Razor's questions about a Razor file's HTML go (<see cref="RazorHtml"/>): the HTML language server,
    /// started if it is not yet, or null while there is none, when Passenger answers what it can itself.</summary>
    public Func<CancellationToken, Task<LanguageServer?>>? HtmlServer
    {
        get => client.Html.Server;
        set => client.Html.Server = value;
    }

    public string LogFolder { get; }

    /// <summary>The files the server asked the client to watch for it, rather than watch them itself.</summary>
    public WatchedFiles WatchedFiles { get; }

    /// <summary>A line of the client's own in the server's log, timed as the server's are.</summary>
    internal void Note(string line) => log.Write($"{DateTime.Now:HH:mm:ss.fff} {line}");

    /// <summary>Why Passenger's extension to the server (<see cref="RoslynExtension"/>) cannot be asked, or null when every
    /// handler of it is active.</summary>
    public string? ExtensionProblem { get; private set; } = "the C# language server has not started";

    public JsonElement Capabilities { get; private set; }

    public SemanticTokensLegend? Legend { get; private set; }

    /// <summary>The legend of a Razor file's colours, which Razor registers as it starts: C#'s token types, then markup's
    /// and Razor's own. Null until it has.</summary>
    public SemanticTokensLegend? RazorLegend => client.RazorLegend;

    /// <summary>The diagnostic sources the server registered (Roslyn splits them: syntax, semantic,
    /// analyzers), each pulled on its own.</summary>
    public IReadOnlyList<string?> DiagnosticSources => client.DiagnosticSources;

    /// <summary>Lets go of what Razor gave the client of a closed Razor file's HTML.</summary>
    internal void ForgetRazorHtml(string path) => client.Html.Forget(path);

    /// <summary>A Razor file's HTML as Razor last gave it, its C# blanked out; null before it gave any.</summary>
    internal string? RazorHtmlOf(string path) => client.Html.TextOf(path);

    /// <summary>The HTML server's diagnostics of a Razor file's HTML, on the lines it has as the file does.</summary>
    internal Task<IReadOnlyList<Diagnostic>> RazorHtmlDiagnosticsAsync(string path, string text, CancellationToken cancel) =>
        client.Html.DiagnosticsAsync(path, text, cancel);

    /// <summary>The sources that answer for the whole workspace (<c>workspace/diagnostic</c>), as well as for one document.</summary>
    public IReadOnlyList<string?> WorkspaceDiagnosticSources => client.WorkspaceDiagnosticSources;

    /// <summary>The options set on the server as it stands: those it started with and every change since.</summary>
    public IReadOnlyDictionary<string, object> Options
    {
        get
        {
            lock (options)
            {
                return new Dictionary<string, object>(options, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Changes several of the server's options at once, which it asks for again once.</summary>
    public void SetOptions(IReadOnlyDictionary<string, object> changed)
    {
        lock (options)
        {
            foreach ((string name, object value) in changed)
            {
                options[name] = value;
            }
        }
        Notify(LspMethods.DidChangeConfiguration, new { settings = new { } });
    }

    /// <summary>Makes the server's options these alone, as one started with them has them, which it asks for again if
    /// they changed: a server another window leaves, or one started ahead of its window, takes its window's.</summary>
    public void ReplaceOptions(IReadOnlyDictionary<string, object> wanted)
    {
        lock (options)
        {
            if (options.Count == wanted.Count && options.All(option => wanted.TryGetValue(option.Key, out object? value) && Equals(option.Value, value)))
            {
                return;
            }
            options.Clear();
            foreach ((string name, object value) in wanted)
            {
                options[name] = value;
            }
        }
        Notify(LspMethods.DidChangeConfiguration, new { settings = new { } });
    }

    /// <summary>Every document's diagnostics from one source, as the solution-wide analysis has them, each report given to
    /// <paramref name="streamed"/> as the server sends it, since Roslyn holds the pull open until the source next changes.
    /// The answer's own reports are given back; none is given to <paramref name="streamed"/> after it.</summary>
    public async Task<IReadOnlyList<WorkspaceDocumentDiagnosticReport>> WorkspaceDiagnosticsAsync(string? source, IReadOnlyList<PreviousResultId> previous,
        Action<IReadOnlyList<WorkspaceDocumentDiagnosticReport>> streamed, CancellationToken cancel = default)
    {
        string token = $"passenger-workspace-diagnostic-{Interlocked.Increment(ref partialResults)}";
        client.Partials[token] = report =>
        {
            if (report is { Items.Count: > 0 })
            {
                streamed(report.Items);
            }
        };
        try
        {
            // How long it is held says nothing of the server, nor is it work in flight.
            return (await InvokeAsync<WorkspaceDiagnosticReport>(LspMethods.WorkspaceDiagnostic, new WorkspaceDiagnosticParams(source, previous, token), cancel, inFlight: false))?.Items ?? [];
        }
        finally
        {
            // The answer is read apart from the server's other messages: those it sent before it are handled first.
            await HandledSoFarAsync().ConfigureAwait(false);
            client.Partials.TryRemove(token, out _);
        }
    }

    /// <summary>Done once every message the server sent before now has been handled, in the order they are (one at a time).</summary>
    private Task HandledSoFarAsync()
    {
        TaskCompletionSource handled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (rpc.SynchronizationContext ?? throw new InvalidOperationException("The server's messages have no order to wait in")).Post(_ => handled.SetResult(), null);
        return handled.Task;
    }

    /// <summary>An option's value: one changed while running, else Passenger's own, else null, which leaves the server's as it is.</summary>
    private object? Option(string name)
    {
        lock (options)
        {
            return Answered(options, name);
        }
    }

    /// <summary>Whether a Roslyn server started with the options <paramref name="set"/> takes <paramref name="wanted"/> as it
    /// runs (<see cref="ReplaceOptions"/>): told they changed, it asks again for every option, but passes over one answered
    /// empty, so an option set at its start that <paramref name="wanted"/> leaves out stays as it was.</summary>
    public static bool TakesOptions(IReadOnlyDictionary<string, object> set, IReadOnlyDictionary<string, object> wanted) =>
        set.Keys.All(name => Answered(wanted, name) is not null);

    /// <summary>What the server is answered for an option with these set: their value, else Passenger's own, else null.</summary>
    private static object? Answered(IReadOnlyDictionary<string, object> set, string name) =>
        set.TryGetValue(name, out object? value) ? value : Settings.TryGetValue(name, out object? own) ? own : null;

    /// <summary>The process has ended, or been ended by <see cref="Kill"/>, or let go of (<see cref="DisposeAsync"/>).</summary>
    public bool HasExited => killed || (process is null ? ended is not null || socket is not { Connected: true } : !DotnetProcesses.Runs(process));

    /// <summary>Starts the Roslyn language server Passenger ships (under <c>roslyn/</c> beside the app, run from its copy
    /// in <paramref name="stateCache"/> where it may not write there, <see cref="RoslynServerFolder"/>), initializes it for
    /// a folder, and returns once it has said what it can do. Cancelled or failed, it leaves no process behind, at once
    /// rather than once the server answers.</summary>
    public static async Task<LanguageServer> StartRoslynAsync(string folder, string logFolder, CancellationToken cancel, IReadOnlyDictionary<string, object>? options = null,
        string? stateCache = null)
    {
        string carried = Path.Combine(RoslynServerFolder.Carried, RoslynServerFolder.Assembly);
        if (!File.Exists(carried))
        {
            throw new FileNotFoundException("The Roslyn language server is missing from the app's folder.", carried);
        }
        string? copy = await RoslynServerFolder.CopyAsync(stateCache, cancel).ConfigureAwait(false);
        string server = Path.Combine(copy ?? RoslynServerFolder.Carried, RoslynServerFolder.Assembly);
        Directory.CreateDirectory(logFolder);
        List<string> arguments =
        [
            "--stdio", "--logLevel", "Information", "--telemetryLevel", "off",
            "--extensionLogDirectory", logFolder, "--clientProcessId", Environment.ProcessId.ToString(),
        ];
        if (File.Exists(RoslynExtension.Assembly))
        {
            arguments.AddRange(["--extension", RoslynExtension.Assembly]);
        }
        // On the runtime Passenger runs on, whatever the machine has: with no .NET at all, a lone file still has its help.
        ProcessStartInfo start = ChildProcesses.ConservingMemory(ChildProcesses.NamingDotnet(ChildProcesses.HelperCommand(server, arguments)));
        // A server's MSBuild nodes are its own: a restart ends them, which would cut short a build that took one up.
        start.Environment[MSBuildNames.NodeHandshakeSalt] += $"-server-{Interlocked.Increment(ref started)}";
        // The server's restore takes no properties, and off Windows a WPF project restores only with this (NETSDK1100).
        if (!OperatingSystem.IsWindows() && !start.Environment.ContainsKey(MSBuildNames.EnableWindowsTargeting))
        {
            start.Environment[MSBuildNames.EnableWindowsTargeting] = "true";
            // Or every project, a plain one too, would fetch the Windows packs it never uses, and fail to restore offline.
            start.Environment.TryAdd(MSBuildNames.DisableTransitiveFrameworkReferenceDownloads, "true");
        }
        // Its parser recurses unchecked, 2 KB a pattern's level: 16 MB holds the 1,000 brackets a file it's given goes to.
        start.Environment.TryAdd(RuntimeVariables.ThreadStackSize, "1000000");
        // The server collector kept 3 to 5 times the live heap and never gave it back; the workstation one holds near it (#559).
        if (!start.Environment.ContainsKey(RuntimeVariables.GcServer))
        {
            start.Environment[RuntimeVariables.GcServer] = "0";
        }
        LanguageServer server2 = await StartCoreAsync(start, folder, logFolder, ClientCapabilities.Value, null, options, null, null, cancel).ConfigureAwait(false);
        if (copy is not null)
        {
            server2.ServerCopy = copy;
            RoslynServerFolder.Running(copy, server2);
        }
        try
        {
            try
            {
                server2.ExtensionProblem = await RoslynExtension.ActivateAsync(server2, cancel).ConfigureAwait(false);
            }
            catch (LanguageServerFailed failed)
            {
                // The server runs on without it.
                server2.ExtensionProblem = $"the C# language server did not activate Passenger's extension: {failed.Reason}";
            }
        }
        catch
        {
            // A start given up (another folder opened) leaves no server behind.
            server2.Abandon();
            throw;
        }
        return server2;
    }

    /// <summary>Starts a language server other than Roslyn (a web language's, on Node.js), initializes it for a folder
    /// with the capabilities and options it is given, and returns once it has said what it can do. Cancelled or failed,
    /// it leaves no process behind.</summary>
    public static Task<LanguageServer> StartAsync(ServerStart start, CancellationToken cancel)
    {
        Directory.CreateDirectory(start.LogFolder);
        return start.Port is { } port
            ? StartOnPortAsync(start, port, cancel)
            : StartCoreAsync(start.Process ?? throw new ArgumentException("A server on its standard input and output needs its program", nameof(start)), start.Folder,
                start.LogFolder, start.Capabilities, start.InitializationOptions, null, start.Configuration ?? (_ => null), start.Content, cancel);
    }

    /// <summary>A server reached over TCP on a loopback port: its program started first where the start names one, its
    /// output and errors heard into its log, and connected to once it listens; else the one already listening there.</summary>
    private static async Task<LanguageServer> StartOnPortAsync(ServerStart start, int port, CancellationToken cancel)
    {
        Process? process = null;
        if (start.Process is { } program)
        {
            program.RedirectStandardInput = true;
            program.RedirectStandardOutput = true;
            program.RedirectStandardError = true;
            process = ChildProcesses.Start(program);
            ChildProcesses.HoldFamily(process);
        }
        ServerLog log;
        try
        {
            log = process is null ? ServerLog.Begin(start.LogFolder, $"port{port}") : ServerLog.Begin(start.LogFolder, process.Id);
        }
        catch
        {
            if (process is not null)
            {
                End(process);
            }
            throw;
        }
        Task heard = process is null ? Task.CompletedTask : HearAllAsync(process, log);
        UnawaitedWork.Observe(heard);
        TcpClient socket;
        try
        {
            socket = await ConnectAsync(port, process, start.Listening, start.Taken, cancel).ConfigureAwait(false);
        }
        catch
        {
            if (process is not null)
            {
                End(process);
            }
            log.Dispose();
            throw;
        }
        SocketStream stream = new(socket.GetStream());
        TracedStream input = new(stream, process?.Id ?? port, "-->", static () => Trace);
        TracedStream output = new(stream, process?.Id ?? port, "<--", static () => Trace, across: input);
        string who = process is null ? $"the server on port {port}" : $"{Named(process.StartInfo)}, process {process.Id} on port {port}";
        return await ConnectedAsync(process, socket, who, input, output, log, heard, start.Folder, start.LogFolder, start.Capabilities, start.InitializationOptions,
            null, start.Configuration ?? (_ => null), start.Content, start.TellsRootPath, cancel).ConfigureAwait(false);
    }

    /// <summary>The connection to a server listening on a loopback port, tried while the program started to serve there
    /// runs, until it listens, <paramref name="limit"/> passes or the port is held where it cannot listen
    /// (<paramref name="taken"/>, asked each second); tried once where no program was started.</summary>
    private static async Task<TcpClient> ConnectAsync(int port, Process? process, TimeSpan limit, Func<int, string?>? taken, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        for (int tries = 1; ; tries++)
        {
            TcpClient client = new() { NoDelay = true };
            bool connected = false;
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancel).ConfigureAwait(false);
                connected = true;
                return client;
            }
            catch (SocketException refused)
            {
                if (process is null)
                {
                    throw new LanguageServerFailed($"nothing listens on port {port}", LspMethods.Initialize, refused) { Ended = true };
                }
                if (!DotnetProcesses.Runs(process))
                {
                    throw new LanguageServerFailed($"it ended ({ExitCodes.Said(process.ExitCode)}) before it listened on port {port}", LspMethods.Initialize, refused) { Ended = true };
                }
                if (tries % 10 == 0 && taken?.Invoke(port) is { } holder)
                {
                    throw new LanguageServerFailed($"port {port} was taken by {holder} before it listened there", LspMethods.Initialize, refused) { PortTaken = true };
                }
                if (Stopwatch.GetElapsedTime(began) > limit)
                {
                    throw new LanguageServerFailed($"it did not listen on port {port} in {limit.TotalSeconds:0} s", LspMethods.Initialize, refused);
                }
            }
            finally
            {
                if (!connected)
                {
                    client.Dispose();
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancel).ConfigureAwait(false);
        }
    }

    /// <summary>A server program's output and errors heard into its log to their end, which closes the log: one reached
    /// over TCP says nothing of the protocol on them.</summary>
    private static async Task HearAllAsync(Process process, ServerLog log)
    {
        try
        {
            await ChildProcesses.Hear(process, output: log.Write, errors: log.Write).ConfigureAwait(false);
        }
        finally
        {
            log.Dispose();
        }
    }

    private static async Task<LanguageServer> StartCoreAsync(ProcessStartInfo start, string folder, string logFolder, JsonElement capabilities,
        object? initializationOptions, IReadOnlyDictionary<string, object>? options, Func<string?, object?>? configure,
        Func<string, CancellationToken, Task<string>>? content, CancellationToken cancel)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process process = ChildProcesses.Start(start);
        // Its restores' MSBuild nodes outlive the restores, and would keep the folder they started in for their idle time.
        ChildProcesses.HoldFamily(process);
        // Every message passes the trace from initialize on, so one turned on while the server runs starts at the next.
        TracedStream input = new(process.StandardInput.BaseStream, process.Id, "-->", static () => Trace);
        TracedStream output = new(ChildProcesses.OutputStream(process), process.Id, "<--", static () => Trace, across: input);
        ServerLog log;
        try
        {
            log = ServerLog.Begin(logFolder, process.Id);
        }
        catch
        {
            End(process);
            throw;
        }
        Task errorsHeard = HearErrorsAsync(process, log);
        UnawaitedWork.Observe(errorsHeard);
        return await ConnectedAsync(process, null, $"{Named(process.StartInfo)}, process {process.Id}", input, output, log, errorsHeard, folder, logFolder, capabilities,
            initializationOptions, options, configure, content, false, cancel).ConfigureAwait(false);
    }

    /// <summary>The JSON-RPC connection made over a server's streams, initialized for a folder; a start given up or failed
    /// leaves no server behind.</summary>
    private static async Task<LanguageServer> ConnectedAsync(Process? process, TcpClient? socket, string who, TracedStream input, TracedStream output, ServerLog log,
        Task errorsHeard, string folder, string logFolder, JsonElement capabilities, object? initializationOptions, IReadOnlyDictionary<string, object>? options,
        Func<string?, object?>? configure, Func<string, CancellationToken, Task<string>>? content, bool rootPath, CancellationToken cancel)
    {
        EnvelopeFormatter formatter = new(Json.Options);
        CountedMessages handler = new(input, output, formatter);
        ClientTarget client = new();
        // One at a time, in order: a progress report handled after its end would leave the load showing.
        JsonRpc rpc = new(handler) { SynchronizationContext = new Microsoft.VisualStudio.Threading.NonConcurrentSynchronizationContext(sticky: false) };
        ToldCancellation.Use(rpc);
        rpc.AddLocalRpcTarget(client, new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true });
        LanguageServer server2 = new(process, socket, who, rpc, handler, client, log, errorsHeard, logFolder, options, configure, content);
        client.Owner = server2;
        rpc.StartListening();

        try
        {
            string root = DocumentUris.FromPath(folder);
            InitializeParams initialize = new(Environment.ProcessId, root, [new WorkspaceFolder(root, Path.GetFileName(folder))], capabilities,
                InitializationOptions: initializationOptions, RootPath: rootPath ? folder : null);
            // Given up at once, not cancelled: a start given up ends the server, so it fails with the connection, unseen.
            Task<JsonElement> answer = rpc.InvokeWithParameterObjectAsync<JsonElement>(LspMethods.Initialize, initialize, CancellationToken.None);
            UnawaitedWork.Dismiss(answer);
            JsonElement result = await answer.WaitAsync(cancel).ConfigureAwait(false);
            server2.Capabilities = JsonMembers.Object(result, "capabilities") ?? default;
            if (JsonMembers.Value(JsonMembers.Value(server2.Capabilities, "semanticTokensProvider"), "legend") is { } legend)
            {
                server2.Legend = legend.Deserialize<SemanticTokensLegend>(Json.Options);
            }
            cancel.ThrowIfCancellationRequested();
            // What it said before its answer (Godot's word of the folder it works on) is heard before its start returns.
            await server2.HandledSoFarAsync().ConfigureAwait(false);
            await rpc.NotifyWithParameterObjectAsync(LspMethods.Initialized, new { }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A start given up (another folder opened) or failed leaves no server behind, nor its log held open.
            server2.Abandon();
            if (server2.Failure(LspMethods.Initialize, error, cancel) is { } failed)
            {
                throw failed;
            }
            throw;
        }
        return server2;
    }

    /// <summary>Whether a fault nothing observed is StreamJsonRpc's own word that the connection was lost, on the answer a
    /// request waited for, which it drops when the request's sending fails as the server goes (its caller is told of that
    /// failure, thrown): never thrown itself, so never a caller's.</summary>
    public static bool IsDroppedAnswer(AggregateException unobserved) =>
        unobserved.InnerExceptions.All(e => e is ConnectionLostException { InnerException: null, StackTrace: null });

    /// <summary>Ends a server whose start was given up or failed, at once, with its log: there is no shutdown to ask it for.</summary>
    private void Abandon()
    {
        disposing = true;
        Live.TryRemove(this, out _);
        watch.Dispose();
        rpc.Dispose();
        socket?.Dispose();
        if (process is not null)
        {
            End(process);
        }
        log.Dispose();
    }

    /// <summary>Whether the server said it answers a request (its capability for it, such as <c>renameProvider</c>, there
    /// and not false).</summary>
    public bool Provides(string capability) =>
        JsonMembers.Value(Capabilities, capability) is { ValueKind: not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined) };

    /// <summary>Whether the server takes each change of a document as its whole text (<c>textDocumentSync</c>'s change kind
    /// Full, Godot's), not as the edits made.</summary>
    public bool TakesWholeTexts =>
        (JsonMembers.Number(Capabilities, "textDocumentSync") ?? JsonMembers.Number(JsonMembers.Object(Capabilities, "textDocumentSync"), "change")) == FullSync;

    /// <summary>LSP's document sync by whole texts.</summary>
    private const int FullSync = 1;

    /// <summary>The characters after which the server offers completion unasked (<c>completionProvider.triggerCharacters</c>).</summary>
    public IReadOnlyList<string> CompletionTriggers => JsonMembers.Texts(JsonMembers.Object(Capabilities, "completionProvider"), "triggerCharacters");

    /// <summary>The server's error stream heard into its log to its end, which closes the log.</summary>
    private static async Task HearErrorsAsync(Process process, ServerLog log)
    {
        try
        {
            await ChildProcesses.Hear(process, errors: log.Write).ConfigureAwait(false);
        }
        finally
        {
            log.Dispose();
        }
    }

    /// <summary>Ends a server that never finished starting, at once: there is no shutdown to ask it for.</summary>
    private static void End(Process process)
    {
        ChildProcesses.Kill(process);
        process.Dispose();
    }

    /// <summary>A request someone waits for the answer to: one still waiting after <see cref="SlowRequest"/> has the
    /// server checked, and raises <see cref="NotResponding"/> if that goes unanswered too; <paramref name="about"/> names
    /// what it asks where the method alone does not (an extension handler's). A <see cref="LanguageServerFailed"/> when the
    /// server does not answer it.</summary>
    public async Task<T?> RequestAsync<T>(string method, object parameters, CancellationToken cancel = default, string? about = null)
    {
        long id = Interlocked.Increment(ref requests);
        if (method.StartsWith("passenger/", StringComparison.Ordinal))
        {
            Changing(method);
        }
        lock (waiting)
        {
            waiting[id] = (about is null ? method : $"{method} ({about[(about.LastIndexOf('.') + 1)..]})", Stopwatch.GetTimestamp());
        }
        try
        {
            // Not back on the caller's thread: an answer read where it arrives (completion's) must not wait for a window's.
            return await InvokeAsync<T>(method, parameters, cancel).ConfigureAwait(false);
        }
        finally
        {
            lock (waiting)
            {
                waiting.Remove(id);
            }
        }
    }

    /// <summary>A notification the caller waits to see sent (the solution to open); a <see cref="LanguageServerFailed"/>
    /// when the server can no longer take it.</summary>
    public async Task NotifyAsync(string method, object parameters)
    {
        Changing(method);
        try
        {
            await rpc.NotifyWithParameterObjectAsync(method, parameters);
        }
        catch (Exception error) when (Failure(method, error, CancellationToken.None) is { } failed)
        {
            throw failed;
        }
    }

    /// <summary>Refuses the next requests of a method as the server may refuse one or cancel it unanswered (a pull a change
    /// made stale), for a test of what follows. Done once all are refused.</summary>
    public Task RefuseNext(string method, int requests)
    {
        lock (refusing)
        {
            TaskCompletionSource spent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            refusing[method] = (requests, spent);
            return spent.Task;
        }
    }

    /// <summary>Holds the next request or notification of a method whose parameters <paramref name="which"/> takes unsent
    /// until <paramref name="until"/> ends, as a server busy with it holds its answer, or one slow to take in a file
    /// changed on disk, for a test of what goes on meanwhile. Done once one is held.</summary>
    public Task HoldNext(string method, Func<object, bool> which, Task until)
    {
        Hold hold = new(method, which, until, Answered: false, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (holding)
        {
            holding.Add(hold);
        }
        return hold.Holding.Task;
    }

    /// <summary>Sends the next request of a method whose parameters <paramref name="which"/> takes and holds its answer until
    /// <paramref name="until"/> ends, as one the server worked out before what goes on meanwhile, for a test of that. Done
    /// once the answer has come and is held.</summary>
    public Task HoldNextAnswer(string method, Func<object, bool> which, Task until)
    {
        Hold hold = new(method, which, until, Answered: true, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (holding)
        {
            holding.Add(hold);
        }
        return hold.Holding.Task;
    }

    /// <summary>A request a test holds: unsent, or its answer once it has come; <see cref="Holding"/> done once it is held.</summary>
    private sealed record Hold(string Method, Func<object, bool> Which, Task Until, bool Answered, TaskCompletionSource Holding);

    private Hold? Held(string method, object parameters)
    {
        lock (holding)
        {
            int at = holding.FindIndex(h => h.Method == method && h.Which(parameters));
            if (at < 0)
            {
                return null;
            }
            Hold held = holding[at];
            holding.RemoveAt(at);
            return held;
        }
    }

    private bool Refused(string method)
    {
        lock (refusing)
        {
            if (!refusing.TryGetValue(method, out (int Left, TaskCompletionSource Spent) next) || next.Left == 0)
            {
                return false;
            }
            refusing[method] = (next.Left - 1, next.Spent);
            if (next.Left == 1)
            {
                next.Spent.SetResult();
            }
            return true;
        }
    }

    private async Task<T?> InvokeAsync<T>(string method, object parameters, CancellationToken cancel, bool inFlight = true)
    {
        using IDisposable flight = inFlight ? InFlight.Begin(method, who) : InFlight.None;
        try
        {
            if (Refused(method))
            {
                throw new LanguageServerFailed("the language server refused it: as a test asked", method);
            }
            Hold? held = Held(method, parameters);
            if (held is { Answered: false })
            {
                held.Holding.TrySetResult();
                await held.Until.WaitAsync(cancel).ConfigureAwait(false);
            }
            Task<T?> answer = rpc.InvokeWithParameterObjectAsync<T?>(method, parameters, cancel);
            try
            {
                T? answered = await answer.WaitAsync(cancel).ConfigureAwait(false);
                if (held is { Answered: true })
                {
                    held.Holding.TrySetResult();
                    await held.Until.WaitAsync(cancel).ConfigureAwait(false);
                }
                return answered;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                UnawaitedWork.Dismiss(answer);
                throw;
            }
        }
        catch (Exception error) when (Failure(method, error, cancel) is { } failed)
        {
            throw failed;
        }
    }

    /// <summary>The program a server runs, by name: Roslyn's assembly or a web server's script under dotnet or Node.js.</summary>
    private static string Named(ProcessStartInfo start) =>
        start.ArgumentList.FirstOrDefault(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || a.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)) is { } program
            ? Path.GetFileNameWithoutExtension(program) : Path.GetFileNameWithoutExtension(start.FileName);

    /// <summary>LSP's error for a request the server refuses as made of a document it has changed since.</summary>
    private const int ContentModified = -32801;

    /// <summary>The connection's exception as the server's failure: gone (it crashed, was ended for hanging or let go for
    /// one started again, the connection disposed or its pipe closed), refused (an error it answered with), or cancelled
    /// by the server rather than the caller (Roslyn cancels what a change made stale). Null for anything else: the
    /// caller's own cancellation, or a bug (an answer that does not read as asked), which must surface as itself.</summary>
    private LanguageServerFailed? Failure(string method, Exception error, CancellationToken cancel) => error switch
    {
        OperationCanceledException when cancel.IsCancellationRequested => null,
        OperationCanceledException => new LanguageServerFailed("the language server cancelled it", method, error),
        ConnectionLostException or ObjectDisposedException or IOException => new LanguageServerFailed(
            killed ? "the language server stopped responding, so it was ended" : "the language server stopped before it answered", method, error) { Ended = true },
        RemoteRpcException refused => new LanguageServerFailed($"the language server refused it: {refused.Message.Split('\n')[0].Trim()}", method, error)
        {
            Outdated = refused is RemoteInvocationException { ErrorCode: ContentModified },
        },
        _ => null,
    };

    /// <summary>Makes a request nothing waits on the answer to (Razor's word that a file closed), from the thread pool, where
    /// its failure is caught: awaited on a window's thread as it closes, the failure would be left to a dispatcher that never
    /// runs again, unobserved.</summary>
    internal void Tell(string method, object parameters) => UnawaitedWork.Observe(BackgroundWork.Unheld(async () =>
    {
        try
        {
            await InvokeAsync<JsonElement>(method, parameters, CancellationToken.None).ConfigureAwait(false);
        }
        catch (LanguageServerFailed)
        {
            // Gone, or refused: nothing waits on it.
        }
    }));

    /// <summary>Sends a notification nothing waits on (a document's change, its closing). One the server can no longer
    /// take, having ended or been ended, is dropped: a server started again is sent the open documents afresh.</summary>
    public void Notify(string method, object parameters)
    {
        Changing(method);
        UnawaitedWork.Observe(NotifyOrDropAsync(method, parameters));
    }

    private async Task NotifyOrDropAsync(string method, object parameters)
    {
        try
        {
            if (Held(method, parameters) is { Answered: false } held)
            {
                held.Holding.TrySetResult();
                await held.Until.ConfigureAwait(false);
            }
            await rpc.NotifyWithParameterObjectAsync(method, parameters);
        }
        catch (Exception error) when (error is ConnectionLostException or ObjectDisposedException or IOException)
        {
        }
    }

    /// <summary>Ends a server that stopped answering, at once, since it would answer no shutdown either: every request
    /// waiting on it fails, and <see cref="Exited"/> is not raised, its caller having let it go.</summary>
    public void Kill()
    {
        disposing = true;
        killed = true;
        Live.TryRemove(this, out _);
        socket?.Dispose();
        // Its tree and its process group, so a BuildHost it was loading with goes too.
        if (process is not null)
        {
            ChildProcesses.Kill(process);
        }
    }

    /// <summary>Checks the server, at most once in <see cref="SlowRequest"/>, while a request has waited that long; runs
    /// on the timer's thread each second.</summary>
    private void Watch()
    {
        if (!disposing)
        {
            SayLongWaits();
        }
        if (disposing || hung || Stopwatch.GetElapsedTime(Interlocked.Read(ref checkedAt)) < SlowRequest)
        {
            return;
        }
        string? slowest = null;
        TimeSpan waited = SlowRequest;
        lock (waiting)
        {
            foreach ((string method, long since) in waiting.Values)
            {
                TimeSpan age = Stopwatch.GetElapsedTime(since);
                if (age >= waited)
                {
                    (slowest, waited) = (method, age);
                }
            }
        }
        if (slowest is not null && Interlocked.CompareExchange(ref checking, 1, 0) == 0)
        {
            Interlocked.Exchange(ref checkedAt, Stopwatch.GetTimestamp());
            UnawaitedWork.Observe(CheckAsync(slowest, waited));
        }
    }

    private async Task CheckAsync(string slowest, TimeSpan waited)
    {
        try
        {
            // Run apart, so a write held up by a server that reads nothing never holds the limit's clock.
            await BackgroundWork.Unheld(AnswerAsync).WaitAsync(ResponseLimit);
        }
        catch (TimeoutException)
        {
            if (!disposing)
            {
                hung = true;
                NotResponding?.Invoke(this, $"{slowest} waited {waited.TotalSeconds:0} s, then a check went unanswered for {ResponseLimit.TotalSeconds:0} s");
            }
        }
        finally
        {
            Interlocked.Exchange(ref checking, 0);
        }
    }

    private async Task AnswerAsync()
    {
        try
        {
            await rpc.InvokeWithParameterObjectAsync<JsonElement>(LspMethods.FoldingRange, Check);
            Interlocked.Exchange(ref checkAnsweredAt, Stopwatch.GetTimestamp());
        }
        catch (Exception error) when (error is RemoteRpcException or ObjectDisposedException or IOException or OperationCanceledException)
        {
            // An error is an answer too, and a connection gone is Exited's to report.
        }
    }

    /// <summary>Each request that has waited past <see cref="Patience"/> said once, the conversation's words with it, into
    /// the server's log and to <see cref="Waited"/>.</summary>
    private void SayLongWaits()
    {
        List<(string Method, TimeSpan Age)> past = [];
        lock (waiting)
        {
            waitSaid.RemoveWhere(id => !waiting.ContainsKey(id));
            foreach ((long id, (string asked, long since)) in waiting)
            {
                TimeSpan age = Stopwatch.GetElapsedTime(since);
                if (age >= Patience && waitSaid.Add(id))
                {
                    past.Add((asked, age));
                }
            }
        }
        if (past.Count == 0)
        {
            return;
        }
        (string method, TimeSpan longest) = past.MaxBy(p => p.Age);
        LongWait wait = new($"{method} has waited {longest.TotalSeconds:0} s{(past.Count > 1 ? $", with {past.Count - 1} more past {Patience.TotalSeconds:0} s" : string.Empty)}",
            Conversation());
        Note($"{wait.Asked}: {wait.Conversation}");
        Waited?.Invoke(this, wait);
    }

    /// <summary>Where the conversation with the server stands, in words: the requests waiting on it, the oldest first; when
    /// it last answered a check and last sent anything; its requests Passenger has not answered; Passenger's messages not
    /// yet taken in by it; and the last messages each way.</summary>
    public string Conversation()
    {
        List<(string Method, TimeSpan Age)> asking;
        lock (waiting)
        {
            asking = [.. waiting.Values.Select(w => (w.Method, Stopwatch.GetElapsedTime(w.Since))).OrderByDescending(w => w.Item2)];
        }
        IReadOnlyList<(string Method, long Since)> unanswered = messages.Unanswered;
        long unsent = messages.Unsent;
        long heard = messages.LastHeard;
        long check = Interlocked.Read(ref checkAnsweredAt);
        List<string> parts =
        [
            asking.Count == 0 ? "no request waits on it" : $"{Plural.Of(asking.Count, "request")} {Plural.Agreeing(asking.Count, "waits", "wait")} on it ("
                + string.Join(", ", asking.Take(6).Select(a => $"{a.Method} {a.Age.TotalSeconds:0} s")) + (asking.Count > 6 ? ", …" : string.Empty) + ")",
            check == 0 ? "it has answered no check" : $"it answered Passenger's check {Stopwatch.GetElapsedTime(check).TotalSeconds:0} s ago",
            heard == 0 ? "it has sent nothing" : $"it last sent a message {Stopwatch.GetElapsedTime(heard).TotalSeconds:0} s ago",
            unanswered.Count == 0 ? "Passenger owes it no answer"
                : "Passenger has not yet answered its " + string.Join(", ", unanswered.Take(4).Select(u => $"{u.Method} ({Stopwatch.GetElapsedTime(u.Since).TotalSeconds:0} s)")),
            unsent == 0 ? "every message Passenger wrote has reached it" : $"{unsent} of Passenger's messages have not yet reached its input",
        ];
        return $"{who}: {string.Join("; ", parts)}. Its last messages: {string.Join(", ", messages.Recent())}";
    }

    /// <summary>Where the conversation stands with each server of this process that has a request waiting, a line each;
    /// empty with none, for a test's words as a wait gives up.</summary>
    public static string Conversations()
    {
        List<string> lines = [];
        foreach (LanguageServer server in Live.Keys)
        {
            bool asking;
            lock (server.waiting)
            {
                asking = server.waiting.Count > 0;
            }
            if (asking || server.messages.Unanswered.Count > 0)
            {
                lines.Add(server.Conversation());
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>How many servers this process holds that log to a folder, for a test that a start leaves none behind.</summary>
    public static int LiveIn(string logFolder) => Live.Keys.Count(server => server.LogFolder == logFolder);

    /// <summary>Runs one of the server's own commands (<c>workspace/executeCommand</c>) that asks nothing of the client as it
    /// runs (TypeScript's tsserver requests): an edit it asks for is refused.</summary>
    public Task<JsonElement> ExecuteCommandAsync(LspCommand command, CancellationToken cancel = default) =>
        RequestAsync<JsonElement>(LspMethods.ExecuteCommand, new ExecuteCommandParams(command.Command, command.Arguments ?? []), cancel);

    /// <summary>Runs one of the server's own commands (ESLint's fixes, TypeScript's refactorings), what it asks of the client
    /// as it runs taken by <paramref name="run"/>: one such command at a time, so each edit the server asks for is the one's
    /// it came during.</summary>
    public async Task<JsonElement> ExecuteCommandAsync(LspCommand command, CommandRun run, CancellationToken cancel = default)
    {
        await commandRuns.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref commandRun, run);
            return await ExecuteCommandAsync(command, cancel).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref commandRun, null);
            commandRuns.Release();
        }
    }

    /// <summary>Whether the server runs a command itself: one its <c>executeCommandProvider</c> lists. Roslyn's lists none,
    /// leaving its commands to the client (<see cref="ClientCommands"/>).</summary>
    public bool Runs(string command) => JsonMembers.Texts(JsonMembers.Object(Capabilities, "executeCommandProvider"), "commands").Contains(command);

    /// <summary>A call hierarchy item's callers, each with where the calls are.</summary>
    public async Task<IReadOnlyList<IncomingCall>> IncomingCallsAsync(HierarchyItem item, CancellationToken cancel = default) =>
        await RequestAsync<List<IncomingCall>>(LspMethods.IncomingCalls, new HierarchyItemParams(item), cancel) ?? [];

    public async Task<IReadOnlyList<OutgoingCall>> OutgoingCallsAsync(HierarchyItem item, CancellationToken cancel = default) =>
        await RequestAsync<List<OutgoingCall>>(LspMethods.OutgoingCalls, new HierarchyItemParams(item), cancel) ?? [];

    public async Task<IReadOnlyList<HierarchyItem>> SupertypesAsync(HierarchyItem item, CancellationToken cancel = default) =>
        await RequestAsync<List<HierarchyItem>>(LspMethods.Supertypes, new HierarchyItemParams(item), cancel) ?? [];

    public async Task<IReadOnlyList<HierarchyItem>> SubtypesAsync(HierarchyItem item, CancellationToken cancel = default) =>
        await RequestAsync<List<HierarchyItem>>(LspMethods.Subtypes, new HierarchyItemParams(item), cancel) ?? [];

    /// <summary>Asks the server to shut down and exit, waits a little for it to go, then ends it and its log. Nothing in it
    /// blocks the caller's thread, the window's included: each wait is awaited, and what follows one runs off it.</summary>
    public async ValueTask DisposeAsync()
    {
        disposing = true;
        Live.TryRemove(this, out _);
        // However it goes, a write into the pipes closed under it included (Node's SIGPIPE, 141), it did not fail.
        if (process is not null)
        {
            ChildProcesses.Ending(process);
        }
        watch.Dispose();
        try
        {
            if (!HasExited)
            {
                using CancellationTokenSource limit = new(TimeSpan.FromSeconds(3));
                // Given up at its limit, not cancelled: a cancelled request waits for the server's word, which a hung one never gives.
                Task<object?> answer = rpc.InvokeWithCancellationAsync<object?>(LspMethods.Shutdown, [], CancellationToken.None);
                UnawaitedWork.Dismiss(answer);
                await answer.WaitAsync(limit.Token).ConfigureAwait(false);
                await rpc.NotifyAsync(LspMethods.Exit).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ConnectionLostException or RemoteRpcException or ObjectDisposedException)
        {
        }
        rpc.Dispose();
        socket?.Dispose();
        if (process is null)
        {
            // Another program's server goes on without this connection.
            log.Dispose();
            return;
        }
        using (CancellationTokenSource limit = new(TimeSpan.FromSeconds(2)))
        {
            // Its error stream read into its log to its end too, which holds its last words.
            await Task.WhenAll(process.WaitForExitAsync(limit.Token), errorsHeard).WaitAsync(limit.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        // Killed whether it went or not: a server that exits can leave its BuildHost running, in its process group.
        ChildProcesses.Kill(process);
        process.Dispose();
        log.Dispose();
    }

    /// <summary>The server's options Passenger sets: parameter name hints before a literal, a null or an indexer's argument,
    /// never where the argument already says the name; type hints on a <c>var</c>, a lambda's parameter and a target-typed
    /// <c>new</c>, none for a collection expression; no file-based program found in the folder by the server itself, since
    /// the client names those it loads (a second project of the same program would answer beside it).</summary>
    private static readonly Dictionary<string, object> Settings = new(StringComparer.Ordinal)
    {
        ["dotnet_enable_automatic_discovery"] = false,
        ["dotnet_enable_inlay_hints_for_parameters"] = true,
        ["dotnet_enable_inlay_hints_for_literal_parameters"] = true,
        ["dotnet_enable_inlay_hints_for_indexer_parameters"] = true,
        ["dotnet_enable_inlay_hints_for_object_creation_parameters"] = false,
        ["dotnet_enable_inlay_hints_for_other_parameters"] = false,
        ["dotnet_suppress_inlay_hints_for_parameters_that_differ_only_by_suffix"] = true,
        ["dotnet_suppress_inlay_hints_for_parameters_that_match_method_intent"] = true,
        ["dotnet_suppress_inlay_hints_for_parameters_that_match_argument_name"] = true,
        ["csharp_enable_inlay_hints_for_types"] = true,
        ["csharp_enable_inlay_hints_for_implicit_variable_types"] = true,
        ["csharp_enable_inlay_hints_for_lambda_parameter_types"] = true,
        ["csharp_enable_inlay_hints_for_implicit_object_creation"] = true,
        ["csharp_enable_inlay_hints_for_collection_expressions"] = false,
    };

    /// <summary>A server's socket read as its pipe is: the connection reset or aborted by the other end (the server's program
    /// gone) is the end of the stream, as a pipe's writer gone is, never a fault of the read nothing awaits; a write after it
    /// fails, as the connection lost.</summary>
    private sealed class SocketStream(NetworkStream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return inner.Read(buffer);
            }
            catch (IOException reset) when (Ended(reset))
            {
                return 0;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException reset) when (Ended(reset))
            {
                return 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private static bool Ended(IOException error) =>
            error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The server's log: its standard error and the messages it logs (a project load that never ends says where
    /// it stopped), a file to each server, since Windows lets no second writer open a file one has open and a server
    /// started again while the last one's log is still held would otherwise fail to start. Shared for reading and
    /// deleting, and closed at the stream's end or the server's, so the folder it is in can go.</summary>
    private sealed class ServerLog(string path) : IDisposable
    {
        /// <summary>The servers' logs kept in a folder, the newest: older ones go as a server starts, so a folder that every
        /// server of every session writes to stays small.</summary>
        private const int Kept = 20;

        private readonly StreamWriter writer = new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        private bool closed;

        /// <summary>A new server's log, named by when it began, so the names sort oldest first, and by its process, the id
        /// the IDE's log names when it ends; the oldest logs past <see cref="Kept"/> are deleted, but those of servers
        /// still running (another window's, another IDE's on the same settings).</summary>
        public static ServerLog Begin(string folder, int processId) => Begin(folder, processId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>A new server's log named by what it is (<c>port6005</c> for one another program runs), which no process
        /// keeps from deletion.</summary>
        public static ServerLog Begin(string folder, string named)
        {
            string[] older;
            try
            {
                older = [.. Directory.EnumerateFiles(folder, "server-*.log").Order(StringComparer.Ordinal)];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                older = [];
            }
            foreach (string stale in older.SkipLast(Kept - 1).Where(log => !Running(log)))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Open somewhere that shares no deletion (a reader on Windows): it goes another time.
                }
            }
            return new ServerLog(Path.Combine(folder, $"server-{DateTime.Now:yyyyMMdd-HHmmss}-{named}.log"));
        }

        /// <summary>Whether a log's process, the last part of its name, is running: its own or, its id reused, another's,
        /// which only keeps the log a while longer.</summary>
        private static bool Running(string log)
        {
            string name = Path.GetFileNameWithoutExtension(log);
            if (!int.TryParse(name.AsSpan(name.LastIndexOf('-') + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id))
            {
                return false;
            }
            try
            {
                using Process process = Process.GetProcessById(id);
                return true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>A line the server wrote, or null for the end of its stream, which closes the file.</summary>
        public void Write(string? line)
        {
            lock (writer)
            {
                if (closed)
                {
                    return;
                }
                if (line is null)
                {
                    closed = true;
                    writer.Dispose();
                    return;
                }
                try
                {
                    writer.WriteLine(line);
                }
                catch (IOException)
                {
                    // A log that cannot be written (a full disk) is never the end of the IDE: this runs on the reader's thread.
                }
            }
        }

        public void Dispose() => Write(null);
    }

    /// <summary>What the client answers when the server asks: the options in <see cref="Settings"/>, the rest left to
    /// their defaults, every registration and progress token accepted.</summary>
    private sealed class ClientTarget
    {
        private readonly List<string?> diagnosticSources = [];
        private readonly List<string?> workspaceSources = [];

        /// <summary>What takes each partial result of a request under way, by the token it was asked with.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, Action<WorkspaceDiagnosticReport?>> Partials { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string?> WorkspaceDiagnosticSources
        {
            get
            {
                lock (diagnosticSources)
                {
                    return [.. workspaceSources];
                }
            }
        }

        public LanguageServer? Owner { get; set; }

        public RazorHtml Html { get; } = new();

        public SemanticTokensLegend? RazorLegend { get; private set; }

        /// <summary>Whether a registration is one of Razor's: Razor's are each for its files, by a document selector,
        /// where Roslyn's are for every file.</summary>
        private static bool ForRazor(Registration registration) =>
            JsonMembers.Items(registration.RegisterOptions, "documentSelector").Any(filter => JsonMembers.Text(filter, "language") == LanguageIds.Razor);

        public IReadOnlyList<string?> DiagnosticSources
        {
            get
            {
                // One with no identifier repeats what the named ones report: pulled only when there are none.
                lock (diagnosticSources)
                {
                    List<string?> named = diagnosticSources.Where(s => s is not null).ToList();
                    return named.Count > 0 ? named : [null];
                }
            }
        }

        /// <summary>Godot's word that it works on another folder than it was told: its own project's.</summary>
        [JsonRpcMethod(LspMethods.GodotChangeWorkspace)]
        public void GodotChangeWorkspace(JsonElement said)
        {
            if (Owner is { } owner && JsonMembers.Text(said, "path") is { } path)
            {
                owner.OtherFolder = path;
            }
        }

        [JsonRpcMethod(LspMethods.LogMessage)]
        public void LogMessage(LogMessageParams message)
        {
            Owner?.log.Write($"{DateTime.Now:HH:mm:ss.fff} {message.Message}");
            if (Owner is { } owner)
            {
                owner.Say(() => owner.Logged?.Invoke(owner, message));
            }
        }

        [JsonRpcMethod(LspMethods.ShowMessage)]
        public void ShowMessage(LogMessageParams message)
        {
            Owner?.log.Write($"{DateTime.Now:HH:mm:ss.fff} Shown: {message.Message}");
            if (Owner is { } owner)
            {
                owner.Say(() => owner.Shown?.Invoke(owner, message));
            }
        }

        /// <summary>Roslyn's own showMessage, with commands for VS Code's C# extension (Show C# logs) that have no meaning here.</summary>
        [JsonRpcMethod(LspMethods.ShowToast)]
        public void ShowToast(ShowToastParams toast) => ShowMessage(new LogMessageParams(toast.MessageType, toast.Message));

        /// <summary>Roslyn asks for each option as <c>csharp|group.name</c> or <c>group.name</c>; the ones named in
        /// <see cref="Settings"/> are answered, the rest left to the server's defaults. Another server's sections
        /// (<c>css</c>, <c>html</c>, <c>typescript</c>) are answered as it was started to answer them.</summary>
        [JsonRpcMethod(LspMethods.Configuration)]
        public object?[] Configuration(ConfigurationParams request) => Owner?.configure is { } configure
            ? [.. request.Items.Select(item => configure(item.Section))]
            : [.. request.Items.Select(item => item.Section is { } section ? Owner?.Option(section[(section.LastIndexOfAny(['.', '|']) + 1)..]) : null)];

        /// <summary>A document's diagnostics from a server that pushes them.</summary>
        [JsonRpcMethod(LspMethods.PublishDiagnostics)]
        public void PublishDiagnostics(PublishDiagnosticsParams published)
        {
            if (Owner is { } owner)
            {
                owner.DiagnosticsPublished?.Invoke(owner, published);
            }
        }

        /// <summary>Content the server cannot fetch itself (the JSON server's schemas by their URI, which it is told to leave
        /// to the client); asked of the client's answer, awaited apart from the messages that follow.</summary>
        [JsonRpcMethod("vscode/content")]
        public async Task<string> Content(JsonElement asked)
        {
            string? uri = asked.ValueKind switch
            {
                JsonValueKind.String => asked.GetString(),
                JsonValueKind.Array when asked.GetArrayLength() > 0 && asked[0].ValueKind == JsonValueKind.String => asked[0].GetString(),
                JsonValueKind.Object => JsonMembers.Text(asked, "uri"),
                _ => null,
            };
            if (uri is null || Owner?.content is not { } content)
            {
                throw new LocalRpcException($"Passenger has no content for {uri ?? asked.ToString()}") { ErrorCode = -32603 };
            }
            try
            {
                return await content(uri, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not LocalRpcException)
            {
                // The server says so where the file names the schema.
                throw new LocalRpcException(error.Message) { ErrorCode = -32603 };
            }
        }

        /// <summary>A question for the person with choices (the TypeScript server's, of a version it cannot use): none chosen,
        /// what it said is logged.</summary>
        [JsonRpcMethod(LspMethods.ShowMessageRequest)]
        public object? ShowMessageRequest(LogMessageParams message)
        {
            LogMessage(message);
            return null;
        }

        [JsonRpcMethod(LspMethods.ShowDocument)]
        public object ShowDocument(JsonElement? request = null) => new { success = false };

        /// <summary>An edit a server's own command makes (ESLint's fixes, TypeScript's refactorings): made by the command run
        /// it came during, else refused. Awaited apart from the messages that follow.</summary>
        [JsonRpcMethod(LspMethods.ApplyEdit)]
        public async Task<object> ApplyEdit(JsonElement request)
        {
            if (Owner is not { } owner || Volatile.Read(ref owner.commandRun) is not { } run || JsonMembers.Value(request, "edit") is not { } edit)
            {
                return new { applied = false, failureReason = "Passenger makes a server's edits only for a command it runs" };
            }
            string? refused = await run.Apply(edit.Clone()).ConfigureAwait(false);
            return refused is null ? new { applied = true } : new { applied = false, failureReason = refused };
        }

        /// <summary>The TypeScript server's word, as a refactoring it ran ends, of a name it wrote to be renamed at once: kept for
        /// the command run it came during.</summary>
        [JsonRpcMethod(LspMethods.TypeScriptRename)]
        public object? TypeScriptRename(JsonElement place)
        {
            if (Owner is { } owner && Volatile.Read(ref owner.commandRun) is { } run && JsonMembers.Text(JsonMembers.Object(place, "textDocument"), "uri") is { } uri
                && JsonMembers.Value(place, "position") is { } position && position.Deserialize<LspPosition>(Json.Options) is { } at)
            {
                run.RenameAt = new LspLocation(uri, new LspRange(at, at));
            }
            return null;
        }

        [JsonRpcMethod("eslint/status")]
        public void EslintStatus(JsonElement said) => Say("eslint/status", said);

        [JsonRpcMethod("eslint/exitCalled")]
        public void EslintExitCalled(JsonElement said) => Say("eslint/exitCalled", said);

        [JsonRpcMethod("eslint/showOutputChannel")]
        public void EslintShowOutput(JsonElement? said = null) => Say("eslint/showOutputChannel", said ?? default);

        [JsonRpcMethod("eslint/noLibrary")]
        public object EslintNoLibrary(JsonElement said) => Asked("eslint/noLibrary", said);

        [JsonRpcMethod("eslint/noConfig")]
        public object EslintNoConfig(JsonElement said) => Asked("eslint/noConfig", said);

        [JsonRpcMethod("eslint/probeFailed")]
        public object EslintProbeFailed(JsonElement said) => Asked("eslint/probeFailed", said);

        [JsonRpcMethod("eslint/openDoc")]
        public object EslintOpenDoc(JsonElement said) => Asked("eslint/openDoc", said);

        private void Say(string method, JsonElement said)
        {
            Owner?.log.Write($"{DateTime.Now:HH:mm:ss.fff} {method} {(said.ValueKind == JsonValueKind.Undefined ? string.Empty : said.GetRawText())}");
            if (Owner is { } owner)
            {
                owner.Said?.Invoke(owner, new ServerMessage(method, said.ValueKind == JsonValueKind.Undefined ? said : said.Clone()));
            }
        }

        private object Asked(string method, JsonElement said)
        {
            Say(method, said);
            return new { };
        }

        /// <summary>The TypeScript server's word on which TypeScript it runs, the project's own or the one Passenger carries.</summary>
        [JsonRpcMethod(LspMethods.TypeScriptVersion)]
        public void TypeScriptVersion(JsonElement version)
        {
            Owner?.log.Write($"{DateTime.Now:HH:mm:ss.fff} TypeScript {version}");
            if (Owner is { } owner && JsonMembers.Text(version, "version") is { } number)
            {
                string? source = JsonMembers.Text(version, "source");
                owner.TypeScript = (number, source);
                owner.TypeScriptVersion?.Invoke(owner, (number, source));
            }
        }

        [JsonRpcMethod(LspMethods.RegisterCapability)]
        public object? RegisterCapability(RegistrationParams request)
        {
            foreach (Registration registration in request.Registrations.Where(r => r.Method == LspMethods.DidChangeWatchedFiles))
            {
                Owner?.WatchedFiles.Register(registration.Id, registration.RegisterOptions);
            }
            foreach (Registration registration in request.Registrations.Where(ForRazor))
            {
                if (registration.Method == LspMethods.SemanticTokens && JsonMembers.Value(registration.RegisterOptions, "legend") is { } legend)
                {
                    RazorLegend = legend.Deserialize<SemanticTokensLegend>(Json.Options);
                }
            }
            // Razor's are left out: a Razor file's diagnostics are its one source, pulled as the file's own.
            foreach (Registration registration in request.Registrations.Where(r => r.Method == LspMethods.DocumentDiagnostic && !ForRazor(r)))
            {
                string? identifier = JsonMembers.Text(registration.RegisterOptions, "identifier");
                bool workspace = JsonMembers.Bool(registration.RegisterOptions, "workspaceDiagnostics") == true;
                lock (diagnosticSources)
                {
                    if (!diagnosticSources.Contains(identifier))
                    {
                        diagnosticSources.Add(identifier);
                    }
                    if (workspace && !workspaceSources.Contains(identifier))
                    {
                        workspaceSources.Add(identifier);
                    }
                }
            }
            return null;
        }

        /// <summary>Registrations the server takes back: its files watched, by the protocol's own spelling of the list
        /// (<c>unregisterations</c>) or the plain one.</summary>
        [JsonRpcMethod(LspMethods.UnregisterCapability)]
        public object? UnregisterCapability(JsonElement? request = null)
        {
            if ((JsonMembers.Value(request, "unregisterations") ?? JsonMembers.Value(request, "unregistrations")) is { ValueKind: JsonValueKind.Array } list)
            {
                foreach (JsonElement taken in list.EnumerateArray())
                {
                    if (JsonMembers.Text(taken, "id") is { } id && JsonMembers.Text(taken, "method") == LspMethods.DidChangeWatchedFiles)
                    {
                        Owner?.WatchedFiles.Unregister(id);
                    }
                }
            }
            return null;
        }

        [JsonRpcMethod(LspMethods.WorkDoneProgressCreate)]
        public object? CreateProgress(JsonElement? request = null) => null;

        /// <summary>Work done progress, or a request's partial result where the server names its token and value as work
        /// done progress names them (<see cref="Partial"/>).</summary>
        [JsonRpcMethod(LspMethods.Progress)]
        public void Progress(ProgressParams progress)
        {
            if (WorkProgress.From(progress) is { } work && Owner is { } owner)
            {
                owner.Say(() => owner.Progressed?.Invoke(owner, work));
            }
            else if (progress.Token.ValueKind == JsonValueKind.String && Partials.ContainsKey(progress.Token.GetString()!))
            {
                Partial(progress.Token, progress.Value.ValueKind == JsonValueKind.Object ? progress.Value.Deserialize<WorkspaceDiagnosticReport>(Json.Options) : null);
            }
        }

        /// <summary>A request's partial result, which a server may send with its token and value in order rather than named,
        /// read as the workspace diagnostics' reports it streams, the one request that asks for partial results; one no
        /// request under way asked for is dropped.</summary>
        [JsonRpcMethod(LspMethods.Progress)]
        public void Progress(JsonElement partialToken, WorkspaceDiagnosticReport? partialValue) => Partial(partialToken, partialValue);

        private void Partial(JsonElement partialToken, WorkspaceDiagnosticReport? partialValue)
        {
            if (partialToken.ValueKind == JsonValueKind.String && partialToken.GetString() is { } token && Partials.TryGetValue(token, out Action<WorkspaceDiagnosticReport?>? take))
            {
                take(partialValue);
            }
        }

        [JsonRpcMethod(LspMethods.ProjectInitializationComplete)]
        public void ProjectsLoaded(JsonElement? message = null)
        {
            Owner?.Changing(LspMethods.ProjectInitializationComplete);
            Owner?.WatchedFiles.Loaded();
            Owner?.ProjectsLoaded?.Invoke(Owner, EventArgs.Empty);
        }

        /// <summary>Passenger's project loader in the server asking for a project's design-time builds, from the client's
        /// feed (its project host's builds), what they read that changed since told as changed on disk, kept for the
        /// registrations its load sends; awaited apart from the messages that follow, which go on being handled.</summary>
        [JsonRpcMethod(Roslyn.Extension.ProjectMethods.Info)]
        public async Task<Roslyn.Extension.ProjectInfoResult> ProjectInfo(Roslyn.Extension.ProjectInfoParams asked)
        {
            if (Owner is not { Feed: { } feed } owner)
            {
                return new Roslyn.Extension.ProjectInfoResult(null, "Passenger has no design-time builds to give");
            }
            DateTime at = DateTime.UtcNow;
            owner.Changing(Roslyn.Extension.ProjectMethods.Info);
            FedProject fed = await feed.InfoAsync(asked.Path, CancellationToken.None).ConfigureAwait(false);
            owner.WatchedFiles.Tell(fed.Changed);
            owner.ProjectGiven?.Invoke(owner, new ProjectGiven(asked.Path, at, fed.Changed.Count > 0));
            return fed.Info;
        }

        [JsonRpcMethod(LspMethods.SemanticTokensRefresh)]
        public object? RefreshTokens(JsonElement? request = null)
        {
            // Noted as it was read (CountedMessages), perhaps well before now: what came before it was handled first.
            Owner?.ColoursRefreshRequested?.Invoke(Owner, EventArgs.Empty);
            return null;
        }

        [JsonRpcMethod(LspMethods.DiagnosticRefresh)]
        public object? RefreshDiagnostics(JsonElement? request = null)
        {
            Owner?.Changing(LspMethods.DiagnosticRefresh);
            Owner?.DiagnosticsRefreshRequested?.Invoke(Owner, EventArgs.Empty);
            return null;
        }

        [JsonRpcMethod(LspMethods.InlayHintRefresh)]
        public object? RefreshHints(JsonElement? request = null) => null;

        [JsonRpcMethod(LspMethods.CodeLensRefresh)]
        public object? RefreshLenses(JsonElement? request = null) => null;

        [JsonRpcMethod(LspMethods.TelemetryEvent)]
        public void Telemetry(JsonElement? message = null)
        {
        }

        /// <summary>A Razor file's HTML, which Razor sends before it asks HTML's answer at a place in it: the requests below,
        /// each as Razor received it wrapped with the file and its HTML's checksum (<see cref="RazorHtml"/>). Each goes to
        /// the HTML server when there is one; without it, a list answers empty, so Razor goes on with its own part (the C#'s
        /// formatting, its components' completion), and one answers null. The HTML is taken at once, and each request
        /// awaited apart from the messages that follow.</summary>
        [JsonRpcMethod(LspMethods.RazorUpdateHtml)]
        public object? UpdateHtml(JsonElement update)
        {
            Html.UpdateNow(update);
            return null;
        }

        [JsonRpcMethod(LspMethods.Completion)]
        public async Task<object> HtmlCompletion(JsonElement forwarded) =>
            await Html.ForwardAsync(LspMethods.Completion, forwarded).ConfigureAwait(false) is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } answer
                ? answer : Html.Completion(forwarded);

        [JsonRpcMethod(LspMethods.CompletionResolve)]
        public async Task<JsonElement?> HtmlCompletionResolve(JsonElement forwarded) =>
            await Html.ForwardAsync(LspMethods.CompletionResolve, forwarded).ConfigureAwait(false) ?? Asked(forwarded);

        [JsonRpcMethod(LspMethods.CodeActionResolve)]
        public JsonElement? HtmlCodeActionResolve(JsonElement forwarded) => Asked(forwarded);

        [JsonRpcMethod(LspMethods.Formatting)]
        public Task<object> HtmlFormatting(JsonElement forwarded) => List(LspMethods.Formatting, forwarded);

        [JsonRpcMethod(LspMethods.RangeFormatting)]
        public Task<object> HtmlRangeFormatting(JsonElement forwarded) => List(LspMethods.RangeFormatting, forwarded);

        [JsonRpcMethod(LspMethods.OnTypeFormatting)]
        public object[] HtmlOnTypeFormatting(JsonElement? forwarded = null) => [];

        [JsonRpcMethod(LspMethods.CodeAction)]
        public object[] HtmlCodeActions(JsonElement? forwarded = null) => [];

        [JsonRpcMethod(LspMethods.DocumentHighlight)]
        public Task<object> HtmlHighlights(JsonElement forwarded) => List(LspMethods.DocumentHighlight, forwarded);

        [JsonRpcMethod(LspMethods.References)]
        public Task<object> HtmlReferences(JsonElement forwarded) => List(LspMethods.References, forwarded);

        [JsonRpcMethod(LspMethods.FoldingRange)]
        public Task<object> HtmlFolds(JsonElement forwarded) => List(LspMethods.FoldingRange, forwarded);

        [JsonRpcMethod(LspMethods.DocumentColor)]
        public Task<object> HtmlColours(JsonElement forwarded) => List(LspMethods.DocumentColor, forwarded);

        [JsonRpcMethod(LspMethods.ColorPresentation)]
        public Task<object> HtmlColourPresentations(JsonElement forwarded) => List(LspMethods.ColorPresentation, forwarded);

        [JsonRpcMethod(LspMethods.Hover)]
        public Task<JsonElement?> HtmlHover(JsonElement forwarded) => Html.ForwardAsync(LspMethods.Hover, forwarded);

        [JsonRpcMethod(LspMethods.SignatureHelp)]
        public Task<JsonElement?> HtmlSignatureHelp(JsonElement forwarded) => Html.ForwardAsync(LspMethods.SignatureHelp, forwarded);

        [JsonRpcMethod(LspMethods.Definition)]
        public Task<JsonElement?> HtmlDefinition(JsonElement forwarded) => Html.ForwardAsync(LspMethods.Definition, forwarded);

        [JsonRpcMethod(LspMethods.Implementation)]
        public object? HtmlImplementation(JsonElement? forwarded = null) => null;

        [JsonRpcMethod(LspMethods.PrepareRename)]
        public object? HtmlPrepareRename(JsonElement? forwarded = null) => null;

        [JsonRpcMethod(LspMethods.Rename)]
        public Task<JsonElement?> HtmlRename(JsonElement forwarded) => Html.ForwardAsync(LspMethods.Rename, forwarded);

        [JsonRpcMethod(LspMethods.LinkedEditingRange)]
        public object? HtmlLinkedEditing(JsonElement? forwarded = null) => null;

        /// <summary>Asked by Razor after an '=' alone, for an attribute's quotes, which Passenger's typing never asks
        /// for: its tags are closed from Razor's own answer and its HTML (<see cref="LspDocument.AutoInsertAsync"/>).</summary>
        [JsonRpcMethod(LspMethods.OnAutoInsert)]
        public object? HtmlAutoInsert(JsonElement? forwarded = null) => null;

        /// <summary>A forwarded request whose answer is a list: the HTML server's, or none.</summary>
        private async Task<object> List(string method, JsonElement forwarded) =>
            await Html.ForwardAsync(method, forwarded).ConfigureAwait(false) is { ValueKind: JsonValueKind.Array } answer ? answer : Array.Empty<object>();

        /// <summary>The request Razor wrapped, which a resolve answers unchanged: HTML has nothing to add to it.</summary>
        private static JsonElement? Asked(JsonElement forwarded) =>
            JsonMembers.Value(forwarded, "request")?.Clone();
    }
}
