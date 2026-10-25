using System.Diagnostics.CodeAnalysis;
using Passenger.App.CSharp;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The window's session (<see cref="ProjectSession"/>): a folder opened makes one and the window's views are
/// bound to it; another folder opened here first leaves this one (its editors kept with it and closed, what it runs ended
/// and waited for, within <see cref="EndLimit"/>), and Close Solution leaves it for the Welcome screen.</summary>
public partial class MainWindow
{
    /// <summary>The end of the session left last, which the next one waits for before it opens.</summary>
    private Task ending = Task.CompletedTask;

    /// <summary>Bumped by each folder opened, so an open still waiting for the last session's end gives way to a later one.</summary>
    private int opens;

    /// <summary>The folder an open waiting to begin will open, as of the open <see cref="openingAt"/> counts.</summary>
    private string? opening;
    private int openingAt;

    /// <summary>The folder open in the window and what it has and runs; one with no folder before any opens.</summary>
    public ProjectSession Session => session;

    /// <summary>The open folder's solution and projects; null with no folder open.</summary>
    public WorkspaceModel? WorkspaceModel => session.Workspace;

    /// <summary>The open folder's solution, read from its files, or null for a folder without one.</summary>
    public SolutionModel? Solution => session.Solution;

    /// <summary>The workspace's projects, but those unloaded in the Solution view.</summary>
    public IReadOnlyList<ProjectModel> Projects => session.Projects;

    /// <summary>The loaded project a file or folder (a canonical path) belongs to; null outside them all.</summary>
    public ProjectModel? ProjectOf(string path) => session.ProjectOf(path);

    private string ProjectNameOf(string path) => session.ProjectNameOf(path);

    /// <summary>The read of the open folder under way, done once it has landed.</summary>
    public Task WorkspaceRead => session.WorkspaceRead;

    /// <summary>What a session of this window is made with: its editors, where it keeps state, how it watches folders, the
    /// warm server it may take up, its consoles, Debug window and tests tree, a test run's build (which the session's builds
    /// and runs make), the Terminal tab the debugger asks for, and where its container tools come from.</summary>
    private SessionSetup Setup() => new(Editors, StateFolder, HistoryKeeping, Watching, () => settings.SolutionWideAnalysis, WarmServer, Bottom,
        cancel => buildRun.BuildAsync(BuildCause.Tests, cancel), RunForDebugger, () => settings, work, () => Engine)
    {
        ContainerTools = () => ContainerToolsFrom ?? Containers.ContainerToolsSource.Default(Releases),
        DockerHub = DockerHub,
        StartsLanguageServer = StartsLanguageServer,
    };

    /// <summary>Where this window's Dockerfile server asks for image tags in Docker Hub's place: the one given, else
    /// the place <c>PASSENGER_DOCKER_HUB</c> names, else Docker Hub's own.</summary>
    public Uri? DockerHub { get; init; }

    /// <summary>What the builds and runs of this window's sessions show themselves in and start with.</summary>
    private BuildRunParts BuildRunParts() => new(Bottom, Editors, Dock, () => settings, Say, ShowInTerminal,
        OpenBrowserOnceListening, () => StateFolder, work, ShowAspireEngineAsync)
    {
        Reveal = project =>
        {
            containersView?.Reveal(project);
            containersView?.TelemetryChanged();
        },
        DevCertificates = DevCertificates,
        AskCertificate = AskCertificateAsync,
        AskReplace = AskReplaceAsync,
        AsksNoOne = AgentsOwn,
        ProfileService = RecordRunServiceAsync,
        ShowFolder = ShowInFileManager,
    };

    /// <summary>Opens a folder, or a solution (.sln, .slnx) or C# project (.csproj) over its folder, as the workspace (a
    /// person's choice through <see cref="Open"/>). The workspace already open is only read again, its server and files
    /// kept; another folder's session is left first, the new one made once what it ran has ended. Done once the folder is
    /// open or a later open took its place. <paramref name="opener"/>, the agent whose own open it is, stays let in;
    /// <paramref name="opened"/> runs once the folder's session has begun (or been read again), never for one given up.</summary>
    public Task OpenFolder(string path, Mcp.AgentConnection? opener = null, Action? opened = null)
    {
        UiThread.Check();
        string named = PathNames.Canonical(path);
        bool fileNamed = File.Exists(named) && WorkspaceModel.Opens(named);
        string opening = fileNamed ? Path.GetDirectoryName(named)! : named;
        if (opening == session.Folder)
        {
            ReadAgain(opening, fileNamed ? named : null);
            opened?.Invoke();
            return Task.CompletedTask;
        }
        // Another folder's files are its own: this one's are kept with it and close, saved.
        if (session.Folder is not null)
        {
            if (!LeaveFolder())
            {
                return Task.CompletedTask;
            }
            EndSession();
        }
        AgentsGoTo(opening, opener);
        Autosave();
        // The session with no folder ends too, with what it ran; its open files stay, the new session's.
        Task none = session.LeaveAsync();
        ending = ending.IsCompleted ? none : Task.WhenAll(ending, none);
        // The folder's documents read off the window's thread meanwhile, so its restore takes them in with no read.
        Task reading = keepsWorkspace ? Store().Project(opening).ReadAsync() : Task.CompletedTask;
        int open = ++opens;
        if (ending.IsCompleted && reading.IsCompleted)
        {
            Begin(opening, fileNamed ? named : null);
            opened?.Invoke();
            return Task.CompletedTask;
        }
        (this.opening, openingAt) = (opening, open);
        return BeginOnceEndedAsync(open, opening, fileNamed ? named : null, reading, opened);
    }

    /// <summary>The folder open now, opened again: with the solution it has, read again; with another (a solution or project
    /// named), read with that one, its files and what it runs kept.</summary>
    private void ReadAgain(string folder, string? named)
    {
        WorkspaceModel? withSolution = WorkspaceModel.ReadSolution(folder, named ?? session.NamedSolution);
        if ((withSolution?.OpenedAs ?? folder) == session.Workspace?.OpenedAs)
        {
            session.Reopen(session.NamedSolution, withSolution);
            return;
        }
        Autosave();
        session.Reopen(named, withSolution);
        Explorer.Load(session.Workspace!);
        ShowWorkspaceName();
        UpdateStatus();
        Remember();
    }

    private async Task BeginOnceEndedAsync(int open, string folder, string? named, Task reading, Action? opened)
    {
        // Held while it waits, so a screenshot or a test finds the folder open rather than the moment between.
        using IDisposable held = BackgroundWork.Scope();
        status.Text = $"Opening {Path.GetFileName(folder)}…";
        await ending;
        await reading;
        if (open == opens && Ended is null)
        {
            Begin(folder, named);
            opened?.Invoke();
        }
    }

    /// <summary>The folder's session made and the window bound to it: the Explorer, Git, the language server, the tests
    /// found from code, what the store kept of the folder, and the folder among the recent ones.</summary>
    private void Begin(string folder, string? named)
    {
        // Begun, the folder is the session's: kept as opening, a Close Solution after would leave the window holding it.
        opening = null;
        WorkspaceModel? withSolution = WorkspaceModel.ReadSolution(folder, named);
        // The projects as MSBuild last evaluated them, from the folder's cache, before any is evaluated again.
        ProjectSession opened = ProjectSession.Open(Setup(), folder, named, withSolution, keepsWorkspace ? Store().Project(folder) : null);
        // The consoles are another folder's: what it ran said its last there before its end landed.
        Bottom.Run.Clear();
        Bottom.Build.Clear();
        Bottom.Debug.Clear();
        Bind(opened);
        Explorer.Load(opened.Workspace!);
        opened.Start(walk: withSolution is null);
        FindTests();
        Editors.ShowFolderHint(folder);
        RestoreWorkspace();
        ShowWorkspaceName();
        UpdateStatus();
        Remember();
        IdeErrors.Observe(KeepSnapshotsAsync());
    }

    /// <summary>What the open folder had in the window goes, kept with it in the store first: its editors close, each saved
    /// (false, the rest left open, when one cannot be), and the editors' undo across files with them.</summary>
    private bool LeaveFolder()
    {
        KeepWorkspace();
        if (!Editors.CloseAll())
        {
            return false;
        }
        Editors.Commands.Clear();
        return true;
    }

    /// <summary>Ends the session left: programs and builds at once, the debugger, the tests and the folder's terminals as
    /// asked, then its language server; a session with no folder takes its place, the window's views of the folder
    /// emptied, and its end is what the next folder waits for.</summary>
    private void EndSession()
    {
        ProjectSession leaving = session;
        IdeErrors.Write($"Closed {leaving.Folder}");
        Task terminals = Task.CompletedTask;
        if (terminal is { Tabs.Count: > 0 })
        {
            Dock.Hide(ToolWindowIds.Terminal);
            terminals = terminal.CloseAllAsync();
        }
        ending = leaving.LeaveAsync(("the terminal", terminals));
        Bind(ProjectSession.None(Setup()));
        localHistoryView?.CloseAll();
        Explorer.Unload();
    }

    /// <summary>The window's views and controllers follow a session from now: what it says reaches them while it is the
    /// window's, never after.</summary>
    [MemberNotNull(nameof(buildRun))]
    private void Bind(ProjectSession next)
    {
        session = next;
        ForgetRunMarks();
        buildRun = new BuildRunController(next, BuildRunParts());
        BindAspire(next);
        buildRun.RunningChanged += (_, _) =>
        {
            if (session == next)
            {
                ShowStop();
                ShowHotReloadButtons();
            }
        };
        next.ProfiledChanged += (_, _) =>
        {
            if (session == next)
            {
                ShowStop();
            }
        };
        BindTests(next);
        BindDebug(next);
        profiler?.Rebind();
        liveTree?.Bind(next);
        bindingValues?.Bind(next);
        overlays?.Bind(next);
        BindVcs(next);
        BindBookmarks(next);
        BindEndpoints(next);
        BindMigrations(next);
        BindPackages(next);
        BindContainers(next);
        // Files open as it binds (a file opened with no folder) are its too.
        foreach (Workspace.OpenDocument document in Editors.Documents)
        {
            next.Attach(document);
        }
        ShowStop();
        ShowHotReloadButtons();
        // The last folder's build said nothing of this one.
        Bottom.Problems.ForgetBuild();
        buildRun.ShowBindings();
        Dock[ToolWindowIds.Problems].Title = "Problems";
        Explorer.Tree.Unloaded = next.Unloaded;
        next.CSharpChanged += (_, _) =>
        {
            if (session == next)
            {
                OnCSharpChanged(next.CSharp);
            }
        };
        next.Xaml.Message += (_, message) =>
        {
            if (session == next)
            {
                status.Text = message;
            }
        };
        next.Xaml.ProjectChanged += (_, project) =>
        {
            if (session == next)
            {
                assist.XamlProjectRead(project);
            }
        };
        next.MSBuild.Message += (_, message) =>
        {
            if (session == next)
            {
                status.Text = message;
            }
        };
        if (next.Web is { } web)
        {
            web.StateChanged += (_, _) =>
            {
                if (session == next)
                {
                    status.ShowLanguage(LanguageLabel());
                    structure?.FollowServer();
                }
            };
            web.Stopped += (_, message) =>
            {
                if (session == next)
                {
                    Say(message, LogPlace);
                }
            };
            web.Message += (_, message) =>
            {
                if (session == next)
                {
                    status.Text = message;
                }
            };
            web.Eslint.Browse += (_, page) =>
            {
                if (session == next)
                {
                    Browse(page);
                }
            };
            // ESLint's fixes are made by its server's commands, which hand their edit to the window to make.
            web.Eslint.ApplyEdit = edit =>
            {
                List<string> dropped = [];
                try
                {
                    WorkspaceEdits.Apply(Editors, edit, null, dropped);
                }
                catch (WorkspaceEditFailed failed)
                {
                    return failed.Message;
                }
                return dropped.Count > 0 ? $"{string.Join(", ", dropped)} changed since ESLint was asked" : null;
            };
        }
        next.WorkspaceChanged += (_, _) =>
        {
            if (session == next)
            {
                Explorer.Reload(next.Workspace!);
                ShowWorkspaceName();
                FindTests();
                // Each file's project, which Problems lists the solution's under, as the projects now are.
                ShowSolutionProblems();
            }
        };
        next.ProjectsEvaluated += (_, evaluated) =>
        {
            if (session == next)
            {
                Explorer.UpdateProjects(next.Workspace!, evaluated.Projects);
                if (evaluated.TestsChanged)
                {
                    FindTests();
                }
                // A package installed, updated or removed shows so in the NuGet window once the project's evaluation says so.
                if (evaluated.PackagesChanged && nuget is { Rows.Count: > 0 })
                {
                    IdeErrors.Observe(nuget.RefreshAsync());
                }
            }
        };
        next.Configurations.Changed += (_, _) =>
        {
            if (session == next)
            {
                ShowChosenConfiguration(next.Configurations);
                // An AppHost evaluated or found since: its open files' resources are read, the others' kept.
                ReadOpenAspireMarks(again: false);
                // A file shown before its AppHost was evaluated pulled its diagnostics as no service's.
                next.CSharp?.AspireRolesChanged();
            }
        };
        if (next.Watcher is { } watcher)
        {
            watcher.Changed += (_, changes) =>
            {
                if (session == next)
                {
                    OnFolderChanged(changes);
                }
            };
        }
        ShowChosenConfiguration(next.Configurations);
        agentSocket?.Describe(next.Folder);
        OnCSharpChanged(next.CSharp);
        BindPreviews(next);
    }

    /// <summary>A session's debugger in the window: what it asks to run, what it says, where it stops and the toolbar's
    /// Stop, while it is the window's.</summary>
    private void BindDebug(ProjectSession next)
    {
        Debugging.DebugController debugger = next.Debug;
        BindHotReload(next);
        debugger.ActionRequested += (_, action) =>
        {
            if (session == next && next.Debug == debugger)
            {
                Request(action);
            }
        };
        debugger.Message += (_, message) =>
        {
            if (session == next && next.Debug == debugger)
            {
                // A stop at a binding error says it, and Problems' Bindings lists it.
                Say(message, debugger.BindingStop is { } stop && message == debugger.Summary ? BindingPlace(stop.Problem.Problem) : null);
            }
        };
        debugger.Navigate += (_, target) =>
        {
            if (session == next && next.Debug == debugger)
            {
                ShowStop(target.Path, target.Position);
            }
        };
        debugger.TextOpened += (_, sql) =>
        {
            if (session == next && next.Debug == debugger)
            {
                OpenQuerySql(sql);
            }
        };
        debugger.StateChanged += (_, _) =>
        {
            if (session == next)
            {
                OnDebugStateChanged();
            }
        };
    }

    /// <summary>A new language server (or none) for the open folder: the status bar, the usages and Problems follow it.</summary>
    private void OnCSharpChanged(CSharpService? service)
    {
        if (service is not null)
        {
            service.StateChanged += (_, _) => status.ShowLanguage(LanguageLabel());
            service.StateChanged += (_, _) => usages.Refresh();
            service.StateChanged += (_, _) => OutlineWhenUnseen();
            service.StateChanged += (_, _) => structure?.FollowServer();
            service.StateChanged += (_, _) => RoutesServerChanged(service);
            service.ProjectsLoaded += (_, _) => RoutesProjectsLoaded(service);
            service.StateChanged += (_, _) =>
            {
                // Ready, as it loaded the folder's projects: the open AppHosts' resources are read from it once.
                if (service == session.CSharp && !session.IsEnded && service.IsReady && aspireMarksFrom != service)
                {
                    aspireMarksFrom = service;
                    ReadOpenAspireMarks(again: true);
                }
            };
            // Each stop is logged with why: the log is where to see more.
            service.Stopped += (_, message) => Say(message, LogPlace);
            service.Said += (_, said) => status.Say(said.Text, said.Severity, said.Problem is { } problem ? LoadProblemPlace(problem) : null);
            WatchProblems(service);
            service.FollowIndent(Editors.Active);
        }
        status.ShowLanguage(LanguageLabel());
        ShowSolutionProblems();
        ShowProjectProblems();
    }

    /// <summary>Reads the solution's files again (after a package or project change, here or on disk), keeping the run
    /// configuration chosen when it is still there and the Explorer's rows open.</summary>
    public void ReloadSolution() => session.ReadAgain();

    /// <summary>The window's project widget and its title name the solution (or the project opened alone), the folder
    /// only without one.</summary>
    private void ShowWorkspaceName()
    {
        if (session.Workspace is not { } workspace)
        {
            return;
        }
        string shown = workspace.Name;
        ProjectName.Text = shown;
        ProjectBadgeText.Text = Initials(shown);
        ProjectBadge.IsVisible = true;
        ShowTitle();
    }
}
