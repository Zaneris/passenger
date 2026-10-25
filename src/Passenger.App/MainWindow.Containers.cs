using System.Text.Json;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The window's Containers: its tool window over the person's container engine, the actions on the row chosen
/// there (Start, Stop, Restart, Pause, Remove, Scale, Pull, Prune, Inspect, Open Shell), what the status bar and Problems
/// say of the engine when it cannot serve, and the engine's check before a run of tests that use Testcontainers.</summary>
public partial class MainWindow
{
    private ContainersView? containersView;

    /// <summary>The container engine the window asks: the person's, unless one is given.</summary>
    public ContainerEngine Engine { get; init; } = ContainerEngine.Shared;

    /// <summary>The Containers tool window, built as it is first shown or asked for (an agent's or a run's action on a row).</summary>
    public ContainersView ContainersWindow => containersView ?? (ContainersView)Dock[ToolWindowIds.Containers].Content;

    private void AddContainers()
    {
        // The actions first: the window's toolbar and menu take their titles, and they act on its row chosen.
        ContainerRow? Chosen() => containersView?.Chosen;
        string? OneContainer(ActionContext _) => Chosen()?.Container is null ? "No container is chosen in the Containers window" : null;
        foreach ((string id, string title, string action) in (ReadOnlySpan<(string, string, string)>)[
            (AppActions.ContainerStart, "Start", "start"),
            (AppActions.ContainerStop, "Stop", "stop"),
            (AppActions.ContainerRestart, "Restart", "restart"),
            (AppActions.ContainerPause, "Pause or Unpause", "pause"),
            (AppActions.ContainerRemove, "Remove", "remove"),
            (AppActions.ContainerPull, "Pull", "pull")])
        {
            actions!.Add(new IdeAction(id, title, "Containers", context => IdeErrors.Observe(id == AppActions.ContainerRemove ? RemoveAsync(Chosen()!) : ContainerActAsync(id, action, Chosen()!)))
            {
                WhyNot = _ => ContainerCommands.For(action, Chosen()) is { Words: null } refused ? refused.WhyNot ?? $"{Chosen()?.Name} cannot {action}" : null,
                Works = id,
            });
        }
        actions!.Add(new IdeAction(AppActions.ContainersRefresh, "Refresh", "Containers", context => IdeErrors.Observe(ContainersWindow.RefreshAsync())));
        actions.Add(new IdeAction(AppActions.ContainerScale, "Scale Service…", "Containers", context => IdeErrors.Observe(ScaleAsync(Chosen()!)))
        {
            WhyNot = _ => Chosen() is { Kind: ContainerRowKind.Service, Project: not null } chosen
                ? chosen.LeftUnstarted ? ContainerCommands.NotStarted(chosen) : null : "No Compose service is chosen in the Containers window",
            Works = AppActions.ContainerScale,
        });
        actions.Add(new IdeAction(AppActions.ContainerPrune, "Prune…", "Containers", context => IdeErrors.Observe(PruneAsync(Chosen())))
        {
            WhyNot = _ => Engine.Known is { IsReady: true } ? null : "The container engine is not running",
            Works = AppActions.ContainerPrune,
        });
        actions.Add(new IdeAction(AppActions.ContainerInspect, "Inspect", "Containers", context => IdeErrors.Observe(InspectAsync(Chosen()!)))
        {
            WhyNot = _ => Chosen() is { Kind: not ContainerRowKind.Group and not ContainerRowKind.Project and not ContainerRowKind.AppHost } ? null
                : "No container, image, volume or network is chosen in the Containers window",
        });
        actions.Add(new IdeAction(AppActions.ContainerShell, "Open Shell", "Containers", context => IdeErrors.Observe(OpenShellAsync(Chosen()!.Container!))) { WhyNot = OneContainer, Words = ["docker exec", "Attach"] });
        foreach (string tab in ContainersView.Pages)
        {
            // Log and Stats are every row's; the rest are a chosen row's, which a window not yet built has none of.
            actions.Add(new IdeAction(ContainersView.PageAction(tab), $"Show Container {tab}", "Containers", _ =>
            {
                ShowToolWindow(ToolWindowIds.Containers);
                ContainersWindow.ShowTab(tab);
            })
            {
                WhyNot = _ => tab is RunWindows.LogTab or RunWindows.StatsTab ? null
                    : containersView is { } view ? view.WhyNotTab(tab) : $"Nothing chosen in the Containers window has a {tab} tab",
            });
        }
        actions.Add(new IdeAction(AppActions.ContainersLogFind, "Find in Container Logs", "Containers", _ =>
        {
            ShowToolWindow(ToolWindowIds.Containers);
            ContainersWindow.ShowTab(RunWindows.LogTab);
            ContainersWindow.Log.OpenFind();
        }));
        actions.Add(new IdeAction(AppActions.ContainersShowTrace, "Show Container Log Trace", "Containers", _ => ContainersWindow.Telemetry.ShowSelectedTrace())
        {
            WhyNot = _ => containersView?.Telemetry.SelectedLog is { TraceId.Length: > 0 } ? null : "No log with a trace is selected",
        });
        actions.Add(new IdeAction(AppActions.ContainersShowRelatedLogs, "Show Related Container Logs", "Containers", _ => ContainersWindow.Telemetry.ShowRelatedLogs())
        {
            WhyNot = _ => containersView?.Telemetry is { SelectedTrace: not null, SelectedSpan: not null } ? null : "No trace span is selected",
        });

        AddServiceActions();
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Containers, "Containers", IconKeys.Containers, MakeContainers, Docking.ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowContainers,
            Menus = [new MenuPlace("Tools", 0, 4)],
            Chosen = () => containersView?.Chosen,
        });
    }

    /// <summary>The Containers window's view, as it is first shown or asked for.</summary>
    private ContainersView MakeContainers()
    {
        ContainersView view = new(new ContainersParts(() => Engine, work, Say, Browse, () => StateFolder, id => actions![id]!.Title, RowMenuItem));
        containersView = view;
        view.RowChosen += (_, _) => lastServiceWindow = ToolWindowIds.Containers;
        view.AppHostOf = container => session is { IsEnded: false } current
            && current.Aspire.Apps.FirstOrDefault(app => app.KnownContainerIds.Any(id => ContainerRow.SameId(id, container.Id))) is { } run
            ? (run.Configuration.Project, run.Configuration.Name) : null;
        // Those an orchestrator ran on its own runtime, where it is not the engine docker reaches.
        view.OtherEngines = () => session is { IsEnded: false } current ? current.Aspire.ContainerEngines : [];
        view.ActionRequested += (_, action) => Request(action);
        view.LinkClicked += (_, link) => OpenLink(link);
        view.TelemetryOf = project => session.ContainerRun is { } run && run.Plan.ProjectName == project ? run.Telemetry : null;
        view.UnstartedOf = project => buildRun.Containers is { } run && run.Plan.ProjectName == project ? run.Unstarted : [];
        view.StateRead += (_, state) => ShowEngine(state);
        view.Files.Opened += (_, copy) => OpenCopy(copy);
        view.Files.Problem += (_, problem) => status.Text = problem;
        // The window's first use of it: Inspect's copies an earlier Passenger wrote unmasked go before anything reads them.
        view.AttachedToVisualTree += (_, _) => IdeErrors.Observe(ForgetUnmaskedCopiesAsync());
        // Added after the window marks its tool windows' buttons (ShowActionWork): its own marked as it is built.
        ActionButtons.ShowMarked(view, actions!);
        return view;
    }

    /// <summary>The window's folder opened: when it holds a Compose file or a Dockerfile, the engine is asked once, off the
    /// UI thread, so a missing or stopped one is said before anything needs it.</summary>
    private void CheckEngineFor(string? folder)
    {
        string[] names = ["compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml", "Dockerfile"];
        if (folder is null || !names.Any(name => File.Exists(Path.Combine(folder, name))))
        {
            return;
        }
        ProjectSession asking = session;
        ContainerEngine engine = Engine;
        IdeErrors.Observe(ShowOnceRead());

        async Task ShowOnceRead()
        {
            EngineState state = await BackgroundWork.Run(() => engine.StateAsync());
            if (session == asking)
            {
                ShowEngine(state);
            }
        }
    }

    /// <summary>The engine's state, said in the status bar while it cannot serve (with where a click starts it, or shows
    /// what to do) and listed in Problems' Containers tab, which empties once it can; when a Dockerfile went unchecked, what
    /// the engine cannot check of it too (Podman's missing BuildKit, while Settings would check with it).</summary>
    private void ShowEngine(EngineState state, bool dockerfile = false)
    {
        UiThread.Check();
        string where = state.Context ?? state.EngineName;
        if (state.Problem is not { } problem)
        {
            bool limited = dockerfile && settings.BuildKitChecks && state.Limit is not null;
            Bottom.Problems.ShowContainers(limited ? [new Problem(ProblemSeverity.Warning, state.Limit!, null, null, new TextPosition(0, 0), where)] : []);
            if (limited)
            {
                status.Say(state.Limit!, ProblemSeverity.Warning, StatusPlace.Problems(Dock, Bottom.Problems, ProblemsTab.Containers));
            }
            return;
        }
        Bottom.Problems.ShowContainers([new Problem(ProblemSeverity.Warning, problem, null, null, new TextPosition(0, 0), where)]);
        status.Say(problem, ProblemSeverity.Warning, EnginePlace(state));
    }

    /// <summary>Where a click on the engine's problem goes: Docker Desktop started, where it is installed and Docker is
    /// not running; otherwise Problems' Containers tab, which says what to do.</summary>
    private StatusPlace EnginePlace(EngineState state) =>
        state is { Status: EngineStatus.NotRunning, IsPodman: false } && EngineState.DesktopApp is { } desktop
            ? new StatusPlace("start Docker Desktop", () => StartDesktop(desktop))
            : StatusPlace.Problems(Dock, Bottom.Problems, ProblemsTab.Containers);

    /// <summary>Starts Docker Desktop, which outlives the IDE: through LaunchServices on macOS, as a child that ends
    /// nothing of Passenger's on Windows.</summary>
    private void StartDesktop(string desktop)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                IdeErrors.Observe(ChildProcesses.RunAsync(ChildProcesses.Command("/usr/bin/open", ["-a", desktop]), limit: TimeSpan.FromSeconds(30)));
            }
            else
            {
                ChildProcesses.StartApart(ChildProcesses.Command(desktop, []));
            }
            status.Text = "Starting Docker Desktop…";
        }
        catch (Exception failed) when (failed is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            status.Text = $"Docker Desktop did not start: {failed.Message}";
        }
    }

    /// <summary>A session's test runner asks the window's engine before a run of tests that use Testcontainers, and what
    /// the engine finds in its open Dockerfiles and compose files is listed in Problems' Containers tab.</summary>
    private void BindContainers(ProjectSession next)
    {
        Bottom.Problems.ShowContainerFiles([]);
        if (next.Web is { } web)
        {
            web.Checks.Changed += (_, _) =>
            {
                if (session == next)
                {
                    Bottom.Problems.ShowContainerFiles(web.Checks.Problems);
                }
            };
            web.Checks.Limited += (_, state) =>
            {
                if (session == next)
                {
                    ShowEngine(state, dockerfile: true);
                }
            };
        }
        next.Tests.ContainerEngine = async cancel =>
        {
            EngineState state = await Engine.StateAsync(again: true, cancel);
            UiThread.Post(() =>
            {
                if (session == next)
                {
                    ShowEngine(state);
                }
            });
            return state.Status is EngineStatus.Ready or EngineStatus.NoCompose ? (null, state.Endpoint) : (state.Problem, null);
        };
        CheckEngineFor(next.Folder);
    }

    /// <summary>Prune, asked first: what the chosen heading (or the chosen object's kind) gathers that nothing uses.</summary>
    private async Task PruneAsync(ContainerRow? chosen)
    {
        string kind = chosen?.Object?.Kind ?? (chosen?.Kind == ContainerRowKind.Group ? chosen.Label : "container");
        (string what, IReadOnlyList<string> words) = ContainerCommands.Prune(kind);
        ConfirmDialog asked = new("Prune", $"Remove {what}? This cannot be undone.", "Prune");
        Asked = asked;
        if (!await asked.ShowDialog<bool>(this))
        {
            return;
        }
        ContainerRow row = chosen ?? new ContainerRow(ContainerRowKind.Group, 0, "group:Containers", "Containers");
        await ContainersWindow.ActAsync(AppActions.ContainerPrune, "prune", row, words);
    }

    /// <summary>Remove: a container, image or network at once, a Compose project taken down with its volumes kept, and a
    /// volume only once asked, since its data goes with it.</summary>
    private async Task<string?> RemoveAsync(ContainerRow row)
    {
        if (row.Object is { Kind: "volume" } volume)
        {
            ConfirmDialog asked = new("Remove Volume", $"Remove the volume {volume.Name} and the data it holds? This cannot be undone.", "Remove");
            Asked = asked;
            if (!await asked.ShowDialog<bool>(this))
            {
                return "Not removed";
            }
        }
        return await ContainerActAsync(AppActions.ContainerRemove, "remove", row);
    }

    /// <summary>The question Prune or a volume's Remove asked last.</summary>
    public ConfirmDialog? Asked { get; private set; }

    /// <summary>Scale: how many containers a Compose service runs, asked for, then <c>docker compose up --scale</c>.</summary>
    private async Task ScaleAsync(ContainerRow service)
    {
        string? count = await AskNameAsync($"Containers of {service.Name}", service.Containers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            text => int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n is >= 0 and <= 100
                ? null : "A number from 0 to 100");
        if (count is null || service.Project is not { } project)
        {
            return;
        }
        await ContainersWindow.ActAsync(AppActions.ContainerScale, "scale", service,
            [.. project.Arguments, "up", "-d", "--no-recreate", "--scale", $"{service.Service}={count}", "--", service.Service!]);
    }

    /// <summary>Inspect: what the engine says of a container, image, volume or network, as JSON in a read-only tab, each
    /// secret's value in its environment masked (<see cref="ContainerSecrets"/>), so no copy of it is written to disk.</summary>
    private async Task InspectAsync(ContainerRow row)
    {
        IReadOnlyList<string> words = row.Container is { } container ? ["inspect", container.Id]
            : row.Object is { Kind: "volume" } volume ? ["volume", "inspect", volume.Name]
            : row.Object is { Kind: "network" } network ? ["network", "inspect", network.Id]
            : row.Object is { } image ? ["image", "inspect", image.Id] : [];
        if (words.Count == 0)
        {
            return;
        }
        ContainerEngine engine = row.Container?.Engine ?? Engine;
        await ForgetUnmaskedCopiesAsync();
        string copy = Path.Combine(ContainerSecrets.InspectCopies(StateFolder), $"{string.Concat(row.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'))}.json");
        string? problem = await BackgroundWork.Run(async () =>
        {
            try
            {
                System.Text.Json.Nodes.JsonNode? said = System.Text.Json.Nodes.JsonNode.Parse((await engine.JsonAsync(words)).GetRawText());
                ContainerSecrets.Mask(said);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                AtomicFile.Write(copy, System.Text.Encoding.UTF8.GetBytes(said?.ToJsonString(Inspected) + "\n"));
                return null;
            }
            catch (Exception failed) when (failed is ContainerCommandException or TimeoutException or JsonException or IOException)
            {
                return failed.Message;
            }
        });
        if (problem is not null)
        {
            status.Text = $"{row.Name} could not be inspected: {problem}";
            return;
        }
        OpenCopy(copy);
    }

    /// <summary>Inspect's copies an earlier Passenger wrote before it masked them, removed off the UI thread once a process
    /// for the window's state (<see cref="ContainerSecrets.ForgetUnmasked"/>): as the window starts and at its Containers
    /// window's first use. A tab that showed one closes, since its text holds the secret, and the status bar says why.</summary>
    private async Task ForgetUnmaskedCopiesAsync()
    {
        string state = StateFolder;
        IReadOnlyList<string> removed = await BackgroundWork.Run(() => ContainerSecrets.ForgetUnmasked(state));
        if (removed.Count == 0)
        {
            return;
        }
        foreach (OpenDocument shown in Editors.Documents.Where(d => removed.Contains(d.Path)).ToList())
        {
            Editors.CloseDeleted(shown);
        }
        status.Text = removed.Count == 1
            ? $"Removed {Path.GetFileName(removed[0])}, Inspect's copy written before Passenger masked the secrets in it"
            : $"Removed {removed.Count} of Inspect's copies written before Passenger masked the secrets in them";
    }

    /// <summary>Inspect's JSON as it reads: indented, its characters (a masked secret's dots, an image's names) as they are.</summary>
    private static readonly JsonSerializerOptions Inspected = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Opens a copy Passenger wrote under its cache (a container's file, Inspect's JSON), read-only as every such
    /// copy is: a tab that shows it already takes the copy's new text, which no watcher reports outside the folder.</summary>
    private void OpenCopy(string copy)
    {
        string full = PathNames.Canonical(copy);
        if (Editors.Find(full) is { } open && open.ChangedOnDisk())
        {
            open.Reload(overUnsaved: true);
        }
        OpenFile(full);
    }

    /// <summary>Open Shell: <c>docker exec -it … sh</c> in a Terminal tab of the container's name; an image with no shell
    /// is said, rather than a tab that ends at once.</summary>
    public async Task<TerminalRun?> OpenShellAsync(ContainerInfo container)
    {
        ContainerEngine engine = container.Engine ?? Engine;
        string? problem = await BackgroundWork.Run(async () =>
        {
            try
            {
                ChildResult tried = await engine.RunAsync(["exec", container.Id, "sh", "-c", "exit 0"]);
                string said = ContainerEngine.Said(tried);
                return tried.Succeeded ? null
                    : said.Contains("executable file not found", StringComparison.Ordinal) || said.Contains("no such file", StringComparison.OrdinalIgnoreCase)
                    ? $"{container.Image} has no shell (sh) to open in {container.Name}"
                    : said.Contains("is not running", StringComparison.Ordinal) ? $"Start {container.Name} to open a shell in it" : said;
            }
            catch (Exception failed) when (failed is TimeoutException or FileNotFoundException or System.ComponentModel.Win32Exception)
            {
                return failed.Message;
            }
        });
        if (problem is not null)
        {
            status.Text = problem;
            return null;
        }
        try
        {
            TerminalRun run = await TerminalPanel.RunAsync($"{container.Name} (shell)", engine.Command(["exec", "-it", container.Id, "sh"]));
            TerminalPanel.FocusView();
            return run;
        }
        catch (LaunchException failed)
        {
            status.Text = failed.Message;
            return null;
        }
    }
}
