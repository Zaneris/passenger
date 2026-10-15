using Microsoft.Diagnostics.NETCore.Client;
using Passenger.App.Docking;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Profiler;
using Passenger.Text;

namespace Passenger.App.Profiling;

/// <summary>What a window lends its profiler: its session and that session's builds and runs, the tool windows' frame and
/// the window's actions, the Run console a profiled program writes to, the status bar, the store's folder, the system's
/// file panel for a snapshot to open, going to a place in the code, and choosing a control of a process in the Live Visual
/// Tree.</summary>
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
    Func<int, long, bool> RevealControl);

/// <summary>The window's profiler, as Rider's: Profile builds and starts the chosen configuration under the sampling
/// profiler (an Avalonia app with the inspector in it, which records its frames), or Attach Profiler profiles the program
/// Run or Debug started from now on; the Performance Profiler window follows the run, Get Snapshot (or the program's end)
/// shows where its time went and a memory snapshot what its heap holds, snapshots written before open again, and a method
/// or a control named there opens in the code. The run is the session's (<see cref="ProjectSession.Profiling"/>), ended
/// with its folder. A UI-thread object of the window.</summary>
public sealed class ProfilerCommands
{
    private const string NothingRunning = "Nothing is running to profile: Run or Debug a configuration first, or Profile one";

    private readonly ProfilerParts parts;
    private string profilingName = string.Empty;
    private Task<SnapshotRead>? snapshotting;

    /// <summary>The process the snapshot shown came from, none for one opened from its file.</summary>
    private int? shownProcess;

    public ProfilerCommands(ProfilerParts parts)
    {
        this.parts = parts;
        View = new ProfilerView();
        parts.Dock.Add(new ToolWindow("profiler", "Performance Profiler", "Icon.Profile", View, ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowProfiler,
            Menus = [new MenuPlace("Tools", 0, 1)],
        });
        parts.Actions.Add(new IdeAction(AppActions.Profile, "Profile", "Run", context => _ = ProfileReportingAsync())
        {
            WhyNot = _ => Session.Profiling is not null ? "A run is already being profiled"
                : Session.Configurations.Chosen is null ? "Nothing to profile: no executable project" : null,
            Menus = [new MenuPlace("Run", 0, 2)],
        });
        parts.Actions.Add(new IdeAction(AppActions.AttachProfiler, "Attach Profiler to Running Program", "Run", context => _ = AttachReportingAsync())
        {
            WhyNot = _ => Session.Profiling is not null ? "A run is already being profiled" : Attachable() is null ? NothingRunning : null,
            Menus = [new MenuPlace("Run", 3, 0)],
        });
        View.StopRequested += (_, _) => _ = SnapshotAsync();
        View.KillRequested += (_, _) => Session.Profiling?.Kill();
        View.OpenRequested += (_, method) => _ = OpenMethodAsync(method);
        View.FramesPage.ControlOpened += (_, control) => OpenControl(control);
        View.OpenSnapshotRequested += (_, _) => _ = PickSnapshotAsync();
        View.OpenKeptRequested += (_, path) => _ = OpenSnapshotAsync(path);
        View.RevealSnapshotsRequested += (_, _) =>
        {
            Directory.CreateDirectory(SnapshotFolder);
            ChildProcesses.Reveal(SnapshotFolder);
        };
        View.MemoryRequested += (_, _) => _ = MemorySnapshotAsync();
    }

    /// <summary>The Performance Profiler tool window.</summary>
    public ProfilerView View { get; }

    private ProjectSession Session => parts.Session();

    /// <summary>Where the open folder's snapshots are kept, a .nettrace file a run (which PerfView and speedscope read
    /// too): beside its state, as Rider keeps a solution's, the newest <see cref="Snapshots.Kept"/>. With no folder open,
    /// the store's own.</summary>
    public string SnapshotFolder => Path.Combine(Session.Folder is not { } folder ? parts.StateFolder() : State.ProjectState.FolderOf(parts.StateFolder(), folder), "snapshots");

    /// <summary>Lists the open folder's snapshots in the profiler window, after taking off those past the cap; read off
    /// the UI thread.</summary>
    public async Task KeepSnapshotsAsync(bool trim = false)
    {
        string kept = SnapshotFolder;
        IReadOnlyList<SnapshotFile> files = await BackgroundWork.Run(() =>
        {
            if (trim)
            {
                Snapshots.Trim(kept);
            }
            return Snapshots.List(kept);
        });
        if (kept == SnapshotFolder)
        {
            View.ShowKept(kept, files);
        }
    }

    /// <summary>Resolves and builds the chosen configuration, then starts its plan under the profiler from its first
    /// instruction, as Rider's Profile does, with these arguments in place of its own if given. Null once it runs, or what
    /// stopped it, which the profiler window says too (all but a failed build, which the Build window shows).</summary>
    public async Task<string?> ProfileAsync(string? arguments = null)
    {
        ProjectSession profiled = Session;
        BuildRunController builds = parts.Builds();
        if (profiled.Profiling is not null)
        {
            return "A run is already being profiled";
        }
        if (profiled.Configurations.Chosen is not { } chosen)
        {
            return "Nothing to profile: no executable project";
        }
        (LaunchPlan? plan, string? problem) = await builds.ResolvePlanAsync(chosen, arguments);
        if (plan?.Build is { } step && await builds.BuildBeforeLaunchAsync(step) is { Succeeded: false } unbuilt)
        {
            return unbuilt.Message;
        }
        if (profiled.IsEnded)
        {
            return "The solution was closed";
        }
        parts.Dock.Show("profiler");
        if ((plan is null ? problem : plan.WhyNot("profile")) is { } why)
        {
            View.Stopped(why);
            return why;
        }
        OutputView run = parts.RunConsole;
        // An Avalonia app is profiled with the inspector in it, which records its frames into the run's trace.
        LaunchPlan launched = profiled.InspectedProfile(plan!);
        LiveApp? inspected = profiled.ProfiledApp;
        try
        {
            ProfileTarget target = new(launched.Program, launched.Arguments, launched.WorkingDirectory, launched.Environment);
            run.Clear();
            ConsoleSink console = run.Sink;
            ProfilingSession running;
            // Held until the program has reached the profiler (or never will), not while it runs: that is the person's.
            using (BackgroundWork.Scope())
            {
                running = await ProfilingSession.StartAsync(target, SnapshotFolder,
                    (text, error) => console.Write(text, error ? ConsoleStream.Error : ConsoleStream.Output));
            }
            profiled.Profiling = running;
            if (profiled.IsEnded)
            {
                // Its folder was left as it started: it ends with the folder's other programs.
                running.Kill();
                return "The solution was closed";
            }
            profilingName = chosen.Name;
            run.Input = running.Input is { } input ? ProgramInput.Of(input) : null;
            View.Running(chosen.Name, () => running.Elapsed);
            parts.Say($"Profiling {chosen.Name}");
            // A program that ends by itself ends the run, and its snapshot shows.
            _ = running.ExitedAsync().ContinueWith(_ => UiThread.Post(() =>
            {
                inspected?.Dispose();
                console.End(ConsoleStream.Output);
                console.End(ConsoleStream.Error);
                if (!profiled.Runner.IsRunning)
                {
                    run.Input = null;
                }
                run.Flush();
                if (profiled.Profiling == running && profiled == Session)
                {
                    _ = SnapshotAsync();
                }
            }), TaskScheduler.Default);
            return null;
        }
        catch (Exception error) when (error is ProfilingException or InvalidOperationException or IOException or UnauthorizedAccessException
            or TimeoutException or System.ComponentModel.Win32Exception or DiagnosticsClientException)
        {
            inspected?.Dispose();
            profiled.Profiling = null;
            View.Stopped(error.Message);
            return error.Message;
        }
    }

    /// <summary>Attach Profiler: profiles the program Debug or Run started, from now until Get Snapshot or its end, as
    /// dotTrace attaches to a running process; an Avalonia app's inspector records its frames for as long. The program goes
    /// on as it was, after too. Null once it is profiled, else why not, which the profiler window says too.</summary>
    public async Task<string?> AttachAsync()
    {
        ProjectSession profiled = Session;
        if (profiled.Profiling is not null)
        {
            return "A run is already being profiled";
        }
        if (Attachable() is not { } target)
        {
            return NothingRunning;
        }
        parts.Dock.Show("profiler");
        ProfilingSession running;
        try
        {
            using (BackgroundWork.Scope())
            {
                running = await ProfilingSession.AttachAsync(target.ProcessId, target.File, SnapshotFolder);
            }
        }
        catch (ProfilingException failed)
        {
            View.Stopped(failed.Message);
            return failed.Message;
        }
        if (profiled.IsEnded || profiled.Profiling is not null)
        {
            await running.DisposeAsync();
            return profiled.IsEnded ? "The solution was closed" : "A run is already being profiled";
        }
        profiled.Profiling = running;
        profilingName = target.Name;
        View.Running(target.Name, () => running.Elapsed, attached: true);
        parts.Say($"Profiling {target.Name} from now: Get Snapshot when it has done what is to be measured");
        _ = running.ExitedAsync().ContinueWith(_ => UiThread.Post(() =>
        {
            if (profiled.Profiling == running && profiled == Session)
            {
                _ = SnapshotAsync();
            }
        }), TaskScheduler.Default);
        return null;
    }

    /// <summary>The program Attach Profiler profiles: the one being debugged, else the one Run runs (an Avalonia app by the
    /// process its inspector said it is, which under Hot Reload is not dotnet watch's); its name as the window says it,
    /// and as its snapshot's file is named.</summary>
    private (int ProcessId, string Name, string File)? Attachable()
    {
        ProjectSession current = Session;
        if (current.Debug.IsDebugging && current.Debug.Session is { ProcessStarted.IsCompletedSuccessfully: true } debugging)
        {
            int process = debugging.ProcessStarted.Result;
            LiveApp? app = current.LiveApp is { ProcessId: int inspected } live && inspected == process ? live : null;
            string program = app?.Plan?.Built ?? debugging.Program;
            return (process, app?.Name ?? Path.GetFileNameWithoutExtension(program), FileName(Path.GetFileNameWithoutExtension(program)));
        }
        if (current.Runner.IsRunning && (current.RunApp?.ProcessId ?? current.Runner.ProcessId) is int running)
        {
            if (current.RunApp is { State: not LiveAppState.Connected })
            {
                // Under watch the runner is the watcher, never the target. Wait for that target's inspector after restart.
                return null;
            }
            string name = current.RunApp?.Name ?? current.Configurations.Chosen?.Name ?? "the program";
            return (running, name, FileName(current.RunApp?.Plan?.Built is { } built ? Path.GetFileNameWithoutExtension(built) : name));
        }
        return null;
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
            parts.Navigate(PathNames.Canonical(file), new TextPosition(Math.Max(0, control.Line - 1), 0));
        }
        else if (!revealed)
        {
            parts.Say($"{control.Label} was made in code, or by XAML compiled without source information");
        }
    }

    /// <summary>Ends the run (Get Snapshot) and shows what it found: the last run's snapshot, or why it has none. Asked
    /// again while the snapshot is read (Get Snapshot as the program ends, say), it answers with that same reading.</summary>
    public Task<SnapshotRead> SnapshotAsync()
    {
        if (Session.Profiling is { } run)
        {
            Session.Profiling = null;
            snapshotting = ReadSnapshotAsync(run);
        }
        return snapshotting ?? Task.FromResult(new SnapshotRead(null, "Nothing has been profiled"));
    }

    private async Task<SnapshotRead> ReadSnapshotAsync(ProfilingSession run)
    {
        string name = profilingName;
        parts.Say($"Reading the snapshot of {name}...");
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            Snapshot snapshot = await run.StopAsync();
            shownProcess = run.ProcessId;
            View.Show(snapshot, name);
            // Brought forward over the console or Debug window the run's program was driven from.
            parts.Dock.Show("profiler");
            await KeepSnapshotsAsync(trim: true);
            string read = $"Snapshot of {name}: {snapshot.Samples:N0} samples, kept in {Path.GetDirectoryName(snapshot.TracePath)}";
            parts.Say(read);
            return new SnapshotRead(snapshot, read);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            string unread = $"The snapshot could not be read: {error.Message}";
            View.Stopped(unread);
            parts.Say(unread);
            return new SnapshotRead(null, unread);
        }
        finally
        {
            await run.DisposeAsync();
        }
    }

    /// <summary>Get Memory Snapshot: the running program's heap, listed on the Memory page with the run's earlier ones to
    /// compare it with; the run goes on.</summary>
    public async Task<MemorySnapshot?> MemorySnapshotAsync()
    {
        if (Session.Profiling is not { } run)
        {
            parts.Say("A memory snapshot needs a program being profiled");
            return null;
        }
        parts.Say($"Taking a memory snapshot of {profilingName}...");
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            MemorySnapshot heap = await run.TakeMemorySnapshotAsync();
            View.ShowMemory(heap);
            parts.Say($"{heap.Name} of {profilingName}: {heap.Objects:N0} objects, {heap.Bytes / 1048576.0:N1} MB");
            return heap;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ServerNotAvailableException or DiagnosticsClientException
            or TimeoutException or HeapUnreadableException)
        {
            parts.Say($"No memory snapshot: {error.Message}");
            return null;
        }
    }

    /// <summary>Opens a snapshot written before (a .nettrace file, Passenger's or dotnet-trace's) in the profiler window.</summary>
    public async Task<Snapshot?> OpenSnapshotAsync(string path)
    {
        parts.Dock.Show("profiler");
        parts.Say($"Reading {Path.GetFileName(path)}...");
        try
        {
            Snapshot snapshot = await BackgroundWork.Run(() => Snapshot.Read(path));
            shownProcess = null;
            View.Show(snapshot, Path.GetFileNameWithoutExtension(path));
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
        if (await AttachAsync() is { } problem)
        {
            parts.Say(problem);
        }
    }

    /// <summary>A method the profiler names, found in the source by Roslyn's symbol search: its name, in its type.</summary>
    private async Task OpenMethodAsync(string method)
    {
        string bare = method.Split('(')[0];
        int dot = bare.LastIndexOf('.');
        if (dot <= 0 || Session.CSharp?.Session is not { IsLoaded: true } server)
        {
            parts.Say($"{method} is not in the open code");
            return;
        }
        string name = bare[(dot + 1)..];
        string type = bare[..dot];
        string typeName = type[(type.LastIndexOf('.') + 1)..];
        // Roslyn names a constructor by its type, and a lambda's method is the one that holds it.
        string query = name is ".ctor" ? typeName : name.Split('>')[0].TrimStart('<').Split('|')[0];
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
        Lsp.SymbolInformation? best = found.FirstOrDefault(s => s.Name.StartsWith(query, StringComparison.Ordinal) && (s.ContainerName ?? string.Empty).EndsWith(typeName, StringComparison.Ordinal))
            ?? found.FirstOrDefault(s => s.Name.StartsWith(query, StringComparison.Ordinal));
        if (best?.Location is not { } location)
        {
            parts.Say($"{method} is not in the open code");
            return;
        }
        parts.Navigate(Lsp.DocumentUris.ToPath(location.Uri), new TextPosition(location.Range.Start.Line, location.Range.Start.Character));
    }
}

/// <summary>A profiled run's snapshot as read at its end, or none, and what the person is told of it: the profiler window
/// and the status bar say the same.</summary>
public sealed record SnapshotRead(Snapshot? Snapshot, string Message);
