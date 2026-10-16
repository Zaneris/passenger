using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>C# for the open folder: Roslyn's language server started on the workspace's solution or
/// projects, its state for the status bar, and a <see cref="CSharpDocument"/> for every C# file
/// open in a tab. Files opened before the server is ready are attached once it is. A server that ends
/// on its own, or stops answering, is started again with the open files, as Rider restarts its backend, a few times at
/// most. What the server tells the person (a project that did not load or loaded with warnings, and why) is said in the
/// status bar and logged, each project's load problems kept for Problems, the load's progress shown, and each start's
/// phases timed. A UI-thread object: what the server says from its threads crosses through <see cref="UiThread"/>.</summary>
public sealed class CSharpService : IAsyncDisposable
{
    /// <summary>How long after an error the server logged a message it shows can be its reason (Roslyn logs a project's
    /// failure to load, then shows that there were problems loading it).</summary>
    private static readonly TimeSpan ReasonWindow = TimeSpan.FromSeconds(10);

    /// <summary>How many times a server that keeps ending or hanging is started again within <see cref="RestartWindow"/>.</summary>
    private const int RestartLimit = 3;

    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);

    private readonly Dictionary<OpenDocument, CSharpDocument> documents = [];
    private readonly List<DateTime> restarts = [];
    private readonly WarmServer? warm;

    /// <summary>What this service hung on its server's events, undone when the server is kept for another window.</summary>
    private readonly List<Action> unhooks = [];
    private RoslynSession? session;
    private CancellationTokenSource? starting;
    private WorkspaceModel? workspace;
    private bool disposed;

    /// <summary>A service that starts its own server and ends it with itself, or, given a <see cref="WarmServer"/>, takes
    /// up the server kept there and leaves its own there as it ends.</summary>
    public CSharpService(WarmServer? warm = null) => this.warm = warm;

    /// <summary>The projects a starting server opens before the rest, asked on the UI thread as it is ready to open them:
    /// the file shown's and those it references, so that file's answers come first.</summary>
    public Func<Task<IReadOnlyList<string>>>? FirstProjects { get; set; }

    /// <summary>The projects' design-time builds, made by the project host: a server with Passenger's project loader loads
    /// its projects from these rather than building them itself.</summary>
    public IProjectFeed? Feed { get; set; }

    /// <summary>The HTML language server, which Razor's questions about a Razor file's HTML go to (the folder's web
    /// languages'); null for Passenger's own few answers.</summary>
    public Func<CancellationToken, Task<LanguageServer?>>? HtmlServer { get; set; }

    /// <summary>Has the server load projects again whose files or imports changed (a Directory.Build.props edited), which
    /// it watches only in part; one that builds its own projects finds what it watches itself.</summary>
    public void Reload(IReadOnlyList<string> projects)
    {
        UiThread.Check();
        if (session is { Fed: true } && projects.Count > 0)
        {
            IdeErrors.Write($"C# reload requested for {projects.Count} projects: {string.Join(", ", projects.Take(8))}{(projects.Count > 8 ? ", ..." : string.Empty)}");
        }
        session?.Reload(projects);
    }

    /// <summary>The state changed (starting, loading projects, ready, failed); raised on the UI thread.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Where the language servers keep their logs, a file to each server: beside the IDE's own log, so Help, Show
    /// Log finds them, and the newest kept as servers start.</summary>
    public static string LogFolder => Path.Combine(IdeErrors.LogFolder, "roslyn");

    public string State { get; private set; } = string.Empty;
    public bool IsReady => session is { IsLoaded: true };
    public RoslynSession? Session => session;

    /// <summary>The server ended on its own or stopped answering, with what was done about it; raised on the UI thread.</summary>
    public event EventHandler<string>? Stopped;

    /// <summary>What the server tells the person (<c>window/showMessage</c>, Roslyn's toasts), with what it logged when
    /// it says to see the log (the project and the warning or error it loaded with); raised on the UI thread, for the
    /// status bar, with whether it is an error or a warning.</summary>
    public event EventHandler<ServerSaid>? Said;

    /// <summary>The problems the server met loading the projects changed; raised on the UI thread.</summary>
    public event EventHandler? ProjectProblemsChanged;

    /// <summary>The warnings and errors each project last loaded with, as the server logged them, by project.</summary>
    public IReadOnlyList<ProjectLoadProblem> ProjectProblems => [.. loadProblems.Values.SelectMany(p => p)];

    /// <summary>What each project's last load reported, by its project file; a project that loads again without any
    /// problem leaves.</summary>
    private readonly Dictionary<string, List<ProjectLoadProblem>> loadProblems = new(StringComparer.FromComparison(PathNames.Comparison));

    /// <summary>The problems of the loads under way, in the order they came: one toast says the batch had problems, the
    /// first project's, so the status bar says the rest when the batch ends.</summary>
    private readonly List<ProjectLoadProblem> batch = [];

    /// <summary>How many of <see cref="batch"/> the status bar has said.</summary>
    private int batchSaid;

    /// <summary>The project whose load problem the status bar last named, until it loads without one.</summary>
    private string? saidProject;

    /// <summary>The work the server reports under way (its projects loading), as the status bar shows it: its title and how
    /// far it is; null when there is none.</summary>
    public string? Progress { get; private set; }

    /// <summary>The first error the server logged since it last showed a message, and when: the reason a message that
    /// says to see the log is about.</summary>
    private (string Text, long At)? unsaid;

    /// <summary>The title of the work under way, from its begin: the reports that follow give only how far it is.</summary>
    private string? progressTitle;

    /// <summary>The solution's problems changed (solution-wide analysis, or an open file's); raised on the UI thread.</summary>
    public event EventHandler? SolutionProblemsChanged;

    /// <summary>The scope Roslyn's analyzers and compiler run over with solution-wide analysis on: every document.</summary>
    private static readonly IReadOnlyDictionary<string, object?> FullSolution = new Dictionary<string, object?>
    {
        ["dotnet_analyzer_diagnostics_scope"] = "fullSolution",
        ["dotnet_compiler_diagnostics_scope"] = "fullSolution",
    };

    private readonly Dictionary<(string? Source, string Uri), (string? ResultId, IReadOnlyList<Diagnostic> Items)> solutionReports = [];
    private CancellationTokenSource? analysing;
    private bool solutionWide;

    /// <summary>Rider's solution-wide analysis: every file's errors and warnings across the solution, kept current as code
    /// changes, without a build. Roslyn is asked to analyse every document, and its workspace diagnostics are pulled
    /// again after each change; an open file's are its own pull's, as they are typed.</summary>
    public bool SolutionWide
    {
        get => solutionWide;
        set
        {
            UiThread.Check();
            if (solutionWide == value)
            {
                return;
            }
            solutionWide = value;
            solutionReports.Clear();
            analysing?.Cancel();
            if (session is { } running)
            {
                foreach (string option in FullSolution.Keys)
                {
                    running.Server.SetOption(option, value ? FullSolution[option] : null);
                }
            }
            StartAnalysis();
            SolutionProblemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The indentation Roslyn was told to write where no .editorconfig gives one; its own default at first.</summary>
    private IndentOptions told = new();

    /// <summary>Roslyn's indentation where no .editorconfig gives one (its fixes, completions, generated code) made the C#
    /// file being edited's, so what it writes there indents as that file's own lines do. Roslyn's options are the whole
    /// server's, so they follow the file shown; an .editorconfig's, which Roslyn reads itself, come first in any case.</summary>
    public void FollowIndent(OpenDocument? shown)
    {
        UiThread.Check();
        if (shown is not { Language.Name: "C#" } || shown.Indentation.Options == told)
        {
            return;
        }
        told = shown.Indentation.Options;
        session?.Server.SetOptions(IndentSettings(told));
    }

    /// <summary>Roslyn's options for an indentation, by the names it asks for them under.</summary>
    private static Dictionary<string, object?> IndentSettings(IndentOptions indent) => new()
    {
        ["indent_style"] = indent.UseTabs ? "tab" : "space",
        ["indent_size"] = indent.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["tab_width"] = indent.TabWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Every error and warning the solution-wide analysis has found, by file; null with it off.</summary>
    public IReadOnlyList<(string Path, Diagnostic Diagnostic)>? SolutionProblems
    {
        get
        {
            if (!solutionWide)
            {
                return null;
            }
            Dictionary<string, IReadOnlyList<Diagnostic>> byPath = [];
            foreach (((string? _, string uri), (string? _, IReadOnlyList<Diagnostic> items)) in solutionReports)
            {
                string path = DocumentUris.ToPath(uri);
                byPath[path] = byPath.TryGetValue(path, out IReadOnlyList<Diagnostic>? before) ? [.. before, .. items] : items;
            }
            // An open file's problems are its own pull's, which the workspace's leave out and which follow typing.
            foreach ((OpenDocument open, CSharpDocument features) in documents)
            {
                byPath[open.Path] = features.Diagnostics;
            }
            return [.. byPath.SelectMany(p => p.Value
                .Where(d => d.Severity is 1 or 2)
                .DistinctBy(d => (d.Range, d.Message))
                .Select(d => (p.Key, d)))];
        }
    }

    /// <summary>An open file's own diagnostics arrived: the solution's problems include them.</summary>
    internal void FileProblemsChanged()
    {
        if (solutionWide)
        {
            SolutionProblemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Pulls the workspace's diagnostics from every source the server has for them, each in a loop of its own:
    /// Roslyn holds a pull open until something changed since the results it is given, so each answer is the next
    /// change. Only the first round is background work a screenshot waits for.</summary>
    private void StartAnalysis()
    {
        analysing?.Cancel();
        if (!solutionWide || session is not { IsLoaded: true } running)
        {
            return;
        }
        CancellationTokenSource cancel = new();
        analysing = cancel;
        foreach (string? source in running.Server.WorkspaceDiagnosticSources)
        {
            _ = AnalyseAsync(running, source, cancel.Token);
        }
    }

    private async Task AnalyseAsync(RoslynSession running, string? source, CancellationToken cancel)
    {
        bool first = true;
        BackgroundWork.Begin();
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                List<PreviousResultId> previous = [.. solutionReports
                    .Where(r => r.Key.Source == source && r.Value.ResultId is not null)
                    .Select(r => new PreviousResultId(r.Key.Uri, r.Value.ResultId!))];
                DateTime asked = DateTime.UtcNow;
                IReadOnlyList<WorkspaceDocumentDiagnosticReport> reports = await running.Server.WorkspaceDiagnosticsAsync(source, previous, cancel);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }
                List<WorkspaceDocumentDiagnosticReport> changed = [.. reports.Where(r => r.Kind != "unchanged")];
                foreach (WorkspaceDocumentDiagnosticReport report in changed)
                {
                    solutionReports[(source, report.Uri)] = (report.ResultId, report.Items ?? []);
                }
                if (changed.Count > 0 || first)
                {
                    SolutionProblemsChanged?.Invoke(this, EventArgs.Empty);
                }
                if (first)
                {
                    first = false;
                    BackgroundWork.End();
                }
                // A source with nothing to hold a pull open on answers at once: it is asked again only now and then.
                if (changed.Count == 0 && DateTime.UtcNow - asked < TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Turned off, or the server let go.
        }
        catch (LanguageServerFailed)
        {
            // The server went: one started again is analysed afresh once it has loaded.
        }
        finally
        {
            if (first)
            {
                BackgroundWork.End();
            }
        }
    }

    /// <summary>Starts the server on the workspace's solution, or with none its projects: what the Explorer and the build
    /// see, so their answers agree.</summary>
    public void Start(WorkspaceModel workspace)
    {
        UiThread.Check();
        this.workspace = workspace;
        starting?.Cancel();
        CancellationTokenSource cancel = new();
        starting = cancel;
        SetState("Starting C#…");
        BackgroundWork.Begin();
        _ = Run(workspace, cancel.Token);
    }

    /// <summary>Gives an open document its C# features, now or once the server is ready: a C# file, or a Razor file
    /// (.razor, .cshtml), which the server's Razor answers for, the C# in it included.</summary>
    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (document.Language.Name is not ("C#" or "Razor") || documents.ContainsKey(document))
        {
            return;
        }
        CSharpDocument features = new(this, document);
        documents[document] = features;
        if (session is not null)
        {
            features.Connect(session);
        }
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out CSharpDocument? features))
        {
            features.Dispose();
        }
    }

    public CSharpDocument? For(OpenDocument document) => documents.GetValueOrDefault(document);

    /// <summary>Lets the open files go and ends the server, or with a <see cref="WarmServer"/> leaves a loaded one there,
    /// its files closed and nothing of this service's hung on it. The server's end (shutdown, exit, a kill when it will
    /// not go) runs on a worker thread, so the process can wait for it once the window's loop has stopped.</summary>
    public ValueTask DisposeAsync()
    {
        UiThread.Check();
        disposed = true;
        analysing?.Cancel();
        starting?.Cancel();
        foreach (CSharpDocument features in documents.Values)
        {
            features.Dispose();
        }
        documents.Clear();
        RoslynSession? ending = session;
        Unhook();
        // Kept only once its projects loaded cleanly: load problems it said, a window taking it up would never hear.
        if (warm is not null && ending is { IsLoaded: true } && !ending.Server.HasExited && loadProblems.Count == 0 && batch.Count == 0)
        {
            return new ValueTask(warm.Keep(ending));
        }
        // Not held: the window's end waits for it with no UI thread to come back to (MainWindow.Ended).
        return ending is null ? ValueTask.CompletedTask : new ValueTask(Task.Run(() => ending.DisposeAsync().AsTask()));
    }

    /// <summary>Remembers how to take one of this service's handlers off its server again; from the UI thread, or the
    /// server's own as it starts.</summary>
    private void Hook(Action unhook)
    {
        lock (unhooks)
        {
            unhooks.Add(unhook);
        }
    }

    private void Unhook()
    {
        Action[] all;
        lock (unhooks)
        {
            all = [.. unhooks];
            unhooks.Clear();
        }
        foreach (Action unhook in all)
        {
            unhook();
        }
    }

    private async Task Run(WorkspaceModel opening, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        try
        {
            string? solution = opening.Solution?.Path;
            string[] projects = [.. opening.Projects.Select(p => p.Path)];
            string logs = LogFolder;
            IndentOptions startedWith = told;
            Dictionary<string, object?> options = new(IndentSettings(startedWith));
            foreach ((string name, object? value) in solutionWide ? FullSolution : new Dictionary<string, object?>())
            {
                options[name] = value;
            }
            RoslynSession started;
            string loading = solution is not null ? Path.GetFileName(solution) : $"{projects.Length} project{(projects.Length == 1 ? "" : "s")}";
            if (warm?.Take(opening.Folder, solution, projects, options) is { } taken)
            {
                started = taken;
                Hear(started.Server);
                // What it asks for from now on is this window's to answer.
                if (started.Fed)
                {
                    started.Server.Feed = Feed;
                }
                IdeErrors.Write($"C# language server (process {started.Server.ProcessId}) taken up warm for {opening.Folder}, {loading} loaded");
            }
            else
            {
                // Started on a worker: the process, its log and the wait for its answer are no work for the window's thread.
                Func<Task<IReadOnlyList<string>>>? first = FirstProjects is { } ahead ? () => UiThread.InvokeAsync(async () =>
                {
                    IReadOnlyList<string> named = await ahead();
                    if (named.Count > 0)
                    {
                        IdeErrors.Write($"C# language server loads {string.Join(", ", named.Select(Path.GetFileNameWithoutExtension))} first, for the file shown");
                    }
                    return named;
                }) : null;
                IProjectFeed? feed = Feed;
                started = await BackgroundWork.Run(() => RoslynSession.StartAsync(opening.Folder, solution, projects, logs, cancel, options, Hear, first, feed), cancel);
                if (cancel.IsCancellationRequested)
                {
                    // Disposed, or started again, just as this server started: nothing else will end it.
                    await started.DisposeAsync();
                    return;
                }
                IdeErrors.Write($"C# language server (process {started.Server.ProcessId}) started for {opening.Folder} in {Since(began)}, loading {loading}");
                _ = TimeFirstColoursAsync(started, began);
                _ = TimeActiveFileAsync(started, began);
            }
            TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler onLoaded = (_, _) => loaded.TrySetResult();
            EventHandler onColours = (_, _) => UiThread.Post(() => Each(document => document.ColoursMoved()));
            EventHandler onDiagnostics = (_, _) => UiThread.Post(() => Each(document => document.RefreshDiagnostics()));
            // Projects loaded (the file shown's first, then the rest, or one loaded again): the files' projects may be others.
            EventHandler onProjects = (_, _) => UiThread.Post(() => Each(document => document.FindContexts()));
            EventHandler<string> onExited = (_, why) => UiThread.Post(() => OnExited(started, why));
            EventHandler<string> onHung = (_, why) => UiThread.Post(() => OnNotResponding(started, why));
            started.Loaded += onLoaded;
            started.Server.ColoursRefreshRequested += onColours;
            started.Server.DiagnosticsRefreshRequested += onDiagnostics;
            started.Server.ProjectsLoaded += onProjects;
            started.Server.Exited += onExited;
            started.Server.NotResponding += onHung;
            started.Server.HtmlServer = HtmlServer;
            Hook(() =>
            {
                // A server kept for another window asks that window's HTML server, or none.
                started.Server.HtmlServer = null;
                started.Loaded -= onLoaded;
                started.Server.ColoursRefreshRequested -= onColours;
                started.Server.DiagnosticsRefreshRequested -= onDiagnostics;
                started.Server.ProjectsLoaded -= onProjects;
                started.Server.Exited -= onExited;
                started.Server.NotResponding -= onHung;
            });
            session = started;
            if (told != startedWith)
            {
                started.Server.SetOptions(IndentSettings(told));
            }
            SetState(started.Solution is { } opened ? $"Loading {Path.GetFileName(opened)}…" : "Loading projects…");
            foreach (CSharpDocument features in documents.Values)
            {
                features.Connect(started);
            }
            if (!started.IsLoaded)
            {
                // A wait with no fault to leave behind: a window gone meanwhile (a test's, its dispatcher with it) never hears its end.
                await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromMinutes(5), cancel));
                cancel.ThrowIfCancellationRequested();
                if (!loaded.Task.IsCompleted)
                {
                    IdeErrors.Write($"C# language server (process {started.Server.ProcessId}) had not loaded {loading} after 5 minutes");
                    Progress = null;
                    SetState("C# unavailable: the projects did not load in 5 minutes (the language server's log says where it stopped)");
                    return;
                }
            }
            IdeErrors.Write($"C# language server (process {started.Server.ProcessId}) loaded {loading} in {Since(began)}");
            SetState("C# ready");
            RefreshAll();
            StartAnalysis();
        }
        catch (Exception error) when (error is OperationCanceledException || cancel.IsCancellationRequested)
        {
            // Given up (another folder opened, the window closed): however its end came, there is nothing to say of it.
        }
        catch (Exception error)
        {
            string why = error is LanguageServerFailed failed ? failed.Reason : error.Message;
            IdeErrors.Write($"The C# language server could not start for {opening.Folder}: {error}");
            Progress = null;
            SetState($"C# unavailable: {why}");
        }
        finally
        {
            Dispatcher.UIThread.Post(BackgroundWork.End);
        }
    }

    /// <summary>Listens to a server as it starts, before it is asked to load anything (or as a warm one is taken up); on the
    /// server's thread, where its errors and warnings go to the log at once and only what the window shows crosses to it.</summary>
    private void Hear(LanguageServer server)
    {
        EventHandler<LogMessageParams> logged = (_, message) =>
        {
            if (message.Type is 1 or 2)
            {
                IdeErrors.Write($"C# language server (process {server.ProcessId}) {(message.Type == 1 ? "error" : "warning")}: {message.Message}");
            }
            if (message.Type == 1)
            {
                long at = Stopwatch.GetTimestamp();
                UiThread.Post(() => unsaid ??= (message.Message, at));
            }
            if (LoadReport.Read(message.Message) is { } report)
            {
                UiThread.Post(() => OnLoadReport(server, report));
            }
        };
        EventHandler<LogMessageParams> shown = (_, message) =>
        {
            IdeErrors.Write($"C# language server (process {server.ProcessId}) says: {message.Message}");
            UiThread.Post(() => OnShown(server, message));
        };
        EventHandler<WorkProgress> progressed = (_, progress) => UiThread.Post(() => OnProgress(server, progress));
        server.Logged += logged;
        server.Shown += shown;
        server.Progressed += progressed;
        Hook(() =>
        {
            server.Logged -= logged;
            server.Shown -= shown;
            server.Progressed -= progressed;
        });
    }

    /// <summary>A message the server shows: in the status bar, an error as an error and a warning as a warning. Roslyn's
    /// toast that there were problems loading a project and to see the log is said as what it logged (the project and
    /// its warning or error); another that says to see the log for an error is the error it logged just before.</summary>
    private void OnShown(LanguageServer from, LogMessageParams message)
    {
        if (disposed || (session is { } current && current.Server != from))
        {
            return;
        }
        (string Text, long At)? reason = unsaid;
        unsaid = null;
        if (message.Type is 1 or 2 && batch.Count > batchSaid)
        {
            SayLoadProblems();
            return;
        }
        ProblemSeverity? severity = message.Type switch { 1 => ProblemSeverity.Error, 2 => ProblemSeverity.Warning, _ => null };
        Said?.Invoke(this, new ServerSaid(message.Type == 1 && reason is { } error && Stopwatch.GetElapsedTime(error.At) < ReasonWindow
            ? Readable(error.Text, workspace?.Folder)
            : message.Message, severity));
    }

    /// <summary>What the server logged of a project's load: a warning or error replaces what the project's last load
    /// reported (the first of a batch) or joins it; a load with none takes them away; a batch's end says any problems the
    /// toast did not.</summary>
    private void OnLoadReport(LanguageServer from, LoadReport report)
    {
        if (disposed || (session is { } current && current.Server != from))
        {
            return;
        }
        switch (report)
        {
            case { Kind: LoadReportKind.Problem, Problem: { } problem }:
                if (!batch.Any(p => PathNames.Same(p.Project, problem.Project)) || !loadProblems.ContainsKey(problem.Project))
                {
                    loadProblems[problem.Project] = [];
                }
                if (!loadProblems[problem.Project].Contains(problem))
                {
                    loadProblems[problem.Project].Add(problem);
                }
                batch.Add(problem);
                ProjectProblemsChanged?.Invoke(this, EventArgs.Empty);
                break;
            case { Kind: LoadReportKind.Loaded, Project: { } project }:
                if (loadProblems.Remove(project))
                {
                    ProjectProblemsChanged?.Invoke(this, EventArgs.Empty);
                }
                // The project the status bar named loads cleanly now (its vulnerable package updated): it says so.
                if (saidProject is { } named && PathNames.Same(named, project))
                {
                    saidProject = null;
                    Said?.Invoke(this, new ServerSaid($"{Shown(project)} loaded without problems", null));
                }
                break;
            case { Kind: LoadReportKind.BatchEnded }:
                if (batch.Count > batchSaid)
                {
                    SayLoadProblems();
                }
                batch.Clear();
                batchSaid = 0;
                break;
        }
    }

    /// <summary>The status bar's word on the batch's load problems: the first error, else the first warning, by project and
    /// in the server's words, and how many more Problems lists.</summary>
    private void SayLoadProblems()
    {
        batchSaid = batch.Count;
        ProjectLoadProblem first = batch.OrderBy(p => p.Severity).First();
        saidProject = first.Project;
        int more = batch.Distinct().Count() - 1;
        string kind = first.Severity == ProblemSeverity.Error ? "Error" : "Warning";
        string rest = more == 0 ? string.Empty : $"{(first.Message.EndsWith('.') ? " " : "; ")}Problems lists {more} more";
        Said?.Invoke(this, new ServerSaid($"{kind} while loading {Shown(first.Project)}: {first.Message}{rest}", first.Severity));
    }

    /// <summary>A project as the status bar names it: its path from the open folder, else its file's name.</summary>
    private string Shown(string project) => workspace is { } open && PathNames.IsUnder(project, open.Folder)
        ? Path.GetRelativePath(open.Folder, project)
        : Path.GetFileName(project);

    private void OnProgress(LanguageServer from, WorkProgress progress)
    {
        if (disposed || (session is { } current && current.Server != from))
        {
            return;
        }
        if (progress.Ended)
        {
            progressTitle = null;
            Progress = null;
        }
        else
        {
            progressTitle = progress.Title ?? progressTitle;
            string what = (progressTitle ?? progress.Message ?? "Working").TrimEnd('.', '…');
            Progress = progress.Percentage is { } percent ? $"{what}… {percent}%" : $"{what}…";
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The last phase of a start the log times: the first colours the server gives an open file.</summary>
    private async Task TimeFirstColoursAsync(RoslynSession started, long began)
    {
        // A wait with no fault to leave behind, never holding a screenshot: a folder may have no C# file open for a long while.
        await Task.WhenAny(started.FirstColours, Task.Delay(TimeSpan.FromMinutes(10)));
        if (started.FirstColours.IsCompleted && !disposed && session == started)
        {
            IdeErrors.Write($"C# language server (process {started.Server.ProcessId}) gave an open file its colours {Since(began)} after it was started");
        }
    }

    /// <summary>The phases of a start that say when the person can work: the open file in its own project at the server
    /// (not Miscellaneous Files, whose answers know nothing of the solution), then that project's colours and diagnostics
    /// for it. Asked every tenth of a second until then, for the file shown first.</summary>
    private async Task TimeActiveFileAsync(RoslynSession started, long began)
    {
        // A wait that never holds a screenshot: a folder may have no C# file open for a long while.
        DateTime until = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (!disposed && (session is null || session == started) && DateTime.UtcNow < until && !started.Server.HasExited)
        {
            if (documents.Values.FirstOrDefault(d => d.Lsp is { IsRazor: false }) is { Lsp: { } lsp })
            {
                try
                {
                    if ((await lsp.ProjectContextsAsync())?.Default is { IsMiscellaneous: false } project)
                    {
                        string placed = Since(began);
                        await lsp.SemanticTokensAsync();
                        string coloured = Since(began);
                        await lsp.DiagnosticsAsync();
                        IdeErrors.Write($"C# language server (process {started.Server.ProcessId}) had {Path.GetFileName(DocumentUris.ToPath(lsp.Uri))} in {project.Label} {placed}, its colours {coloured} and its diagnostics {Since(began)} after it was started");
                        return;
                    }
                }
                catch (LanguageServerFailed)
                {
                    // Not yet able to say (a request cancelled as projects load), or gone: asked again, or the loop ends.
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private static string Since(long began) => $"{Stopwatch.GetElapsedTime(began).TotalSeconds:0.00} s";

    /// <summary>A message the server logged, as the status bar says it: without its tags ("[project/open]"), the open
    /// folder's paths made relative, its spaces single, and without MSBuild's repeat of the file at its end.</summary>
    private static string Readable(string logged, string? folder)
    {
        string text = Regex.Replace(logged, @"^\s*(\[[^\]]*\]\s*)+", string.Empty);
        if (folder is not null)
        {
            text = text.Replace(folder + Path.DirectorySeparatorChar, string.Empty, StringComparison.Ordinal);
        }
        text = Regex.Replace(text, @"\s+", " ").Trim();
        int last = text.LastIndexOf(". ", StringComparison.Ordinal);
        return last > 0 && text[..last].Contains(text[(last + 2)..], StringComparison.Ordinal) ? text[..(last + 1)] : text;
    }

    /// <summary>The server ended on its own: it crashed, or something else ended it.</summary>
    private void OnExited(RoslynSession lost, string why)
    {
        if (disposed || session != lost || workspace is null)
        {
            return;
        }
        IdeErrors.Write($"The C# language server (process {lost.Server.ProcessId}) ended: {why}");
        Replace(lost, workspace, "The C# language server ended");
    }

    /// <summary>The server stopped answering: it is ended at once, so every request waiting on it fails rather than
    /// waiting for ever, and replaced as one that ended is, the status bar saying so as Rider's does.</summary>
    private void OnNotResponding(RoslynSession hung, string why)
    {
        if (disposed || session != hung || workspace is null)
        {
            return;
        }
        IdeErrors.Write($"The C# language server (process {hung.Server.ProcessId}) is not responding: {why}");
        hung.Server.Kill();
        Replace(hung, workspace, "C# server not responding");
    }

    /// <summary>A server gone: the open files go back to the parser's colours and are opened again in a new server,
    /// unless servers have gone too often to be worth it.</summary>
    private void Replace(RoslynSession lost, WorkspaceModel restarting, string what)
    {
        session = null;
        Progress = null;
        progressTitle = null;
        unsaid = null;
        ForgetLoadProblems();
        analysing?.Cancel();
        solutionReports.Clear();
        _ = lost.DisposeAsync();
        foreach (OpenDocument open in documents.Keys.ToList())
        {
            Detach(open);
            Attach(open);
        }
        DateTime now = DateTime.UtcNow;
        restarts.RemoveAll(at => now - at > RestartWindow);
        if (restarts.Count >= RestartLimit)
        {
            SetState("C# stopped");
            Stopped?.Invoke(this, $"{what} {RestartLimit + 1} times in {RestartWindow.TotalMinutes:0} minutes, so it was left stopped; the IDE's log says why");
            return;
        }
        restarts.Add(now);
        Stopped?.Invoke(this, $"{what}; starting it again");
        Start(restarting);
    }

    /// <summary>A server gone: one started again loads every project afresh and reports their problems again.</summary>
    private void ForgetLoadProblems()
    {
        batch.Clear();
        batchSaid = 0;
        saidProject = null;
        if (loadProblems.Count > 0)
        {
            loadProblems.Clear();
            ProjectProblemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshAll() => Each(document => document.Refresh());

    private void Each(Action<CSharpDocument> act)
    {
        foreach (CSharpDocument features in documents.Values)
        {
            act(features);
        }
    }

    /// <summary>Set at once, not posted: what asks the server as it starts again says why it cannot from <see cref="State"/>.</summary>
    private void SetState(string state)
    {
        UiThread.Check();
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the status bar says when a command (<paramref name="what"/>) asks the server before it is ready.</summary>
    public string Unready(string what) => $"{what} needs the C# language server: {State}";
}
