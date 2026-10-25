using Passenger.App.Aspire;
using Passenger.App.Containers;
using Passenger.App.Run;

namespace Passenger.App;

/// <summary>A .NET service of a run of services under way (a container run's, an AppHost's), acted on without taking the
/// run down, one set of actions from the Containers and Aspire windows' rows and toolbars and the gutter's run icon: Debug
/// restarts it held and attaches (kept for the run's restarts and Rerun; an AppHost's that does not run started so), Attach
/// attaches as it runs, Run without Debugger lets its session go, and Profile records it where it runs as Attach Profiler
/// does; the window's Restart, Stop and Remove of one, and an agent's, go through the run, its session ended first and,
/// restarted, attached again.</summary>
public partial class MainWindow
{
    /// <summary>The question a container run asked last of containers it would replace.</summary>
    public ReplaceContainersDialog? ReplaceQuestion { get; private set; }

    /// <summary>Asks whether a container run may replace containers of its Compose project Passenger did not make; a start
    /// stopped meanwhile closes the question, answered Cancel.</summary>
    private async Task<bool> AskReplaceAsync(string configuration, string project, IReadOnlyList<string> containers, CancellationToken stop)
    {
        ReplaceContainersDialog question = new(configuration, project, containers);
        ReplaceQuestion = question;
        try
        {
            return await AskAsync(question, stop);
        }
        finally
        {
            if (ReplaceQuestion == question)
            {
                ReplaceQuestion = null;
            }
        }
    }

    /// <summary>A yes-or-no question's answer, the question closed unanswered (no) once <paramref name="stop"/> is canceled.</summary>
    private async Task<bool> AskAsync(Avalonia.Controls.Window question, CancellationToken stop)
    {
        using CancellationTokenRegistration closing = stop.Register(() => UiThread.Post(() =>
        {
            if (question.IsVisible)
            {
                question.Close(false);
            }
        }));
        return await question.ShowDialog<bool>(this);
    }

    /// <summary>The Containers or Aspire window, whichever had a row chosen last: the service actions take their service from
    /// it when neither has the keys and both or neither is shown.</summary>
    private string lastServiceWindow = ToolWindowIds.Containers;

    /// <summary>What a service action acts on: a .NET service of a run (<paramref name="Run"/>, by its <paramref name="Name"/>
    /// there), one among its run's services now (<paramref name="Runs"/>: running, or a container run's left unstarted, which
    /// its actions start) or an AppHost's project resource that does not run, which Debug and Run without Debugger start
    /// (<paramref name="Resource"/> as the Aspire window chose it, else found by its name as the action runs); or why there is
    /// none.</summary>
    private sealed record ServiceAim(ServiceRun? Run, string? Name, bool Runs, AspireSelection? Resource, string? WhyNot)
    {
        public static ServiceAim Refused(string why) => new(null, null, false, null, why);
    }

    /// <summary>The service actions, one set for every kind of run (a container run's services, an AppHost's), from the
    /// Containers window's rows, the Aspire window's, either's toolbar and the gutter's icon beside a service that runs, greyed
    /// with why where they cannot act; Find Action finds them by the names each window gave them before too.</summary>
    private void AddServiceActions()
    {
        foreach ((string id, string title, string formerly) in (ReadOnlySpan<(string, string, string)>)[
            (AppActions.ServiceDebug, "Debug Service", "Debug Aspire Resource"),
            (AppActions.ServiceAttach, "Attach to Service", "Attach to Aspire Resource"),
            (AppActions.ServiceRun, "Run Service without Debugger", "Run Aspire Resource Without Debugger"),
            (AppActions.ServiceProfile, "Profile Service", "Profile Aspire Resource")])
        {
            actions!.Add(new IdeAction(id, title, "Services", context => IdeErrors.Observe(ServiceActionAsync(id, AimOf(context))))
            {
                WhyNot = context => WhyNotOn(id, AimOf(context)),
                Words = [formerly, title.Replace("Service", "Container Service", StringComparison.Ordinal)],
            });
        }
    }

    /// <summary>The service a service action acts on, by where it was asked: the one its gutter menu names, else the one chosen
    /// in the Containers or Aspire window: the one with the keys, else the one of the two shown, else the one chosen in last.</summary>
    private ServiceAim AimOf(ActionContext context)
    {
        if (context.Service is { } named)
        {
            return named.Run.IsStopped ? ServiceAim.Refused($"{named.Run.Name} is not running")
                : new(named.Run, named.Name, named.Run.ServiceNamed(named.Name) is not null, null, null);
        }
        bool aspireShown = Dock.IsShown(ToolWindowIds.Aspire);
        string from = context.ToolWindow?.Id is ToolWindowIds.Aspire or ToolWindowIds.Containers ? context.ToolWindow.Id
            : aspireShown != Dock.IsShown(ToolWindowIds.Containers) ? aspireShown ? ToolWindowIds.Aspire : ToolWindowIds.Containers
            : lastServiceWindow;
        return from == ToolWindowIds.Aspire ? AimOf(Aspire.Selection) : AimOf(containersView?.Chosen);
    }

    /// <summary>The .NET service of the container run a Containers window's row stands for, or why there is none.</summary>
    private ServiceAim AimOf(ContainerRow? row) => buildRun.ServiceOfRun(ServiceOfRow(row)) is ({ } service, null)
        ? new(buildRun.Containers, service.Name, true, null, null) : ServiceAim.Refused(buildRun.ServiceOfRun(ServiceOfRow(row)).WhyNot!);

    /// <summary>The .NET service of an AppHost the window runs that an Aspire window's resource is, whether it runs now or
    /// not; or why there is none: nothing chosen, an AppHost Passenger does not run, or a resource that is no .NET project (a
    /// container, a parameter, an external service).</summary>
    private ServiceAim AimOf(AspireSelection? chosen)
    {
        if (chosen is null)
        {
            return ServiceAim.Refused("No resource is chosen in the Aspire window");
        }
        if (session.Aspire.FindApp(chosen.AppHostPath) is not { IsTest: false, IsStopped: false } app)
        {
            return ServiceAim.Refused(NotRunHere(chosen.AppHostPath));
        }
        if (!chosen.Resource.IsProject)
        {
            return ServiceAim.Refused($"{chosen.Resource.DisplayName} is not a .NET service of {app.Name}");
        }
        return session.Aspire.FindService(chosen) is IRunService running ? new(app, running.Name, true, chosen, null)
            : new(app, app.UnstartedName(chosen.Resource) ?? chosen.Resource.DisplayName, false, chosen, null);
    }

    /// <summary>Why an AppHost's services cannot be acted on: Passenger does not run it.</summary>
    private static string NotRunHere(string appHost) =>
        $"Passenger does not run {Path.GetFileNameWithoutExtension(appHost)}: Run, Debug or Profile its configuration first";

    /// <summary>Why a service action cannot act on what it aims at, or null: a service that runs as its run says
    /// (<see cref="BuildRunController.ServiceOf"/>); an AppHost's resource that does not run, Debug and Run without Debugger
    /// starting it where it advertises a start, Attach and Profile needing it running.</summary>
    private string? WhyNotOn(string id, ServiceAim aim) =>
        aim.WhyNot ?? (aim.Runs ? buildRun.WhyNotOnService(id, aim.Name, aim.Run)
            : id is not (AppActions.ServiceDebug or AppActions.ServiceRun)
                ? aim.Name is { } named && aim.Run!.LeftUnstarted(named) ? aim.Run.WhyNotStarted(named) : $"{aim.Name} of {aim.Run!.Name} is not running: Debug Service starts it"
            : aim.Resource is { } chosen && StartOf(chosen.Resource) is null ? $"{aim.Name} of {aim.Run!.Name} advertises no start command"
            : null);

    /// <summary>A resource's start command as its AppHost advertises it, enabled (its restart, else its start), or null.</summary>
    private static AspireResourceCommand? StartOf(AspireResource resource) =>
        resource.Commands.FirstOrDefault(candidate => candidate.Name is "restart" or "resource-restart" && candidate.Enabled)
            ?? resource.Commands.FirstOrDefault(candidate => candidate.Name is "start" or "resource-start" && candidate.Enabled);

    /// <summary>A service action on what it aims at: a .NET service that runs through its run of services (its kind's own way
    /// where it has one: Debug of an AppHost's service debugged already chooses its tab); an AppHost's project resource that
    /// does not run now started by its AppHost, Debug held for the debugger and Run without Debugger unheld, as an agent's
    /// <c>debug_start</c> and <c>run</c> start one. Null once done, else why not (said too).</summary>
    private async Task<string?> ServiceActionAsync(string id, ServiceAim aim)
    {
        if (WhyNotOn(id, aim) is { } why)
        {
            status.Text = why;
            return why;
        }
        if (aim.Run is not AspireAppRun app)
        {
            return await ServiceActionAsync(id, aim.Name);
        }
        ProjectSession current = session;
        if (aim.Runs && app.ServiceNamed(aim.Name!) is AspireResourceRun running)
        {
            return id switch
            {
                AppActions.ServiceDebug or AppActions.ServiceAttach => await DebugAspireRunningAsync(current, app, running, attach: id == AppActions.ServiceAttach),
                AppActions.ServiceRun => await buildRun.RunServiceAsync(app, aim.Name!),
                _ => await ProfileAspireRunningAsync(current, app, running),
            };
        }
        if (id is not (AppActions.ServiceDebug or AppActions.ServiceRun))
        {
            return Told($"{aim.Name} of {app.Name} is not running: Debug Service starts it");
        }
        try
        {
            AspireSelection? resource = aim.Resource ?? One((await ReadAspireResourcesAsync(app.Configuration.Project)).Select(r => new AspireSelection(app.Configuration.Project, r)),
                s => s.Resource.IsNamed(aim.Name));
            return resource is null ? Told($"{aim.Name} is not one resource of {app.Name}")
                : await RestartAspireServiceAsync(current, resource, debugging: id == AppActions.ServiceDebug);
        }
        catch (Exception error) when (AspireFailure(error))
        {
            return Told(error.Message);
        }

        string Told(string words)
        {
            if (session == current)
            {
                Say(words);
            }
            return words;
        }
    }

    /// <summary>The service of the container run a row of the Containers window stands for (a service of its Compose
    /// project, or one of its containers), or null.</summary>
    private string? ServiceOfRow(ContainerRow? row)
    {
        if (buildRun.Containers is not { } run || row is null)
        {
            return null;
        }
        (string? project, string? service) = row.Project is { } compose ? (compose.Name, row.Service) : (row.Container?.Project, row.Container?.Service);
        return project == run.Plan.ProjectName ? service : null;
    }

    /// <summary>One of a service's actions on the container run under way: null once done, else why not (said too).</summary>
    public Task<string?> ServiceActionAsync(string id, string? service)
    {
        if (buildRun.ServiceOfRun(service) is not ({ } running, null))
        {
            string why = buildRun.ServiceOfRun(service).WhyNot!;
            status.Text = why;
            return Task.FromResult<string?>(why);
        }
        return id switch
        {
            AppActions.ServiceDebug => buildRun.DebugServiceAsync(running.Name),
            AppActions.ServiceAttach => buildRun.AttachServiceAsync(running.Name),
            AppActions.ServiceRun => buildRun.RunServiceAsync(running.Name),
            _ => buildRun.ProfileServiceAsync(buildRun.Containers, running.Name),
        };
    }

    /// <summary>A Containers window action on a row, through the container run when it is one of its .NET services: its
    /// session (and the profiler's recording) ended before Stop or Remove takes its container, and Restart restarting it as
    /// the run does, held again and attached to again where it was debugged; and on the run's own project, its Restart, Stop
    /// and Remove the run's (<see cref="BuildRunController.ProjectActionAsync"/>). Null once done, else why not.</summary>
    public async Task<string?> ContainerActAsync(string id, string action, ContainerRow row)
    {
        // The run's own project: its Restart, Stop and Remove are the run's, its sessions ended first.
        if (row is { Kind: ContainerRowKind.Project, Project: { } project } && buildRun.Containers?.Plan.ProjectName == project.Name && action is "restart" or "stop" or "remove")
        {
            return await buildRun.ProjectActionAsync(action);
        }
        if (ServiceOfRow(row) is { } service && buildRun.ServiceOfRun(service).Service is not null)
        {
            // One the run left unstarted has no container to start: the run starts it, as its Restart does.
            if (action == "restart" || action == "start" && buildRun.LeftUnstarted(service))
            {
                return await buildRun.RestartServiceAsync(service);
            }
            if (action is "stop" or "remove")
            {
                await buildRun.EndServiceSessionsAsync(service);
            }
        }
        return await ContainersWindow.ActAsync(id, action, row);
    }
}
