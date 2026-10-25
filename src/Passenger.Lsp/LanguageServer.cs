using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using StreamJsonRpc;

namespace Passenger.Lsp;

/// <summary>A language server process and the JSON-RPC connection to it over its standard input and
/// output. Requests and notifications go out typed, and each failure of the server's comes back as a
/// <see cref="LanguageServerFailed"/>, the connection's own exceptions never past this class; what the server asks of
/// the client (its settings, a capability it registers, a refresh) is answered here and surfaced as events, each raised
/// on any thread, as is what it tells the person (<see cref="Logged"/>, <see cref="Shown"/>, <see cref="Progressed"/>).
/// Its standard error and its own logs go to <see cref="LogFolder"/>, and with <see cref="Trace"/> set every message.</summary>
public sealed class LanguageServer : IAsyncDisposable
{
    /// <summary>How long a request waits before the server is checked: one this old may be slow work (usages across a
    /// large solution) or a server that will never answer. Settable, with <see cref="ResponseLimit"/>, so a test waits
    /// them out quickly.</summary>
    public TimeSpan SlowRequest { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the check may go unanswered before the server is called not responding. Roslyn answers it in
    /// milliseconds, through the queue every request goes through, even while it loads projects or runs a long request.</summary>
    public TimeSpan ResponseLimit { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The check: the folding ranges of a document never opened, which Roslyn answers with none.</summary>
    private static readonly FoldingRangeParams Check = new(new TextDocumentIdentifier("file:///passenger-is-the-server-answering.cs"));

    /// <summary>Servers started in this IDE, for the handshake salt of each one's MSBuild nodes.</summary>
    private static int started;

    private readonly Process process;
    private readonly JsonRpc rpc;
    private readonly CountedMessages messages;
    private readonly ClientTarget client;
    private readonly ServerLog log;
    private readonly Dictionary<long, (string Method, long Since)> waiting = [];
    private readonly Dictionary<string, (int Left, TaskCompletionSource Spent)> refusing = [];
    private readonly Timer watch;
    private long requests;
    private long partialResults;
    private long checkedAt;
    private int checking;
    private volatile bool hung;
    private volatile bool killed;
    private volatile bool disposing;
    private volatile string? ended;

    /// <summary>The server's name among the requests in flight (<see cref="InFlight"/>).</summary>
    private readonly string who;

    private readonly Dictionary<string, object?> options;

    /// <summary>What <c>workspace/configuration</c> answers for a section, for a server that is not Roslyn; null for
    /// Roslyn's options (<see cref="Option"/>).</summary>
    private readonly Func<string?, object?>? configure;

    /// <summary>What <c>vscode/content</c> answers (a JSON schema by its URI); null for a server that asks none.</summary>
    private readonly Func<string, CancellationToken, Task<string>>? content;

    /// <summary>What makes an edit the server asks for (<c>workspace/applyEdit</c>); null refuses every one.</summary>
    private readonly Func<JsonElement, Task<string?>>? applyEdit;

    private LanguageServer(Process process, JsonRpc rpc, CountedMessages messages, ClientTarget client, ServerLog log, string logFolder,
        IReadOnlyDictionary<string, object?>? options, Func<string?, object?>? configure = null, Func<string, CancellationToken, Task<string>>? content = null,
        Func<JsonElement, Task<string?>>? applyEdit = null)
    {
        this.configure = configure;
        this.content = content;
        this.applyEdit = applyEdit;
        this.options = new Dictionary<string, object?>(options ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
        this.process = process;
        this.rpc = rpc;
        this.messages = messages;
        this.client = client;
        this.log = log;
        LogFolder = logFolder;
        ProcessId = process.Id;
        who = $"{Named(process.StartInfo)}, process {process.Id}";
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

    /// <summary>The copy of Roslyn's server this one runs from, in the state's cache (<see cref="RoslynServerFolder"/>); null
    /// where it runs from the app's own roslyn/, and for every other server.</summary>
    public string? ServerCopy { get; private set; }

    /// <summary>The server's process id, kept once the process is gone.</summary>
    public int ProcessId { get; }

    /// <summary>A message the server logged (<c>window/logMessage</c>), every one of which is in its own log too; raised on
    /// any thread.</summary>
    public event EventHandler<LogMessageParams>? Logged;

    /// <summary>A message the server shows the person: <c>window/showMessage</c>, or Roslyn's toast. Raised on any
    /// thread.</summary>
    public event EventHandler<LogMessageParams>? Shown;

    /// <summary>Work the server reports the progress of (<c>$/progress</c>: Roslyn's project loads); raised on any thread.</summary>
    public event EventHandler<WorkProgress>? Progressed;

    /// <summary>Where every message to and from every server goes while set, with the server's process and an arrow
    /// for its direction ("-->" to the server); called on the connection's threads.</summary>
    public static Action<int, string>? Trace { get; set; }

    /// <summary>Roslyn has finished loading the projects it was asked to open; raised on any thread.</summary>
    public event EventHandler? ProjectsLoaded;

    /// <summary>Where Passenger's project loader in the server gets the projects' design-time builds it asks for.</summary>
    public IProjectFeed? Feed { get; set; }

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

    /// <summary>The HTML server's diagnostics of a Razor file's HTML, on the lines it has as the file does.</summary>
    internal Task<IReadOnlyList<Diagnostic>> RazorHtmlDiagnosticsAsync(string path, string text, CancellationToken cancel) =>
        client.Html.DiagnosticsAsync(path, text, cancel);

    /// <summary>The sources that answer for the whole workspace (<c>workspace/diagnostic</c>), as well as for one document.</summary>
    public IReadOnlyList<string?> WorkspaceDiagnosticSources => client.WorkspaceDiagnosticSources;

    /// <summary>Changes one of the server's options while it runs (a setting turned on): it hears the configuration
    /// changed and asks for it again. A server that has stopped is not told: one started again reads its options afresh.</summary>
    public void SetOption(string name, object? value) => SetOptions(new Dictionary<string, object?> { [name] = value });

    /// <summary>The options set on the server as it stands: those it started with and every change since.</summary>
    public IReadOnlyDictionary<string, object?> Options
    {
        get
        {
            lock (options)
            {
                return new Dictionary<string, object?>(options, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Changes several of the server's options at once, which it asks for again once.</summary>
    public void SetOptions(IReadOnlyDictionary<string, object?> changed)
    {
        lock (options)
        {
            foreach ((string name, object? value) in changed)
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
        client.Partials[token] = value =>
        {
            if (value.Deserialize<WorkspaceDiagnosticReport>(Json.Options) is { Items.Count: > 0 } report)
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

    /// <summary>An option's value: one changed while running, else Passenger's own, else null for the server's default.</summary>
    private object? Option(string name)
    {
        lock (options)
        {
            if (options.TryGetValue(name, out object? set))
            {
                return set;
            }
        }
        return Settings.TryGetValue(name, out object? value) ? value : null;
    }

    /// <summary>The process has ended, or been ended by <see cref="Kill"/>, or let go of (<see cref="DisposeAsync"/>).</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return killed || process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // Disposed: the process is no longer this server's to ask about.
                return true;
            }
        }
    }

    /// <summary>Starts the Roslyn language server Passenger ships (under <c>roslyn/</c> beside the app, run from its copy
    /// in <paramref name="stateCache"/> where it may not write there, <see cref="RoslynServerFolder"/>), initializes it for
    /// a folder, and returns once it has said what it can do. Cancelled or failed, it leaves no process behind, at once
    /// rather than once the server answers.</summary>
    public static async Task<LanguageServer> StartRoslynAsync(string folder, string logFolder, CancellationToken cancel, IReadOnlyDictionary<string, object?>? options = null,
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
            server, "--stdio", "--logLevel", "Information", "--telemetryLevel", "off",
            "--extensionLogDirectory", logFolder, "--clientProcessId", Environment.ProcessId.ToString(),
        ];
        if (File.Exists(RoslynExtension.Assembly))
        {
            arguments.AddRange(["--extension", RoslynExtension.Assembly]);
        }
        ProcessStartInfo start = ChildProcesses.DotnetCommand(arguments);
        // A server's MSBuild nodes are its own: a restart ends them, which would cut short a build that took one up.
        start.Environment["MSBUILDNODEHANDSHAKESALT"] += $"-server-{Interlocked.Increment(ref started)}";
        // The server's restore takes no properties, and off Windows a WPF project restores only with this (NETSDK1100).
        if (!OperatingSystem.IsWindows() && !start.Environment.ContainsKey("EnableWindowsTargeting"))
        {
            start.Environment["EnableWindowsTargeting"] = "true";
        }
        LanguageServer server2 = await StartCoreAsync(start, folder, logFolder, ClientCapabilities.Value, null, options, null, null, null, cancel).ConfigureAwait(false);
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
        return StartCoreAsync(start.Process, start.Folder, start.LogFolder, start.Capabilities, start.InitializationOptions, null,
            start.Configuration ?? (_ => null), start.Content, start.ApplyEdit, cancel);
    }

    private static async Task<LanguageServer> StartCoreAsync(ProcessStartInfo start, string folder, string logFolder, JsonElement capabilities,
        object? initializationOptions, IReadOnlyDictionary<string, object?>? options, Func<string?, object?>? configure,
        Func<string, CancellationToken, Task<string>>? content, Func<JsonElement, Task<string?>>? applyEdit, CancellationToken cancel)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process process = ChildProcesses.Start(start);
        // Every message passes the trace from initialize on, so one turned on while the server runs starts at the next.
        TracedStream input = new(process.StandardInput.BaseStream, process.Id, "-->");
        TracedStream output = new(process.StandardOutput.BaseStream, process.Id, "<--", across: input);
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
        process.ErrorDataReceived += (_, e) => log.Write(e.Data);
        process.BeginErrorReadLine();

        SystemTextJsonFormatter formatter = new() { JsonSerializerOptions = Json.Options };
        CountedMessages handler = new(input, output, formatter);
        ClientTarget client = new();
        // One at a time, in order: a progress report handled after its end would leave the load showing.
        JsonRpc rpc = new(handler) { SynchronizationContext = new Microsoft.VisualStudio.Threading.NonConcurrentSynchronizationContext(sticky: false) };
        rpc.AddLocalRpcTarget(client, new JsonRpcTargetOptions { UseSingleObjectParameterDeserialization = true });
        LanguageServer server2 = new(process, rpc, handler, client, log, logFolder, options, configure, content, applyEdit);
        client.Owner = server2;
        rpc.StartListening();

        try
        {
            string root = DocumentUris.FromPath(folder);
            InitializeParams initialize = new(Environment.ProcessId, root, [new WorkspaceFolder(root, Path.GetFileName(folder))], capabilities,
                InitializationOptions: initializationOptions);
            // Given up at once, not cancelled (whose $/cancelRequest can fail unobserved as the connection goes): it fails with the connection, unseen.
            Task<JsonElement> answer = rpc.InvokeWithParameterObjectAsync<JsonElement>(LspMethods.Initialize, initialize, CancellationToken.None);
            _ = answer.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            JsonElement result = await answer.WaitAsync(cancel).ConfigureAwait(false);
            server2.Capabilities = result.GetProperty("capabilities");
            if (server2.Capabilities.TryGetProperty("semanticTokensProvider", out JsonElement tokens) && tokens.TryGetProperty("legend", out JsonElement legend))
            {
                server2.Legend = legend.Deserialize<SemanticTokensLegend>(Json.Options);
            }
            cancel.ThrowIfCancellationRequested();
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

    /// <summary>Ends a server whose start was given up or failed, at once, with its log: there is no shutdown to ask it for.</summary>
    private void Abandon()
    {
        disposing = true;
        watch.Dispose();
        rpc.Dispose();
        End(process);
        log.Dispose();
    }

    /// <summary>Whether the server said it answers a request (its capability for it, such as <c>renameProvider</c>, there
    /// and not false).</summary>
    public bool Provides(string capability) =>
        Capabilities.ValueKind == JsonValueKind.Object && Capabilities.TryGetProperty(capability, out JsonElement value)
        && value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>The characters after which the server offers completion unasked (<c>completionProvider.triggerCharacters</c>).</summary>
    public IReadOnlyList<string> CompletionTriggers =>
        Capabilities.ValueKind == JsonValueKind.Object && Capabilities.TryGetProperty("completionProvider", out JsonElement provider)
        && provider.ValueKind == JsonValueKind.Object && provider.TryGetProperty("triggerCharacters", out JsonElement triggers) && triggers.ValueKind == JsonValueKind.Array
            ? [.. triggers.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!)]
            : [];

    /// <summary>Ends a server that never finished starting, at once: there is no shutdown to ask it for.</summary>
    private static void End(Process process)
    {
        ChildProcesses.Kill(process);
        process.Dispose();
    }

    /// <summary>A request someone waits for the answer to: one still waiting after <see cref="SlowRequest"/> has the
    /// server checked, and raises <see cref="NotResponding"/> if that goes unanswered too. A
    /// <see cref="LanguageServerFailed"/> when the server does not answer it.</summary>
    public async Task<T?> RequestAsync<T>(string method, object parameters, CancellationToken cancel = default)
    {
        long id = Interlocked.Increment(ref requests);
        lock (waiting)
        {
            waiting[id] = (method, Stopwatch.GetTimestamp());
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
            Task<T?> answer = rpc.InvokeWithParameterObjectAsync<T?>(method, parameters, cancel);
            try
            {
                return await answer.WaitAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                _ = answer.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

    /// <summary>The connection's exception as the server's failure: gone (it crashed, was ended for hanging or let go for
    /// one started again, the connection disposed or its pipe closed), refused (an error it answered with), or cancelled
    /// by the server rather than the caller (Roslyn cancels what a change made stale). Null for anything else: the
    /// caller's own cancellation, or a bug (an answer that does not read as asked), which must surface as itself.</summary>
    private LanguageServerFailed? Failure(string method, Exception error, CancellationToken cancel) => error switch
    {
        OperationCanceledException when cancel.IsCancellationRequested => null,
        OperationCanceledException => new LanguageServerFailed("the language server cancelled it", method, error),
        ConnectionLostException or ObjectDisposedException or IOException => new LanguageServerFailed(
            killed ? "the language server stopped responding, so it was ended" : "the language server stopped before it answered", method, error),
        RemoteRpcException refused => new LanguageServerFailed($"the language server refused it: {refused.Message.Split('\n')[0].Trim()}", method, error),
        _ => null,
    };

    /// <summary>Makes a request nothing waits on the answer to (Razor's word that a file closed), from the thread pool, where
    /// its failure is caught: awaited on a window's thread as it closes, the failure would be left to a dispatcher that never
    /// runs again, unobserved.</summary>
    internal void Tell(string method, object parameters) => UnawaitedWork.Observe(Task.Run(async () =>
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
    public void Notify(string method, object parameters) => UnawaitedWork.Observe(NotifyOrDropAsync(method, parameters));

    private async Task NotifyOrDropAsync(string method, object parameters)
    {
        try
        {
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
        // Its tree and its process group, so a BuildHost it was loading with goes too.
        ChildProcesses.Kill(process);
    }

    /// <summary>Checks the server, at most once in <see cref="SlowRequest"/>, while a request has waited that long; runs
    /// on the timer's thread each second.</summary>
    private void Watch()
    {
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
            await Task.Run(AnswerAsync).WaitAsync(ResponseLimit);
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
        }
        catch (Exception error) when (error is RemoteRpcException or ObjectDisposedException or IOException or OperationCanceledException)
        {
            // An error is an answer too, and a connection gone is Exited's to report.
        }
    }

    /// <summary>Runs one of the server's own commands (<c>workspace/executeCommand</c>, as ESLint's fixes are), which may ask
    /// the client to make an edit on the way (<see cref="ServerStart.ApplyEdit"/>).</summary>
    public Task<JsonElement> ExecuteCommandAsync(LspCommand command, CancellationToken cancel = default) =>
        RequestAsync<JsonElement>(LspMethods.ExecuteCommand, new ExecuteCommandParams(command.Command, command.Arguments ?? []), cancel);

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
        // However it goes, a write into the pipes closed under it included (Node's SIGPIPE, 141), it did not fail.
        ChildProcesses.Ending(process);
        watch.Dispose();
        try
        {
            if (!HasExited)
            {
                using CancellationTokenSource limit = new(TimeSpan.FromSeconds(3));
                await rpc.InvokeWithCancellationAsync<object?>(LspMethods.Shutdown, [], limit.Token).ConfigureAwait(false);
                await rpc.NotifyAsync(LspMethods.Exit).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ConnectionLostException or RemoteRpcException or ObjectDisposedException)
        {
        }
        rpc.Dispose();
        try
        {
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        // Killed whether it went or not: a server that exits can leave its BuildHost running, in its process group.
        ChildProcesses.Kill(process);
        process.Dispose();
        log.Dispose();
    }

    /// <summary>The server's options Passenger sets: parameter name hints before a literal, a null or an indexer's argument,
    /// never where the argument already says the name; type hints on a <c>var</c>, a lambda's parameter and a target-typed
    /// <c>new</c>, none for a collection expression.</summary>
    private static readonly Dictionary<string, object?> Settings = new(StringComparer.Ordinal)
    {
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
        public static ServerLog Begin(string folder, int processId)
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
            return new ServerLog(Path.Combine(folder, $"server-{DateTime.Now:yyyyMMdd-HHmmss}-{processId}.log"));
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
        public System.Collections.Concurrent.ConcurrentDictionary<string, Action<JsonElement>> Partials { get; } = new(StringComparer.Ordinal);

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
            registration.RegisterOptions is { ValueKind: JsonValueKind.Object } options && options.TryGetProperty("documentSelector", out JsonElement selector)
            && selector.ValueKind == JsonValueKind.Array && selector.EnumerateArray().Any(filter =>
                filter.ValueKind == JsonValueKind.Object && filter.TryGetProperty("language", out JsonElement language) && language.GetString() == "aspnetcorerazor");

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

        [JsonRpcMethod(LspMethods.LogMessage)]
        public void LogMessage(LogMessageParams message)
        {
            Owner?.log.Write($"{DateTime.Now:HH:mm:ss.fff} {message.Message}");
            Owner?.Logged?.Invoke(Owner, message);
        }

        [JsonRpcMethod(LspMethods.ShowMessage)]
        public void ShowMessage(LogMessageParams message)
        {
            Owner?.log.Write($"{DateTime.Now:HH:mm:ss.fff} Shown: {message.Message}");
            Owner?.Shown?.Invoke(Owner, message);
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
                JsonValueKind.Object when asked.TryGetProperty("uri", out JsonElement named) && named.ValueKind == JsonValueKind.String => named.GetString(),
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

        /// <summary>An edit a server's own command makes (ESLint's fixes): made by what its start names, else refused.
        /// Awaited apart from the messages that follow.</summary>
        [JsonRpcMethod(LspMethods.ApplyEdit)]
        public async Task<object> ApplyEdit(JsonElement request)
        {
            if (Owner?.applyEdit is not { } apply || request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("edit", out JsonElement edit))
            {
                return new { applied = false, failureReason = "Passenger does not run commands that edit through the server" };
            }
            string? refused = await apply(edit.Clone()).ConfigureAwait(false);
            return refused is null ? new { applied = true } : new { applied = false, failureReason = refused };
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
            if (Owner is { } owner && version.ValueKind == JsonValueKind.Object && version.TryGetProperty("version", out JsonElement number)
                && number.ValueKind == JsonValueKind.String)
            {
                string? source = version.TryGetProperty("source", out JsonElement from) && from.ValueKind == JsonValueKind.String ? from.GetString() : null;
                owner.TypeScript = (number.GetString()!, source);
                owner.TypeScriptVersion?.Invoke(owner, (number.GetString()!, source));
            }
        }

        [JsonRpcMethod(LspMethods.RegisterCapability)]
        public object? RegisterCapability(RegistrationParams request)
        {
            foreach (Registration registration in request.Registrations.Where(ForRazor))
            {
                if (registration.Method == LspMethods.SemanticTokens && registration.RegisterOptions is { } options
                    && options.TryGetProperty("legend", out JsonElement legend))
                {
                    RazorLegend = legend.Deserialize<SemanticTokensLegend>(Json.Options);
                }
            }
            // Razor's are left out: a Razor file's diagnostics are its one source, pulled as the file's own.
            foreach (Registration registration in request.Registrations.Where(r => r.Method == LspMethods.DocumentDiagnostic && !ForRazor(r)))
            {
                string? identifier = registration.RegisterOptions is { ValueKind: JsonValueKind.Object } options
                    && options.TryGetProperty("identifier", out JsonElement id) ? id.GetString() : null;
                bool workspace = registration.RegisterOptions is { ValueKind: JsonValueKind.Object } registered
                    && registered.TryGetProperty("workspaceDiagnostics", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
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

        [JsonRpcMethod(LspMethods.UnregisterCapability)]
        public object? UnregisterCapability(JsonElement? request = null) => null;

        [JsonRpcMethod(LspMethods.WorkDoneProgressCreate)]
        public object? CreateProgress(JsonElement? request = null) => null;

        [JsonRpcMethod(LspMethods.Progress)]
        public void Progress(ProgressParams progress)
        {
            if (WorkProgress.From(progress) is { } work)
            {
                Owner?.Progressed?.Invoke(Owner, work);
            }
        }

        /// <summary>A request's partial result, which the server's own progress sends with its token and value in order
        /// rather than named as work done progress names them; one no request under way asked for is dropped.</summary>
        [JsonRpcMethod(LspMethods.Progress)]
        public void Progress(JsonElement partialToken, JsonElement partialValue)
        {
            if (partialToken.ValueKind == JsonValueKind.String && partialToken.GetString() is { } token && Partials.TryGetValue(token, out Action<JsonElement>? take))
            {
                take(partialValue);
            }
        }

        [JsonRpcMethod(LspMethods.ProjectInitializationComplete)]
        public void ProjectsLoaded(JsonElement? message = null) => Owner?.ProjectsLoaded?.Invoke(Owner, EventArgs.Empty);

        /// <summary>Passenger's project loader in the server asking for a project's design-time builds, from the client's
        /// feed (its project host's builds); awaited apart from the messages that follow, which go on being handled.</summary>
        [JsonRpcMethod(Roslyn.Extension.ProjectMethods.Info)]
        public async Task<Roslyn.Extension.ProjectInfoResult> ProjectInfo(Roslyn.Extension.ProjectInfoParams asked) =>
            Owner?.Feed is { } feed
                ? await feed.InfoAsync(asked.Path, CancellationToken.None).ConfigureAwait(false)
                : new Roslyn.Extension.ProjectInfoResult(null, "Passenger has no design-time builds to give");

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
        /// formatting, its components' completion), and one answers null. Awaited apart from the messages that follow.</summary>
        [JsonRpcMethod(LspMethods.RazorUpdateHtml)]
        public async Task<object?> UpdateHtml(JsonElement update)
        {
            await Html.UpdateAsync(update).ConfigureAwait(false);
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

        [JsonRpcMethod(LspMethods.OnAutoInsert)]
        public object? HtmlAutoInsert(JsonElement? forwarded = null) => null;

        /// <summary>A forwarded request whose answer is a list: the HTML server's, or none.</summary>
        private async Task<object> List(string method, JsonElement forwarded) =>
            await Html.ForwardAsync(method, forwarded).ConfigureAwait(false) is { ValueKind: JsonValueKind.Array } answer ? answer : Array.Empty<object>();

        /// <summary>The request Razor wrapped, which a resolve answers unchanged: HTML has nothing to add to it.</summary>
        private static JsonElement? Asked(JsonElement forwarded) =>
            forwarded.ValueKind == JsonValueKind.Object && forwarded.TryGetProperty("request", out JsonElement request) ? request.Clone() : null;
    }
}
