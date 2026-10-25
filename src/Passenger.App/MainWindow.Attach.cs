using Passenger.App.Containers;
using Passenger.App.Debugging;
using Passenger.Core;
using Passenger.Profiler;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Attach to Process and Reattach to Process: the .NET programs running on this machine and in the container
/// engine's running containers, listed in a dialog, and the debugger attached to the one chosen as a session like any
/// other, which Detach (the debugger's own action) lets go of again with the program running on. On macOS it asks
/// nothing more of the system than a debugged start does: the runtime is reached through its debugger pipes, never the
/// task port. A container's program is debugged by Passenger's adapter inside its container, over docker exec
/// (<see cref="ContainerTools"/>).</summary>
public partial class MainWindow
{
    /// <summary>The programs attached to in this window, newest last: by name and command line too (and container), so
    /// one started again under another process id is found again.</summary>
    private readonly List<DotnetProcess> attachedTo = [];

    /// <summary>The last session Attach to Process or Reattach opened, which Rerun reattaches.</summary>
    private Debugging.DebugSession? attachSession;

    /// <summary>The Attach to Process dialog open over the window, or null.</summary>
    public AttachToProcessDialog? AttachToProcessDialog { get; private set; }

    /// <summary>Where this window's container tools come from, which its sessions hold: by default beside the app, a dev
    /// build's artifacts and the state's cache, downloaded from <see cref="Releases"/>' release of this version.</summary>
    public ContainerToolsSource? ContainerToolsFrom { get; init; }

    /// <summary>Attach to Process and Reattach to Process, in the Run menu before Edit Configurations.</summary>
    private void AddAttaching()
    {
        Add(A.AttachToProcess, "Attach to Process…", "Debug", _ => ShowAttachToProcess(), menu: ("Run", 0, 3));
        Add(A.ReattachToProcess, "Reattach to Process…", "Debug", context => IdeErrors.Observe(Reattach()),
            _ => attachedTo.Count == 0 ? "Nothing has been attached to in this window: Attach to Process chooses a program" : null, ("Run", 0, 4));
    }

    /// <summary>Every .NET program a debugger can be attached to: this machine's, then those in running containers.</summary>
    public Task<IReadOnlyList<DotnetProcess>> AttachableAsync()
    {
        ContainerEngine engine = Engine;
        return BackgroundWork.Run(async () =>
        {
            Task<IReadOnlyList<DotnetProcess>> containers = ContainerProcesses.ListAsync(engine, CancellationToken.None);
            IReadOnlyList<DotnetProcess> local = DotnetProcesses.List();
            IReadOnlyList<DotnetProcess> inContainers;
            try
            {
                inContainers = await containers;
            }
            catch (Exception unread) when (unread is ContainerCommandException or ContainerToolsException or IOException or TimeoutException
                or System.Text.Json.JsonException or FileNotFoundException)
            {
                IdeLog.Write(IdeLog.Debugger, $"Attach to Process could not list the containers' programs: {unread.Message}");
                inContainers = [];
            }
            return (IReadOnlyList<DotnetProcess>)[.. local, .. inContainers];
        });
    }

    /// <summary>The dialog over the window, searched for what is given; the program chosen there is attached to.</summary>
    public AttachToProcessDialog ShowAttachToProcess(string search = "")
    {
        AttachToProcessDialog dialog = new(AttachableAsync, WasAttached, search);
        AttachToProcessDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            if (AttachToProcessDialog == dialog)
            {
                AttachToProcessDialog = null;
            }
        };
        IdeErrors.Observe(Chosen());
        return dialog;

        async Task Chosen()
        {
            if (await dialog.ShowDialog<DotnetProcess?>(this) is { } chosen && await AttachAsync(chosen) is { } problem)
            {
                status.Text = problem;
            }
        }
    }

    /// <summary>Attaches the debugger to a .NET program, the Debug window open on its frames, beside any other program
    /// debugged: null once the session is open, else why it could not, as the status bar says. The program's last session,
    /// still attached (Reattach), is let go of first, the program running on. A container's program is attached to from
    /// inside its container, Passenger's tools copied in first when they are not there.</summary>
    public async Task<string?> AttachAsync(DotnetProcess process)
    {
        UiThread.Check();
        if (process.WhyNot is { } why)
        {
            return $"Cannot attach to {process.Program}: {why}";
        }
        if (work.Begin(AppActions.AttachToProcess, $"Attaching to {process.Program}…") is not { } attaching)
        {
            return ActionWork.Busy(work.Of(AppActions.AttachToProcess)!);
        }
        using Working held = attaching;
        using IDisposable background = BackgroundWork.Scope();
        DotnetProcess target = process;
        AdapterPlace? place = null;
        string name = process.Program;
        if (process.Container is { } container)
        {
            name = $"{process.Program} in {container}";
            try
            {
                (target, place) = await InContainerAsync(process, container, held);
            }
            catch (Exception failed) when (failed is ContainerToolsException or ContainerCommandException or FileNotFoundException or OperationCanceledException or TimeoutException
                or System.Text.Json.JsonException)
            {
                return failed is OperationCanceledException ? $"Attaching to {name} was stopped" : $"Could not attach to {name}: {failed.Message}";
            }
            if (target.WhyNot is { } blocked)
            {
                return $"Cannot attach to {name}: {blocked}";
            }
        }
        attachedTo.RemoveAll(p => Same(p, target));
        attachedTo.Add(target);
        ShowToolWindow(ToolWindowIds.Debug);
        Bottom.Debug.ShowTab(console: false);
        string? refused = await session.Debug.AttachAsync(target.Id, name, hotReload: place is not null && target.HotReload, place: place);
        attachSession = session.Debug.SessionNamed(name);
        if (refused is not null && target.Note is { } note && place is not null)
        {
            return $"{refused} ({note})";
        }
        if (refused is null && target.Note is { } known)
        {
            Say($"Debugging {name}: {known}");
        }
        return refused;
    }

    /// <summary>What attaching in a container takes: the program by its id there, reached through Passenger's tools
    /// (<see cref="ProgramInContainerAsync"/>), and the adapter's place, docker exec.</summary>
    private async Task<(DotnetProcess Target, AdapterPlace Place)> InContainerAsync(DotnetProcess process, string container, Working held)
    {
        (DotnetProcess target, ContainerReach reach) = await ProgramInContainerAsync(process, container, UiThread.Marshal<string>(held.Say), held.Stopping);
        return (target, reach.Adapter(process.Program, ContainerPaths.None));
    }

    /// <summary>A program in a container reached through Passenger's tools (<see cref="ContainerTools.ReachAsync"/>) and found
    /// there by its id: the one of its command line, or, one named by its id alone (a debugged session's), as it is; its note
    /// given the container's caveat.</summary>
    private Task<(DotnetProcess Target, ContainerReach Reach)> ProgramInContainerAsync(DotnetProcess process, string container, Action<string> say, CancellationToken stop)
    {
        ContainerEngine engine = Engine;
        ContainerToolsSource source = session.ContainerTools;
        return BackgroundWork.Run(async () =>
        {
            ContainerReach reach = await ContainerTools.ReachAsync(engine, container, source, say, stop);
            if (process.Id != 0 && process.CommandLine.Length == 0)
            {
                return (process with { Note = reach.Noted(process.Note) }, reach);
            }
            IReadOnlyList<DotnetProcess> there = await reach.ProcessesAsync(stop);
            DotnetProcess found = process.Id != 0 && there.FirstOrDefault(p => p.Id == process.Id && p.CommandLine == process.CommandLine) is { } same ? same
                : there.FirstOrDefault(p => p.CommandLine == process.CommandLine)
                ?? throw new ContainerToolsException($"{process.Program} no longer runs in {container}");
            return (found with { Note = reach.Noted(found.Note ?? process.Note) }, reach);
        }, stop);
    }

    /// <summary>A program in a container made ready for the profiler (Attach Profiler's): reached and found by its id there
    /// (<see cref="ProgramInContainerAsync"/>), and the place it is recorded at, docker exec, the paths its PDBs name made
    /// this machine's through <paramref name="paths"/> (as they are where it was built here).</summary>
    private async Task<(DotnetProcess Target, ProfilePlace Place)> ProfiledInContainerAsync(DotnetProcess process, ContainerPaths paths, Action<string> say,
        CancellationToken stop)
    {
        string container = process.Container ?? throw new ArgumentException($"{process.Program} runs on this machine, in no container", nameof(process));
        (DotnetProcess target, ContainerReach reach) = await ProgramInContainerAsync(process, container, say, stop);
        return (target, reach.Recorder(paths));
    }

    /// <summary>Attach Profiler's choice among the .NET programs of the engine's running containers, in Attach to Process's
    /// dialog (null when cancelled): what it offers when nothing of the folder's runs.</summary>
    private async Task<DotnetProcess?> ChooseProfiledAsync()
    {
        ContainerEngine engine = Engine;
        AttachToProcessDialog dialog = new(() => BackgroundWork.Run(async () =>
        {
            try
            {
                return await ContainerProcesses.ListAsync(engine, CancellationToken.None);
            }
            catch (Exception unread) when (unread is ContainerCommandException or ContainerToolsException or IOException or TimeoutException
                or System.Text.Json.JsonException or FileNotFoundException)
            {
                IdeLog.Write(IdeLog.Debugger, $"Attach Profiler could not list the containers' programs: {unread.Message}");
                return (IReadOnlyList<DotnetProcess>)[];
            }
        }), _ => false, purpose: AttachPurpose.Profiler);
        AttachToProcessDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            if (AttachToProcessDialog == dialog)
            {
                AttachToProcessDialog = null;
            }
        };
        return await dialog.ShowDialog<DotnetProcess?>(this);
    }

    /// <summary>Reattach to Process: the program last attached to, by its process while it runs, else the one running
    /// under its name and command line (in its container), or its name; the dialog, searched for it, when several are
    /// or none is.</summary>
    private async Task Reattach()
    {
        DotnetProcess last = attachedTo[^1];
        status.Text = $"Looking for {last.Program} to attach to";
        IReadOnlyList<DotnetProcess> running = await AttachableAsync();
        List<DotnetProcess> candidates = running.FirstOrDefault(p => p.Id == last.Id && Same(p, last)) is { } same ? [same]
            : running.Where(p => Same(p, last)).ToList() is { Count: > 0 } alike ? alike
            : [.. running.Where(p => p.Program == last.Program && p.Container == last.Container)];
        if (candidates is [{ } only])
        {
            if (await AttachAsync(only) is { } problem)
            {
                status.Text = problem;
            }
            return;
        }
        status.Text = candidates.Count == 0
            ? $"Reattach to Process: no .NET process named {last.Program} is running{(last.Container is { } container ? $" in {container}" : string.Empty)}"
            : $"Reattach to Process: {candidates.Count} programs are named {last.Program}; choose one";
        ShowAttachToProcess(last.Program);
    }

    private bool WasAttached(DotnetProcess process) => attachedTo.Any(p => Same(p, process));

    private static bool Same(DotnetProcess one, DotnetProcess other) =>
        one.Name == other.Name && one.CommandLine == other.CommandLine && one.Container == other.Container;
}
