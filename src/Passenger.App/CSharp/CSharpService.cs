using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml.Localization;

namespace Passenger.App.CSharp;

/// <summary>C# for the open folder: Roslyn's language server started on the workspace's solution or projects, its state
/// for the status bar, and a <see cref="CSharpDocument"/> for every C# file open in a tab. Files opened before the
/// server is ready are attached once it is, a file of a project it is loading once that project has loaded. A server
/// that ends on its own, or stops answering, is started again with the open files, a few times at most. What the server
/// tells the person (a project that did not load or loaded with warnings, and why) is said in the status bar and logged,
/// each project's load problems kept for Problems, the load's progress shown, and each start's phases timed. A UI-thread
/// object: what the server says from its threads crosses through <see cref="UiThread"/>.</summary>
public sealed class CSharpService : IAsyncDisposable
{
    /// <summary>How long after an error the server logged a message it shows can be its reason (a project's failure to
    /// load is logged before the message that there were problems loading it).</summary>
    private static readonly TimeSpan ReasonWindow = TimeSpan.FromSeconds(10);

    private readonly Dictionary<OpenDocument, CSharpDocument> documents = [];
    private readonly ServerRestarts restarts = new();
    private readonly WarmServer? warm;

    /// <summary>What this service hung on its server's events, undone when the server is kept for another window.</summary>
    private readonly List<Action> unhooks = [];
    private RoslynSession? session;
    private CancellationTokenSource? starting;
    private WorkspaceModel? workspace;
    private bool disposed;

    /// <summary>When the window heard the server had loaded every project (a Stopwatch timestamp), for the start's
    /// timing.</summary>
    private (RoslynSession Session, long At)? loadedAt;

    /// <summary>The projects a server has said it loaded (a load report naming each), and that server: a file one of them
    /// holds is opened in it from then on (<see cref="ConnectLoaded"/>).</summary>
    private (LanguageServer? Server, HashSet<string> Projects) reported = (null, new HashSet<string>(PathNames.Comparer));

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
        session?.Reload(projects);
    }

    /// <summary>The state changed (starting, loading projects, ready, failed); raised on the UI thread.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The server finished loading projects (the file shown's first, then the rest, or one loaded again); raised on
    /// the UI thread.</summary>
    public event EventHandler? ProjectsLoaded;

    /// <summary>Where the language servers keep their logs, a file to each server: beside the IDE's own log, so Help, Show
    /// Log finds them, and the newest kept as servers start.</summary>
    public static string LogFolder => Path.Combine(IdeErrors.LogFolder, "roslyn");

    /// <summary>The state's cache, where the server runs from a copy when it may not write beside itself in the
    /// app.</summary>
    public static string ServerCache => Path.Combine(Passenger.App.State.StateStore.DefaultRoot, "cache");

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
    private readonly Dictionary<string, List<ProjectLoadProblem>> loadProblems = new(PathNames.Comparer);

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

    /// <summary>The solution's problems changed (solution-wide analysis, or an open file's), with the files whose problems
    /// changed, or null for every file (the analysis turned on or off, or its first answer); raised on the UI thread.</summary>
    public event EventHandler<IReadOnlyCollection<string>?>? SolutionProblemsChanged;

    /// <summary>The scope Roslyn's analyzers and compiler run over with solution-wide analysis on: every document.</summary>
    private static readonly IReadOnlyDictionary<string, object?> FullSolution = new Dictionary<string, object?>
    {
        ["dotnet_analyzer_diagnostics_scope"] = "fullSolution",
        ["dotnet_compiler_diagnostics_scope"] = "fullSolution",
    };

    /// <summary>Each file's last report by source, its URI made a path and its errors and warnings placed on the editor's
    /// lines off the UI thread as the report came (<see cref="Placed"/>).</summary>
    private readonly Dictionary<(string? Source, string Uri), WorkspaceReport> solutionReports = [];

    /// <summary>The keys of <see cref="solutionReports"/> by file, so a pull merges again only the files it changed.</summary>
    private readonly Dictionary<string, List<(string? Source, string Uri)>> reportsOf = new(PathNames.Comparer);

    /// <summary>The errors and warnings of each file the reports have any in, every source's merged, each once.</summary>
    private readonly Dictionary<string, IReadOnlyList<SolutionProblem>> analysed = new(PathNames.Comparer);
    private CancellationTokenSource? analysing;
    private bool solutionWide;

    /// <summary>The analysis's sources whose first pull has not been answered.</summary>
    private int unanswered;

    /// <summary>Whether solution-wide analysis has yet to answer its first pull: an agent's look at the solution's problems
    /// waits for it, as the Problems window fills once it has.</summary>
    public bool IsAnalysing => unanswered > 0;

    /// <summary>How long after a pull the server refused it is asked again, doubled while the server keeps refusing, up
    /// to <see cref="RefusedPullsAtMost"/>.</summary>
    private static readonly TimeSpan RefusedPull = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RefusedPullsAtMost = TimeSpan.FromSeconds(30);

    /// <summary>Solution-wide analysis: every file's errors and warnings across the solution, kept current as code
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
            ForgetReports();
            analysing?.Cancel();
            if (session is { } running)
            {
                foreach (string option in FullSolution.Keys)
                {
                    running.Server.SetOption(option, value ? FullSolution[option] : null);
                }
            }
            StartAnalysis();
            SolutionProblemsChanged?.Invoke(this, null);
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
        if (shown is null || shown.Language != Languages.Language.CSharp || shown.Indentation.Options == told)
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

    /// <summary>Every error and warning the solution-wide analysis has found, by file, each where it starts as the editor
    /// counts the file's lines; null with it off. An open file's are its own pull's, as they stand.</summary>
    public IReadOnlyList<SolutionProblem>? SolutionProblems
    {
        get
        {
            if (!solutionWide)
            {
                return null;
            }
            List<SolutionProblem> all = [];
            foreach ((string path, IReadOnlyList<SolutionProblem> problems) in analysed)
            {
                if (OpenAt(path) is null)
                {
                    all.AddRange(problems);
                }
            }
            foreach ((OpenDocument open, CSharpDocument features) in documents)
            {
                all.AddRange(OpenProblems(open, features));
            }
            return all;
        }
    }

    /// <summary>One file's errors and warnings as <see cref="SolutionProblems"/> has them, for a file it said changed.</summary>
    public IReadOnlyList<SolutionProblem> SolutionProblemsOf(string path) =>
        !solutionWide ? [] : OpenAt(path) is { } open ? OpenProblems(open.Key, open.Value) : analysed.GetValueOrDefault(path) ?? [];

    /// <summary>The open C# or Razor file at a path, with its features; null when it is not open.</summary>
    private KeyValuePair<OpenDocument, CSharpDocument>? OpenAt(string path)
    {
        foreach (KeyValuePair<OpenDocument, CSharpDocument> open in documents)
        {
            if (PathNames.Same(open.Key.Path, path))
            {
                return open;
            }
        }
        return null;
    }

    /// <summary>An open file's errors and warnings: its own pull's, which the workspace's leave out and which follow typing.</summary>
    private static List<SolutionProblem> OpenProblems(OpenDocument open, CSharpDocument features)
    {
        List<SolutionProblem> problems = [];
        HashSet<(LspRange, string)>? seen = null;
        ServerLines? lines = null;
        foreach (Diagnostic diagnostic in features.Diagnostics)
        {
            if (diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && (seen ??= []).Add((diagnostic.Range, diagnostic.Message)))
            {
                lines ??= ServerLines.Of(open.Text.Snapshot);
                problems.Add(new SolutionProblem(open.Path, diagnostic, ServerPlaces.PositionOf(lines, diagnostic.Range.Start)));
            }
        }
        return problems;
    }

    /// <summary>An open file's own diagnostics arrived: the solution's problems include them.</summary>
    internal void FileProblemsChanged(OpenDocument document)
    {
        if (solutionWide)
        {
            SolutionProblemsChanged?.Invoke(this, [document.Path]);
        }
    }

    /// <summary>The reports let go of: the analysis turned on or off, or the server gone.</summary>
    private void ForgetReports()
    {
        solutionReports.Clear();
        reportsOf.Clear();
        analysed.Clear();
    }

    /// <summary>The reports a pull changed, each URI made a path once and each file with problems read as the server read it
    /// (a file not open is the disk's), its problems placed on the editor's lines: off the UI thread, as they come.</summary>
    private static List<WorkspaceReport> Placed(IReadOnlyList<WorkspaceDocumentDiagnosticReport> changed)
    {
        Dictionary<string, string> paths = DocumentUris.ToPaths(changed.Select(r => r.Uri));
        Dictionary<string, ServerLines?> lines = new(PathNames.Comparer);
        List<WorkspaceReport> placed = new(changed.Count);
        foreach (WorkspaceDocumentDiagnosticReport report in changed)
        {
            string path = paths[report.Uri];
            List<SolutionProblem> problems = [];
            foreach (Diagnostic diagnostic in report.Items ?? [])
            {
                if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
                {
                    continue;
                }
                if (!lines.TryGetValue(path, out ServerLines? counted))
                {
                    lines[path] = counted = ServerPlaces.Differing(ServerPlaces.Read(path));
                }
                problems.Add(new SolutionProblem(path, diagnostic, ServerPlaces.PositionOf(counted, diagnostic.Range.Start)));
            }
            placed.Add(new WorkspaceReport(report.Uri, path, report.ResultId is { } id ? new PreviousResultId(report.Uri, id) : null, problems));
        }
        return placed;
    }

    /// <summary>A file's problems merged again from every source's report of it, each once.</summary>
    private void Merge(string path)
    {
        List<SolutionProblem> merged = [];
        HashSet<(LspRange, string)> seen = [];
        foreach ((string? Source, string Uri) key in reportsOf.GetValueOrDefault(path) ?? [])
        {
            foreach (SolutionProblem problem in solutionReports[key].Problems)
            {
                if (seen.Add((problem.Diagnostic.Range, problem.Diagnostic.Message)))
                {
                    merged.Add(problem);
                }
            }
        }
        if (merged.Count > 0)
        {
            analysed[path] = merged;
        }
        else
        {
            analysed.Remove(path);
        }
    }

    /// <summary>Pulls the workspace's diagnostics from every source the server has for them, each in a loop of its own,
    /// the server holding each pull open until the source's next change. Only the first round is background
    /// work.</summary>
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
            IdeErrors.Observe(AnalyseAsync(running, source, cancel.Token));
        }
    }

    /// <summary>One source's pulls, each asked again as the last answers, until the analysis is turned off or the session
    /// ends (a server started again is analysed afresh once it has loaded), each pull's reports listed as the server sends
    /// them rather than as it answers. A pull the server refuses or cuts short (Roslyn cancels one a change made stale) is
    /// asked again after a pause that grows while it keeps refusing.</summary>
    private async Task AnalyseAsync(RoslynSession running, string? source, CancellationToken cancel)
    {
        bool first = true;
        bool listed = false;
        TimeSpan pause = RefusedPull;
        BackgroundWork.Begin();
        unanswered++;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                List<PreviousResultId> previous = [.. solutionReports
                    .Where(r => r.Key.Source == source && r.Value.Previous is not null)
                    .Select(r => r.Value.Previous!)];
                DateTime asked = DateTime.UtcNow;
                Channel<IReadOnlyList<WorkspaceDocumentDiagnosticReport>> streamed = Channel.CreateUnbounded<IReadOnlyList<WorkspaceDocumentDiagnosticReport>>(
                    new UnboundedChannelOptions { SingleReader = true });
                Task<IReadOnlyList<WorkspaceDocumentDiagnosticReport>> pull = running.Server.WorkspaceDiagnosticsAsync(source, previous, reports => streamed.Writer.TryWrite(reports), cancel);
                IReadOnlyList<WorkspaceDocumentDiagnosticReport> answered = [];
                LanguageServerFailed? refused = null;
                int changed = 0;
                try
                {
                    // Roslyn sends what a pull found and holds it open until the next change, which an earlier pull may have taken.
                    Task<bool> more = streamed.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
                    while (await Task.WhenAny(pull, more) == more)
                    {
                        changed += await TakeAsync(Drain(streamed.Reader));
                        more = streamed.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
                    }
                    answered = await pull;
                }
                catch (LanguageServerFailed failed) when (!running.Server.HasExited)
                {
                    refused = failed;
                }
                finally
                {
                    streamed.Writer.TryComplete();
                    if (!pull.IsCompleted)
                    {
                        // Left as the analysis ended: its end is nobody's to hear.
                        _ = pull.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
                // What a pull sent before it answered or failed is the server's word as much as its answer.
                changed += await TakeAsync([.. Drain(streamed.Reader), .. answered]);
                cancel.ThrowIfCancellationRequested();
                if (refused is not null)
                {
                    IdeErrors.Write($"Solution-wide analysis ({source ?? "every source"}) asks again in {pause.TotalSeconds:0} s: {refused.Message}");
                    await Task.Delay(pause, cancel);
                    pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, RefusedPullsAtMost.Ticks));
                    continue;
                }
                pause = RefusedPull;
                if (first)
                {
                    if (!listed)
                    {
                        SolutionProblemsChanged?.Invoke(this, null);
                        listed = true;
                    }
                    first = false;
                    BackgroundWork.End();
                    unanswered--;
                }
                // A source with nothing to hold a pull open on answers at once: it is asked again only now and then.
                if (changed == 0 && DateTime.UtcNow - asked < TimeSpan.FromSeconds(1))
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
            // The server went for good: one started again is analysed afresh once it has loaded.
        }
        finally
        {
            if (first)
            {
                BackgroundWork.End();
                unanswered--;
            }
        }

        // The reports that changed placed off the UI thread and merged into each file's problems, which are said: how many.
        async Task<int> TakeAsync(List<WorkspaceDocumentDiagnosticReport> reports)
        {
            List<WorkspaceDocumentDiagnosticReport> changed = [.. reports.Where(r => r.Kind != "unchanged")];
            if (changed.Count == 0)
            {
                return 0;
            }
            List<WorkspaceReport> placed = await BackgroundWork.Run(() => Placed(changed), cancel);
            cancel.ThrowIfCancellationRequested();
            HashSet<string> files = new(PathNames.Comparer);
            foreach (WorkspaceReport report in placed)
            {
                if (!solutionReports.ContainsKey((source, report.Uri)))
                {
                    if (!reportsOf.TryGetValue(report.Path, out List<(string? Source, string Uri)>? keys))
                    {
                        reportsOf[report.Path] = keys = [];
                    }
                    keys.Add((source, report.Uri));
                }
                solutionReports[(source, report.Uri)] = report;
                files.Add(report.Path);
            }
            foreach (string path in files)
            {
                Merge(path);
            }
            SolutionProblemsChanged?.Invoke(this, listed ? files : null);
            listed = true;
            return changed.Count;
        }
    }

    /// <summary>Every batch of reports a pull has streamed so far, as one list.</summary>
    private static List<WorkspaceDocumentDiagnosticReport> Drain(ChannelReader<IReadOnlyList<WorkspaceDocumentDiagnosticReport>> streamed)
    {
        List<WorkspaceDocumentDiagnosticReport> all = [];
        while (streamed.TryRead(out IReadOnlyList<WorkspaceDocumentDiagnosticReport>? batch))
        {
            all.AddRange(batch);
        }
        return all;
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
        IdeErrors.Observe(Run(workspace, cancel.Token));
    }

    /// <summary>Gives an open document its C# features, now or once the server is ready: a C# file, or a Razor file
    /// (.razor, .cshtml), which the server's Razor answers for, the C# in it included.</summary>
    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (document.Language.Service != Languages.LanguageService.CSharp || documents.ContainsKey(document))
        {
            return;
        }
        CSharpDocument features = new(this, document);
        documents[document] = features;
        if (session is not null)
        {
            ConnectLoaded(session);
        }
    }

    /// <summary>Opens in the server each open file it can have in a project of its own now: every one once all the projects
    /// have loaded, before that one no project of the folder holds, or whose project the server has said it loaded. One
    /// opened while its project loads would be one of the server's Miscellaneous Files meanwhile, whose project it restores.</summary>
    private void ConnectLoaded(RoslynSession started)
    {
        foreach ((OpenDocument open, CSharpDocument features) in documents)
        {
            if (started.IsLoaded || HolderOf(open.Path) is not { } project || (reported.Server == started.Server && reported.Projects.Contains(project)))
            {
                features.Connect(started);
            }
        }
    }

    /// <summary>The project of the folder's that holds a file, by its path; null for none.</summary>
    private string? HolderOf(string path) => workspace is { } opened ? ProjectModel.Owning(opened.Projects, path)?.Path : null;

    /// <summary>A server has said a project loaded, with problems or none: its files open are opened in it.</summary>
    private void Reported(LanguageServer from, string project)
    {
        if (reported.Server != from)
        {
            reported = (from, new HashSet<string>(PathNames.Comparer));
        }
        reported.Projects.Add(project);
        if (session is { } current && current.Server == from)
        {
            ConnectLoaded(current);
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
        // Kept once its projects loaded, never in a load under way, whose problems are still coming.
        if (warm is not null && ending is { IsLoaded: true } && !ending.Server.HasExited && batch.Count == 0)
        {
            return new ValueTask(warm.Keep(ending, ProjectProblems));
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
            string loading = solution is not null ? Path.GetFileName(solution) : Plural.Of(projects.Length, "project");
            if (warm?.Take(opening.Folder, solution, projects, options) is { } taken)
            {
                started = taken.Session;
                Hear(started.Server);
                HearKept(taken.Problems);
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
                started = await BackgroundWork.Run(async () =>
                {
                    RoslynSession begun = await RoslynSession.StartAsync(opening.Folder, solution, projects, logs, cancel, options, Hear, first, feed, ServerCache);
                    // Said as its start comes back, however long the window's thread takes to get to it.
                    if (!cancel.IsCancellationRequested)
                    {
                        string copy = begun.Server.ServerCopy is { } from ? $", run from its copy in {from}" : string.Empty;
                        IdeErrors.Write($"C# language server (process {begun.Server.ProcessId}) started for {opening.Folder} in {Since(began)}{copy}, loading {loading}");
                    }
                    return begun;
                }, cancel);
                if (cancel.IsCancellationRequested)
                {
                    // Disposed, or started again, just as this server started: nothing else will end it.
                    await started.DisposeAsync();
                    return;
                }
                IdeErrors.Observe(TimeFirstColoursAsync(started, began));
                IdeErrors.Observe(TimeActiveFileAsync(started, began));
            }
            TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler onLoaded = (_, _) => loaded.TrySetResult();
            EventHandler onColours = (_, _) => UiThread.Post(() => Each(document => document.ColoursMoved()));
            EventHandler onDiagnostics = (_, _) => UiThread.Post(() => Each(document => document.RefreshDiagnostics()));
            // Projects loaded (the file shown's first, then the rest, or one loaded again): the files' projects may be others.
            EventHandler onProjects = (_, _) => UiThread.Post(() =>
            {
                Each(document => document.FindContexts());
                ProjectsLoaded?.Invoke(this, EventArgs.Empty);
            });
            EventHandler<string> onExited = (_, _) => UiThread.Post(() => OnExited(started));
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
            // One that ended before Exited was listened to (as its start came back to this thread) is replaced as any that ends.
            if (started.Server.Ended is not null)
            {
                UiThread.Post(() => OnExited(started));
            }
            if (told != startedWith)
            {
                started.Server.SetOptions(IndentSettings(told));
            }
            SetState(started.Solution is { } opened ? $"Loading {Path.GetFileName(opened)}…" : "Loading projects…");
            ConnectLoaded(started);
            if (!started.IsLoaded)
            {
                // A wait with no fault to leave behind: a window gone meanwhile never hears its end.
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
            ConnectLoaded(started);
            loadedAt = (started, Stopwatch.GetTimestamp());
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
            UiThread.Post(() => BackgroundWork.End());
        }
    }

    /// <summary>Listens to a server as it starts, before it is asked to load anything (or as a warm one is taken up); on the
    /// server's thread, where its errors and warnings go to the log at once and only what the window shows crosses to it.</summary>
    private void Hear(LanguageServer server)
    {
        EventHandler<LogMessageParams> logged = (_, message) =>
        {
            // The server's own project for files no project holds yet is restored as it is made: not a problem of the person's.
            if (message.Type == MessageType.Error || (message.Type == MessageType.Warning && !CanonicalProject.IsAbout(message.Message)))
            {
                IdeErrors.Write($"C# language server (process {server.ProcessId}) {(message.Type == MessageType.Error ? "error" : "warning")}: {message.Message}");
            }
            if (message.Type == MessageType.Error)
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
        // Said as it is heard, from its start on: one that ends before the window listens (OnExited) is in the log all the same.
        EventHandler<string> ended = (_, why) => IdeErrors.Write($"The C# language server (process {server.ProcessId}) ended: {why}");
        server.Logged += logged;
        server.Shown += shown;
        server.Progressed += progressed;
        server.Exited += ended;
        Hook(() =>
        {
            server.Logged -= logged;
            server.Shown -= shown;
            server.Progressed -= progressed;
            server.Exited -= ended;
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
        if (message.Type is MessageType.Error or MessageType.Warning && batch.Count > batchSaid)
        {
            SayLoadProblems();
            return;
        }
        ProblemSeverity? severity = message.Type switch { MessageType.Error => ProblemSeverity.Error, MessageType.Warning => ProblemSeverity.Warning, _ => null };
        Said?.Invoke(this, new ServerSaid(message.Type == MessageType.Error && reason is { } error && Stopwatch.GetElapsedTime(error.At) < ReasonWindow
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
                Reported(from, problem.Project);
                break;
            case { Kind: LoadReportKind.Loaded, Project: { } project }:
                Reported(from, project);
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

    /// <summary>The problems a server taken up warm loaded its projects with, heard as its load reported them: in Problems,
    /// and said in the status bar as its batch's end said them.</summary>
    private void HearKept(IReadOnlyList<ProjectLoadProblem> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }
        foreach (ProjectLoadProblem problem in problems)
        {
            if (!loadProblems.TryGetValue(problem.Project, out List<ProjectLoadProblem>? listed))
            {
                loadProblems[problem.Project] = listed = [];
            }
            listed.Add(problem);
            batch.Add(problem);
        }
        ProjectProblemsChanged?.Invoke(this, EventArgs.Empty);
        SayLoadProblems();
        batch.Clear();
        batchSaid = 0;
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
        Said?.Invoke(this, new ServerSaid($"{kind} while loading {Shown(first.Project)}: {first.Message}{rest}", first.Severity, first));
    }

    /// <summary>A project as the status bar names it.</summary>
    private string Shown(string project) => PathNames.Shown(project, workspace?.Folder);

    /// <summary>The status bar's word on the server's work, each piece by its token: its title, and whether it is held back
    /// from the person, as the canonical project's restore is (a restore is held until it names a project of theirs).</summary>
    private void OnProgress(LanguageServer from, WorkProgress progress)
    {
        if (disposed || (session is { } current && current.Server != from))
        {
            return;
        }
        if (progress.Ended)
        {
            works.Remove(progress.Token, out Work? ended);
            if (ended is { Held: true } || (shownWork is not null && shownWork != progress.Token))
            {
                return;
            }
            shownWork = null;
            Progress = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!works.TryGetValue(progress.Token, out Work? work))
        {
            // A restore's start names no project: what it then restores says whose it is.
            work = new Work(progress.Title) { Held = progress.Title == RestoreTitle };
            works[progress.Token] = work;
        }
        else if (progress.Message is { Length: > 0 })
        {
            work.Held = false;
        }
        if (progress.Message is { Length: > 0 } said)
        {
            work.Said = said;
            work.Held |= CanonicalProject.IsNamedIn(said, ProjectNames);
        }
        if (work.Held)
        {
            if (shownWork == progress.Token)
            {
                shownWork = null;
                Progress = null;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        shownWork = progress.Token;
        string what = ((work.Title == RestoreTitle ? work.Said : null) ?? work.Title ?? work.Said ?? "Working").TrimEnd('.', '…');
        Progress = progress.Percentage is { } percent ? $"{what}… {percent}%" : $"{what}…";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The title Roslyn's server gives a restore, whose start names no project yet: its English resource.</summary>
    private const string RestoreTitle = "Restore";

    /// <summary>The server's work under way, by its progress token.</summary>
    private readonly Dictionary<string, Work> works = new(StringComparer.Ordinal);

    /// <summary>The token of the work the status bar shows.</summary>
    private string? shownWork;

    /// <summary>A piece of the server's work: its title, said as it began, what it last said it was doing, and whether the
    /// person is not shown it.</summary>
    private sealed class Work(string? title)
    {
        public string? Title { get; } = title;

        public string? Said { get; set; }

        public bool Held { get; set; }
    }

    /// <summary>The file names of the folder's projects, against which a project the server names is told from its own.</summary>
    private IEnumerable<string> ProjectNames => workspace?.Projects.Select(p => Path.GetFileName(p.Path)) ?? [];

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

    /// <summary>The phases of a start that say when the person can work: an open file in its own project at the server
    /// (not Miscellaneous Files, whose answers know nothing of the solution), then the first colours and diagnostics
    /// the editor asked for once the server had it there (<see cref="CSharpDocument.Placed"/>, or every project loaded,
    /// when the editor asks again; <see cref="LspDocument.Coloured"/>). Looked for every tenth of a second, asking the
    /// server nothing of its own.</summary>
    private async Task TimeActiveFileAsync(RoslynSession started, long began)
    {
        // A wait that never holds a screenshot: a folder may have no C# file open for a long while.
        DateTime until = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (!disposed && (session is null || session == started) && DateTime.UtcNow < until && !started.Server.HasExited)
        {
            foreach ((OpenDocument open, CSharpDocument features) in documents)
            {
                if (features is not { Lsp: { IsRazor: false } lsp, Placed: { } placed })
                {
                    continue;
                }
                long inProject = loadedAt is { } loaded && loaded.Session == started ? Math.Min(placed.Asked, loaded.At) : placed.Asked;
                (long Asked, long Came) coloured = lsp.Coloured;
                (long Asked, long Came) diagnosed = lsp.Diagnosed;
                if (coloured.Asked >= inProject && diagnosed.Asked >= inProject)
                {
                    IdeErrors.Write($"C# language server (process {started.Server.ProcessId}) had {open.Name} in {placed.Project} {Since(began, placed.At)}, its colours {Since(began, coloured.Came)} and its diagnostics {Since(began, diagnosed.Came)} after it was started");
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private static string Since(long began) => $"{Stopwatch.GetElapsedTime(began).TotalSeconds:0.00} s";

    private static string Since(long began, long at) => $"{Stopwatch.GetElapsedTime(began, at).TotalSeconds:0.00} s";

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

    /// <summary>The server ended on its own: it crashed, or something else ended it (<see cref="Hear"/> logs why).</summary>
    private void OnExited(RoslynSession lost)
    {
        if (disposed || session != lost || workspace is null)
        {
            return;
        }
        Replace(lost, workspace, "The C# language server ended");
    }

    /// <summary>The server stopped answering: it is ended at once, so every request waiting on it fails rather than
    /// waiting for ever, and replaced as one that ended is, the status bar saying so.</summary>
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
        works.Clear();
        shownWork = null;
        unsaid = null;
        ForgetLoadProblems();
        analysing?.Cancel();
        ForgetReports();
        IdeErrors.Observe(lost.DisposeAsync().AsTask());
        foreach (OpenDocument open in documents.Keys.ToList())
        {
            Detach(open);
            Attach(open);
        }
        if (!restarts.Again())
        {
            SetState("C# stopped");
            Stopped?.Invoke(this, ServerRestarts.Said(what, again: false));
            return;
        }
        Stopped?.Invoke(this, ServerRestarts.Said(what, again: true));
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

    /// <summary>What a C# file is to Aspire (the session's <see cref="Aspire.AspireFiles.RoleOf"/>): only an AppHost's and a
    /// service's ask Passenger's extension for Aspire's help; none for a service started without a session.</summary>
    public Func<string, Aspire.AspireRole>? AspireRoleOf { get; init; }

    /// <summary>The AppHost an AppHost's C# file is of, as its parameters' values are read (its folder, its UserSecretsId); null
    /// for any other file, and without a session.</summary>
    public Func<string, Aspire.AspireAppHostSettings?>? AspireAppHostOf { get; init; }

    /// <summary>An AppHost's text changed, so its graph may have: each open service file's Aspire warnings go and are pulled
    /// again, no other file's.</summary>
    internal void AspireGraphChanged() => Each(document =>
    {
        if (document.AspireRole == Aspire.AspireRole.Service)
        {
            document.AspireChanged();
        }
    });

    /// <summary>The folder's AppHosts or the projects they reference were evaluated or found again: each open file that is
    /// another thing to Aspire than its last pull asked as pulls again, no other file.</summary>
    internal void AspireRolesChanged() => Each(document => document.AspireRoleChanged());

    /// <summary>The configuration of the project a C# file is of (the session's <see cref="ProjectSession.ConfigurationOf(string)"/>),
    /// against which its reads of keys nothing gives are warned of; none without a session, which warns of none.</summary>
    public Func<string, Workspace.ConfigurationProject?>? ConfigurationOf { get; init; }

    /// <summary>The open files' texts as the editor has them now, for work off the UI thread (<see cref="Editors.EditorArea.Texts"/>);
    /// the disk's alone without a session.</summary>
    public Func<Editors.OpenTexts>? Texts { get; init; }

    /// <summary>The open files' texts now (<see cref="Texts"/>), taken on the UI thread.</summary>
    internal Editors.OpenTexts OpenTexts() => Texts?.Invoke() ?? Editors.OpenTexts.None;

    /// <summary>A settings file, user secrets or launch profile changed: each open file that warned of a key nothing gave, that
    /// reads configuration, or is an AppHost's, pulls its diagnostics again; no other file.</summary>
    internal void ConfigurationChanged() => Each(document => document.ConfigurationChanged());

    /// <summary>A .resx or XLIFF file changed: each open file that reads localized strings, or was warned of one a language
    /// lacks, pulls its diagnostics again; no other file.</summary>
    internal void LocalizationChanged() => Each(document => document.LocalizationChanged());

    /// <summary>The localized strings a C# or Razor file reads, asked of Passenger's extension for the code as the server holds it,
    /// each key's resource with its files found and read off the UI thread (<see cref="ResourceFiles"/>), open ones as the
    /// editor has them; null without the server.</summary>
    internal async Task<Localized?> LocalizedAsync(string path, CancellationToken cancel)
    {
        if (session is not { } roslyn)
        {
            return null;
        }
        Roslyn.Extension.LocalizedReadsFound found;
        try
        {
            found = await roslyn.ExtensionAsync<Roslyn.Extension.LocalizedReadsFound>(Roslyn.Extension.Handlers.LocalizedReads, new Roslyn.Extension.LocalizedReadsRequest(path), cancel);
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
        if (found.Keys.Count == 0)
        {
            return new Localized([], [], []);
        }
        Func<string, TextSnapshot?> open = OpenTexts().Open;
        return await BackgroundWork.Run(() =>
        {
            IReadOnlyList<ResourceSet?> sets = [.. found.Resources.Select(r => Localized.SetOf(r, open))];
            return new Localized(found.Keys, found.Resources, sets) { Unnamed = Localized.UnnamedOf(path, found.Resources, sets, open) };
        }, cancel);
    }

    /// <summary>The connection strings the open compose files' .NET services read that their environments do not set (the
    /// session's <see cref="Containers.EngineChecks.Gaps"/>), whose reads in the code are marked too; none without a session.</summary>
    public Func<IReadOnlyList<Containers.ComposeGap>>? ComposeGaps { get; init; }

    /// <summary>What the open compose files find missing changed: each open file that reads such a name, or was marked for one,
    /// pulls its diagnostics again; no other file.</summary>
    internal void ComposeGapsChanged() => Each(document => document.ComposeChanged());

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

/// <summary>One error or warning of the solution's, in its file, where it starts as the editor counts the file's lines.</summary>
public readonly record struct SolutionProblem(string Path, Diagnostic Diagnostic, TextPosition At);

/// <summary>A file's last report from one source of the solution-wide analysis: its URI, made a path once, the result id
/// to give back on the next pull, and its errors and warnings placed.</summary>
internal sealed record WorkspaceReport(string Uri, string Path, PreviousResultId? Previous, IReadOnlyList<SolutionProblem> Problems);
