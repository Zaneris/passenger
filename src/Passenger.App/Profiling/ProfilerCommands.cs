using Microsoft.Diagnostics.NETCore.Client;
using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Profiler;
using Passenger.Text;

namespace Passenger.App.Profiling;

/// <summary>What a window lends its profiler: its session and that session's builds and runs, the tool windows' frame and
/// the window's actions, the Run console a profiled program writes to, the status bar, the store's folder, the system's
/// file panel for a snapshot to open, going to a place in the code, choosing a control of a process in the Live Visual
/// Tree, the editors, whose text a control's XAML place is read in, the choice of a .NET program in a running container
/// (Attach to Process's dialog), and such a program made ready to profile: Passenger's tools put in its container, its id
/// there, and the place it is recorded at.</summary>
public sealed record ProfilerParts(
    Func<ProjectSession> Session,
    Func<BuildRunController> Builds,
    DockHost Dock,
    ActionRegistry Actions,
    OutputView RunConsole,
    Action<string> Say,
    Func<string> StateFolder,
    Func<Task<string?>> PickSnapshot,
    Action<string, TextPosition> Navigate,
    Action<string, Lsp.LspPosition> NavigateToServer,
    Func<int, long, bool> RevealControl,
    EditorArea Editors,
    Func<Task<DotnetProcess?>> ChooseInContainer,
    Func<DotnetProcess, ContainerPaths, Action<string>, CancellationToken, Task<(DotnetProcess Target, ProfilePlace Place)>> InContainer,
    Action<string> ShowInFileManager,
    Func<string, string, string, Task<bool>> Confirm,
    Func<RunConfiguration, string?, Task<string?>>? ProfileAspire = null);

/// <summary>The window's profiler: Profile builds and starts the chosen configuration under the sampling profiler (an
/// Avalonia app with the inspector in it, which records its frames), or Attach Profiler profiles the program Run or Debug
/// started from now on, in a container too (else one chosen among the running containers' .NET programs), there by
/// Passenger's container tools; the Performance Profiler window follows the run, Get Snapshot (or the program's end)
/// shows where its time went and a memory snapshot what its heap holds, snapshots written before open again, and a
/// method or a control named there opens in the code. The run is the session's (<see cref="ProjectSession.Profiling"/>),
/// ended with its folder. A UI-thread object of the window.</summary>
public sealed class ProfilerCommands
{
    private const string NothingRunning = "Nothing is running to profile: Run or Debug a configuration first, or Profile one";

    /// <summary>What Attach Profiler says when its choice among the containers' programs was closed with none chosen.</summary>
    private const string NoneChosen = "Attach Profiler chose no program: Run or Debug a configuration first, or start a .NET program in a container";

    private const string NoneProfiling = "A memory snapshot needs a program being profiled";

    /// <summary>What Attach Profiler, Get Memory Snapshot and Get Snapshot do, as a refusal for the debugger's pause names
    /// them (<see cref="Refusals.PausedInDebugger"/>).</summary>
    private const string ToProfile = "profile";
    private const string ToTakeHeap = "take a memory snapshot";
    private const string ToGetSnapshot = "get its snapshot";

    private readonly ProfilerParts parts;
    private readonly ToolWindow window;
    private ProfilerView? view;

    /// <summary>The folder's snapshots as last listed, which the view lists as it is built.</summary>
    private (string Folder, IReadOnlyList<SnapshotFile> Files)? listed;
    private string profilingName = string.Empty;

    /// <summary>The run whose memory snapshots the person let the runtime write into its program's output.</summary>
    private ProfilingSession? intoOutputAgreed;

    /// <summary>Ends the start under way, which waits for the program to reach the profiler: Kill while it starts.</summary>
    private CancellationTokenSource? starting;
    private Task<SnapshotRead>? snapshotting;

    /// <summary>The process the snapshot shown came from, none for one opened from its file.</summary>
    private int? shownProcess;

    public ProfilerCommands(ProfilerParts parts)
    {
        this.parts = parts;
        window = new ToolWindow(ToolWindowIds.Profiler, "Performance Profiler", IconKeys.Profile, Make, ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowProfiler,
            Menus = [new MenuPlace("Tools", 0, 1)],
        };
        parts.Dock.Add(window);
        parts.Actions.Add(new IdeAction(AppActions.Profile, "Profile", "Run", context => IdeErrors.Observe(ProfileReportingAsync()))
        {
            WhyNot = _ => WhyNotProfile(Session) ?? (Session.Configurations.Chosen is null ? Session.NothingTo("profile") : null),
            Menus = [new MenuPlace("Run", 0, 2)],
        });
        // Nothing of the folder's running, it offers the programs of the engine's running containers to choose from.
        parts.Actions.Add(new IdeAction(AppActions.AttachProfiler, "Attach Profiler to Running Program", "Run", context => IdeErrors.Observe(AttachReportingAsync()))
        {
            WhyNot = _ => Busy(Session) ?? (Attachable(out string? _) is { } target ? Paused(target) : null),
            Menus = [new MenuPlace("Run", 3, 0)],
        });
        parts.Actions.Add(new IdeAction(AppActions.MemorySnapshot, "Get Memory Snapshot", "Run", context => IdeErrors.Observe(MemorySnapshotAsync()))
        {
            WhyNot = _ => Session.Profiling is null ? NoneProfiling : Held(ToTakeHeap),
        });
    }

    /// <summary>The Performance Profiler tool window's view, built as it is first shown or asked for: each profile's start,
    /// run and snapshot shows the window first, so only the folder's kept snapshots are kept for it until then.</summary>
    public ProfilerView View => view ?? (ProfilerView)window.Content;

    private ProfilerView Make()
    {
        ProfilerView made = view = new ProfilerView();
        made.StopRequested += (_, _) => IdeErrors.Observe(SnapshotAsync());
        made.KillRequested += (_, _) =>
        {
            if (starting is { } start)
            {
                start.Cancel();
            }
            else
            {
                Session.Profiled?.Run.Kill();
            }
        };
        made.OpenRequested += (_, method) => IdeErrors.Observe(OpenMethodAsync(method));
        made.FramesPage.ControlOpened += (_, control) => OpenControl(control);
        made.OpenSnapshotRequested += (_, _) => IdeErrors.Observe(PickSnapshotAsync());
        made.OpenKeptRequested += (_, path) => IdeErrors.Observe(OpenSnapshotAsync(path));
        made.RevealSnapshotsRequested += (_, _) =>
        {
            Directory.CreateDirectory(SnapshotFolder);
            parts.ShowInFileManager(SnapshotFolder);
        };
        made.MemoryRequested += (_, _) => IdeErrors.Observe(MemorySnapshotAsync());
        if (listed is ({ } folder, { } files))
        {
            made.ShowKept(folder, files);
        }
        return made;
    }

    /// <summary>The open folder's snapshots listed on the Snapshots page, or kept for it until it is built.</summary>
    private void ShowKept(string folder, IReadOnlyList<SnapshotFile> files)
    {
        listed = (folder, files);
        view?.ShowKept(folder, files);
    }

    private ProjectSession Session => parts.Session();

    /// <summary>The window follows another session from now: what the profiler window showed of the last one's run, which
    /// that folder's end ended, goes with it, its memory snapshots too, and Get Snapshot no longer answers with that run's
    /// reading.</summary>
    public void Rebind()
    {
        snapshotting = null;
        if (view is not null && (view.IsFollowing || view.CanKill))
        {
            view.Stopped($"Profiling {profilingName} ended with its folder");
        }
        view?.ForgetMemory();
    }

    /// <summary>Where the open folder's snapshots are kept, a .nettrace file a run: beside its state, the newest
    /// <see cref="Snapshots.Kept"/>. With no folder open, the store's own.</summary>
    public string SnapshotFolder => Path.Combine(Session.Folder is not { } folder ? parts.StateFolder() : State.ProjectState.FolderOf(parts.StateFolder(), folder), "snapshots");

    /// <summary>Lists the open folder's snapshots in the profiler window, after taking off those past the cap; read off
    /// the UI thread.</summary>
    public async Task KeepSnapshotsAsync(bool trim = false)
    {
        ProjectSession profiled = Session;
        CancellationToken closing = profiled.Aspire.ClosingToken;
        string kept = SnapshotFolder;
        using IDisposable held = BackgroundWork.Scope();
        // Held by the scope, and back on no thread of the window's, which may be gone by then.
        IReadOnlyList<SnapshotFile> files = await BackgroundWork.Unheld(() =>
        {
            if (trim)
            {
                Snapshots.Trim(kept);
            }
            return Snapshots.List(kept);
        }).ConfigureAwait(false);
        if (closing.IsCancellationRequested)
        {
            return;
        }
        try
        {
            await UiThread.InvokeAsync(() =>
            {
                if (!closing.IsCancellationRequested && !profiled.IsEnded && profiled == Session && kept == SnapshotFolder)
                {
                    ShowKept(kept, files);
                }
                return true;
            }).WaitAsync(closing).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
        }
    }

    /// <summary>Resolves and builds the chosen configuration, then starts its plan under the profiler from its first
    /// instruction, with these arguments in place of its own if given. Null once it runs, or what stopped it, which the
    /// profiler window says too (all but a failed build, which the Build window shows).</summary>
    public async Task<string?> ProfileAsync(string? arguments = null, RunConfiguration? configuration = null)
    {
        ProjectSession profiled = Session;
        if (WhyNotProfile(profiled, configuration) is { } busy)
        {
            return busy;
        }
        if ((configuration ?? profiled.Configurations.Chosen) is { } chosen && profiled.Aspire.IsAspire(chosen) && parts.ProfileAspire is { } aspire)
        {
            return await aspire(chosen, arguments);
        }
        string name = (configuration ?? profiled.Configurations.Chosen)?.Name ?? "the program";
        if (parts.Actions.Work.Begin(AppActions.Profile, $"Working out how to profile {name}…") is not { } work)
        {
            return ActionWork.Busy(parts.Actions.Work.Of(AppActions.Profile)!);
        }
        launching = profiled;
        try
        {
            return await LaunchAsync(profiled, arguments, work, configuration);
        }
        finally
        {
            work.Dispose();
            if (launching == profiled)
            {
                launching = null;
            }
        }
    }

    /// <summary>The folder whose Profile has yet to hand its run over (resolving, building, or waiting for the program to
    /// reach the profiler): another Profile or Attach Profiler there would start a run the window could not reach.</summary>
    private ProjectSession? launching;

    /// <summary>Why another run cannot be profiled in a folder now: one is, or one is still starting.</summary>
    private string? Busy(ProjectSession folder) => folder.Profiling is not null || launching is not null ? "A run is already being profiled" : null;

    /// <summary>Why Profile cannot start another program: the one it started last goes on after its snapshot, and Kill, Stop
    /// and the folder's end reach one such program at a time.</summary>
    private static string? GoesOn(ProjectSession folder) =>
        folder.Profiled is { Run.HasExited: false } program ? $"{program.Name} goes on after its snapshot: Kill it first, or Attach Profiler to profile it again" : null;

    /// <summary>Why Profile cannot start a program now: the Run console it writes to is another configuration's program's,
    /// running or about to, whose output it would clear and whose typing it would take. The program Run started for the
    /// configuration profiled is ended first (<see cref="BuildRunController.EndOwnRunAsync"/>).</summary>
    private string? RunConsoleTaken(ProjectSession folder, RunConfiguration? chosen) =>
        parts.Actions.Work.Of(AppActions.RunProgram) is { } running ? ActionWork.Busy(running)
        : folder.Runner.IsRunning && (chosen is null || !parts.Builds().RunsOwnProgram(chosen.Name)) ? "Run's program uses the Run console: stop it first, or Attach Profiler to profile it"
        : null;

    /// <summary>Why Profile cannot start now, or null when it can.</summary>
    private string? WhyNotProfile(ProjectSession folder, RunConfiguration? configuration = null)
    {
        RunConfiguration? chosen = configuration ?? folder.Configurations.Chosen;
        return Busy(folder) ?? (chosen is not null && folder.Aspire.IsAspire(chosen) ? null : GoesOn(folder) ?? RunConsoleTaken(folder, chosen));
    }

    private async Task<string?> LaunchAsync(ProjectSession profiled, string? arguments, Working work, RunConfiguration? configuration)
    {
        BuildRunController builds = parts.Builds();
        if ((configuration ?? profiled.Configurations.Chosen) is not { } chosen)
        {
            return profiled.NothingTo("profile");
        }
        if (chosen.IsContainer)
        {
            return await LaunchInContainersAsync(profiled, chosen, work);
        }
        if (await builds.EndOwnRunAsync(chosen, work, "profile") is { } left)
        {
            return left;
        }
        (LaunchPlan? plan, string? problem, bool unresolved) = await builds.PrepareAsync(chosen, arguments, work);
        // A failed build is the Build window's to show, a stop or the folder's end nobody's.
        if (plan is null && !unresolved)
        {
            return problem;
        }
        parts.Dock.Show(ToolWindowIds.Profiler);
        if ((plan is null ? problem : plan.WhyNot("profile")) is { } why)
        {
            View.Stopped(why);
            return why;
        }
        OutputView run = parts.RunConsole;
        // An Avalonia app is profiled with the inspector in it, which records its frames into the run's trace.
        LaunchPlan launched = profiled.InspectedProfile(plan!);
        LiveApp? inspected = profiled.ProfiledApp;
        using CancellationTokenSource stop = new();
        // The toolbar's Stop ends the start as the window's Kill does.
        using CancellationTokenRegistration toolbarStop = work.Stopping.Register(stop.Cancel);
        work.Say($"Starting {chosen.Name} under the profiler…");
        try
        {
            ProfileTarget target = new(launched.Program, launched.Arguments, launched.WorkingDirectory, launched.Environment);
            run.Clear();
            ConsoleSink console = run.Sink;
            ProfilingSession running;
            // Kill is there while the program starts; the folder's end ends it too.
            starting = stop;
            profiled.ProfileStarting = stop;
            View.Starting(chosen.Name);
            // Held until the program has reached the profiler (or never will), not while it runs: that is the person's.
            using (BackgroundWork.Scope())
            {
                running = await ProfilingSession.StartAsync(target, SnapshotFolder, SocketFolders.Own(State.StateStore.DefaultRoot),
                    (reader, error, lines) => console.PumpAsync(reader, error ? ConsoleStream.Error : ConsoleStream.Output, lines: lines), stop.Token);
            }
            starting = null;
            profiled.Profiling = running;
            TaskCompletionSource landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ProfiledProgram program = new(running, chosen.Name, target.Name, landed.Task);
            profiled.Profiled = program;
            ConsoleInput? input = running.Input is { } writer ? ProgramInput.Of(writer) : null;
            // Only its end puts the run away, however that comes: a snapshot leaves the program running.
            IdeErrors.Observe(running.ExitedAsync().ContinueWith(_ => UiThread.Post(() => Ended(profiled, program, inspected, input, landed)), TaskScheduler.Default));
            if (profiled.IsEnded)
            {
                // Its folder was left as it started: it ends with the folder's other programs.
                running.Kill();
                return Refusals.SolutionClosed;
            }
            profilingName = chosen.Name;
            run.Input = input;
            View.Running(chosen.Name, () => running.Elapsed);
            parts.Say($"Profiling {chosen.Name}");
            return null;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Killed as it started, or its folder closed: the start ended the program and its port.
            inspected?.Dispose();
            if (profiled.IsEnded)
            {
                // Its Kill goes, unless the next folder's Profile has started since.
                if (starting == stop)
                {
                    View.Stopped();
                }
                return Refusals.SolutionClosed;
            }
            profiled.Profiling = null;
            string stopped = $"Profiling {chosen.Name} was stopped as it started";
            View.Stopped(stopped);
            parts.Say(stopped);
            return stopped;
        }
        catch (Exception error) when (error is ProfilingException or InvalidOperationException or IOException or UnauthorizedAccessException
            or TimeoutException or System.ComponentModel.Win32Exception or DiagnosticsClientException)
        {
            inspected?.Dispose();
            profiled.Profiling = null;
            View.Stopped(error.Message);
            return error.Message;
        }
        finally
        {
            if (starting == stop)
            {
                starting = null;
            }
            if (profiled.ProfileStarting == stop)
            {
                profiled.ProfileStarting = null;
            }
        }
    }

    /// <summary>Profile of a container configuration: brought up as Run brings it (its log in the Run console, Stop taking it
    /// down), the one .NET service it profiles held at its start until the one recording of a service records it there
    /// (<see cref="RecordServiceAsync"/>), which lets it go, so the run has it from its first instruction; the rest run as Run
    /// runs them. The program is the container's, not Kill's: Get Snapshot ends the recording and the run goes on, and Stop
    /// reads the snapshot before the containers go.</summary>
    private async Task<string?> LaunchInContainersAsync(ProjectSession profiled, RunConfiguration chosen, Working work)
    {
        BuildRunController builds = parts.Builds();
        using CancellationTokenSource stop = new();
        // The window's Kill, and the folder's end, end the start as the toolbar's Stop does: its containers taken down.
        using CancellationTokenRegistration killed = stop.Token.Register(work.Cancel);
        starting = stop;
        profiled.ProfileStarting = stop;
        parts.Dock.Show(ToolWindowIds.Profiler);
        View.Starting(chosen.Name);
        string? failed;
        try
        {
            failed = await builds.ProfileContainersAsync(chosen, work);
        }
        finally
        {
            if (starting == stop)
            {
                starting = null;
            }
            if (profiled.ProfileStarting == stop)
            {
                profiled.ProfileStarting = null;
            }
        }
        if (profiled.IsEnded)
        {
            return Refusals.SolutionClosed;
        }
        // A start that failed once recording had begun read its snapshot as it took its containers down, which the window shows.
        bool recorded = profiled.ContainerRun?.Profiled is not null;
        string? why = failed ?? (recorded ? null : NoneProfiled(chosen.Name, profiled.ContainerRun));
        if (why is not null && !recorded)
        {
            View.Stopped(why);
        }
        return why;
    }

    /// <summary>Why a container configuration's Profile recorded nothing: each service it held for the profiler that the
    /// profiler could not reach, with why, else that it started none.</summary>
    private static string NoneProfiled(string name, ContainerRun? run) => run?.Unreached is { Count: > 0 } unreached
        ? $"{name} started no .NET service the profiler can reach ({string.Join("; ", unreached.Select(u => $"{u.Key}: {u.Value}"))})"
        : $"{name} started no .NET service to profile";

    /// <summary>A run handed over to the window: the session's, the profiler window following it, and its snapshot read at its
    /// program's end (or its recording's, in a container) unless Get Snapshot read it first.</summary>
    private void Follow(ProjectSession profiled, ProfilingSession running, string name, bool killable)
    {
        profiled.Profiling = running;
        profilingName = name;
        View.Running(name, () => running.Elapsed, killable);
        parts.Dock.Show(ToolWindowIds.Profiler);
        parts.Say($"Profiling {name} from now: Get Snapshot when it has done what is to be measured");
        IdeErrors.Observe(running.ExitedAsync().ContinueWith(_ => UiThread.Post(() =>
        {
            if (profiled.Profiling == running && profiled == Session)
            {
                IdeErrors.Observe(SnapshotAsync());
            }
            else if (profiled.IsEnded)
            {
                // Its folder's end let go of the run, read by no one.
                BackgroundWork.Track(running.DisposeAsync().AsTask());
            }
        }), TaskScheduler.Default));
    }

    /// <summary>A program Profile started has ended (by itself, killed, or with its folder) and the Run console has the last
    /// of what it wrote: a run still recording shows its snapshot, and the run is put away.</summary>
    private void Ended(ProjectSession profiled, ProfiledProgram program, LiveApp? inspected, ConsoleInput? input, TaskCompletionSource landed)
    {
        try
        {
            inspected?.Dispose();
            OutputView run = parts.RunConsole;
            // Typing goes on to whatever has had the Run console since.
            if (run.Input == input)
            {
                run.Input = null;
            }
            run.Flush();
            if (profiled.Profiled == program)
            {
                profiled.Profiled = null;
            }
            if (profiled.Profiling == program.Run && profiled == Session)
            {
                // Read, then put away.
                IdeErrors.Observe(SnapshotAsync());
                return;
            }
            BackgroundWork.Track(program.Run.DisposeAsync().AsTask());
            if (profiled == Session)
            {
                ShowKill();
            }
        }
        finally
        {
            landed.TrySetResult();
        }
    }

    /// <summary>Kill is there while the program Profile started runs, unless the window follows another's run.</summary>
    private void ShowKill() =>
        view?.ShowKill(starting is not null || Session.Profiled is { } program
            && (Session.Profiling is not { } recording || recording.Place is null && recording.ProcessId == program.Run.ProcessId));

    /// <summary>Attach Profiler: profiles the program Debug, Run or Profile started, from now until Get Snapshot or its end;
    /// an Avalonia app's inspector records its frames for as long, and the program goes on as it was after. A program in a
    /// container (the one debugged there, <paramref name="inContainer"/>, or, with <paramref name="choose"/> and nothing of
    /// the folder's running, one chosen among the running containers' .NET programs) is recorded there by Passenger's tools,
    /// a program held at its start let go once recording. Null once it is profiled, else why not.</summary>
    public Task<string?> AttachAsync(DotnetProcess? inContainer = null, bool choose = false)
    {
        UiThread.Check();
        if (inContainer is not null)
        {
            AttachTarget there = InContainer(inContainer, ContainerPaths.None, resume: true);
            return Paused(there) is { } held ? Task.FromResult<string?>(held) : AttachTargetAsync(there);
        }
        AttachTarget? target = Attachable(out string? problem);
        if ((problem ?? (target is null ? null : Paused(target))) is { } refused)
        {
            return Task.FromResult<string?>(refused);
        }
        // A service of a run of services, recorded through its run, which Stop reads first.
        if (target?.Run is { } run)
        {
            return Busy(Session) is { } busy ? Task.FromResult<string?>(busy) : parts.Builds().ProfileServiceAsync(run, target.Name);
        }
        return AttachTargetAsync(target, choose);
    }

    /// <summary>The one recording of a service of a run of services, a container run's or an Aspire AppHost's, held at its
    /// start (its run's Profile) or as it runs (<paramref name="asItRuns"/>): its program recorded where its kind runs it
    /// (<see cref="ServiceProgram.Recorded"/>, else on this machine), a program held at its start let go once recording, the
    /// profiler window following it, and the run told what Stop, a restart and Rerun end first
    /// (<see cref="ServiceRun.Profiled"/>, <see cref="ServiceRun.EndProfiling"/>). Null once it records, else why not.</summary>
    public async Task<string?> RecordServiceAsync(ServiceRun run, IRunService service, ServiceProgram program, bool asItRuns, CancellationToken stop)
    {
        UiThread.Check();
        ProjectSession profiled = Session;
        // Profile's own start (a container configuration's) is under way as its held service is recorded: that is no other.
        if (profiled.Profiling is not null || asItRuns && launching is not null)
        {
            return "A run is already being profiled";
        }
        string name = run.NameOf(service);
        bool ours = launching is null;
        launching ??= profiled;
        ProfilingSession recording;
        try
        {
            View.Starting(name);
            parts.Dock.Show(ToolWindowIds.Profiler);
            string snapshots = SnapshotFolder;
            string file = FileName(service.Name);
            using (BackgroundWork.Scope())
            {
                recording = program.Recorded is { } place
                    ? await ProfilingSession.AttachAsync(program.Id, file, snapshots, place, resume: true, stop)
                    : await BackgroundWork.Run(() => ProfilingSession.AttachAsync(program.Id, file, snapshots, stop, resume: true), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            View.Stopped($"Profiling {name} was stopped as it started");
            throw;
        }
        catch (Exception refused) when (refused is ProfilingException or InvalidOperationException or IOException or UnauthorizedAccessException
            or TimeoutException or System.ComponentModel.Win32Exception or DiagnosticsClientException)
        {
            string why = $"Could not profile {service.Name}: {refused.Message}";
            if (profiled == Session && !profiled.IsEnded)
            {
                View.Stopped(why);
            }
            return why;
        }
        finally
        {
            if (ours && launching == profiled)
            {
                launching = null;
            }
        }
        if (profiled != Session || profiled.IsEnded || run.IsStopped || profiled.Profiling is not null)
        {
            // Its run, or its folder, ended as the recording began, or another took the window meanwhile: read by no one.
            await recording.DisposeAsync();
            return profiled != Session || profiled.IsEnded ? Refusals.SolutionClosed
                : run.IsStopped ? $"{run.Name} was stopped as {service.Name}'s recording began" : "A run is already being profiled";
        }
        Follow(profiled, recording, name, killable: false);
        run.Profiled = service;
        run.EndProfiling = () => profiled.Profiling == recording && profiled == Session ? SnapshotAsync() : Task.CompletedTask;
        return null;
    }

    private async Task<string?> AttachTargetAsync(AttachTarget? target, bool choose = false)
    {
        UiThread.Check();
        ProjectSession profiled = Session;
        if (Busy(profiled) is { } busy)
        {
            return busy;
        }
        launching = profiled;
        string snapshotFolder = SnapshotFolder;
        try
        {
            if (target is null && choose)
            {
                DotnetProcess? chosen = await parts.ChooseInContainer();
                if (profiled != Session || profiled.IsEnded)
                {
                    return Refusals.SolutionClosed;
                }
                if (chosen is null)
                {
                    return NoneChosen;
                }
                target = InContainer(chosen, ContainerPaths.None, resume: true);
            }
            return target is null ? NothingRunning : await AttachTargetAsync(profiled, target, snapshotFolder);
        }
        finally
        {
            if (launching == profiled)
            {
                launching = null;
            }
        }
    }

    /// <summary>The attach itself, its work said in the status bar from the first moment to the run's hand-over, Stop ending
    /// it; a program the debugger pauses as it waits is said to hold it.</summary>
    private async Task<string?> AttachTargetAsync(ProjectSession profiled, AttachTarget target, string snapshotFolder)
    {
        parts.Dock.Show(ToolWindowIds.Profiler);
        if (parts.Actions.Work.Begin(AppActions.AttachProfiler, $"Attaching the profiler to {target.Name}…") is not { } work)
        {
            return ActionWork.Busy(parts.Actions.Work.Of(AppActions.AttachProfiler)!);
        }
        using (work)
        {
            if (target.Watched is { } app)
            {
                int? started = await BackgroundWork.Run(() => DotnetProcesses.Under(target.ProcessId).FirstOrDefault(p => p.Program.Equals(app, StringComparison.OrdinalIgnoreCase))?.Id);
                if (started is not int process)
                {
                    string why = $"{target.Name} runs under dotnet watch, which has yet to start {app}: Attach Profiler once it runs";
                    if (profiled == Session && !profiled.IsEnded)
                    {
                        View.Stopped(why);
                    }
                    return why;
                }
                target = target with { ProcessId = process };
            }
            if (profiled.IsEnded || profiled != Session)
            {
                return Refusals.SolutionClosed;
            }
            if (Paused(target) is { } paused)
            {
                View.Stopped(paused);
                return paused;
            }
            ProfilingSession running;
            Action unfollow = SayPauses(work, target.ProcessId, target.Elsewhere?.Container, target.Name, ToProfile);
            try
            {
                using (BackgroundWork.Scope())
                {
                    running = target.Elsewhere is not null
                        ? await AttachElsewhereAsync(target, snapshotFolder, work)
                        : await ProfilingSession.AttachAsync(target.ProcessId, target.File, snapshotFolder, work.Stopping);
                }
            }
            catch (Exception failed) when (failed is ProfilingException || failed is OperationCanceledException && work.IsStopped)
            {
                string why = failed is ProfilingException ? failed.Message : $"Profiling {target.Name} was stopped as it started";
                if (profiled == Session && !profiled.IsEnded)
                {
                    View.Stopped(why);
                }
                return why;
            }
            finally
            {
                unfollow();
            }
            if (profiled.IsEnded || profiled != Session || profiled.Profiling is not null)
            {
                await running.DisposeAsync();
                return profiled.IsEnded || profiled != Session ? Refusals.SolutionClosed : "A run is already being profiled";
            }
            // Profile's own program, profiled again, is still Kill's to end.
            Follow(profiled, running, target.Name, killable: target.Elsewhere is null && profiled.Profiled?.Run.ProcessId == target.ProcessId);
            return null;
        }
    }

    /// <summary>Why a program cannot be profiled now: the debugger holds it paused.</summary>
    private string? Paused(AttachTarget target) => Paused(target.ProcessId, target.Elsewhere?.Container, target.Name, ToProfile);

    /// <summary>Why a program (of this machine, or of a container by its id there) cannot be profiled or have its heap read
    /// now, <paramref name="doing"/> it: the debugger holds it paused.</summary>
    private string? Paused(int processId, string? container, string name, string doing) =>
        Session.Debug.Debugging(processId, container) is { State: DebugState.Paused } ? Refusals.PausedInDebugger(name, doing) : null;

    /// <summary>Has a wait for a program's runtime say, while the debugger pauses that program, that it waits for it to go on,
    /// and its own stage again once it does, until the returned action ends it.</summary>
    private Action SayPauses(Working work, int processId, string? container, string name, string doing)
    {
        if (Session.Debug.Debugging(processId, container) is not { } debugged)
        {
            return () => { };
        }
        string? waiting = null;
        void Said(object? sender, EventArgs e)
        {
            if (debugged.State == DebugState.Paused)
            {
                waiting ??= work.Stage;
                work.Say(Refusals.PausedInDebugger(name, doing));
            }
            else if (waiting is { } stage)
            {
                waiting = null;
                work.Say(stage);
            }
        }
        debugged.StateChanged += Said;
        return () => debugged.StateChanged -= Said;
    }

    /// <summary>A program in a container to profile there: by its name in its container, its file's name its program's, its
    /// PDBs' paths made this machine's through <paramref name="paths"/>.</summary>
    private static AttachTarget InContainer(DotnetProcess program, ContainerPaths paths, bool resume) =>
        new(program.Id, $"{program.Program} in {program.Container}", FileName(program.Program)) { Elsewhere = program, Paths = paths, Resume = resume };

    /// <summary>Attach Profiler in a container, under the attach's work: Passenger's tools put in it as the window's (each
    /// stage said, Stop ending it), then the program recorded there.</summary>
    private async Task<ProfilingSession> AttachElsewhereAsync(AttachTarget target, string snapshotFolder, Working work)
    {
        try
        {
            (DotnetProcess found, ProfilePlace place) = await parts.InContainer(target.Elsewhere!, target.Paths ?? ContainerPaths.None,
                UiThread.Marshal<string>(work.Say), work.Stopping);
            work.Say($"Recording {target.Name}…");
            return await ProfilingSession.AttachAsync(found.Id, target.File, snapshotFolder, place, target.Resume, work.Stopping);
        }
        catch (OperationCanceledException) when (work.IsStopped)
        {
            throw new ProfilingException($"Profiling {target.Name} was stopped as it started");
        }
        catch (Exception failed) when (failed is ContainerToolsException or ContainerCommandException or FileNotFoundException or TimeoutException
            or System.Text.Json.JsonException or OperationCanceledException)
        {
            throw new ProfilingException($"Could not profile {target.Name}: {failed.Message}", failed);
        }
    }

    /// <summary>The program Attach Profiler profiles, of those the folder runs (<see cref="ProjectSession.Running"/>): the
    /// selected Aspire service, else the chosen Debug tab's (in its container, too), else the container run's service Profile
    /// records, else the one Profile started that goes on after its snapshot, else the one Run runs, in the Run console or the
    /// terminal (an Avalonia app by the process its inspector said it is; under Hot Reload, the app dotnet watch started,
    /// found as the attach begins).</summary>
    private AttachTarget? Attachable(out string? problem)
    {
        ProjectSession current = Session;
        IReadOnlyList<RunningProgram> running = current.Running;
        problem = null;
        if (current.Aspire.SelectedService is { } selected)
        {
            if (selected.ProcessId is not > 0 || selected.Stopping.IsCancellationRequested
                || running.FirstOrDefault(p => p.Service == selected)?.Services is not { } app)
            {
                problem = $"The selected Aspire service {selected.Plan.Name} is no longer available to profile";
                return null;
            }
            return new(selected.ProcessId.Value, ((IRunService)selected).Name, FileName(selected.Plan.ProgramName)) { Run = app };
        }
        if (running.FirstOrDefault(p => p.Tab is { } tab && tab == current.Debug.Chosen) is { Tab.Session: { } debugging } debugged)
        {
            // A container's program, by its id there, recorded there through the debugger's own map of its paths.
            if (debugging is { Elsewhere: true, Place: { } place, ProcessStarted: { IsCompletedSuccessfully: true } started })
            {
                string program = place.Program ?? $"process {started.Result}";
                DotnetProcess there = new(started.Result, program, string.Empty, string.Empty, true) { Container = place.Where };
                return new AttachTarget(there.Id, debugged.Name, FileName(Path.GetFileNameWithoutExtension(program))) { Elsewhere = there, Paths = place.Paths };
            }
            // Its process on this machine: a container's process id names none of this one's.
            if (debugged.Process is int process)
            {
                LiveApp? app = debugged.App is { ProcessId: int inspected } live && inspected == process ? live : null;
                string program = app?.Plan?.Built ?? debugging.Program;
                return new(process, app?.Name ?? Path.GetFileNameWithoutExtension(program), FileName(Path.GetFileNameWithoutExtension(program)));
            }
        }
        // A container run's service as it runs (Run's log is the CLI's, no program of this machine's): the one Profile records.
        if (current.ServiceRuns.OfType<ContainerRun>().FirstOrDefault() is { } containers && containers.Plan.Profiled() is { } service)
        {
            return new(0, service.Service, FileName(service.Service)) { Run = containers };
        }
        if (running.FirstOrDefault(p => p.Kind == RunningKind.Profile) is { Process: int profiling } profile && current.Profiled is { } profiled)
        {
            return new(profiling, profile.Name, profiled.File);
        }
        if (running.FirstOrDefault(p => p.Kind == RunningKind.Run) is { Plan: { } plan } ran && (ran.App?.ProcessId ?? ran.Process) is int run)
        {
            string name = ran.App?.Name ?? plan.Name;
            string program = ran.App?.Plan?.Built is { } built ? Path.GetFileNameWithoutExtension(built) : plan.ProgramName;
            // Under Hot Reload the process Run started is dotnet watch's own, and the app one it started in turn.
            return new(run, name, FileName(program), parts.Builds().WatchesHotReload ? program : null);
        }
        return null;
    }

    /// <summary>A program Attach Profiler can profile: its process, its name as the window says it and as its snapshot's
    /// file is named, and, when the process is dotnet watch's, the app's program to find among the ones it started.</summary>
    private sealed record AttachTarget(int ProcessId, string Name, string File, string? Watched = null)
    {
        /// <summary>The program in a container, recorded there; null for one of this machine's.</summary>
        public DotnetProcess? Elsewhere { get; init; }

        /// <summary>The run of services whose service by <see cref="Name"/> this is, recorded through the run.</summary>
        public ServiceRun? Run { get; init; }

        /// <summary>How its PDBs' paths are this machine's, for one in a container.</summary>
        public ContainerPaths? Paths { get; init; }

        /// <summary>Whether a program held at its start is let go once recording: one the debugger holds is the debugger's.</summary>
        public bool Resume { get; init; }
    }

    /// <summary>A name made fit for a file's: what a file system refuses, and a colon, as dashes.</summary>
    private static string FileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' || c == '/' || c == '\\' ? '-' : c)).Trim();

    /// <summary>A control the Frames page names: its XAML opened at its element, and, when the snapshot is of the app the
    /// Live Visual Tree follows, the control chosen there.</summary>
    private void OpenControl(UiControl control)
    {
        bool revealed = shownProcess is int process && parts.RevealControl(process, control.Id);
        if (control.File is { } file && File.Exists(file))
        {
            int line = AppPlaces.LineOf(control, path => CSharp.ServerPlaces.TextOf(parts.Editors, path));
            parts.Navigate(PathNames.Canonical(file), new TextPosition(Math.Max(0, line - 1), 0));
        }
        else if (!revealed)
        {
            parts.Say($"{control.Label} was made in code, or by XAML compiled without source information");
        }
    }

    /// <summary>Shows a snapshot in the profiler window, its controls' rows naming their elements' lines in the editor's
    /// count, each file read once for it.</summary>
    private void Show(Snapshot snapshot, string name)
    {
        Func<string, TextSnapshot?> texts = CSharp.ServerPlaces.Texts(parts.Editors);
        View.FramesPage.LineOf = control => AppPlaces.LineOf(control, texts);
        View.Show(snapshot, name);
    }

    /// <summary>Ends the run (Get Snapshot) and shows what it found: the last run's snapshot, or why it has none. Asked
    /// again while the snapshot is read (Get Snapshot as the program ends, say), it answers with that same reading. While
    /// the debugger holds the program paused, the run's end could not be read, so it is refused and the run goes on.</summary>
    public Task<SnapshotRead> SnapshotAsync()
    {
        if (Held(ToGetSnapshot) is { } paused)
        {
            parts.Say(paused);
            return Task.FromResult(new SnapshotRead(null, paused));
        }
        if (Session.Profiling is { } run)
        {
            Session.Profiling = null;
            snapshotting = ReadSnapshotAsync(Session, run);
        }
        return snapshotting ?? Task.FromResult(new SnapshotRead(null, "Nothing has been profiled"));
    }

    /// <summary>Why the run's program cannot be acted on now, <paramref name="doing"/> it: the debugger holds it paused.</summary>
    private string? Held(string doing) =>
        Session.Profiling is { HasExited: false } run ? Paused(run.ProcessId, run.Place?.Where, profilingName, doing) : null;

    /// <summary>An agent's end of a run whose program the debugger holds paused, which cannot be read until it goes on: the
    /// run let go unread, the program going on as it was, so that another may begin; and why. Null when it is not held.</summary>
    public string? LetGoHeld()
    {
        if (Session.Profiling is not { } run || Held(ToProfile) is not { } paused)
        {
            return null;
        }
        Session.Profiling = null;
        string why = $"{paused} (its run so far was let go unread)";
        View.Stopped(why);
        parts.Say(why);
        // A program Profile started goes on, Kill's to end, and is put away once it has ended.
        BackgroundWork.Track(Session.Profiled?.Run == run ? run.LetGoAsync().AsTask() : run.DisposeAsync().AsTask());
        ShowKill();
        return why;
    }

    private async Task<SnapshotRead> ReadSnapshotAsync(ProjectSession profiled, ProfilingSession run)
    {
        bool dispose = profiled.Profiled?.Run != run;
        CancellationToken closing = profiled.Aspire.ClosingToken;
        string name = profilingName;
        string kept = SnapshotFolder;
        parts.Say($"Reading the snapshot of {name}…");
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            // Held by the scope, and back on no thread of the window's, which may be gone by then.
            Snapshot snapshot = await BackgroundWork.Unheld(() => run.StopAsync()).ConfigureAwait(false);
            string read = $"Snapshot of {name}: {snapshot.Samples:N0} samples, kept in {Path.GetDirectoryName(snapshot.TracePath)}";
            if (closing.IsCancellationRequested)
            {
                // Its folder was left as it was read: it is that folder's to list when it opens again.
                return new SnapshotRead(snapshot, read);
            }
            IReadOnlyList<SnapshotFile> files = await BackgroundWork.Unheld(() =>
            {
                Snapshots.Trim(kept);
                return Snapshots.List(kept);
            }).ConfigureAwait(false);
            try
            {
                await UiThread.InvokeAsync(() =>
                {
                    if (!closing.IsCancellationRequested && profiled == Session && !profiled.IsEnded)
                    {
                        shownProcess = run.Place is null ? run.ProcessId : null;
                        Show(snapshot, name);
                        parts.Dock.Show(ToolWindowIds.Profiler);
                        ShowKept(kept, files);
                        parts.Say(read);
                    }
                    return true;
                }).WaitAsync(closing).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (closing.IsCancellationRequested)
            {
            }
            return new SnapshotRead(snapshot, read);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            string unread = $"The snapshot could not be read: {error.Message}";
            if (!closing.IsCancellationRequested)
            {
                try
                {
                    await UiThread.InvokeAsync(() =>
                    {
                        if (!closing.IsCancellationRequested && profiled == Session && !profiled.IsEnded)
                        {
                            View.Stopped(unread);
                            parts.Say(unread);
                        }
                        return true;
                    }).WaitAsync(closing).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (closing.IsCancellationRequested)
                {
                }
            }
            return new SnapshotRead(null, unread);
        }
        finally
        {
            // A program Profile started goes on, Kill's to end, and is put away once it has ended.
            if (dispose)
            {
                await run.DisposeAsync().ConfigureAwait(false);
            }
            if (!closing.IsCancellationRequested)
            {
                try
                {
                    await UiThread.InvokeAsync(() =>
                    {
                        if (!closing.IsCancellationRequested && profiled == Session && !profiled.IsEnded)
                        {
                            ShowKill();
                        }
                        return true;
                    }).WaitAsync(closing).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (closing.IsCancellationRequested)
                {
                }
            }
        }
    }

    /// <summary>Get Memory Snapshot: the running program's heap, listed on the Memory page with the run's earlier ones to
    /// compare it with; the run goes on. One given up (<paramref name="cancel"/>) is said so.</summary>
    public async Task<MemorySnapshot?> MemorySnapshotAsync(CancellationToken cancel = default) => (await MemorySnapshotAsync(ask: true, cancel)).Heap;

    /// <summary>Get Memory Snapshot, or why there is none. Where only the program's runtime can write it, putting
    /// createdump's lines into the program's output, the person is asked first, once for the run; an agent's
    /// (<paramref name="ask"/> false), which asked for it with no one to ask, is taken, its note saying so. One the debugger
    /// holds paused is refused, its work, said from the first moment, Stop ends, and one whose program ends says so.</summary>
    public async Task<(MemorySnapshot? Heap, string? Note)> MemorySnapshotAsync(bool ask, CancellationToken cancel = default)
    {
        if (Session.Profiling is not { } run)
        {
            parts.Say(NoneProfiling);
            return (null, "no program is being profiled");
        }
        if (Held(ToTakeHeap) is { } paused)
        {
            parts.Say(paused);
            return (null, paused);
        }
        string name = profilingName;
        if (parts.Actions.Work.Begin(AppActions.MemorySnapshot, $"Taking a memory snapshot of {name}…") is not { } work)
        {
            string busy = ActionWork.Busy(parts.Actions.Work.Of(AppActions.MemorySnapshot)!);
            parts.Say(busy);
            return (null, busy);
        }
        using IDisposable held = BackgroundWork.Scope();
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancel, work.Stopping);
        Action unfollow = SayPauses(work, run.ProcessId, run.Place?.Where, name, ToTakeHeap);
        try
        {
            MemorySnapshot heap;
            string? note = null;
            try
            {
                heap = await run.TakeMemorySnapshotAsync(intoOutput: run == intoOutputAgreed, stopping.Token);
            }
            catch (DumpIntoOutputException only) when (!run.HasExited)
            {
                if (ask && !await parts.Confirm("Get Memory Snapshot", $"{only.Message}. Take it anyway?", "Take Snapshot"))
                {
                    parts.Say($"No memory snapshot of {name}: its output would carry createdump's lines");
                    return (null, only.Message);
                }
                intoOutputAgreed = run;
                note = $"{only.Message}: it was taken so, and the program's output carries createdump's lines";
                heap = await run.TakeMemorySnapshotAsync(intoOutput: true, stopping.Token);
            }
            View.ShowMemory(heap);
            parts.Say($"{heap.Name} of {name}: {heap.Objects:N0} objects, {heap.Bytes / 1048576.0:N1} MB");
            return (heap, note);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ServerNotAvailableException or DiagnosticsClientException
            or TimeoutException or HeapUnreadableException or ProfilingException or OperationCanceledException)
        {
            if (error is not OperationCanceledException && await EndedAsync(run))
            {
                string ended = $"{name} ended before its memory snapshot";
                parts.Say(ended);
                return (null, ended);
            }
            string why = error is OperationCanceledException ? "it was stopped" : error.Message;
            parts.Say($"No memory snapshot: {why}");
            return (null, why);
        }
        finally
        {
            unfollow();
            work.Dispose();
        }
    }

    /// <summary>Whether a run's program has ended, or ends within a moment: what a snapshot of it failed on as it ended.</summary>
    private static async Task<bool> EndedAsync(ProfilingSession run)
    {
        if (!run.HasExited)
        {
            // Bounded: a program that goes on was not what failed the snapshot.
            await Task.WhenAny(run.ExitedAsync(), run.PutAway, Task.Delay(Waits.Of(TimeSpan.FromSeconds(2))));
        }
        return run.HasExited;
    }

    /// <summary>Opens a snapshot written before (a .nettrace file, Passenger's or dotnet-trace's) in the profiler window.</summary>
    public async Task<Snapshot?> OpenSnapshotAsync(string path)
    {
        parts.Dock.Show(ToolWindowIds.Profiler);
        parts.Say($"Reading {Path.GetFileName(path)}…");
        try
        {
            Snapshot snapshot = await BackgroundWork.Run(() => Snapshot.Read(path));
            shownProcess = null;
            Show(snapshot, Path.GetFileNameWithoutExtension(path));
            ShowKill();
            parts.Say($"{Path.GetFileName(path)}: {snapshot.Samples:N0} samples");
            return snapshot;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            parts.Say($"{Path.GetFileName(path)} is not a snapshot Passenger can read: {error.Message}");
            return null;
        }
    }

    private async Task PickSnapshotAsync()
    {
        if (await parts.PickSnapshot() is { } path)
        {
            await OpenSnapshotAsync(path);
        }
    }

    private async Task ProfileReportingAsync()
    {
        if (await ProfileAsync() is { } problem)
        {
            parts.Say(problem);
        }
    }

    private async Task AttachReportingAsync()
    {
        if (await AttachAsync(choose: true) is { } problem)
        {
            parts.Say(problem);
        }
    }

    /// <summary>A method a row names opened at its source: by its module's PDB where it has one (read in its container for
    /// a program there), at its first statement's line; else by Roslyn's symbol search for its name in its type.</summary>
    private async Task OpenMethodAsync(ProfiledMethod method)
    {
        using IDisposable held = BackgroundWork.Scope();
        if (method.Code is { } code && View.Snapshot is { } snapshot)
        {
            IReadOnlyDictionary<MethodCode, (string File, int Line)> found = await MethodSources.FindAsync(snapshot, [code]);
            if (found.TryGetValue(code, out (string File, int Line) at) && File.Exists(at.File))
            {
                string file = PathNames.Canonical(at.File);
                // The PDB's line is its compiler's, counted apart from the editor's in a file holding a break only it counts.
                parts.Navigate(file, CSharp.ServerPlaces.PositionOfCompiled(file, CSharp.ServerPlaces.TextOf(parts.Editors, file), new Lsp.LspPosition(at.Line - 1, 0)));
                return;
            }
        }
        await OpenByNameAsync(method.Name);
    }

    /// <summary>A method found by its name in its type: a state machine's or a lambda's by the method it was written in, a
    /// generic type's by its name without its arguments; one of no type of the open code's is not looked for elsewhere.</summary>
    private async Task OpenByNameAsync(string method)
    {
        if (FrameName.Of(method) is not { } split || Session.CSharp?.Session is not { IsLoaded: true } server)
        {
            parts.Say($"{method} is not in the open code");
            return;
        }
        string name = split.Method;
        string[] nesting = split.Type.Split('+');
        // The compiler's own types (<>c, <CrunchAsync>d__0) are named for nothing written: the type is the one holding them.
        string written = nesting.LastOrDefault(t => !t.StartsWith('<')) ?? nesting[0];
        string typeName = written[(written.LastIndexOf('.') + 1)..].Split('`')[0];
        string generated = nesting[^1].StartsWith('<') && name == "MoveNext" ? nesting[^1] : name;
        // Roslyn names a constructor by its type, and a lambda's or a state machine's method is the one that holds it.
        string query = split.IsConstructor ? typeName : generated.Split('>')[0].TrimStart('<').Split('|')[0];
        IReadOnlyList<Lsp.SymbolInformation> found;
        try
        {
            found = await server.SymbolsAsync(query, CancellationToken.None);
        }
        catch (Lsp.LanguageServerFailed failed)
        {
            parts.Say($"{method} could not be looked for: {failed.Reason}");
            return;
        }
        if (found.FirstOrDefault(s => s.Name.StartsWith(query, StringComparison.Ordinal) && InType(s.Owner, typeName))?.Location is not { } location)
        {
            parts.Say($"{method} is not in the open code");
            return;
        }
        parts.NavigateToServer(Lsp.DocumentUris.ToPath(location.Uri), location.Range.Start);
    }

    /// <summary>Whether a symbol Roslyn lists is in a type of this name, its owner (<see cref="Lsp.SymbolInformation.Owner"/>)
    /// named with the type's namespace or outer types, and with its type parameters (Box&lt;T&gt;).</summary>
    private static bool InType(string? owner, string type)
    {
        string named = owner is { } c && c.IndexOf('<', StringComparison.Ordinal) is int angle and >= 0 ? c[..angle] : owner ?? string.Empty;
        return named == type || named.EndsWith($".{type}", StringComparison.Ordinal);
    }
}

/// <summary>A profiled run's snapshot as read at its end, or none, and what the person is told of it: the profiler window
/// and the status bar say the same.</summary>
public sealed record SnapshotRead(Snapshot? Snapshot, string Message);

/// <summary>A program Profile started: its run, which records it until Get Snapshot or its end and holds it after; its
/// configuration's name, as the window says it, and its program's, as its snapshots' files are named; and its end, done
/// once the Run console has the last of what it wrote.</summary>
public sealed record ProfiledProgram(ProfilingSession Run, string Name, string File, Task Landed);
