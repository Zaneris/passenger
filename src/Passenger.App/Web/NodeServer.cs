using System.Diagnostics;
using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.App.Web;

/// <summary>One language server on the web servers' Node.js for the folder open, from its start to its end: started on
/// the Node.js found unless it cannot run there (none, one too old for its package, the server missing from the app's
/// folder), which <see cref="Unavailable"/> hears; its log heard; its end or hang told to <see cref="Lost"/> while it is
/// the one kept. A UI-thread object: what the server says from its threads crosses through <see cref="UiThread"/>.</summary>
internal sealed class NodeServer
{
    private readonly string folder;
    private readonly Func<Task<NodeRuntime>> node;
    private CancellationTokenSource? cancel;
    private bool ended;

    /// <summary>A server named as the status bar names it ("TypeScript", "ESLint"), on the Node.js
    /// <paramref name="node"/> finds, counting its restarts in <paramref name="restarts"/>.</summary>
    public NodeServer(string name, string folder, Func<Task<NodeRuntime>> node, ServerRestarts restarts)
    {
        Name = name;
        this.folder = folder;
        this.node = node;
        Restarts = restarts;
    }

    public string Name { get; }

    /// <summary>The restarts of the servers that came before this one in its place.</summary>
    public ServerRestarts Restarts { get; }

    /// <summary>The package.json whose <c>engines.node</c> says which Node.js the server needs.</summary>
    public required string Package { get; init; }

    /// <summary>How it starts on the Node.js at the path given.</summary>
    public required Func<string, ServerStart> Command { get; init; }

    /// <summary>Why this build cannot run it (a script missing from the app's folder), null when it can.</summary>
    public Func<string?>? Missing { get; init; }

    /// <summary>What it is told once it has started, before any file.</summary>
    public Func<LanguageServer, Task>? Configure { get; init; }

    /// <summary>The owner's own listeners, hung on before it is kept; without <see cref="HearsOwnLog"/>, its errors and
    /// warnings go to the IDE's log.</summary>
    public Action<LanguageSession>? Listen { get; init; }

    public bool HearsOwnLog { get; init; }

    /// <summary>It started and is kept: its files are connected to it.</summary>
    public Action<LanguageSession>? Started { get; init; }

    /// <summary>It cannot run: <see cref="Problem"/> says why, <see cref="State"/> in a few words.</summary>
    public Action? Unavailable { get; init; }

    /// <summary>It asked for every file's diagnostics again.</summary>
    public Action? Refresh { get; init; }

    /// <summary>It ended or stopped answering, as the status bar says it ("The TypeScript language server ended").</summary>
    public Action<string>? Lost { get; init; }

    public LanguageSession? Session { get; private set; }

    public Task<LanguageSession?>? Starting { get; private set; }

    /// <summary>The Node.js it runs on, once it has started.</summary>
    public NodeRuntime? Runtime { get; private set; }

    /// <summary>Why it cannot run, null while it can.</summary>
    public string? Problem { get; private set; }

    /// <summary>Whether it cannot run for want of a Node.js that runs at all, which every web server lacks alike.</summary>
    public bool WithoutNode { get; private set; }

    /// <summary>What the status bar says of it while it cannot answer: starting, ready, or why not in a few words.</summary>
    public string State { get; private set; } = string.Empty;

    public void Start()
    {
        CancellationTokenSource started = new();
        cancel = started;
        State = "starting…";
        BackgroundWork.Begin();
        Task<LanguageSession?> start = StartAsync(started.Token);
        // One that could not start said so before it returned, its end already past.
        Starting = start.IsCompleted ? null : start;
    }

    /// <summary>Left stopped without a start, for the reason given.</summary>
    public void Stop(string problem)
    {
        Problem = problem;
        State = "language server stopped";
    }

    private async Task<LanguageSession?> StartAsync(CancellationToken cancelled)
    {
        long began = Stopwatch.GetTimestamp();
        try
        {
            NodeRuntime runtime = await node();
            if (cancelled.IsCancellationRequested || ended)
            {
                return null;
            }
            if (runtime.Problem is { } none)
            {
                WithoutNode = true;
                Fail(none, runtime.Path is null || !File.Exists(runtime.Path) ? "no Node.js" : "Node.js could not run");
                return null;
            }
            if (runtime.TooOldFor(Package, $"the {Name} language server") is { } old)
            {
                Fail(old, "Node.js too old");
                return null;
            }
            if (Missing?.Invoke() is { } missing)
            {
                Fail(missing, "language server missing");
                return null;
            }
            ServerStart start = Command(runtime.Path!);
            LanguageSession started = await BackgroundWork.Run(() => LanguageSession.StartAsync(start, cancelled), cancelled);
            if (cancelled.IsCancellationRequested || ended)
            {
                IdeErrors.Observe(Task.Run(() => started.DisposeAsync().AsTask()));
                return null;
            }
            if (Configure is { } configure)
            {
                await configure(started.Server);
            }
            Hear(started);
            Session = started;
            Runtime = runtime;
            State = $"{Name} ready";
            IdeErrors.Write($"{Name} language server (process {started.Server.ProcessId}) started for {folder} on Node.js {runtime.Version} in {Stopwatch.GetElapsedTime(began).TotalSeconds:0.00} s");
            Started?.Invoke(started);
            return started;
        }
        catch (Exception error) when (error is OperationCanceledException || cancelled.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error)
        {
            string why = error is LanguageServerFailed failed ? failed.Reason : error.Message;
            IdeErrors.Write($"The {Name} language server could not start for {folder}: {error}");
            Fail($"its language server could not start: {why}", "language server could not start");
            return null;
        }
        finally
        {
            Starting = null;
            UiThread.Post(() => BackgroundWork.End());
        }
    }

    private void Fail(string problem, string label)
    {
        Problem = problem;
        State = label;
        Unavailable?.Invoke();
    }

    /// <summary>Listens to it once it has started: its log, its call to pull diagnostics again, and its end and hang,
    /// each of which ends it as the one kept.</summary>
    private void Hear(LanguageSession started)
    {
        LanguageServer server = started.Server;
        if (!HearsOwnLog)
        {
            server.Logged += (_, message) =>
            {
                if (message.Type is MessageType.Error or MessageType.Warning)
                {
                    IdeErrors.Write($"{Name} language server (process {server.ProcessId}) {(message.Type == MessageType.Error ? "error" : "warning")}: {message.Message}");
                }
            };
        }
        server.Shown += (_, message) => IdeErrors.Write($"{Name} language server (process {server.ProcessId}) says: {message.Message}");
        Listen?.Invoke(started);
        server.DiagnosticsRefreshRequested += (_, _) => UiThread.Post(() =>
        {
            if (Session == started)
            {
                Refresh?.Invoke();
            }
        });
        void Lose(string why) => UiThread.Post(() =>
        {
            if (!ended && Session == started)
            {
                IdeErrors.Write($"The {Name} language server (process {server.ProcessId}) ended: {why}");
                Lost?.Invoke($"The {Name} language server ended");
            }
        });
        server.Exited += (_, why) => Lose(why);
        // One that ended before it was listened to is lost as any that ends.
        if (server.Ended is { } gone)
        {
            Lose(gone);
        }
        server.NotResponding += (_, why) => UiThread.Post(() =>
        {
            if (!ended && Session == started)
            {
                IdeErrors.Write($"The {Name} language server (process {server.ProcessId}) is not responding: {why}");
                server.Kill();
                Lost?.Invoke($"{Name} server not responding");
            }
        });
    }

    /// <summary>Ends it, or its start: a server that still comes is ended rather than kept. The task is its process's end,
    /// on a worker, so the window's end can wait for it with no UI thread to come back to.</summary>
    public Task End()
    {
        ended = true;
        cancel?.Cancel();
        Starting = null;
        LanguageSession? ending = Session;
        Session = null;
        return ending is null ? Task.CompletedTask : Task.Run(() => ending.DisposeAsync().AsTask());
    }
}
