using Avalonia.Controls;
using Avalonia.Interactivity;
using Passenger.App.Run.Devices;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Where the chosen configuration's app runs: for a project whose frameworks run on devices, emulators or
/// simulators, the widget beside the run configuration's names the target chosen, and its action lists what the
/// project's platforms run on as their workloads list it (Android's devices and emulators, those not running too, which
/// Run and Debug start). The choice is the project's, kept with the folder, and decides the framework built; one no
/// longer listed is said.</summary>
public partial class MainWindow
{
    /// <summary>Why the last listing of the project's targets found none, where it could not list them.</summary>
    private string? targetsUnlisted;

    private void AddRunTargets()
    {
        actions!.Add(new IdeAction(A.ChooseRunTarget, "Choose Where It Runs…", "Run", _ => IdeErrors.Observe(ChooseRunTargetAsync()))
        {
            WhyNot = _ => TargetProject() is null ? "The run configuration chosen runs no app on a device, an emulator or a simulator" : null,
        });
    }

    /// <summary>The session's choices and listings shown in the widget as they change.</summary>
    private void BindRunTargets(ProjectSession next)
    {
        next.Targets.Changed += (_, _) =>
        {
            if (session == next)
            {
                ShowRunTarget();
            }
        };
        next.ProjectsEvaluated += (_, _) =>
        {
            if (session == next)
            {
                ShowRunTarget();
            }
        };
        ShowRunTarget();
    }

    /// <summary>The chosen configuration's project where its app runs on targets; null for any other.</summary>
    private ProjectModel? TargetProject() =>
        session.Configurations.Chosen is { Program.Length: 0, IsContainer: false, IsMultiple: false } chosen
        && session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, chosen.Project)) is { } project
        && DeviceTargets.PlatformsOf(project.TargetFrameworks).Count > 0 ? project : null;

    /// <summary>The widget shown for a project of targets, naming the one chosen, its tip saying why where none is or it has
    /// gone; hidden for any other.</summary>
    private void ShowRunTarget()
    {
        ProjectModel? project = TargetProject();
        RunTargetButton.IsVisible = project is not null;
        if (project is null)
        {
            return;
        }
        KeptTarget? chosen = session.Targets.Of(project);
        DeviceTarget? found = session.Targets.Found(project);
        string? gone = session.Targets.WasListed(project) ? DeviceTargets.Gone(chosen, session.Targets.Listed(project), project.Name) : null;
        RunTargetName.Text = found?.Shown ?? chosen?.Name ?? "Choose a device";
        ToolTip.SetTip(RunTargetButton, gone ?? (chosen is null ? Refusals.NoRunTarget(project.Name) : $"{project.Name} runs on {chosen.Name}"));
    }

    private void OnRunTargetClicked(object? sender, RoutedEventArgs e) => Request(A.ChooseRunTarget);

    /// <summary>The project's targets under the widget, the one chosen checked: listed first where they were not yet,
    /// then listed again behind the menu, which shows what came once it closes and opens again.</summary>
    private async Task ChooseRunTargetAsync()
    {
        UiThread.Check();
        if (TargetProject() is not { } project)
        {
            return;
        }
        if (!session.Targets.WasListed(project))
        {
            using Working? work = actions!.Work.Begin(A.ChooseRunTarget, $"Looking for where {project.Name} runs…");
            await ListRunTargetsAsync(project);
        }
        else
        {
            IdeErrors.Observe(ListRunTargetsAsync(project));
        }
        if (TargetProject() == project)
        {
            ShowRunTargets(project);
        }
    }

    /// <summary>The project's targets listed again by each platform's workload (Android's now), off the window's thread.</summary>
    private async Task ListRunTargetsAsync(ProjectModel project)
    {
        ProjectSession listing = session;
        List<DeviceTarget> found = [];
        string? problem = null;
        if (DeviceTargets.FrameworkFor(project.TargetFrameworks, TargetPlatform.Android) is { } android)
        {
            Dictionary<string, string> globals = ProjectModels.Properties(listing.Workspace, project);
            (IReadOnlyList<DeviceTarget> targets, AndroidTools? tools, string? unlisted) =
                await BackgroundWork.Run(() => AndroidDevices.ListAsync(project.Path, android, globals, CancellationToken.None));
            found.AddRange(targets);
            problem = tools is null ? unlisted : tools.Lacks is { } lacks ? Refusals.NoAndroidPlatform(lacks) : null;
        }
        if (session == listing && !listing.IsEnded)
        {
            targetsUnlisted = problem;
            listing.Targets.List(project, found);
        }
    }

    /// <summary>The menu of the project's targets as last listed, with Refresh below them.</summary>
    private void ShowRunTargets(ProjectModel project)
    {
        ContextMenu menu = new();
        KeptTarget? chosen = session.Targets.Of(project);
        foreach (DeviceTarget target in session.Targets.Listed(project))
        {
            menu.Items.Add(MenuChoice.Of(target.Shown, chosen?.Platform == target.Platform && chosen.Key == target.Key, () => ChooseRunTarget(project, target)));
        }
        if (targetsUnlisted is { } unlisted)
        {
            // What the platform lacks is said above its targets: they are listed, but no app is built for them until it has it.
            menu.Items.Insert(0, new MenuItem
            {
                Header = new TextBlock { Text = unlisted, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420, Classes = { StyleClasses.Muted } },
                IsEnabled = false,
            });
        }
        else if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Refusals.NoTargets, IsEnabled = false });
        }
        menu.Items.Add(new Separator());
        MenuItem refresh = new() { Header = "Look Again" };
        refresh.Click += (_, _) => IdeErrors.Observe(LookAgainAsync());
        menu.Items.Add(refresh);
        Open(menu, RunTargetButton);

        async Task LookAgainAsync()
        {
            using Working? work = actions!.Work.Begin(A.ChooseRunTarget, $"Looking for where {project.Name} runs…");
            await ListRunTargetsAsync(project);
            if (TargetProject() == project)
            {
                ShowRunTargets(project);
            }
        }
    }

    /// <summary>A target chosen for a project: kept with the folder, and its platform's framework chosen for the project.</summary>
    public void ChooseRunTarget(ProjectModel project, DeviceTarget target)
    {
        UiThread.Check();
        session.Targets.Choose(project, target);
        if (DeviceTargets.FrameworkFor(project.TargetFrameworks, target.Platform) is { } framework
            && TargetFrameworkChoices.Offered(project).Contains(framework, StringComparer.OrdinalIgnoreCase))
        {
            session.Frameworks.Choose(project, framework);
        }
        Say(target.Running || target.Kind != TargetKind.Emulator
            ? $"{project.Name} runs on {target.Name}" : $"{project.Name} runs on {target.Name}, which Run and Debug start");
    }

    /// <summary>Choose Where It Runs for an agent, which no menu reaches: the chosen configuration's project's targets listed
    /// again, and the one <paramref name="input"/> names (by its name, serial or AVD) chosen as the widget chooses it; with
    /// none named, what there is to choose. Why not, thrown.</summary>
    internal async Task<string> ChooseRunTargetForAgentAsync(string? input)
    {
        UiThread.Check();
        if (TargetProject() is not { } project)
        {
            throw new InvalidOperationException("The run configuration chosen runs no app on a device, an emulator or a simulator");
        }
        await ListRunTargetsAsync(project);
        IReadOnlyList<DeviceTarget> listed = session.Targets.Listed(project);
        string offered = listed.Count == 0 ? targetsUnlisted ?? Refusals.NoTargets : Plural.Listed([.. listed.Select(t => t.Shown)]);
        if (input is not { Length: > 0 } named)
        {
            return $"{project.Name} runs on {session.Targets.Of(project)?.Name ?? "nothing chosen yet"}; it can run on {offered}";
        }
        DeviceTarget target = listed.FirstOrDefault(t => t.Name.Equals(named, StringComparison.OrdinalIgnoreCase) || t.Id.Equals(named, StringComparison.Ordinal)
            || t.Avd is { } avd && avd.Equals(named, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{project.Name} cannot run on {named}: it can run on {offered}");
        ChooseRunTarget(project, target);
        return $"{project.Name} runs on {target.Name}{(targetsUnlisted is { } unlisted ? $"; {unlisted}" : string.Empty)}";
    }
}
